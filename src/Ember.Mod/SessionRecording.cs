using System.Diagnostics;
using Ember.Core;

namespace Ember.Mod;

/// <summary>Client-side journal owner. The standalone supervisor owns capture/finalization and outlives this world.</summary>
internal sealed class SessionRecording
{
    private readonly Collector collector;
    private readonly SessionJournal journal;
    private readonly Action<string> notify;
    private readonly DiagnosticReport seed;
    private readonly Task enrichment;
    private readonly Process worker;
    private readonly object stopGate = new();
    private Task? stopping;
    private volatile string state = "preparing";
    private int stopped, finished;
    public string DirectoryPath { get; }
    public SessionRequest Request { get; }
    public Task<SessionResult> Completion { get; }
    public bool IsActive => Volatile.Read(ref finished) == 0;
    public bool IsRecording => IsActive && Volatile.Read(ref stopped) == 0;
    public string State => state;
    public TimeSpan Elapsed => DateTimeOffset.UtcNow - Request.StartedUtc;

    public SessionRecording(string tracer, string sessionsDirectory, string exportDirectory, Collector collector,
        DiagnosticReport initial, Dictionary<string, string> paths, EmberConfig config, string configDirectory,
        Action<string> notify, int durationSeconds = 0)
    {
        this.collector = collector;
        this.notify = notify;
        seed = initial;
        RemoveRollingData(seed);
        Request = new SessionRequest
        {
            Id = seed.Id, ProcessId = Environment.ProcessId,
            ProcessStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            StartedUtc = DateTimeOffset.UtcNow, ExportDirectory = exportDirectory,
            SegmentSeconds = config.SessionSegmentSeconds,
            MaxSeconds = durationSeconds > 0 ? durationSeconds : config.SessionMaxSeconds,
            MaxDiskMegabytes = config.SessionDiskBudgetMegabytes,
            MaxTraceMegabytes = Math.Min(config.MaxTraceMegabytes, 128),
            SegmentSamples = config.SessionSegmentSamples, KeepExports = config.KeepExports,
            OpenFolderWhenFinished = config.OpenFolderWhenFinished,
            ModMap = seed.Mods.SelectMany(m => m.Assemblies.Select(a => (Assembly: a, m.Id)))
                .GroupBy(x => x.Assembly, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct().ToArray())
        };
        DirectoryPath = Path.Combine(sessionsDirectory, Request.Id);
        Directory.CreateDirectory(DirectoryPath);
        AtomicFile.WriteJson(Path.Combine(DirectoryPath, "request.json"), Request);
        AtomicFile.WriteJson(Path.Combine(DirectoryPath, "seed.json"), seed);
        journal = new SessionJournal(DirectoryPath, Math.Max(16L, Request.MaxDiskMegabytes / 4L) * 1024 * 1024);
        try
        {
            collector.BeginRecording(journal.Enqueue);
            worker = Launch(tracer, "--session", DirectoryPath);
        }
        catch
        {
            collector.EndRecording();
            _ = journal.CompleteAsync();
            AtomicFile.WriteJson(Path.Combine(DirectoryPath, "stop.json"), new SessionStop("Worker could not start", DateTimeOffset.UtcNow));
            throw;
        }
        // Hashing/config IO never runs on the render thread and never blocks EventPipe attachment.
        enrichment = Task.Run(() =>
        {
            try
            {
                ClientSnapshot.EnrichFiles(seed, paths, config, configDirectory);
                AtomicFile.WriteJson(Path.Combine(DirectoryPath, "seed.json"), seed);
            }
            catch (Exception e) { collector.Mark("inventory-error", e.GetType().Name + ": " + e.Message); }
        });
        Completion = Monitor();
    }

