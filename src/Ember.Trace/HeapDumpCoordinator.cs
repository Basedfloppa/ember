using System.Collections.Concurrent;
using System.Diagnostics;
using Ember.Core;

namespace Ember.Trace;

/// <summary>
/// Owns heap-dump requests for one recording. A request is a file the game writes; the supervisor
/// consumes it, holds new stack capture back so the walk describes a single moment, runs one isolated
/// worker, publishes an acknowledgement and records the outcome.
/// Nothing here starts a dump on its own: without an explicit request this class does nothing.
/// </summary>
internal sealed class HeapDumpCoordinator
{
    // A dump writes a trace as large as the heap itself. It is transient, so it is not charged to
    // the recording's disk budget, but the volume still has to hold it while it exists.
    private const long HeadroomBytes = 4L << 30;
    private const long RunawayWorkingSetBytes = 12L << 30;

    private readonly string directory;
    private readonly string requestPath;
    private readonly string statusPath;
    private readonly string dumpsRoot;
    private readonly SessionRequest request;
    private readonly SessionInfo info;
    private readonly Action<string> note;
    private readonly Action snapshot;

    private Process? process;
    private int index;
    private string work = "";
    private DateTimeOffset startedAt;
    private DateTimeOffset? askedToStopAt;
    private string? abortKind;
    private string stderr = "";
    private readonly ConcurrentQueue<string> messages = new();

    // A dump describes one moment only while no session attaches while its walk is captured, because
    // the runtime answers every attach with a complete walk broadcast to every session that asked
    // for one. Measured on one deterministic heap, with a buffer far larger than the trace so the
    // ring could not be the cause: one segment that stayed attached for the whole walk left 0.09% of
    // the heap unreachable against 0.04% with nothing attached, while two-second segments rotating
    // through it produced a 33 s span and 4,553,580 walked objects carrying only 303,949 distinct
    // addresses - fifteen complete copies of a heap that never moved - of which 93.3% had no strong
    // root in the trace. Five-second segments whose first rotation landed after the dump had already
    // started stretched that span to 75 s and left the same 93.3%. Forcing 90 blocking gen2
    // collections inside the capture window changed nothing (19 ms span, 0.06% unreachable), so
    // collections are not the trigger.
    // What the walk needs is therefore not a quiet target but an unchanging set of sessions. The
    // supervisor holds *new* segments back from the moment a request is consumed until the walk has
    // been captured, and leaves a segment that is already attached alone: stopping it measured no
    // benefit to the walk and cost the recording every stack it would have kept, which on a live
    // client was 63 s of a 77 s hold. The first client that ran this design had 34% of its stacks
    // uncovered that way, against four freezes of 2.6-3.4 s that the walk itself caused.
    // The hold ends at CAPTURED, not at exit: the worker closes its session before it reports the
    // capture, so the trace is already closed to further attaches while the reduction - a file read -
    // still has to finish. Holding the target through the reduction only shortened the recording.
    private Phase phase;
    private HeapDumpJob? waiting;
    private DateTimeOffset parkedAt;
    private DateTimeOffset? releasedAt;
    private bool liveCapture;
    private bool captured;

    private enum Phase { Idle, Pausing, Dumping }

    public HeapDumpCoordinator(string directory, SessionRequest request, SessionInfo info, Action<string> note, Action snapshot)
    {
        this.directory = directory;
        this.request = request;
        this.info = info;
        this.note = note;
        this.snapshot = snapshot;
        requestPath = Path.Combine(directory, "heapdump.request.json");
        statusPath = Path.Combine(directory, "heapdump.status.json");
        dumpsRoot = Path.Combine(directory, "heapdumps");
    }

    /// <summary>Next dump number, so a report shows which request a dump answers.</summary>
    public int NextIndex => info.HeapDumps.Count + 1;

    /// <summary>
    /// False while a dump needs the target to itself: from the request until its walk has been
    /// captured, the supervisor must not attach a new stack segment, because the runtime answers
    /// every attach with a whole walk of the heap broadcast into the dump's session. A segment that
    /// is already attached may keep capturing - it measured 0.09% unattributable against 0.04% with
    /// nothing attached - so stopping it would spend coverage the walk does not need.
    /// </summary>
    public bool SegmentsAllowed => phase == Phase.Idle || captured;

