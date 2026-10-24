using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ember.Core;

if (args is ["--target", ..])
{
    // An optional object count gives the parking experiment a deterministic rooted heap instead of
    // whatever the allocator happened to leave behind, so its arms walk comparable graphs.
    if (args.Length > 1 && int.TryParse(args[1], out int objects)) Work.Grow(objects);
    using var cancellation = new CancellationTokenSource();
    _ = Task.Run(() => { while (!cancellation.IsCancellationRequested) Work.Burn(12); });
    // `--target <objects> alloc` keeps handing out managed memory for the whole capture. A target
    // that only burns CPU allocates almost nothing, so it produces no GC allocation ticks and thus no
    // allocation table; the arm that checks that table needs a process that allocates while it is
    // dumped.
    if (args.Contains("alloc"))
        _ = Task.Run(() =>
        {
            var keep = new List<object>();
            var random = new Random(1);
            while (!cancellation.IsCancellationRequested)
            {
                keep.Add(new byte[64 + random.Next(4096)]);
                keep.Add(new object[4]);
                keep.Add("sample-" + random.Next().ToString("x"));
                if (keep.Count > 40000) keep.RemoveRange(0, 20000);
            }
        });
    // `--target <objects> gc <intervalMs> <log>` collects on this process's own timer, so the arm
    // that asks whether a collection alone re-walks the heap has no session attached at all. The log
    // timestamps each collection, so the test counts the ones inside the capture window instead of
    // trusting that the interval held while a forced gen2 was compacting.
    if (args.Length >= 4 && args[2] == "gc" && int.TryParse(args[3], out int gcMs))
    {
        string log = args.Length > 4 ? args[4] : "";
        _ = Task.Run(async () =>
        {
            int collections = 0;
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(gcMs).ConfigureAwait(false);
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                if (log.Length > 0)
                {
                    try { File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {++collections}\n"); }
                    catch (IOException) { }
                }
            }
        });
    }
    Console.WriteLine("TARGET");
    await Console.In.ReadLineAsync();
    cancellation.Cancel();
    return;
}

string root = AppContext.BaseDirectory;
while (!Directory.Exists(Path.Combine(root, "src", "Ember.Trace"))) root = Directory.GetParent(root)?.FullName ?? throw new Exception("Repository root not found.");
string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
string worker = Path.Combine(root, "src", "Ember.Trace", "bin", configuration, "net10.0", "Ember.Trace.dll");
string temporary = Path.Combine(Path.GetTempPath(), "opencode", "ember-session-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
int checks = 0;
try
{
    await AtomicSnapshotConcurrency();
    await HeapDumpMessages();
    if (args is ["--atomic-only"]) { Console.WriteLine("PASS: atomic snapshot concurrency check"); return; }
    if (args is ["--alloc-only"]) { await HeapDumpAllocationSamples(); return; }
    if (args is ["--mods-only"]) { await ProfileModOwnership(); return; }
    if (args is ["--heapdump-only"]) { await HeapDumpMessages(); await HeapDumpCoherentSnapshot(); await HeapDumpAllocationSamples(); await HeapDumpFailureReporting(); await HeapDumpDeadline(); await HeapDumpSoftStop(); await HeapDumpWorkflow(); return; }
    if (args is ["--dumpfail-only"]) { await HeapDumpFailureReporting(); await HeapDumpDeadline(); await HeapDumpSoftStop(); return; }
    if (args is ["--dumppark-only", ..]) { await HeapDumpParking(args.Length > 1 ? args[1] : null); return; }
    await RotationAndStop();
    await CancelDuringStartup();
    await CancelRacingAutomaticStop();
    await ParentDeathAndClosedOutput();
    await RecoveryAndFailure();
    await InterruptedSupervisor();
    await RetainedRawRecovery();
    await ProfileModOwnership();
    await StopDuringAttach();
    await MonitoringOnly();
    await HeapDumpCoherentSnapshot();
    await HeapDumpAllocationSamples();
    await HeapDumpFailureReporting();
    await HeapDumpDeadline();
    await HeapDumpSoftStop();
    await HeapDumpWorkflow();
    await DiskLimit();
    await DurationAndIdentity();
    Console.WriteLine($"PASS: {checks} session integration assertions");
}
finally { try { Directory.Delete(temporary, true); } catch (IOException) { } }

async Task AtomicSnapshotConcurrency()
{
    // Use the real protocol helpers: readers and publishers run concurrently, as they do
    // across the client, supervisor and segment processes on Windows.
    var files = Assembly.LoadFrom(worker).GetType("Ember.Trace.WorkerFiles", throwOnError: true)!;
    var read = files.GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(SessionInfo));
    var write = files.GetMethod("Write", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(SessionInfo));
    string path = Path.Combine(temporary, "atomic-snapshot.json");
    var value = new SessionInfo { Id = "atomic-snapshot", Notes = Enumerable.Repeat(new string('x', 8192), 128).ToList() };
    write.Invoke(null, [path, value, true]);
    using var running = new CancellationTokenSource();
    using var started = new CountdownEvent(3);
    long reads = 0;
    var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
    {
        started.Signal();
        while (!running.IsCancellationRequested)
        {
            var snapshot = (SessionInfo)read.Invoke(null, [path])!;
            if (snapshot.Id != value.Id || snapshot.Notes.Count != 128) throw new InvalidDataException("An atomic snapshot was torn.");
            Interlocked.Increment(ref reads);
        }
    })).ToArray();
    try
    {
        started.Wait();
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0) write.Invoke(null, [path, value, true]);
            else AtomicFile.WriteJson(path, value);
        }
    }
    finally { running.Cancel(); await Task.WhenAll(readers); }
    Check(reads > 0, "concurrent protocol readers permit atomic replacement without sharing violations or torn JSON");
}

async Task RotationAndStop()
{
    using var target = await Target();
    string dir = Fixture("rotation", target);
    using var session = Launch("--session", dir);
    await Until(() => ReadInfo(dir)?.Segments.Count(s => s.Status == "completed") >= 2);
    var completed = ReadInfo(dir)!.Segments.Where(s => s.Status == "completed").ToArray();
    Check(completed.Length >= 2 && completed.All(segment =>
    {
        string work = Path.GetDirectoryName(Path.Combine(dir, segment.FileName!))!;
        return File.Exists(Path.Combine(work, "profile.json")) && File.Exists(Path.Combine(work, "segment.json")) &&
            Directory.EnumerateFileSystemEntries(work).All(path => Path.GetFileName(path) is "profile.json" or "segment.json" or "segment.lock");
    }), "completed rotating segments retain durable profiles/checkpoints but no raw or TraceLog artifacts before stop");
    // The lock prevents a duplicate supervisor from touching an active recording's files.
    using (var duplicate = Launch("--recover", dir)) Check(await duplicate.Exit() == 1, "duplicate supervisor rejected");
    Journal(dir, 2);
    Stop(dir, false, 3);
    Check(await session.Exit() == 0, "rotation exports successfully");
    var result = Read<SessionResult>(Path.Combine(dir, "result.json"));
    var report = Report(result);
    Check(session.Lines.Count(l => l == "READY") == 1, "session emits exactly one READY");
    Check(session.Lines.Any(l => l == "DONE " + result.ExportPath), "session emits DONE with zip path");
    Check(report.Session!.Segments.Count(s => s.Status == "completed") >= 2, "at least two completed real EventPipe segments");
    Check(report.Profile is { Samples.Count: > 0 }, "rotated stacks merged into report");
    Check(report.Session.Segments.Where(s => s.Status == "completed").All(s => s.RetainedSamples <= 30 && s.ObservedSamples >= s.RetainedSamples), "requested per-segment reservoir cap and observed counts honored");
    Check(report.Session.StackCoverageSeconds > 0 && report.Session.StackCoverageSeconds <= report.Session.RecordedSeconds, "truthful bounded interval coverage");
    Check(report.Ticks.Length == 4 && report.ContextHistory.Count >= 3, "journal frames and context included");
    Check(report.Session.DroppedBatches >= 3, "stop drop count included");
    Check(!Directory.Exists(Path.Combine(dir, "segments")) && !File.Exists(Path.Combine(dir, "batches.ndjson")), "successful committed export cleans recovery payload");
    using var idempotent = Launch("--recover", dir);
    Check(await idempotent.Exit() == 0, "already committed recovery is idempotent");
}