    public bool Stop(string reason, bool cancel = false, DiagnosticReport? final = null)
    {
        lock (stopGate)
        {
            if (!IsActive || Volatile.Read(ref stopped) != 0) return false;
            var stop = new SessionStop(reason, DateTimeOffset.UtcNow, cancel);
            if (cancel)
            {
                // Persist cancellation before accepting it, including if the game exits during drain.
                // The supervisor waits for journal.closed.json (or parent exit) after seeing this intent.
                try { AtomicFile.WriteJson(Path.Combine(DirectoryPath, "stop.json"), stop); }
                catch (Exception e) { notify("Could not save cancellation intent: " + Redactor.Text(e.Message)); return false; }
            }
            Interlocked.Exchange(ref stopped, 1);
            state = cancel ? "cancelling" : "finalizing";
            collector.Mark("session-stop", reason);
            collector.EndRecording(); // Detaches synchronously; no batch can race channel completion.
            stopping = FinishJournal(stop, final);
            return true;
        }
    }

    private async Task FinishJournal(SessionStop stop, DiagnosticReport? final)
    {
        try
        {
            // Never abandon the close acknowledgement on a timeout: the writer may still be active.
            // This runs asynchronously; supervisor shutdown waits have their own bounded timeout.
            await journal.CompleteAsync();
            try
            {
                if (final is not null)
                {
                    RemoveRollingData(final);
                    try { await enrichment.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
                    if (enrichment.IsCompletedSuccessfully)
                    {
                        var hashes = seed.Mods.Where(m => m.Sha256 is not null).GroupBy(m => (m.Id, m.Version)).ToDictionary(g => g.Key, g => g.First().Sha256);
                        final.Mods = final.Mods.Select(m => hashes.TryGetValue((m.Id, m.Version), out string? sha) ? m with { Sha256 = sha } : m).ToList();
                        final.ModConfigs = seed.ModConfigs;
                    }
                    final.Id = Request.Id;
                    AtomicFile.WriteJson(Path.Combine(DirectoryPath, "final.json"), final);
                }
            }
            catch (Exception e) { stop = stop with { Reason = stop.Reason + "; last saved context used: " + Redactor.Text(e.Message) }; }
            // An autonomous limit/parent-exit stop may already have been committed by the worker.
            string stopPath = Path.Combine(DirectoryPath, "stop.json");
            var existing = AtomicFile.ReadJson<SessionStop>(stopPath);
            AtomicFile.WriteJson(stopPath, (existing ?? stop) with { Cancelled = stop.Cancelled || existing?.Cancelled == true, DroppedBatches = journal.DroppedBatches });
            // Publish the acknowledgement last: it also commits the final cancellation/drop state.
            AtomicFile.WriteJson(Path.Combine(DirectoryPath, "journal.closed.json"), new
            {
                closedUtc = DateTimeOffset.UtcNow, journal.DroppedBatches, journal.BytesWritten, journal.Error
            });
        }
        catch (Exception e)
        {
            try
            {
                AtomicFile.WriteJson(Path.Combine(DirectoryPath, "stop.json"), stop with
                {
                    Reason = stop.Reason + "; journal finalization: " + Redactor.Text(e.Message), DroppedBatches = journal.DroppedBatches
                });
            }
            catch { /* Supervisor also detects process exit/time/disk limits and preserves its checkpoint. */ }
            notify("Session journal could not close cleanly; recovery files have been retained.");
        }
    }

    private async Task<SessionResult> Monitor()
    {
        Task<string> errors = worker.StandardError.ReadToEndAsync();
        Task output = Task.Run(async () =>
        {
            while (await worker.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line == "READY" && Volatile.Read(ref stopped) == 0)
                {
                    state = "recording";
                    notify("Recording. Play normally; use .ember stop or leave the world when finished.");
                }
            }
        });
        try
        {
            Task exit = worker.WaitForExitAsync();
            while (!exit.IsCompleted)
            {
                await Task.WhenAny(exit, Task.Delay(1000));
                if (journal.Error is not null && IsRecording) Stop("journal error: " + journal.Error);
                var workerStop = AtomicFile.ReadJson<SessionStop>(Path.Combine(DirectoryPath, "stop.json"));
                if (workerStop is not null && IsRecording) Stop(workerStop.Reason, workerStop.Cancelled);
                var info = AtomicFile.ReadJson<SessionInfo>(Path.Combine(DirectoryPath, "session.json"));
                if (info?.Status is "finalizing" or "recovering" && IsRecording) Stop("worker requested finalization");
            }
            await exit;
            await output;
            string stderr = await errors;
            if (IsRecording) Stop("profiler worker ended");
            if (stopping is not null) await stopping;
            var result = AtomicFile.ReadJson<SessionResult>(Path.Combine(DirectoryPath, "result.json"))
                ?? new SessionResult("failed", null, "Worker exited " + worker.ExitCode + ": " + Redactor.Text(stderr), DateTimeOffset.UtcNow);
            state = result.Status;
            if (result.ExportPath is not null)
                notify("Session saved: " + result.ExportPath + "\nGive GPT the included GPT-REPORT.md, or open report.html to inspect the evidence.");
            else if (result.Status == "cancelled") notify("Recording cancelled. Previous exports are unchanged.");
            else notify("Session could not be finalized: " + result.Error + "\nUse .ember recover to retry the saved recording.");
            return result;
        }
        catch (Exception e)
        {
            if (IsRecording) Stop("worker monitor error");
            state = "failed";
            notify("Profiler completion failed: " + Redactor.Text(e.Message) + ". Saved session can be retried with .ember recover.");
            return new("failed", null, Redactor.Text(e.Message), DateTimeOffset.UtcNow);
        }
        finally
        {
            Interlocked.Exchange(ref finished, 1);
            worker.Dispose();
        }
    }