    /// <summary>
    /// Answer at most one request per call. <paramref name="segmentsBusy"/> is read by the caller
    /// before <paramref name="holdSegments"/> is invoked here, so a live capture is observed once.
    /// </summary>
    public void Poll(DateTimeOffset now, int failures, bool stopping, bool segmentsBusy, Action holdSegments)
    {
        if (phase == Phase.Dumping) Watch(now);
        if (File.Exists(requestPath)) Consume(now, failures, stopping, segmentsBusy, holdSegments);
        // The worker starts on the next poll rather than after the live segment detaches: a segment
        // that is merely attached costs the walk nothing measurable, and waiting for it would hold the
        // request for the rest of its interval.
        if (phase == Phase.Pausing) Launch(now);
    }

    /// <summary>Stop a running dump because the recording is ending. Its outcome is still recorded.</summary>
    public async Task StopAsync(string reason)
    {
        // A request that was still waiting for the pause never produced a worker, so it would
        // otherwise leave no trace in the report at all.
        if (phase == Phase.Pausing)
            EndAttempt(index, "failed", $"The recording ended ({reason}) before the heap walk could start, so no heap dump was taken.", DateTimeOffset.UtcNow, HeapDumpFailure.Interrupted);
        if (process is null) return;
        // A dump that is nearly finished is allowed to publish its aggregate; one that is not is asked
        // to stop first, so even a dump the recording ends on records why it stopped instead of being
        // killed with nothing. Either way the outcome is recorded before the report is assembled.
        AskToStop();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        }
        Watch(DateTimeOffset.UtcNow, force: true, ending: $"the recording ended ({reason}) while this dump was running");
    }

    private void Consume(DateTimeOffset now, int failures, bool stopping, bool segmentsBusy, Action holdSegments)
    {
        // Move, then read: the request is consumed exactly once even if the game writes another one
        // while this one is being picked up.
        int next = NextIndex;
        string target = Path.Combine(dumpsRoot, next.ToString());
        try
        {
            Directory.CreateDirectory(target);
            File.Move(requestPath, Path.Combine(target, "request.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(requestPath); } catch (IOException) { }
            WorkerFiles.Emit("A heap dump request could not be consumed: " + ex.Message, error: true);
            return;
        }
        HeapDumpRequest? wanted = null;
        try { wanted = WorkerFiles.Read<HeapDumpRequest>(Path.Combine(target, "request.json")); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        { Settle(next, "refused", "The heap dump request could not be read: " + ex.Message, now); return; }

        // A dump is refused rather than queued in every case below, and the refusal is recorded:
        // a request that was answered with nothing would look like a request never made.
        if (stopping)
        {
            Settle(next, "failed", "The recording is ending, so this heap dump was not taken.", now, HeapDumpFailure.Interrupted);
            return;
        }
        if (phase != Phase.Idle)
        {
            Settle(next, "refused", $"Heap dump {index} is still in progress; wait for it to finish before asking for another.", now);
            return;
        }
        if (failures >= 3)
        {
            Settle(next, "refused", "Stack capture is monitoring-only after repeated attach failures, so this recording does not request heap dumps.", now);
            return;
        }
        try
        {
            long free = new DriveInfo(Path.GetPathRoot(directory) ?? directory).AvailableFreeSpace;
            if (free < HeadroomBytes)
            {
                Settle(next, "refused", $"A heap dump needs about {Bytes(HeadroomBytes)} of free space while it is being reduced, and only {Bytes(free)} is available.", now);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { /* An unknown free-space answer must not silently allow a disk-filling capture. */ }

        var job = new HeapDumpJob
        {
            Index = next, ProcessId = request.ProcessId, ProcessStartedUtc = request.ProcessStartedUtc,
            RequestedUtc = wanted?.RequestedUtc ?? now, Reason = wanted?.Reason ?? "",
            TimeoutSeconds = request.HeapDumpTimeoutSeconds, BufferMegabytes = request.HeapDumpBufferMegabytes,
            MaxNodes = request.HeapDumpMaxNodes, SampledAllocations = request.HeapDumpSampledAllocations,
            ModMap = request.ModMap
        };
        WorkerFiles.Write(Path.Combine(target, "job.json"), job);
        index = next;
        work = target;
        stderr = "";
        waiting = job;
        parkedAt = now;
        liveCapture = segmentsBusy;
        captured = false;
        releasedAt = null;
        phase = Phase.Pausing;
        holdSegments();
        Publish(index, "pausing", liveCapture
            ? $"Heap dump {index}: holding new stack capture back so the heap walk describes a single moment. The segment that is already capturing keeps recording; the walk itself stops the client for a moment, while the runtime performs it during a blocking collection."
            : $"Heap dump {index}: holding new stack capture back so the heap walk describes a single moment. No segment is capturing, so no stacks are interrupted; the walk itself still stops the client for a moment, while the runtime performs it during a blocking collection.", now);
        snapshot();
    }

    /// <summary>Start the isolated worker, which holds new stack capture back until its walk is captured.</summary>
    private void Launch(DateTimeOffset now)
    {
        var job = waiting;
        if (job is null) { phase = Phase.Idle; return; }
        try
        {
            process = Process.Start(WorkerFiles.StartInfo("--heapdump-private", work))
                ?? throw new IOException("Could not launch the heap dump worker.");
        }
        catch (Exception ex)
        {
            EndAttempt(index, "failed", "The heap dump worker could not be started: " + ex.Message, now, HeapDumpFailure.NotStarted);
            return;
        }
        _ = Read(process.StandardOutput, line => messages.Enqueue(line));
        _ = Read(process.StandardError, line => { if (stderr.Length < 8192) stderr += line + "\n"; });
        // The dump's own budget starts here, not when it was requested: the request may have waited a
        // poll to be consumed, and charging that to the walk would cut it short.
        startedAt = now;
        askedToStopAt = null;
        abortKind = null;
        phase = Phase.Dumping;
        Publish(index, "running", $"Heap dump {index}: the managed heap is being walked, which can hitch the game for a moment.", now);
        snapshot();
    }

    private void Watch(DateTimeOffset now, bool force = false, string? ending = null)
    {
        if (process is null) return;
        while (messages.TryDequeue(out string? line))
            if (line == "CAPTURED")
            {
                // The worker closes its EventPipe session before it reports this, so no later attach
                // can add another walk to a trace that is already closed, and the reduction only reads
                // a file: the target is free from here. Releasing at the capture rather than at exit is
                // what keeps a dump's cost to the walk instead of the walk plus the reduction.
                captured = true;
                releasedAt ??= now;
                Publish(index, "running", $"Heap dump {index}: the heap walk is captured, so stack capture resumes while the reduction runs.", now);
            }
        if (!process.HasExited)
        {
            process.Refresh();
            double elapsed = (now - startedAt).TotalSeconds;
            // The worker stops itself at its own deadline and publishes why; this threshold sits a
            // publication grace behind that, so it is a backstop for a worker that cannot stop itself
            // rather than the dump's deadline. On the first real client failure the kill came first
            // and the dump produced no record at all, which is the outcome this ordering prevents.
            (string Message, string Kind)? failure = elapsed > HeapDumpBudget.KillSeconds(request.HeapDumpTimeoutSeconds)
                ? ("the heap dump exceeded its time budget", HeapDumpFailure.TimeBudget)
                : process.WorkingSet64 > RunawayWorkingSetBytes
                    ? ("the heap dump worker exceeded 12 GiB of memory", HeapDumpFailure.NotStarted)
                    : null;
            if (failure is null) return;
            // Recorded as the attempt's cause the moment the decision is made: a worker that is killed
            // anyway never writes a record of its own, and the kind has to survive that path as well as
            // the one where it publishes its own reason.
            abortKind = failure.Value.Kind;
            if (askedToStopAt is null)
            {
                askedToStopAt = now;
                AskToStop();
                note($"Heap dump {index} was asked to stop: {failure.Value.Message}. It records the outcome itself if it can.");
                return;
            }
            if ((now - askedToStopAt.Value).TotalSeconds < HeapDumpBudget.PublishGraceSeconds) return;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            note($"Heap dump {index} was aborted: {failure.Value.Message}, and it published nothing within {HeapDumpBudget.PublishGraceSeconds} s of being asked to stop.");
        }
        if (!force && !process.HasExited) return;

        var saved = ReadResult();
        string state, message;
        if (saved is { Status: "completed" })
        {
            Stamp(saved, now);
            state = "completed";
            message = CompletionMessage(saved);
            foreach (string line in saved.Notes) note(line);
            foreach (string line in saved.Notes) if (!info.Notes.Contains(line)) info.Notes.Add(line);
        }
        else
        {
            state = "failed";
            message = $"Heap dump {index} failed: " + (saved?.Error ??
                (ending is not null ? ending + "." : string.IsNullOrWhiteSpace(stderr) ? "the worker exited without an aggregate." : stderr.Trim()));
        }
        EndAttempt(index, state, message, now,
            saved?.FailureKind ?? abortKind ?? (ending is not null ? HeapDumpFailure.Interrupted : null));
        Dispose();
    }

    // The worker reads "stop" from its standard input, the same protocol the recording's own children
    // use, so a dump that is over budget is asked to finish rather than killed outright.
    private void AskToStop()
    {
        try
        {
            if (process is null) return;
            process.StandardInput.WriteLine("stop");
            process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or NotSupportedException)
        { /* An unreachable stop channel falls through to the kill below. */ }
    }

    /// <summary>
    /// Record the interval for which the dump held new stack capture back. It ends when the walk has
    /// been captured rather than when the dump is published, because that is when the target became
    /// free again, and a reader comparing stacks either side of a dump has to be able to see it.
    /// </summary>
    private void Stamp(HeapDump dump, DateTimeOffset now)
    {
        // The hold is recorded whether or not a segment happened to be capturing when the request
        // arrived: with a recording that attaches whenever it can, a later attach would have started
        // within a poll, so a request that landed in the gap still cost the recording that interval.
        // What differs is only what the interval held: a capture that kept running, or nothing.
        dump.PausedStackCapture = true;
        dump.PausedFromUtc = parkedAt;
        // The worker stamps the moment its capture returned, which is when its session was closed and
        // the target became free; the coordinator only notices within a poll, and stamping that
        // observation instead would lag the true release by up to one poll interval and report a hold
        // that outlives the walk by that much.
        dump.ResumedUtc = dump.CapturedUtc ?? releasedAt ?? now;
        dump.PauseMilliseconds = (dump.ResumedUtc.Value - parkedAt).TotalMilliseconds;
        string line = liveCapture
            ? $"New stack capture was held back for {dump.PauseMilliseconds / 1000:F1} s so this walk describes a single moment: no session attached while the walk was in flight, and capture resumed as soon as it had been captured. The segment that was already capturing kept journaling, so the recording has no hole in it; the walk itself stopped the client once, while the runtime performed it during a blocking collection, and the exported report measures that stop from the recorded frame intervals."
            : $"New stack capture was held back for {dump.PauseMilliseconds / 1000:F1} s so this walk describes a single moment: no session attached while the walk was in flight, and capture resumed as soon as it had been captured. No segment was running when the request arrived, so that interval has no stack coverage at all; the walk itself stopped the client once, while the runtime performed it during a blocking collection.";
        if (!dump.Notes.Contains(line)) dump.Notes.Add(line);
        try { WorkerFiles.Write(Path.Combine(work, "heapdump.json"), dump); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { WorkerFiles.Emit("Could not record the heap dump pause: " + ex.Message, error: true); }
    }

    private HeapDump? ReadResult()
    {
        try { return WorkerFiles.Read<HeapDump>(Path.Combine(work, "heapdump.json")); }
        catch (Exception) { return null; }
    }

    // The line the game shows in chat when a dump settles. It is a pure function of the aggregate, so
    // the wording for a repeated trace can be checked without provoking one on a live target.
    public static string CompletionMessage(HeapDump saved)
    {
        // A trace that repeated the walk is collapsed onto its first copy before it is measured, so
        // the totals are the heap once and they are not a lower bound. The repetition is still worth
        // saying: it is why the dump took as long as it did and its trace was as large as it was.
        string repeated = saved.NodeCount > 0 && saved.WalkedNodeCount > saved.NodeCount
            ? $" The trace carried about {saved.WalkedNodeCount / saved.NodeCount} whole walks of it, whose copies were collapsed."
            : "";
        string held = saved.PausedStackCapture ? $", after holding new stack capture back for {saved.PauseMilliseconds / 1000:F1} s" : "";
        return saved.Complete
            ? $"Heap dump {saved.Index} finished: {Bytes(saved.TotalBytes)} of managed heap in {saved.NodeCount:N0} objects over {saved.SpanMilliseconds:F0} ms{held}.{repeated} Top holder: {TopHolder(saved)}."
            : $"Heap dump {saved.Index} finished but the snapshot is incomplete: {IncompleteReason(saved)}, so its {Bytes(saved.TotalBytes)} total is a lower bound. Top holder: {TopHolder(saved)}.";
    }

    // "Incomplete" has different causes and they must not be reported as one: EventPipe can drop
    // events, the capture can fill its buffer, and a walk whose objects and roots do not belong to
    // the same moment leaves a large share of the heap without a strong root. The classification
    // lives in Core so the chat line and the exported summary cannot name different causes.
    private static string IncompleteReason(HeapDump dump) =>
        HeapDumpReport.Incompleteness(dump) ?? "the byte closure does not balance";

    private static string TopHolder(HeapDump dump) => dump.Holders.Count == 0
        ? "not attributable"
        : $"{dump.Holders[0].Module} at {Bytes(dump.Holders[0].Bytes)} ({dump.Holders[0].Percent:F1}% of the heap)";

    // A refused or failed request is written into its own work directory as well as acknowledged,
    // so the report lists it: an omitted request would be indistinguishable from one never made.
    // This deliberately does not touch the attempt state: refusing a second request while a dump is
    // running must not release the target that dump is holding.
    private void Settle(int at, string state, string message, DateTimeOffset now, string? kind = null)
    {
        if (state is "refused" or "failed")
        {
            try
            {
                string path = Path.Combine(dumpsRoot, at.ToString(), "heapdump.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                HeapDump record;
                try { record = File.Exists(path) ? WorkerFiles.Read<HeapDump>(path) : new HeapDump { Index = at, RequestedUtc = now }; }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { record = new HeapDump { Index = at, RequestedUtc = now }; }
                if (record.Status is "requested" or "running")
                {
                    record.Status = state;
                    record.Error ??= message;
                    // A refusal never started a worker, so its kind is structural; a failure that did
                    // start one keeps the kind the analysis recorded before it threw, and only falls
                    // back to the caller's when the worker never got to write anything at all.
                    record.FailureKind ??= state == "refused" ? HeapDumpFailure.Refused : kind ?? HeapDumpFailure.NotStarted;
                    record.CompletedUtc ??= now;
                    WorkerFiles.Write(path, record);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { WorkerFiles.Emit("Could not record a refused heap dump: " + ex.Message, error: true); }
        }
        Publish(at, state, message, now);
    }

    /// <summary>End the attempt that owns the target, so the next segment may attach again.</summary>
    private void EndAttempt(int at, string state, string message, DateTimeOffset now, string? kind = null)
    {
        Settle(at, state, message, now, kind);
        phase = Phase.Idle;
        waiting = null;
        liveCapture = false;
        captured = false;
        releasedAt = null;
        askedToStopAt = null;
        abortKind = null;
    }

    private void Publish(int at, string state, string message, DateTimeOffset now)
    {
        info.HeapDumps.RemoveAll(d => d.Index == at);
        info.HeapDumps.Add(new HeapDumpStatus(at, state, message, now));
        try { WorkerFiles.Write(statusPath, new HeapDumpStatus(at, state, message, now)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { WorkerFiles.Emit("Could not publish heap dump status: " + ex.Message, error: true); }
    }

    private void Dispose()
    {
        process?.Dispose();
        process = null;
        // The worker deletes its trace itself; this covers a worker that was killed before it could.
        try { File.Delete(Path.Combine(work, "capture.nettrace")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task Read(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) onLine(line);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static string Bytes(long value) => value >= 1L << 30 ? $"{(double)value / (1L << 30):F2} GiB" : $"{value / 1048576.0:F0} MiB";
}