async Task CancelDuringStartup()
{
    using var target = await Target();
    string dir = Fixture("cancel", target);
    using var session = Launch("--session", dir);
    await session.Process.StandardInput.WriteLineAsync("cancel");
    await session.Process.StandardInput.FlushAsync();
    // Cancellation may arrive before EventPipe READY; stop.json remains the flush acknowledgment.
    Stop(dir, true);
    Check(await session.Exit() == 0, "cancel during startup finalizes without hanging");
    var result = Read<SessionResult>(Path.Combine(dir, "result.json"));
    Check(result.Status == "cancelled" && result.ExportPath is null && !Directory.Exists(Read<SessionRequest>(Path.Combine(dir, "request.json")).ExportDirectory), "cancel commits cancelled status without creating a ZIP");
    Check(!File.Exists(Path.Combine(dir, "batches.ndjson")) && !Directory.Exists(Path.Combine(dir, "segments")), "cancel cleans intermediate recording data after committing result");
    using var idempotent = Launch("--recover", dir);
    Check(await idempotent.Exit() == 0 && !idempotent.Lines.Any(l => l.StartsWith("DONE ")), "cancelled session recovery remains cancelled without export");
}

async Task ParentDeathAndClosedOutput()
{
    using var target = await Target();
    string dir = Fixture("parent-death", target);
    using var session = Launch("--session", dir, drainOutput: false);
    Check(await session.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) == "READY", "parent-death recording starts");
    session.Process.StandardOutput.Close(); // Simulate the game disappearing with its stdout read pipe.
    await Task.Delay(1200);
    target.Kill(entireProcessTree: true);
    await target.WaitForExitAsync();
    Check(await session.Exit() == 0, "parent death and broken stdout still finalize");
    var report = Report(Read<SessionResult>(Path.Combine(dir, "result.json")));
    Check(report.Session!.StopReason == "game process exited" && report.Ticks.Length == 2, "parent death preserves journal and reason");
}

async Task CancelRacingAutomaticStop()
{
    using var target = await Target();
    string dir = Fixture("cancel-auto-race", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    request.StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-2); request.MaxSeconds = 1;
    Write(Path.Combine(dir, "request.json"), request);
    using var session = Launch("--session", dir);
    await Until(() => File.Exists(Path.Combine(dir, "stop.json")));
    Write(Path.Combine(dir, "stop.json"), new SessionStop("cancelled", DateTimeOffset.UtcNow, true));
    await Task.Delay(300); // Accepted intent precedes the real journal-close acknowledgment.
    Stop(dir, true);
    Check(await session.Exit() == 0, "cancellation racing an autonomous stop completes");
    Check(Read<SessionResult>(Path.Combine(dir, "result.json")) is { Status: "cancelled", ExportPath: null } && !Directory.Exists(request.ExportDirectory), "accepted cancellation remains monotonic through close acknowledgment");
}

async Task RecoveryAndFailure()
{
    using var target = await Target();
    string dir = Fixture("recover", target);
    using (var active = Launch("--recover", dir)) Check(await active.Exit() == 1, "recovery rejects a live unstopped parent");
    SyntheticSegments(dir);
    File.AppendAllText(Path.Combine(dir, "batches.ndjson"), "{incomplete");
    Stop(dir, false);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    File.WriteAllText(request.ExportDirectory, "blocks export directory");
    using (var failed = Launch("--recover", dir)) Check(await failed.Exit() == 1, "export failure reported");
    Check(Read<SessionResult>(Path.Combine(dir, "result.json")).Status == "failed", "failure result committed");
    Check(Directory.Exists(Path.Combine(dir, "segments")) && File.Exists(Path.Combine(dir, "batches.ndjson")), "failed export retains recoverable data");
    File.Delete(request.ExportDirectory);
    using var recovered = Launch("--recover", dir);
    Check(await recovered.Exit() == 0, "failed export can be recovered");
    var report = Report(Read<SessionResult>(Path.Combine(dir, "result.json")));
    Check(report.Session is { Recovered: true, IncompleteJournalLines: 1 }, "truncated journal recovery recorded");
    Check(report.Profile!.Samples.Count == 2 && report.Session!.Segments.Count == 2, "synthetic stopped session merges both retained profiles without attach");
}

async Task InterruptedSupervisor()
{
    using var target = await Target();
    string dir = Fixture("interrupted", target);
    using (var session = Launch("--session", dir))
    {
        await Until(() => session.Lines.Contains("READY"));
        await Task.Delay(700);
        session.Process.Kill(); // Deliberately leave its segment child alive.
        await session.Process.WaitForExitAsync();
    }
    Stop(dir, false);
    using var recovered = Launch("--recover", dir);
    Check(await recovered.Exit() == 0, "orphaned segment and supervisor recovered under lock");
    var report = Report(Read<SessionResult>(Path.Combine(dir, "result.json")));
    Check(report.Session!.Recovered && report.Ticks.Length == 2, "interrupted recording preserves journal");
    Check(report.Session.Segments.Any(s => s.Status == "completed"), "orphaned current segment recovered");
}

