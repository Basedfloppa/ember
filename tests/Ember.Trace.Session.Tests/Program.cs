using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ember.Core;

if (args is ["--target"])
{
    using var cancellation = new CancellationTokenSource();
    _ = Task.Run(() => { while (!cancellation.IsCancellationRequested) Work.Burn(12); });
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
    if (args is ["--atomic-only"]) { Console.WriteLine("PASS: atomic snapshot concurrency check"); return; }
    await RotationAndStop();
    await CancelDuringStartup();
    await CancelRacingAutomaticStop();
    await ParentDeathAndClosedOutput();
    await RecoveryAndFailure();
    await InterruptedSupervisor();
    await RetainedRawRecovery();
    await StopDuringAttach();
    await MonitoringOnly();
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
static async Task Until(Func<bool> condition)
{
    var watch = Stopwatch.StartNew();
    while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Condition not reached."); await Task.Delay(100); }
}
static async Task<Process> Target(bool diagnostics = true)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    if (!diagnostics) start.Environment["DOTNET_EnableDiagnostics"] = "0";
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--target");
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
    public async Task<int> Exit()
    {
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100)); await output;
        string error = await errors;
        if (Process.ExitCode != 0) Console.WriteLine("Worker diagnostic: " + error.Trim());
        return Process.ExitCode;
    }
    public void Dispose() { if (!Process.HasExited) Process.Kill(true); Process.Dispose(); }
}

static class Work
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static long Burn(int depth)
    {
        if (depth > 0) return Burn(depth - 1) + depth;
        long value = 0; for (int i = 0; i < 100000; i++) value += i;
        Thread.Sleep(1); return value;
    }
}
