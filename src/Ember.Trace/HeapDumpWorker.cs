using System.Diagnostics;
using System.Diagnostics.Tracing;
using Ember.Core;
using Microsoft.Diagnostics.NETCore.Client;

namespace Ember.Trace;

/// <summary>
/// One heap dump per process: attach, capture the GC heap walk, reduce it to an aggregate and
/// delete the raw trace. The raw trace is the only artifact of this feature that cannot be
/// redacted, so it never survives the request and never enters the report.
/// </summary>
internal static class HeapDumpWorker
{
    // The runtime emits the whole walk as one burst. A quiet gap after the burst means the dump is
    // complete; the minimum keeps a slow start from being mistaken for a finished capture.
    private const int QuietMilliseconds = 1500;
    private const int MinimumCaptureSeconds = 3;
    // Within one walk the object nodes and references are emitted before the roots that point into
    // them, so ending the session on a short gap can truncate the roots and leave most of the heap
    // looking unreachable. Every session that attaches while the capture runs makes the runtime emit
    // another whole walk into this session, so while segments keep attaching the stream never goes
    // quiet: silence is only the end of the dump after this long.
    private static readonly TimeSpan StopAfterQuiet = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FinishAfterQuiet = TimeSpan.FromSeconds(5);

    public static async Task<int> Run(string directory)
    {
        var job = WorkerFiles.Read<HeapDumpJob>(Path.Combine(directory, "job.json"));
        string trace = Path.Combine(directory, "capture.nettrace");
        string result = Path.Combine(directory, "heapdump.json");
        var dump = new HeapDump
        {
            Index = job.Index, Reason = job.Reason, RequestedUtc = job.RequestedUtc,
            TimeoutSeconds = job.TimeoutSeconds, BufferMegabytes = job.BufferMegabytes, Status = "running"
        };
        // Three separate reasons to stop, because they are three different outcomes for a reader: the
        // capture's deadline, the reduction's deadline, and a stop the supervisor asked for. The
        // capture's deadline deliberately does not cancel the reduction - they are different phases
        // with budgets of their own - and the supervisor's kill waits behind both of them, so a dump
        // that overruns publishes why it did instead of being killed with nothing recorded.
        using var supervisorGone = new CancellationTokenSource();
        using var stopRequested = new CancellationTokenSource();
        using var captureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(HeapDumpBudget.CaptureSeconds(job.TimeoutSeconds)));
        // The reduction's budget is armed when the reduction starts, so it is what it says it is: a
        // capture that finished early does not silently hand the reduction a budget it never declared.
        using var analysisDeadline = new CancellationTokenSource();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(supervisorGone.Token, stopRequested.Token, captureDeadline.Token);
        using var analysis = CancellationTokenSource.CreateLinkedTokenSource(supervisorGone.Token, stopRequested.Token, analysisDeadline.Token);
        string stage = HeapDumpBudget.CaptureStage;
        try
        {
            WorkerFiles.Write(result, dump);
            using var input = new CancellationTokenSource();
            _ = Task.Run(() => WorkerFiles.ReadCommands(_ => stopRequested.Cancel(), input.Token));
            _ = WatchSupervisor(directory, supervisorGone);
            WorkerFiles.Emit("READY");
            var watch = Stopwatch.StartNew();
            dump.TraceBytes = await Capture(job, trace, dump, capture.Token);
            dump.CapturedUtc = DateTimeOffset.UtcNow;
            dump.CaptureMilliseconds = watch.Elapsed.TotalMilliseconds;
            WorkerFiles.Emit("CAPTURED");
            stage = HeapDumpBudget.AnalysisStage;
            analysisDeadline.CancelAfter(TimeSpan.FromSeconds(HeapDumpBudget.AnalysisSeconds(job.TimeoutSeconds, job.AnalysisSeconds)));
            var reduction = Stopwatch.StartNew();
            try { HeapDumpAnalysis.Analyze(trace, job, dump, analysis.Token); }
            // A reduction that was stopped still costs what it cost, and that is the number a reader
            // compares against the budget when deciding whether to raise it.
            finally { if (dump.ParseMilliseconds <= 0) dump.ParseMilliseconds = reduction.Elapsed.TotalMilliseconds; }
            dump.Status = "completed";
            dump.CompletedUtc = DateTimeOffset.UtcNow;
            WorkerFiles.Write(result, dump);
            WorkerFiles.Emit("DONE");
            return 0;
        }
        catch (OperationCanceledException) when (supervisorGone.IsCancellationRequested)
        {
            return Fail(dump, result, HeapDumpFailure.SupervisorGone,
                "The recording supervisor exited while this dump was running, so the dump stopped with it.");
        }
        catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
        {
            return Fail(dump, result, HeapDumpFailure.Interrupted,
                "The dump was stopped on request before it could be published, so nothing of it is reported as a heap.");
        }
        catch (OperationCanceledException)
        {
            // The deadline is the one case where the measurements taken before it are the evidence:
            // how much trace there was and how many objects had been read is what separates a heap
            // that outgrew the budget from a budget that was too small for the heap.
            return Fail(dump, result, HeapDumpFailure.TimeBudget, HeapDumpBudget.TimeoutMessage(
                stage, job.TimeoutSeconds, job.AnalysisSeconds, dump.TraceBytes, job.BufferMegabytes, dump.WalkedNodeCount));
        }
        catch (Exception ex)
        {
            // The analysis records the kind it stopped for - a filled buffer, the object budget, a
            // trace with no walk in it - before it throws, so it is kept rather than guessed at.
            return Fail(dump, result, dump.FailureKind ?? HeapDumpFailure.NotStarted, ex.Message);
        }
        finally
        {
            // The raw trace is deleted whether or not the reduction succeeded: it is the only
            // artifact of this feature that cannot be redacted.
            Delete(trace);
            Delete(trace + ".etlx");
        }
    }

    // A refused or failed dump is recorded, never omitted: an absent section would be
    // indistinguishable from "was not requested", and the recording continues regardless.
    private static int Fail(HeapDump dump, string result, string kind, string message)
    {
        dump.Status = "failed";
        dump.FailureKind = kind;
        dump.Error = message;
        dump.CompletedUtc = DateTimeOffset.UtcNow;
        // The aggregate on disk carries the same reason the operator reads, exactly as the buffer and
        // object-budget failures already did: report.json has to explain itself without the console.
        if (!dump.Notes.Contains(message)) dump.Notes.Add(message);
        // A failed dump never keeps heap-shaped figures. A reduction interrupted part way through the
        // dominator pass has already counted objects and bytes, and a record carrying a half-computed
        // heap is still a heap a reader could quote. What it keeps is the evidence of how far it got -
        // the trace size, the walked object count and the reason - because that is what says whether
        // the heap or the budget was the limit.
        dump.NodeCount = 0;
        dump.EdgeCount = 0;
        dump.TotalBytes = 0;
        dump.AttributedBytes = 0;
        dump.UnreachableBytes = 0;
        dump.UnattributedBytes = 0;
        dump.Complete = false;
        dump.Holders = [];
        dump.Roots = [];
        dump.Types = [];
        dump.Allocations = [];
        try { WorkerFiles.Write(result, dump); }
        catch (Exception write) { WorkerFiles.Emit("Could not record the dump failure: " + write.Message, error: true); }
        WorkerFiles.Emit("Heap dump failed: " + message, error: true);
        return 1;
    }

    private static async Task<long> Capture(HeapDumpJob job, string tracePath, HeapDump dump, CancellationToken cancel)
    {
        // Allocation figures come from GC allocation ticks, which the GC keyword in the dump mask
        // already enables; the sampled-object-allocation keywords a dump used to add are not
        // requested because .NET 10 emits nothing for them (see HeapDumpAnalysis).
        long keywords = HeapDumpAnalysis.DumpKeywords;
        var providers = new[] { new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, keywords) };
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        startup.CancelAfter(TimeSpan.FromSeconds(10));
        using var session = await new DiagnosticsClient(job.ProcessId)
            .StartEventPipeSessionAsync(providers, requestRundown: true, circularBufferMB: job.BufferMegabytes, token: startup.Token);
        await using var output = new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[1 << 20];
        var wall = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        Task<int>? pending = null;
        Task? stopping = null;
        long bytes = 0;
        var lastData = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                // Exactly one read is ever outstanding: stopping and draining have to overlap, but
                // two concurrent readers on one EventPipe stream are not allowed.
                pending ??= session.EventStream.ReadAsync(buffer, cancel).AsTask();
                if (await Task.WhenAny(pending, Task.Delay(QuietMilliseconds, cancel)) != pending)
                {
                    cancel.ThrowIfCancellationRequested();
                    bool started = bytes > 0 && wall.Elapsed.TotalSeconds >= MinimumCaptureSeconds;
                    if (stopping is null)
                    {
                        // Stop only after a genuinely long silence, or at the deadline: stopping ends
                        // the session, and anything still on its way would be lost.
                        if (started && lastData.Elapsed >= StopAfterQuiet) stopping = session.StopAsync(cancel);
                        else if (wall.Elapsed > TimeSpan.FromSeconds(job.TimeoutSeconds)) stopping = session.StopAsync(cancel);
                        continue;
                    }
                    if (lastData.Elapsed >= FinishAfterQuiet || wall.Elapsed > TimeSpan.FromSeconds(job.TimeoutSeconds + 10)) break;
                    continue;
                }
                int count = await pending;
                pending = null;
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), cancel);
                bytes += count;
                // Recorded as the capture runs, not when it returns: a capture that is stopped by its
                // deadline still has to report how large the trace had grown, because that is what
                // says whether the buffer or the budget was the limit.
                dump.TraceBytes = bytes;
                quiet.Restart();
                lastData.Restart();
                if (wall.Elapsed > TimeSpan.FromSeconds(job.TimeoutSeconds) && stopping is null) stopping = session.StopAsync(cancel);
            }
        }
        finally
        {
            // A read left in flight when the loop breaks is abandoned with the stream; observe its
            // exception so it is never reported as an unhandled task failure.
            if (pending is not null) _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            stopping ??= session.StopAsync(CancellationToken.None);
            try { await stopping.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception) { /* The aggregate is still usable. */ }
            try { await output.FlushAsync(CancellationToken.None); } catch (Exception) { }
            dump.TraceBytes = bytes;
        }
        return bytes;
    }

    private static async Task WatchSupervisor(string directory, CancellationTokenSource cancel)
    {
        try
        {
            string session = Path.GetFullPath(Path.Combine(directory, "..", ".."));
            var info = WorkerFiles.Read<SessionInfo>(Path.Combine(session, "session.json"));
            var identity = new SessionRequest
            {
                ProcessId = info.SupervisorProcessId,
                ProcessStartedUtc = info.SupervisorStartedUtc ?? default
            };
            while (!cancel.IsCancellationRequested)
            {
                if (!WorkerFiles.ParentAlive(identity)) { cancel.Cancel(); return; }
                await Task.Delay(500, cancel.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Losing the supervisor watch must not fail the dump by itself. */ }
    }

    private static void Delete(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) WorkerFiles.Emit($"Could not delete the raw heap trace {Path.GetFileName(path)}: {ex.Message}", error: true);
                else Thread.Sleep(100);
            }
        }
    }
}