    private static void RemoveRollingData(DiagnosticReport report)
    {
        report.Timeline = [];
        report.Ticks = [];
        report.Events = [];
        report.Logs = [];
        report.Profile = null;
    }

    public static Process Launch(string tracer, string mode, string directory)
    {
        if (!File.Exists(tracer)) throw new FileNotFoundException("Profiler worker missing. Install the complete Ember ZIP, including tracer/ and .ignore.");
        var start = new ProcessStartInfo(TraceController.FindDotnet())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(tracer)!
        };
        start.ArgumentList.Add(tracer);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(directory);
        return Process.Start(start) ?? throw new IOException("Could not start the recording supervisor.");
    }

    public static async Task RecoverOrphans(string tracer, string sessionsDirectory, Action<string> notify)
    {
        if (!Directory.Exists(sessionsDirectory)) return;
        foreach (string directory in Directory.EnumerateDirectories(sessionsDirectory).Order().Take(100))
        {
            var request = AtomicFile.ReadJson<SessionRequest>(Path.Combine(directory, "request.json"));
            if (request is null) continue;
            var result = AtomicFile.ReadJson<SessionResult>(Path.Combine(directory, "result.json"));
            if (result?.Status is "complete" or "completed" or "cancelled") continue;
            var info = AtomicFile.ReadJson<SessionInfo>(Path.Combine(directory, "session.json"));
            if (info is not null && Alive(info.SupervisorProcessId, info.SupervisorStartedUtc)) continue;
            if (Alive(request.ProcessId, request.ProcessStartedUtc) && !File.Exists(Path.Combine(directory, "stop.json"))) continue;
            try
            {
                using var child = Launch(tracer, "--recover", directory);
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync();
                await stdout;
                string error = await stderr;
                var recovered = AtomicFile.ReadJson<SessionResult>(Path.Combine(directory, "result.json"));
                if (recovered?.ExportPath is { } path) notify("Recovered session: " + path);
                else notify("Recovery retained its checkpoint: " + (recovered?.Error ?? Redactor.Text(error)));
            }
            catch (Exception e) { notify("Recovery failed: " + Redactor.Text(e.Message)); }
        }
    }
    private static bool Alive(int pid, DateTimeOffset? started)
    {
        if (pid <= 0 || started is null) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && Math.Abs((p.StartTime.ToUniversalTime() - started.Value.UtcDateTime).TotalSeconds) < 1;
        }
        catch { return false; }
    }
}