async Task HeapDumpCoherentSnapshot()
{
    // The same dump mechanism against the same kind of target, but with no recording attached:
    // this is the configuration that produces a single coherent walk, and the one the report's
    // completeness flag is allowed to be true for.
    using var target = await Target();
    string dir = Fixture("heapdump-solo", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    string work = Path.Combine(dir, "heapdumps", "1");
    Directory.CreateDirectory(work);
    Write(Path.Combine(work, "job.json"), new HeapDumpJob
    {
        Index = 1, ProcessId = request.ProcessId, ProcessStartedUtc = request.ProcessStartedUtc,
        RequestedUtc = DateTimeOffset.UtcNow, TimeoutSeconds = 60, BufferMegabytes = 1024,
        MaxNodes = 40_000_000, SampledAllocations = true
    });
    using var worker = Launch("--heapdump-private", work);
    Check(await worker.Exit() == 0, "an isolated heap dump against a target with no other EventPipe session completes");
    var dump = Read<HeapDump>(Path.Combine(work, "heapdump.json"));
    Check(dump.Complete && dump.TotalBytes > 0 && dump.NodeCount > 0, "the isolated snapshot is coherent");
    Check(dump.AttributedBytes + dump.UnreachableBytes + dump.UnattributedBytes == dump.TotalBytes,
        "the isolated snapshot balances its byte closure");
    Check(dump.UnreachableBytes * 4 < dump.TotalBytes, "an isolated snapshot leaves only a minority of bytes unattributable");
    // The cap travels with the aggregate: without it a reader cannot tell whether a trace that
    // looks large had room to fit, and the saturation note is written against it.
    Check(dump.BufferMegabytes == 1024 && dump.TraceBytes > 0 && !HeapDumpBuffer.Saturated(dump.TraceBytes, dump.BufferMegabytes),
        "the isolated dump records the buffer it had to fit in and reports it as unsaturated");
    Check(!File.Exists(Path.Combine(work, "capture.nettrace")), "the isolated dump deletes its raw trace");
    Check(dump.WalkedNodeCount == dump.NodeCount, "a walk with no other session attached carries each address once");
}

async Task HeapDumpFailureReporting()
{
    // The one real client failure hit both limits at once - a trace that filled its buffer and a walk
    // larger than the analysis budget - while its text named only the budget. A buffer that filled
    // discards the walk's oldest events, so no object budget makes that trace readable; the failure
    // has to name the buffer first and still report the other limit, and the aggregate left on disk
    // has to carry the same reason as the message.
    using var target = await Target(objects: 300_000);
    string dir = Fixture("heapdump-failure", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    string work = Path.Combine(dir, "heapdumps", "1");
    Directory.CreateDirectory(work);
    Write(Path.Combine(work, "job.json"), new HeapDumpJob
    {
        Index = 1, ProcessId = request.ProcessId, ProcessStartedUtc = request.ProcessStartedUtc,
        RequestedUtc = DateTimeOffset.UtcNow, TimeoutSeconds = 10, BufferMegabytes = 8,
        MaxNodes = 1000, SampledAllocations = false
    });
    using var worker = Launch("--heapdump-private", work);
    Check(await worker.Exit() == 1, "a dump whose walk exceeds its budgets fails instead of publishing a partial aggregate");
    var dump = Read<HeapDump>(Path.Combine(work, "heapdump.json"));
    Check(dump.Status == "failed" && dump.NodeCount == 0, "a failed dump publishes no analysis at all");
    Check(HeapDumpBuffer.Saturated(dump.TraceBytes, dump.BufferMegabytes),
        $"the failing dump's trace really filled its buffer: {dump.TraceBytes} bytes of {dump.BufferMegabytes} MiB");
    Check(dump.Error!.Contains("HeapDumpBufferMegabytes above", StringComparison.Ordinal) &&
          dump.Error.Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
        "the failure names the buffer as the fix and still reports the object budget: " + dump.Error);
    Check(dump.Notes.Any(n => n.Contains("EventPipe buffer", StringComparison.Ordinal)),
        "the failed aggregate carries the reason the failure text states");
    Check(!File.Exists(Path.Combine(work, "capture.nettrace")), "a failed dump still deletes its raw trace");
}

async Task HeapDumpDeadline()
{
    // The second real client failure: a dump that overran its budget was killed by the supervisor with
    // nothing recorded at all, so neither the heap nor the budget could be told apart afterwards. The
    // reduction now has a budget it can actually be stopped by, and a dump that runs out of time
    // publishes what it measured before the deadline - which is the evidence that was missing.
    using var target = await Target(objects: 6_000_000);
    string dir = Fixture("heapdump-deadline", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    string work = Path.Combine(dir, "heapdumps", "1");
    Directory.CreateDirectory(work);
    Write(Path.Combine(work, "job.json"), new HeapDumpJob
    {
        Index = 1, ProcessId = request.ProcessId, ProcessStartedUtc = request.ProcessStartedUtc,
        RequestedUtc = DateTimeOffset.UtcNow, TimeoutSeconds = 10, BufferMegabytes = 1024,
        // One second for a walk of this size: the reduction cannot finish, so the deadline is what
        // ends it and the failure has to say so instead of publishing a partial heap. The heap is
        // sized from the measured reduction cost, about 300 ns per walked object.
        MaxNodes = 40_000_000, AnalysisSeconds = 1, SampledAllocations = false
    });
    var started = DateTimeOffset.UtcNow;
    using var worker = Launch("--heapdump-private", work);
    int exit = await worker.Exit(HeapDumpBudget.WorkerSeconds(10, 1) + 60);
    var dump = Read<HeapDump>(Path.Combine(work, "heapdump.json"));
    Console.WriteLine($"deadline: exit {exit}, status {dump.Status}, kind {dump.FailureKind}, walked {dump.WalkedNodeCount:N0}, " +
        $"nodes {dump.NodeCount:N0}, reduction {dump.ParseMilliseconds:F0} ms, trace {dump.TraceBytes / 1048576.0:F1} MiB, " +
        $"elapsed {(DateTimeOffset.UtcNow - started).TotalSeconds:F1} s");
    Check(exit == 1, "a dump that runs out of time fails rather than publishing a partial heap");
    Check(dump.Status == "failed" && dump.NodeCount == 0, "a dump stopped by its deadline publishes no heap at all");
    Check(dump.FailureKind == HeapDumpFailure.TimeBudget,
        "the failed dump records that it ran out of time rather than leaving it to be guessed: " + dump.FailureKind);
    Check(dump.Error!.Contains("reducing the walk to an aggregate", StringComparison.Ordinal),
        "the failure names the stage the deadline hit: " + dump.Error);
    Check(dump.Error.Contains("HeapDumpTimeoutSeconds", StringComparison.Ordinal) &&
          !dump.Error.Contains("HeapDumpBufferMegabytes", StringComparison.Ordinal) &&
          !dump.Error.Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
        "the failure sends the reader to the time budget and not to a limit that cannot help");
    Check(dump.TraceBytes > 0 && dump.WalkedNodeCount > 0,
        $"the failure reports what was measured before the deadline: {dump.TraceBytes} bytes, {dump.WalkedNodeCount:N0} walked objects");
    Check(dump.CompletedUtc is not null, "a failed dump carries when the attempt ended, so it can be placed in the timeline");
    Check(dump.Notes.Any(n => n.Contains("ran out of time", StringComparison.Ordinal)),
        "the failed aggregate carries the same reason the message gives, so report.json explains itself");
    Check(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(HeapDumpBudget.WorkerSeconds(10, 1) + 20),
        "the worker stopped itself at its own deadline instead of waiting for the supervisor's kill");
    Check(!File.Exists(Path.Combine(work, "capture.nettrace")), "a dump stopped by its deadline still deletes its raw trace");
    var (cause, next) = HeapDumpReport.Failure(dump);
    Check(cause.Contains("time budget", StringComparison.Ordinal) && next.Contains("HeapDumpTimeoutSeconds", StringComparison.Ordinal),
        "the exported reading of the failure names the time budget: " + cause);
}

async Task HeapDumpSoftStop()
{
    // The supervisor asks a dump that has outrun its budget to stop before it kills it, and that ask
    // travels on the worker's standard input - the same channel the recording's own children use. The
    // worker has to record why it stopped: the client failure this exists for was killed with nothing
    // recorded at all, which is indistinguishable from a dump that never ran.
    using var target = await Target(objects: 6_000_000);
    string dir = Fixture("heapdump-soft-stop", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    string work = Path.Combine(dir, "heapdumps", "1");
    Directory.CreateDirectory(work);
    Write(Path.Combine(work, "job.json"), new HeapDumpJob
    {
        Index = 1, ProcessId = request.ProcessId, ProcessStartedUtc = request.ProcessStartedUtc,
        RequestedUtc = DateTimeOffset.UtcNow, TimeoutSeconds = 60, BufferMegabytes = 1024,
        MaxNodes = 40_000_000, SampledAllocations = false
    });
    using var worker = Launch("--heapdump-private", work);
    await Until(() => File.Exists(Path.Combine(work, "heapdump.json")));
    worker.Process.StandardInput.WriteLine("stop");
    worker.Process.StandardInput.Flush();
    Check(await worker.Exit(120) == 1, "a dump that is asked to stop stops instead of running to its own deadlines");
    var dump = Read<HeapDump>(Path.Combine(work, "heapdump.json"));
    Check(dump.Status == "failed" && dump.FailureKind == HeapDumpFailure.Interrupted,
        "an interrupted dump records that it was stopped: " + dump.FailureKind);
    Check(dump.NodeCount == 0 && dump.CompletedUtc is not null,
        "an interrupted dump publishes no heap figures but does record when it ended");
    Check(!File.Exists(Path.Combine(work, "capture.nettrace")), "an interrupted dump still deletes its raw trace");
    Check(!Directory.EnumerateFiles(work).Any(f => f.EndsWith(".etlx", StringComparison.Ordinal)),
        "an interrupted dump leaves no conversion artifact behind");
}

async Task HeapDumpWorkflow()
{
    using var target = await Target();
    string dir = Fixture("heapdump", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    // A small buffer keeps the fixture light; the production default is larger because a real
    // client heap emits a much bigger burst and a small buffer drops events silently.
    request.HeapDumpBufferMegabytes = 256;
    Write(Path.Combine(dir, "request.json"), request);
    using var session = Launch("--session", dir);
    await Until(() => session.Lines.Contains("READY"));
    // Waiting a moment before asking matches how a player uses it: it avoids racing the first
    // segment's attach/rundown, and it makes the target an actively recorded one, which is exactly
    // the configuration whose concurrent EventPipe session used to split the walk into phases.
    await Task.Delay(5000);
    Write(Path.Combine(dir, "heapdump.request.json"), new HeapDumpRequest { Reason = "session integration test" });
    await Until(() => ReadStatus(dir)?.State == "running");
    // A second request while the first is in flight is refused rather than queued or merged.
    Write(Path.Combine(dir, "heapdump.request.json"), new HeapDumpRequest { Reason = "too late" });
    await Until(() => ReadInfo(dir)?.HeapDumps.Any(d => d.State == "refused") == true);
    await Until(() => ReadInfo(dir)?.HeapDumps.Any(d => d.Index == 1 && d.State is "completed" or "failed") == true, 180);
    Check(ReadInfo(dir)!.HeapDumps.Single(d => d.Index == 1).State == "completed",
        "the requested heap dump completes next to a live recording: " + ReadInfo(dir)!.HeapDumps.Single(d => d.Index == 1).Message);
    // The pause must be temporary. The coordinator releases the target when the walk has been
    // captured and the supervisor then has to attach a new segment: a pause that never ended would
    // trade a coherent snapshot for a silently truncated recording. The release time is the dump's
    // own status stamp, so any segment started after it is proof that capture resumed.
    DateTimeOffset firstRelease = ReadInfo(dir)!.HeapDumps.Single(d => d.Index == 1).UpdatedUtc;
    await Until(() => ReadInfo(dir)?.Segments.Any(s => s.StartedUtc > firstRelease) == true);
    Check(ReadInfo(dir)!.Segments.Any(s => s.StartedUtc > firstRelease),
        "stack capture attaches again after the dump releases the target");
    // A recording may answer any number of requests, so the second one is requested too. It is what
    // proves the attempt state belongs to one request: shared state that was never reset would let
    // the first dump's parked interval leak into the second.
    Write(Path.Combine(dir, "heapdump.request.json"), new HeapDumpRequest { Reason = "second dump" });
    await Until(() => ReadInfo(dir)?.HeapDumps.Any(d => d.Index == 3 && d.State is "completed" or "failed") == true, 180);
    Check(ReadInfo(dir)!.HeapDumps.Single(d => d.Index == 3).State == "completed",
        "a second dump in the same recording completes: " + ReadInfo(dir)!.HeapDumps.Single(d => d.Index == 3).Message);
    Journal(dir, 2); Stop(dir, false);
    Check(await session.Exit() == 0, "session with heap dumps exports normally");
    var report = Report(Read<SessionResult>(Path.Combine(dir, "result.json")));
    Check(report.HeapDumps.Count == 3, "every heap dump request appears in the report, refusals and repeats included");
    var done = report.HeapDumps.Where(d => d.Status == "completed").ToArray();
    Check(done.Length == 2, "both requested dumps produced an aggregate");
    Check(report.HeapDumps.Any(d => d.Status == "refused" && !string.IsNullOrWhiteSpace(d.Error)), "the refusal carries its reason");
    foreach (HeapDump one in done)
    {
        Check(one.NodeCount > 1000 && one.TotalBytes > 0, $"dump {one.Index} covers a real managed heap");
        Check(one.AttributedBytes + one.UnreachableBytes + one.UnattributedBytes == one.TotalBytes,
            $"dump {one.Index} closes its byte accounting");
        // The recording's own segment workers attach EventPipe sessions to the same target, and the
        // runtime answers every attach with a whole walk of the heap: a rotating capture was measured
        // at 4.5M walked objects carrying 300k distinct addresses. Holding new segments back is what
        // keeps the trace from paying for those copies at all, so the walked count is asserted, not
        // just the share: the analysis collapses repeats, but the trace, the capture and the reduction
        // do not get that time back. The segment that is already capturing is deliberately not
        // stopped - it costs the walk nothing measurable and its stacks are not worth throwing away.
        Check(one.PausedStackCapture && one.PauseMilliseconds > 0, $"dump {one.Index} held new stack capture back");
        Check(one.WalkedNodeCount == one.NodeCount,
            $"dump {one.Index} walked each address once: {one.WalkedNodeCount:N0} objects, {one.NodeCount:N0} described");
        Check(one.Complete && one.UnreachableBytes * 4 < one.TotalBytes,
            $"dump {one.Index} describes one moment: {string.Join(" | ", one.Notes)}");
        Check(one.PausedFromUtc is not null && one.ResumedUtc is not null && one.ResumedUtc > one.PausedFromUtc,
            $"dump {one.Index} carries both ends of its held-back interval");
        // The hold ends at the capture, not at publication: the worker closes its EventPipe session
        // before it reports CAPTURED and the reduction only reads a file, so the target is free while
        // the dump is still reducing. A release stamped at publication would be the older behaviour.
        Check(one.CompletedUtc is not null && one.ResumedUtc < one.CompletedUtc,
            $"dump {one.Index} released the target before it was published: resumed {one.ResumedUtc:O}, published {one.CompletedUtc:O}");
        Check(one.Notes.Any(n => n.Contains("held back") && n.Contains("capture resumed")),
            $"dump {one.Index} states that new capture was held back and resumed at the capture");
        Check(one.TraceBytes > 0, $"dump {one.Index} reports the size of the trace it deleted");
        Check(one.Notes.Any(n => n.Contains("dominator tree")), $"dump {one.Index} explains what retained bytes do and do not mean");
    }
    Check(done[0].PausedFromUtc != done[1].PausedFromUtc,
        "each dump records its own parked interval rather than a shared one");
    Check(report.Session!.Notes.Any(n => n.Contains("held new stack capture back")),
        "the recording reports the hold rather than leaving the fresh attach unexplained");
    // The explanation of what a repeated walk means is the same for every dump in a recording. When
    // it carried this dump's measured seconds it was a different string per request, so it was added
    // once per dump and filled the summary's data-quality list with near-identical copies.
    Check(report.Session.Notes.Count(n => n.StartsWith("The heap walk is emitted by the runtime once per session", StringComparison.Ordinal)) == 1,
        "the mechanism explanation is recorded once for the whole recording");
    Check(report.Session.Notes.Any(n => n.StartsWith("This dump spent", StringComparison.Ordinal)),
        "the measured capture cost of a dump still reaches the recording");
    Check(report.Events.Any(e => e.Kind == "heapdump" && e.Message.Contains("held back"))
        && report.Events.Any(e => e.Kind == "heapdump" && e.Message.Contains("released")),
        "the event timeline marks both ends of the held-back interval");
    Check(done[0].SpanMilliseconds >= 0 && done[0].CaptureMilliseconds > 0 && done[0].ParseMilliseconds > 0,
        "walk span, capture and reduction times are recorded");
    // What the walk cost the target is measured from the recording's own frame journal rather than from
    // the walk's span, and the hold is not that cost: the segment that was already capturing kept
    // recording through it. A dump whose journal kept no frame in the attach window has to say the
    // hitch is unknown instead of reporting a hitch of zero.
    foreach (HeapDump one in done)
    {
        Check(one.Notes.Any(n => n.Contains("stopped the client for", StringComparison.Ordinal) ||
                                 n.Contains("unmeasured in this journal", StringComparison.Ordinal)),
            $"dump {one.Index} either prices the hitch the walk caused or says the journal cannot: {string.Join(" | ", one.Notes)}");
        Check(one.FreezeStartUtc is null || one.FreezeMilliseconds > 0,
            $"dump {one.Index} never reports a measured hitch of zero milliseconds");
    }
    Check(report.HeapDumps.Single(d => d.Status == "refused").FreezeStartUtc is null && report.HeapDumps.Single(d => d.Status == "refused").FreezeMilliseconds == 0,
        "a request that never started a worker has no hitch to report");
    var recorded = report.Session!;
    Check(recorded.UncoveredSeconds >= recorded.UncoveredDuringHeapDumpHoldsSeconds &&
          recorded.UncoveredDuringHeapDumpHoldsSeconds <= recorded.HeapDumpHoldSeconds,
        $"the coverage gap is split between the dump holds and everything else: gap {recorded.UncoveredSeconds:F1}s, " +
        $"inside holds {recorded.UncoveredDuringHeapDumpHoldsSeconds:F1}s, holds {recorded.HeapDumpHoldSeconds:F1}s");
    Check(recorded.Notes.Any(n => n.Contains("Stack coverage gap", StringComparison.Ordinal) && n.Contains("a hold is not by itself a gap", StringComparison.Ordinal)),
        "the recording explains its coverage gap instead of leaving it to attach, rundown and backpressure");
    var growth = done[1].DeltaSincePrevious;
    Check(done[0].DeltaSincePrevious is null && growth is { PreviousIndex: var previous } && previous == done[0].Index,
        "the second dump is compared with the first, and the first has nothing to compare with");
    Check(growth!.SecondsBetween > 0 && growth.TotalBytes == done[1].TotalBytes - done[0].TotalBytes,
        "the comparison carries the interval it is a rate over and subtracts the partition directly");
    string brief = Analysis.Summary(report);
    Check(brief.Contains("| Client freeze |", StringComparison.Ordinal) && brief.Contains("### Change since the previous dump", StringComparison.Ordinal),
        "the summary prices the freeze and the change between dumps without opening report.json");
    Check(!Directory.EnumerateFiles(dir, "capture.nettrace", SearchOption.AllDirectories).Any(),
        "the raw heap trace is deleted once it has been reduced");
    Check(!Directory.Exists(Path.Combine(dir, "heapdumps")), "a committed export removes heap dump work directories");
}

// The line the game shows in chat when a dump settles is a pure function of the aggregate, so the
// wording for a trace that repeats the walk can be checked without provoking one on a live target.
async Task HeapDumpMessages()
{
    var coordinator = Assembly.LoadFrom(worker).GetType("Ember.Trace.HeapDumpCoordinator", throwOnError: true)!;
    var method = coordinator.GetMethod("CompletionMessage", BindingFlags.Public | BindingFlags.Static)!;
    string Message(HeapDump dump) => (string)method.Invoke(null, [dump])!;
    string one = Message(new HeapDump
    {
        Index = 1, Status = "completed", Complete = true, NodeCount = 1_000, WalkedNodeCount = 1_000,
        TotalBytes = 21L * 1048576, SpanMilliseconds = 17
    });
    Check(one.Contains("21 MiB of managed heap in 1,000 objects") && !one.Contains("whole walks") && !one.Contains("held back"),
        "a single walk is reported as the heap it measured, without a repetition note or a hold it did not have");
    string repeated = Message(new HeapDump
    {
        Index = 2, Status = "completed", Complete = true, NodeCount = 1_000, WalkedNodeCount = 15_000,
        TotalBytes = 21L * 1048576, SpanMilliseconds = 17, PausedStackCapture = true, PauseMilliseconds = 63_400
    });
    Check(repeated.Contains("21 MiB of managed heap in 1,000 objects") && repeated.Contains("about 15 whole walks") && !repeated.Contains("lower bound")
        && repeated.Contains("holding new stack capture back for 63.4 s"),
        "a repeated walk is reported as the heap it describes once, with the repetition named and the hold it cost: " + repeated);
    await Task.CompletedTask;
}

// The parking assumption is that something attaching to the target while the walk is emitted makes
// the runtime re-walk it, and a merged walk mixes objects from one moment with roots from another.
// These arms separate what the first measurement varied together: one deterministic heap, one
// oversized buffer, one variable each. "steady" showed that a session which stays attached for the
// whole walk is harmless (0.09% unattributable against 0.04% with nothing attached), so the cost is
// in attaching rather than in running concurrently. "gc" then removed the sessions entirely and
// forced 90 blocking gen2 collections inside the capture window: one walk, 19 ms span, 0.06%
// unattributable, so a collection does not re-walk the heap either. What is left is that the
// runtime emits a fresh complete walk for every session that attaches, broadcast to every session
// that asked for one - which is why "rotating" and "leash" carry the same unchanged heap about
// fifteen times over. The analysis collapses those copies, so these arms measure both things at
// once: what the repeated walks cost the trace and the capture, and that collapsing them leaves a
// coherent heap. The two coordinated arms are the production path - hold new segments back until
// the walk has been captured, and leave the segment that is already attached alone - with "parked"
// keeping its segment for the whole capture and "live" letting its segment stop itself inside it.
async Task HeapDumpParking(string? only = null)
{
    const int objects = 300_000, buffer = 1024, timeout = 90;
    Console.WriteLine($"parking experiment: {objects:N0} rooted objects, {buffer} MiB buffer, {timeout} s dump budget");
    Console.WriteLine($"{"arm",-9} {"trace",6} {"of cap",7} {"span",10} {"capture",8} {"walked",10} {"nodes",10} {"heap",9} {"unreach",8} {"complete",8}  segments");
    var arms = new (string Name, Func<Task<(string, HeapDump)>> Run)[]
    {
        ("solo", () => ParkingArm("solo", objects, buffer, timeout, segmentSeconds: 0, coordinated: false)),
        ("steady", () => ParkingArm("steady", objects, buffer, timeout, segmentSeconds: 120, coordinated: false)),
        ("rotating", () => ParkingArm("rotating", objects, buffer, timeout, segmentSeconds: 2, coordinated: false)),
        // Five-second segments with the dump asked for just after one started: the first rotation
        // lands seconds into the dump, so this asks whether protecting only the walk's first burst
        // is enough. It is not: churn that began after the dump was already running stretched the
        // span to 75 s and left 93.3% of the heap without a root before the copies were collapsed.
        // What the coordinator holds back is therefore every new attach until the walk is captured.
        ("leash", () => ParkingArm("leash", objects, buffer, timeout, segmentSeconds: 5, coordinated: false)),
        // The target forces its own gen2 collections on a timer with no session attached at all.
        // The rotating arm varied two things at once - sessions attaching and detaching, and whatever
        // collections that churn caused in the target - so it could not say which of them re-walked
        // the heap. This arm varies only the collections.
        ("gc", () => ParkingArm("gc", objects, buffer, timeout, segmentSeconds: 0, coordinated: false, gcMs: 1_000)),
        ("parked", () => ParkingArm("parked", objects, buffer, timeout, segmentSeconds: 120, coordinated: true)),
        // The production path with a segment short enough to stop itself while the walk is still being
        // captured. The supervisor holds new segments back but no longer stops the segment that is
        // already attached, so this arm asks whether a detach - as opposed to an attach - re-walks the
        // heap, and whether the segment it left alone really ran out its own interval.
        ("live", () => ParkingArm("live", objects, buffer, timeout, segmentSeconds: 4, coordinated: true))
    };
    // A single arm can be selected while iterating on the analysis, so a change can be re-measured
    // against the arm it is about instead of against the whole suite.
    var measured = new List<(string Arm, HeapDump Dump)>();
    foreach (var (name, run) in arms)
        if (only is null || only == name) measured.Add(await run());

    string[] repeatedArms = ["rotating", "leash"];
    foreach (var (arm, dump) in measured)
    {
        // The buffer must not be the explanation for any arm, so occupancy is asserted, not assumed.
        Check(dump.Status == "completed", $"{arm}: the arm produced an aggregate");
        Check(!HeapDumpBuffer.Saturated(dump.TraceBytes, buffer), $"{arm}: a {dump.TraceBytes / 1048576.0:F1} MiB trace in {buffer} MiB cannot have overwritten the walk");
        Check(dump.NodeCount > 1000, $"{arm}: the walk covered a real heap");
        // Which arms carry repeated walks is the measurement, not a given: the two that churn
        // sessions must show it and the other four must not.
        Check(repeatedArms.Contains(arm) == dump.WalkedNodeCount > dump.NodeCount,
            $"{arm}: the walked count says whether the walk repeated: {dump.WalkedNodeCount:N0} walked, {dump.NodeCount:N0} described");
        double share = 100.0 * dump.UnreachableBytes / Math.Max(1, dump.TotalBytes);
        Check(dump.Complete && share <= 5,
            $"{arm}: the heap is coherent: {share:F2}% unattributable, complete {dump.Complete}");
        // Both coordinated arms are the production path. The target is released as soon as the walk
        // has been captured, which is before the reduction finishes and therefore before the aggregate
        // is published; a release stamped at publication is the older behaviour and holds the
        // recording's stacks for the reduction as well.
        if (arm is "parked" or "live")
        {
            Check(dump.PausedStackCapture && dump.PauseMilliseconds > 0, $"{arm}: the hold on new stack capture was recorded");
            Check(dump.ResumedUtc is not null && dump.CompletedUtc is not null && dump.ResumedUtc < dump.CompletedUtc,
                $"{arm}: the target was released at the capture, not at publication");
        }
        if (arm == "live")
        {
            // The segment that was capturing when the request arrived has to have run out its own
            // interval rather than been stopped for the dump: that interval is the stack coverage the
            // hold deliberately does not take. Its segments are four seconds long, so anything near
            // four seconds means it was left alone, and anything near zero means it was still stopped.
            var evidence = Read<SessionInfo>(Path.Combine(root, "test-runs", "parking", arm, "session.json"));
            var capturing = evidence.Segments.OrderBy(s => s.StartedUtc).First();
            double ran = capturing.EndedUtc is { } ended ? (ended - capturing.StartedUtc).TotalSeconds : 0;
            Check(ran >= 3, $"{arm}: the segment that was already capturing was left to finish its interval ({ran:F1} s of 4)");
        }
        if (!repeatedArms.Contains(arm)) continue;
        // Collapsing is the difference between "the heap moved under the walk" and "we were handed the
        // same heap fifteen times": the heap it describes has to be the heap the parked arm describes.
        Check(dump.Notes.Any(n => n.Contains("were collapsed")), $"{arm}: the notes say the copies were collapsed");
        Check(dump.NodeCount is > 290_000 and < 320_000, $"{arm}: the collapsed heap is the one walk's worth of objects");
    }
    Console.WriteLine();
    foreach (var (arm, dump) in measured)
    {
        double share = 100.0 * dump.UnreachableBytes / Math.Max(1, dump.TotalBytes);
        Console.WriteLine($"VERDICT {arm,-9} span {dump.SpanMilliseconds,8:F0} ms  unreachable {share,5:F2}%  complete {dump.Complete}  stopped {dump.Status}  nodes {dump.NodeCount:N0} walked {dump.WalkedNodeCount:N0}");
        if (share > 25) Console.WriteLine($"        {dump.Notes.FirstOrDefault(n => n.Contains("no matching strong root"))}");
    }
}

async Task<(string Arm, HeapDump Dump)> ParkingArm(string arm, int objects, int buffer, int timeout, int segmentSeconds, bool coordinated, int gcMs = 0)
{
    try
    {
        string gcLog = Path.Combine(temporary, "gc-" + arm + ".log");
        using var target = await Target(objects: objects, gcMs: gcMs, gcLog: gcMs > 0 ? gcLog : null);
        string dir = Fixture("park-" + arm, target);
        var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
        request.SegmentSeconds = Math.Max(1, segmentSeconds);
        request.MaxSeconds = 300;
        request.MaxTraceMegabytes = 32;
        request.HeapDumpTimeoutSeconds = timeout;
        request.HeapDumpBufferMegabytes = buffer;
        Write(Path.Combine(dir, "request.json"), request);
        Running? session = null;
        try
        {
            if (segmentSeconds > 0)
            {
                session = Launch("--session", dir);
                await Until(() => session.Lines.Contains("READY"));
                await Until(() => ReadInfo(dir)?.Segments.Any(s => s.Status == "capturing") == true);
            }
            HeapDump dump;
            string aggregate;
            if (coordinated)
            {
                Write(Path.Combine(dir, "heapdump.request.json"), new HeapDumpRequest { Reason = "parking experiment " + arm });
                await Until(() => ReadStatus(dir)?.State == "running", 120);
                await Until(() => ReadInfo(dir)?.HeapDumps.Any(d => d.Index == 1 && d.State is "completed" or "failed") == true, 240);
                aggregate = Path.Combine(dir, "heapdumps", "1", "heapdump.json");
                dump = Read<HeapDump>(aggregate);
            }
            else
            {
                // Driven directly, so the supervisor keeps rotating its own segments throughout the
                // walk: that is the configuration whose unattributable share the docs record as 91-96%.
                string work = Path.Combine(dir, "manual");
                Directory.CreateDirectory(work);
                Write(Path.Combine(work, "job.json"), new HeapDumpJob
                {
                    Index = 1, ProcessId = request.ProcessId, ProcessStartedUtc = request.ProcessStartedUtc,
                    RequestedUtc = DateTimeOffset.UtcNow, TimeoutSeconds = timeout, BufferMegabytes = buffer,
                    MaxNodes = 40_000_000, SampledAllocations = true
                });
                using var worker = Launch("--heapdump-private", work);
                int exit = await worker.Exit(timeout + 60);
                aggregate = Path.Combine(work, "heapdump.json");
                if (!File.Exists(aggregate)) throw new Exception($"the dump worker left no aggregate (exit {exit})");
                dump = Read<HeapDump>(aggregate);
            }
            DateTimeOffset end = dump.CapturedUtc ?? DateTimeOffset.UtcNow;
            DateTimeOffset begin = end.AddMilliseconds(-dump.CaptureMilliseconds);
            var info = ReadInfo(dir);
            int churn = info?.Segments.Count(s => s.StartedUtc >= begin && s.StartedUtc <= end) ?? 0;
            int collections = 0;
            if (gcMs > 0 && File.Exists(gcLog))
                foreach (string line in File.ReadAllLines(gcLog))
                    if (DateTimeOffset.TryParse(line.Split(' ')[0], out var at) && at >= begin && at <= end) collections++;
            string collectionsNote = gcMs > 0 ? $"  {collections} target gc in window" : "";
            Console.WriteLine($"{arm,-9} {dump.TraceBytes / 1048576.0,6:F1} {100.0 * dump.TraceBytes / (buffer * 1048576.0),6:F1}% {dump.SpanMilliseconds,10:F0} {dump.CaptureMilliseconds / 1000,7:F1}s {dump.WalkedNodeCount,10:N0} {dump.NodeCount,10:N0} {dump.TotalBytes / 1048576.0,8:F1}M {100.0 * dump.UnreachableBytes / Math.Max(1, dump.TotalBytes),7:F2}% {dump.Complete,8}  {churn} started / {info?.Segments.Count ?? 0} total{collectionsNote}");
            // The scratch directory is removed with the fixture, so the evidence a reader has to be
            // able to re-check afterwards is copied out next to the run log.
            string evidenceDir = Path.Combine(root, "test-runs", "parking", arm);
            Directory.CreateDirectory(evidenceDir);
            File.Copy(aggregate, Path.Combine(evidenceDir, "heapdump.json"), true);
            if (gcMs > 0 && File.Exists(gcLog)) File.Copy(gcLog, Path.Combine(evidenceDir, "gc.log"), true);
            if (File.Exists(Path.Combine(dir, "session.json"))) File.Copy(Path.Combine(dir, "session.json"), Path.Combine(evidenceDir, "session.json"), true);
            return (arm, dump);
        }
        finally
        {
            if (session is not null)
            {
                Journal(dir, 2);
                Stop(dir, false);
                await session.Exit();
                session.Dispose();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{arm,-9} FAILED: {ex.Message}");
        return (arm, new HeapDump { Status = "failed", Error = ex.Message });
    }
}

async Task HeapDumpAllocationSamples()
{
    // The allocation table is the one part of a dump that describes work rather than retained bytes.
    // It used to be read from GCSampledObjectAllocation events, which .NET 10 never emits under any
    // keyword mask, and nothing asserted the table was non-empty - so every dump ever taken reported
    // zero allocation samples and the note explaining the empty table read as a quiet environment
    // rather than as a broken reader. This arm dumps a target that keeps allocating and requires the
    // table to be there, and requires the switch to remove it when it is off.
    using var target = await Target(objects: 20_000, alloc: true);
    string dir = Fixture("heapdump-allocations", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    string work = Path.Combine(dir, "heapdumps", "1");
    Directory.CreateDirectory(work);
    Write(Path.Combine(work, "job.json"), new HeapDumpJob
    {
        Index = 1, ProcessId = request.ProcessId, ProcessStartedUtc = request.ProcessStartedUtc,
        RequestedUtc = DateTimeOffset.UtcNow, TimeoutSeconds = 60, BufferMegabytes = 1024,
        MaxNodes = 40_000_000, SampledAllocations = true
    });
    using (var worker = Launch("--heapdump-private", work))
        Check(await worker.Exit(180) == 0, "a dump of an allocating target completes");
    var dump = Read<HeapDump>(Path.Combine(work, "heapdump.json"));
    Console.WriteLine($"allocations: {dump.SampledAllocationEvents:N0} samples, {dump.SampledAllocationBytes:N0} bytes, " +
        $"{dump.Allocations.Count} owner rows, trace {dump.TraceBytes / 1048576.0:F1} MiB");
    Check(dump.SampledAllocationEvents > 0 && dump.SampledAllocationBytes > 0,
        "a dump of an allocating target reports the allocations it sampled instead of an empty table");
    Check(dump.Allocations.Count > 0 && dump.Allocations.Sum(a => a.SampledBytes) == dump.SampledAllocationBytes &&
          dump.Allocations.Sum(a => a.Events) == dump.SampledAllocationEvents,
        "the allocation table sums to the reported samples and bytes");
    Check(dump.Notes.Any(n => n.Contains("GC allocation ticks", StringComparison.Ordinal)),
        "the dump says where its allocation figures come from");

    // The same target with the table switched off: the trace still carries the ticks, and the point is
    // that the dump reports none of them and says it was disabled rather than reporting an empty
    // observation as if the target had been idle.
    string off = Path.Combine(dir, "heapdumps", "2");
    Directory.CreateDirectory(off);
    Write(Path.Combine(off, "job.json"), new HeapDumpJob
    {
        Index = 2, ProcessId = request.ProcessId, ProcessStartedUtc = request.ProcessStartedUtc,
        RequestedUtc = DateTimeOffset.UtcNow, TimeoutSeconds = 60, BufferMegabytes = 1024,
        MaxNodes = 40_000_000, SampledAllocations = false
    });
    using (var worker = Launch("--heapdump-private", off))
        Check(await worker.Exit(180) == 0, "a dump with the allocation table disabled completes");
    var disabled = Read<HeapDump>(Path.Combine(off, "heapdump.json"));
    Check(disabled.SampledAllocationEvents == 0 && disabled.Allocations.Count == 0 &&
          disabled.Notes.Any(n => n.Contains("disabled", StringComparison.Ordinal)),
        "a dump with the table switched off reports no samples and says it was disabled");
}

async Task DiskLimit()
{
    using var target = await Target();
    string dir = Fixture("disk", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    request.MaxDiskMegabytes = 1;
    Write(Path.Combine(dir, "request.json"), request);
    using (var file = File.Create(Path.Combine(dir, "budget-padding.bin"))) file.SetLength(1024 * 1024);
    using var session = Launch("--session", dir);
    Check(await session.Exit() == 0, "disk cap automatically exports");
    var report = Report(Read<SessionResult>(Path.Combine(dir, "result.json")));
    Check(report.Session!.StopReason == "session disk budget reached" && report.Session.Segments.Count == 0, "persistent files count toward disk cap before attaching");
}

async Task ProfileModOwnership()
{
    using var target = await Target(objects: 2000);
    string dir = Fixture("mod-ownership", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    request.SegmentSamples = 5000;
    // The client sends this map from its own mod registry, and the target process really runs from
    // this assembly, so the whole chain is exercised end to end: sampled address -> module ->
    // assembly -> mod id -> tally.
    request.ModMap = new() { ["Ember.Trace.Session.Tests"] = ["fixture-mod"] };
    Write(Path.Combine(dir, "request.json"), request);
    // Blocking the parse output keeps a genuinely failed segment's raw trace, which is the input the
    // recovery needs; the fixture then reparses that trace privately.
    File.WriteAllText(request.ExportDirectory, "blocks export directory");
    using (var session = Launch("--session", dir))
    {
        await Until(() => session.Lines.Contains("READY"));
        string blocked = Path.Combine(dir, "segments", "000001", "profile.json");
        Directory.CreateDirectory(blocked);
        await Until(() => ReadInfo(dir)?.Segments.Any(s => s.Status == "failed") == true);
        Stop(dir, false);
        Check(await session.Exit() == 1, "mod-ownership fixture retains its raw trace by failing the parse");
        Directory.Delete(blocked);
    }
    target.Kill(true); await target.WaitForExitAsync();
    string raw = Path.Combine(dir, "segments", "000001");
    Check(File.Exists(Path.Combine(raw, "capture.nettrace")), "mod-ownership fixture kept the raw trace");

    var named = await ParseOwnership("mod-ownership-named", raw, blind: false);
    Check(named.Frames.Any(f => f.Mods.Contains("fixture-mod")), "a sampled frame is attributed to the mod that declares its assembly");
    Check(named.Frames.Where(f => f.Mods.Length > 0).All(f => f.Module == "Ember.Trace.Session.Tests"),
        "a mod id is attached only to frames of the assembly the map named");
    Check(named.ModSamples.Any(m => m.Mod == "fixture-mod" && m.SelfSamples > 0), "the per-mod tally credits the mod with executing samples");
    Check(named.ModSamples.All(m => m.InclusiveSamples >= m.SelfSamples), "inclusive observations are never smaller than self observations");
    Check(named.Notes.Any(n => n.StartsWith("Frame ownership:", StringComparison.Ordinal)), "the profile states how ownership was learned");

    // Blinded, the same raw trace reproduces the client session that lost every mod id (0 of 1404
    // frames). The recovery has to name the modules again from the raw file's method-load events.
    var blinded = await ParseOwnership("mod-ownership-blinded", raw, blind: true);
    Check(blinded.FramesRecoveredFromRawTrace > 0, "modules the converted trace lost are recovered from the raw trace");
    Check(blinded.Frames.Any(f => f.Mods.Contains("fixture-mod")), "recovered modules become the same mod ids");
    Check(blinded.FramesWithoutModule < blinded.Frames.Count, "recovery identifies the managed frames instead of leaving them unowned");
    Check(blinded.FramesWithoutModule <= named.FramesWithoutModule, "recovery leaves no more unnamed frames than the converted trace did");
    Check(blinded.ModSamples.Any(m => m.Mod == "fixture-mod" && m.SelfSamples > 0), "the blinded profile still credits executing samples to the mod");
    Check(blinded.Notes.Any(n => n.Contains("recovered", StringComparison.Ordinal)), "the profile says where the module names came from");

    async Task<StackProfile> ParseOwnership(string name, string rawDirectory, bool blind)
    {
        string work = Path.Combine(temporary, name);
        Directory.CreateDirectory(work);
        File.Copy(Path.Combine(rawDirectory, "capture.nettrace"), Path.Combine(work, "capture.nettrace"));
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(rawDirectory, "capture.json")))!;
        metadata["ignoreConvertedModuleNames"] = blind;
        metadata["maxSamples"] = 200000;
        File.WriteAllText(Path.Combine(work, "capture.json"), metadata.ToJsonString(Json.Options));
        using var parser = Launch("--parse-private", work);
        if (await parser.Exit() != 0) throw new Exception("Mod-ownership fixture failed to parse: " + name);
        return Read<StackProfile>(Path.Combine(work, "profile.json"));
    }
}

async Task RetainedRawRecovery()
{
    using var target = await Target();
    string dir = Fixture("raw-recovery", target);
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    File.WriteAllText(request.ExportDirectory, "blocks export directory");
    using (var session = Launch("--session", dir))
    {
        await Until(() => session.Lines.Contains("READY"));
        // Make the parser's output path unwritable so this fixture retains a genuinely failed
        // segment's raw trace, rather than relying on raw data from completed segments.
        string blockedProfile = Path.Combine(dir, "segments", "000001", "profile.json");
        Directory.CreateDirectory(blockedProfile);
        await Until(() => ReadInfo(dir)?.Segments.Any(s => s.Status == "failed") == true);
        Stop(dir, false);
        Check(await session.Exit() == 1, "raw-recovery fixture fails export after capture");
        Directory.Delete(blockedProfile);
    }
    int traces = Directory.GetFiles(Path.Combine(dir, "segments"), "capture.nettrace", SearchOption.AllDirectories).Length;
    Check(traces >= 1, "failed parsing retains raw traces for recovery");
    target.Kill(true); await target.WaitForExitAsync();
    string rawDirectory = Path.Combine(dir, "segments", "000001");
    var limited = await ParserCoverageRegression(rawDirectory);
    string checkpoint = Path.Combine(rawDirectory, "capture.json");
    var metadata = JsonNode.Parse(File.ReadAllText(checkpoint))!;
    metadata["maxConversionEvents"] = 5000;
    File.WriteAllText(checkpoint, metadata.ToJsonString(Json.Options));
    File.Delete(request.ExportDirectory);
    using var recovery = Launch("--recover", dir);
    Check(await recovery.Exit() == 0, "retained raw checkpoints reparse without a live target");
    var recoveredReport = Report(Read<SessionResult>(Path.Combine(dir, "result.json")));
    Check(recoveredReport.Profile is { Samples.Count: > 0 }, "raw checkpoint recovery restores stacks");
    var recoveredSegment = recoveredReport.Session!.Segments.Single(s => s.Index == 1);
    Check(Math.Abs((recoveredSegment.EndedUtc - limited.StartedUtc).TotalSeconds - limited.UsableDurationSeconds!.Value) < .0001,
        "recovered completed segment endpoint excludes the unconverted tail");
}

async Task<StackProfile> ParserCoverageRegression(string rawDirectory)
{
    var allLimited = await Parse("limited-all", 200000, 5000, false);
    var oneLimited = await Parse("limited-one", 1, 5000, false);
    Check(allLimited.Samples.Count > 10 && allLimited.Samples.Count == allLimited.ObservedStackSamples &&
        allLimited.UsableDurationSeconds is > 0 && allLimited.UsableDurationSeconds < allLimited.DurationSeconds,
        "forced conversion cap produces a resolved prefix shorter than nominal capture");
    double lastResolved = allLimited.Samples.Max(s => s.RelativeMs) / 1000;
    Check(Math.Abs(allLimited.UsableDurationSeconds!.Value - lastResolved) < 1e-9 &&
        Math.Abs(oneLimited.UsableDurationSeconds!.Value - lastResolved) < 1e-9 &&
        oneLimited.Samples.Single().RelativeMs / 1000 < lastResolved,
        "usable prefix follows every resolved event before reservoir decisions, not the last retained sample");
    Check(allLimited.DurationSeconds == oneLimited.DurationSeconds && allLimited.Notes.Any(n => n.Contains("remaining tail is unobserved")),
        "conversion-limited profiles retain nominal coordinates and explain unobserved coverage");
    var truncated = await Parse("truncated", 200000, 2000000, true);
    Check(truncated.UsableDurationSeconds is > 0 && truncated.UsableDurationSeconds < truncated.DurationSeconds - 5 &&
        Math.Abs(truncated.UsableDurationSeconds.Value - truncated.Samples.Max(s => s.RelativeMs) / 1000) < 1e-9,
        "raw truncation credits only the resolved prefix rather than the missing nominal tail");
    var empty = await Parse("truncated-no-stacks", 200000, 2000000, true, empty: true);
    Check(empty.ObservedStackSamples == 0 && empty.UsableDurationSeconds == 0,
        "truncated trace with no resolved target events has zero usable coverage");
    var complete = await Parse("complete", 200000, 2000000, false);
    Check(complete.UsableDurationSeconds is null, "complete conversion preserves normal nominal coverage");
    return allLimited;

    async Task<StackProfile> Parse(string name, int samples, int events, bool truncated, bool empty = false)
    {
        string work = Path.Combine(temporary, "parser-coverage-" + name);
        Directory.CreateDirectory(work);
        File.Copy(Path.Combine(rawDirectory, "capture.nettrace"), Path.Combine(work, "capture.nettrace"));
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(rawDirectory, "capture.json")))!;
        metadata["maxSamples"] = samples;
        metadata["maxConversionEvents"] = events;
        metadata["truncated"] = truncated;
        if (truncated) metadata["endedUtc"] = metadata["endedUtc"]!.GetValue<DateTimeOffset>().AddSeconds(10);
        if (empty) metadata["pid"] = int.MaxValue;
        File.WriteAllText(Path.Combine(work, "capture.json"), metadata.ToJsonString(Json.Options));
        using var parser = Launch("--parse-private", work);
        if (await parser.Exit() != 0) throw new Exception("Parser coverage fixture failed: " + name);
        return Read<StackProfile>(Path.Combine(work, "profile.json"));
    }
}

async Task StopDuringAttach()
{
    using var target = await Target(diagnostics: false);
    string dir = Fixture("stop-attach", target);
    using var session = Launch("--session", dir);
    await Until(() => Directory.Exists(Path.Combine(dir, "segments")) && Directory.GetFiles(Path.Combine(dir, "segments"), "capture.json", SearchOption.AllDirectories).Length > 0);
    var watch = Stopwatch.StartNew();
    await session.Process.StandardInput.WriteLineAsync("cancel"); await session.Process.StandardInput.FlushAsync();
    Stop(dir, true);
    Check(await session.Exit() == 0 && watch.Elapsed < TimeSpan.FromSeconds(15), "cancellation forwarded while EventPipe attach is unavailable");
    Check(Read<SessionResult>(Path.Combine(dir, "result.json")) is { Status: "cancelled", ExportPath: null }, "startup attach cancellation does not export");
}

async Task MonitoringOnly()
{
    using var target = await Target(diagnostics: false);
    string dir = Fixture("monitoring-only", target);
    using var session = Launch("--session", dir);
    await Until(() => ReadInfo(dir)?.Notes.Any(n => n.Contains("retries exhausted")) == true);
    Journal(dir, 2); Stop(dir, false);
    Check(await session.Exit() == 0, "unavailable EventPipe degrades to usable monitoring-only recording");
    var report = Report(Read<SessionResult>(Path.Combine(dir, "result.json")));
    Check(report.Session!.Segments.Count == 3 && report.Session.StackCoverageSeconds == 0, "attach retries bounded and failed intervals have zero coverage");
    Check(report.Ticks.Length == 4 && session.Lines.Count(l => l == "READY") == 1, "monitoring-only journal retained with one READY notification");
}

async Task DurationAndIdentity()
{
    using var target = await Target();
    foreach (bool wrongIdentity in new[] { false, true })
    {
        string dir = Fixture(wrongIdentity ? "identity" : "duration", target);
        var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
        if (wrongIdentity) request.ProcessStartedUtc = request.ProcessStartedUtc.AddHours(-1);
        else { request.MaxSeconds = 1; request.StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-2); }
        Write(Path.Combine(dir, "request.json"), request);
        using var session = Launch("--session", dir);
        if (!wrongIdentity)
        {
            await Until(() => ReadInfo(dir)?.Status == "finalizing");
            Journal(dir, 2);
            Stop(dir, false);
        }
        Check(await session.Exit() == 0, "identity/duration auto-finalization succeeds");
        var info = ReadInfo(dir)!;
        Check(info.StopReason == (wrongIdentity ? "game process exited" : "maximum recording duration reached"), "PID start time and maximum duration enforced");
        Check(info.Segments.Count == 0, "no attach after automatic stop");
        if (!wrongIdentity) Check(Report(Read<SessionResult>(Path.Combine(dir, "result.json"))).Ticks.Length == 4, "automatic stop waits for final journal flush acknowledgment");
    }
}

string Fixture(string name, Process target)
{
    string dir = Path.Combine(temporary, name);
    Directory.CreateDirectory(dir);
    var request = new SessionRequest
    {
        ProcessId = target.Id, ProcessStartedUtc = new DateTimeOffset(target.StartTime.ToUniversalTime()),
        ExportDirectory = Path.Combine(temporary, name + "-exports"),
        SegmentSeconds = 2, MaxSeconds = 60, MaxTraceMegabytes = 8, MaxDiskMegabytes = 256,
        SegmentSamples = 30, OpenFolderWhenFinished = false
    };
    Write(Path.Combine(dir, "request.json"), request);
    Write(Path.Combine(dir, "seed.json"), new DiagnosticReport { Id = request.Id, SlowTickThresholdMs = 50 });
    Journal(dir, 1);
    return dir;
}

void Journal(string dir, long sequence)
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var batch = new SessionBatch(sequence, null, [new(now, 10, 12, 16), new(now + 16, 26, 60, 75)],
        [new(now, "test", "journal event")], [new(now, "info", "journal log")], null,
        new(now, new() { ["setting"] = sequence }, new() { ["world"] = "synthetic" }));
    var compact = new JsonSerializerOptions(Json.Options) { WriteIndented = false };
    using var stream = new FileStream(Path.Combine(dir, "batches.ndjson"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    using var writer = new StreamWriter(stream);
    writer.WriteLine(JsonSerializer.Serialize(batch, compact)); writer.Flush(); stream.Flush(true);
}

void SyntheticSegments(string dir)
{
    var request = Read<SessionRequest>(Path.Combine(dir, "request.json"));
    request.StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-5);
    Write(Path.Combine(dir, "request.json"), request);
    var info = new SessionInfo { Id = request.Id, StartedUtc = request.StartedUtc };
    for (int i = 1; i <= 2; i++)
    {
        string work = Path.Combine(dir, "segments", i.ToString("D6")); Directory.CreateDirectory(work);
        var profile = new StackProfile
        {
            StartedUtc = request.StartedUtc.AddSeconds(i), DurationSeconds = 1, ObservedStackSamples = 1,
            Frames = [new("Synthetic.Frame", "test", [])], Samples = [new(100, 1, true, [0])], Hotspots = [new(0, 1, 1, 1)]
        };
        var segment = new ProfileSegment { Index = i, Status = "completed", StartedUtc = profile.StartedUtc,
            EndedUtc = profile.StartedUtc.AddSeconds(1), FileName = $"segments/{i:D6}/profile.json", ObservedSamples = 1, RetainedSamples = 1 };
        Write(Path.Combine(work, "profile.json"), profile); Write(Path.Combine(work, "segment.json"), segment);
        info.Segments.Add(segment);
    }
    Write(Path.Combine(dir, "session.json"), info);
}

void Stop(string dir, bool cancelled, long dropped = 0)
{
    Write(Path.Combine(dir, "journal.closed.json"), new { closedUtc = DateTimeOffset.UtcNow, droppedBatches = dropped, bytesWritten = new FileInfo(Path.Combine(dir, "batches.ndjson")).Length, error = (string?)null });
    string stopPath = Path.Combine(dir, "stop.json");
    var existing = File.Exists(stopPath) ? Read<SessionStop>(stopPath) : null;
    Write(stopPath, (existing ?? new SessionStop(cancelled ? "cancelled" : "test stop", DateTimeOffset.UtcNow, cancelled)) with { DroppedBatches = dropped, Cancelled = cancelled || existing?.Cancelled == true });
}
HeapDumpStatus? ReadStatus(string dir) => File.Exists(Path.Combine(dir, "heapdump.status.json")) ? Read<HeapDumpStatus>(Path.Combine(dir, "heapdump.status.json")) : null;
SessionInfo? ReadInfo(string dir) => File.Exists(Path.Combine(dir, "session.json")) ? Read<SessionInfo>(Path.Combine(dir, "session.json")) : null;
static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options)!;
static void Write<T>(string path, T value) { File.WriteAllText(path + ".tmp", Json.Write(value)); File.Move(path + ".tmp", path, true); }
static DiagnosticReport Report(SessionResult result)
{
    using var zip = ZipFile.OpenRead(result.ExportPath!);
    using var stream = zip.GetEntry("report.json")!.Open();
    return JsonSerializer.Deserialize<DiagnosticReport>(stream, Json.Options)!;
}
void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
static async Task Until(Func<bool> condition, double seconds = 60)
{
    var watch = Stopwatch.StartNew();
    while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Condition not reached."); await Task.Delay(100); }
}
static async Task<Process> Target(bool diagnostics = true, int objects = 0, int gcMs = 0, string? gcLog = null, bool alloc = false)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    if (!diagnostics) start.Environment["DOTNET_EnableDiagnostics"] = "0";
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--target");
    if (objects > 0) start.ArgumentList.Add(objects.ToString());
    if (gcMs > 0) { start.ArgumentList.Add("gc"); start.ArgumentList.Add(gcMs.ToString()); start.ArgumentList.Add(gcLog ?? ""); }
    if (alloc) start.ArgumentList.Add("alloc");
    var process = Process.Start(start)!;
    if (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "TARGET") throw new Exception("Target startup failed.");
    return process;
}
Running Launch(string mode, string dir, bool drainOutput = true) => new(worker, mode, dir, drainOutput);

sealed class Running : IDisposable
{
    public Process Process { get; }
    public System.Collections.Concurrent.ConcurrentBag<string> Lines { get; } = [];
    private readonly Task output;
    private readonly Task<string> errors;
    public Running(string worker, string mode, string dir, bool drain)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { worker, mode, dir }) start.ArgumentList.Add(arg);
        Process = Process.Start(start)!;
        errors = Process.StandardError.ReadToEndAsync();
        output = drain ? Drain() : Task.CompletedTask;
    }
    private async Task Drain() { while (await Process.StandardOutput.ReadLineAsync() is { } line) Lines.Add(line); }
    public async Task<int> Exit(double seconds = 100)
    {
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds)); await output;
        string error = await errors;
        if (Process.ExitCode != 0) Console.WriteLine("Worker diagnostic: " + error.Trim());
        return Process.ExitCode;
    }
    public void Dispose() { if (!Process.HasExited) Process.Kill(true); Process.Dispose(); }
}

static class Work
{
    public static object? Held;

    // A deterministic rooted binary tree, so two arms of the parking experiment walk the same number
    // of nodes and edges rather than two different heaps.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Grow(int objects)
    {
        var nodes = new Node[objects];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = new Node();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (2 * i + 1 < nodes.Length) nodes[i].Left = nodes[2 * i + 1];
            if (2 * i + 2 < nodes.Length) nodes[i].Right = nodes[2 * i + 2];
        }
        Held = nodes;
    }

    public sealed class Node
    {
        public Node? Left, Right;
        // Payload, so a node is big enough to make the walk interesting without a compiler warning.
        public long A = 1, B = 2, C = 3, D = 4;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static long Burn(int depth)
    {
        if (depth > 0) return Burn(depth - 1) + depth;
        long value = 0; for (int i = 0; i < 100000; i++) value += i;
        Thread.Sleep(1); return value;
    }
}
