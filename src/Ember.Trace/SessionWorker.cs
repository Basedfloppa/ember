using System.Collections.Concurrent;
using System.Diagnostics;
using Ember.Core;

namespace Ember.Trace;

internal sealed class SessionWorker
{
    private readonly string directory;
    private readonly SessionRequest request;
    private readonly SessionInfo info;
    private readonly List<Child> children = [];
    private readonly ConcurrentQueue<bool> commands = new();
    private SessionStop? stopped;
    private bool ready;
    private int failures;
    private DateTimeOffset retryAt;
    private readonly long budget;
    private bool assembledCoverage;

    private SessionWorker(string directory, SessionRequest request, bool recover)
    {
        this.directory = directory;
        this.request = request;
        budget = Math.Min(2048, request.MaxDiskMegabytes) * 1024L * 1024;
        string path = Path.Combine(directory, "session.json");
        info = File.Exists(path) ? WorkerFiles.Read<SessionInfo>(path) : new SessionInfo
        {
            Id = request.Id, StartedUtc = request.StartedUtc
        };
        info.Recovered |= recover;
        info.Id = request.Id;
        info.StartedUtc = request.StartedUtc;
        info.SupervisorProcessId = Environment.ProcessId;
        using var self = Process.GetCurrentProcess();
        info.SupervisorStartedUtc = new DateTimeOffset(self.StartTime.ToUniversalTime());
    }

    public static async Task<int> Run(string directory, bool recover)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The session directory must be absolute.");
            directory = Path.GetFullPath(directory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The session directory does not exist.");
            // Keep the inode/file in place: deleting it after releasing the handle can break mutual exclusion.
            using var supervisorLock = new FileStream(Path.Combine(directory, "supervisor.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var request = WorkerFiles.Read<SessionRequest>(Path.Combine(directory, "request.json"));
            Validate(request);
            string resultPath = Path.Combine(directory, "result.json");
            if (File.Exists(resultPath))
            {
                var previous = WorkerFiles.Read<SessionResult>(resultPath);
                if (previous.Status == "cancelled") { Cleanup(directory); WorkerFiles.Emit("CANCELLED"); return 0; }
                if (previous.ExportPath is { } zip && File.Exists(zip) && previous.Status is "complete" or "completed")
                {
                    Cleanup(directory);
                    WorkerFiles.Emit("DONE " + zip);
                    return 0;
                }
            }
            if (recover && WorkerFiles.ParentAlive(request) && !File.Exists(Path.Combine(directory, "stop.json")))
                throw new InvalidOperationException("Cannot recover an active recording while its original game process is alive; stop it first.");
            var worker = new SessionWorker(directory, request, recover);
            return await worker.Execute(recover);
        }
        catch (Exception ex)
        {
            WorkerFiles.Emit("Ember.Trace session: " + ex.Message, error: true);
            return 1;
        }
    }

    private static void Validate(SessionRequest request)
    {
        if (request.ProcessId <= 0 || request.ProcessStartedUtc == default || request.StartedUtc == default)
            throw new ArgumentException("The session request must identify the original game process and its start time.");
        if (!Path.IsPathFullyQualified(request.ExportDirectory)) throw new ArgumentException("ExportDirectory must be absolute.");
        if (request.SegmentSeconds is < 1 or > 300 || request.MaxSeconds is < 1 or > 86400 ||
            request.MaxDiskMegabytes is < 1 or > 8192 || request.MaxTraceMegabytes is < 1 or > 1024 ||
            request.SegmentSamples is < 1 or > 200000 || request.KeepExports is < 1 or > 100)
            throw new ArgumentException("The session request contains invalid recording limits.");
    }

    private async Task<int> Execute(bool recover)
    {
        using var input = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; commands.Enqueue(true); };
        Console.CancelKeyPress += handler;
        _ = Task.Run(() => WorkerFiles.ReadCommands(cancelled => commands.Enqueue(cancelled), input.Token));
        try
        {
            stopped = ReadStop();
            if (recover) stopped ??= new SessionStop("recovery after interrupted recording", DateTimeOffset.UtcNow);
            else await Record();
            stopped ??= new SessionStop("recording ended", DateTimeOffset.UtcNow);
            PublishStop();
            info.Status = "finalizing";
            info.StopReason = stopped.Reason;
            info.StoppedUtc = stopped.StoppedUtc;
            info.DroppedBatches = stopped.DroppedBatches;
            Snapshot();
            await AwaitJournal();
            info.StopReason = stopped.Reason;
            info.StoppedUtc = stopped.StoppedUtc;
            info.DroppedBatches = stopped.DroppedBatches;
            if (stopped.Cancelled)
                return CommitCancellation();
            await RecoverSegments();
            // These counters are recomputed from the journal. Retrying a failed export must not
            // count its corrupt lines or sequence gaps a second time.
            info.IncompleteJournalLines = 0;
            info.DroppedBatches = stopped.DroppedBatches;
            info.FramesObserved = 0;
            info.FramesOmitted = 0;
            Snapshot();
            DiagnosticReport report = SessionAssembler.Assemble(directory);
            if (report.Session is { } assembled)
            {
                info.FramesObserved = assembled.FramesObserved;
                info.FramesOmitted = assembled.FramesOmitted;
                info.IncompleteJournalLines = assembled.IncompleteJournalLines;
                info.DroppedBatches = assembled.DroppedBatches;
                info.Recovered |= assembled.Recovered;
                info.RecordedSeconds = assembled.RecordedSeconds;
                info.StackCoverageSeconds = assembled.StackCoverageSeconds;
                info.StackCoveragePercent = assembled.StackCoveragePercent;
                assembledCoverage = true;
                foreach (string note in assembled.Notes) AddNote(note);
            }
            info.Status = "completed";
            UpdateCoverage();
            report.Session = info;
            report.Reason = stopped.Reason;
            // Cancellation can be accepted while retained segments are being recovered/assembled.
            // Honor the durable intent again immediately before any ZIP publication.
            RefreshCancellation();
            if (stopped.Cancelled) return CommitCancellation();
            string zip = Exporter.Export(report, request.ExportDirectory, request.KeepExports);
            Snapshot();
            WorkerFiles.Write(Path.Combine(directory, "result.json"), new SessionResult(info.Status, zip, null, DateTimeOffset.UtcNow));
            Cleanup(directory);
            WorkerFiles.Emit("DONE " + zip);
            if (request.OpenFolderWhenFinished)
            {
                try { Process.Start(new ProcessStartInfo(request.ExportDirectory) { UseShellExecute = true }); }
                catch (Exception ex) { WorkerFiles.Emit("Could not open the export folder: " + ex.Message, error: true); }
            }
            return 0;
        }
        catch (Exception ex)
        {
            await ShutdownChildren();
            info.Status = "failed";
            AddNote("Finalization failed; recovery files were retained: " + ex.Message);
            try
            {
                Snapshot();
                WorkerFiles.Write(Path.Combine(directory, "result.json"), new SessionResult("failed", null, ex.Message, DateTimeOffset.UtcNow));
            }
            catch (Exception persist) { WorkerFiles.Emit("Could not save failure status: " + persist.Message, error: true); }
            WorkerFiles.Emit("Ember.Trace session: " + ex.Message, error: true);
            return 1;
        }
        finally
        {
            input.Cancel();
            Console.CancelKeyPress -= handler;
            await ShutdownChildren();
        }
    }

    private async Task Record()
    {
        info.Status = "recording";
        Snapshot();
        var nextSnapshot = DateTimeOffset.MinValue;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            if (ReadStop() is { } diskStop && (stopped is null || diskStop.Cancelled))
                stopped = diskStop with { Cancelled = diskStop.Cancelled || stopped?.Cancelled == true };
            while (commands.TryDequeue(out bool cancelled))
            {
                if (stopped is null || cancelled) stopped = new SessionStop(cancelled ? "cancelled" : "user stop", now, cancelled);
            }
            if (stopped is null && !WorkerFiles.ParentAlive(request)) stopped = new SessionStop("game process exited", now);
            if (stopped is null && (now - request.StartedUtc).TotalSeconds >= request.MaxSeconds)
                stopped = new SessionStop("maximum recording duration reached", now);
            long disk = WorkerFiles.Size(directory);
            // Leave a small reserve for atomic status/metadata writes; raw and parser files count too.
            bool full = disk >= budget - Math.Min(16 * 1024 * 1024, budget / 16);
            if (stopped is null && full) stopped = new SessionStop("session disk budget reached", now);
            if (stopped is not null) PublishStop();

            foreach (Child child in children.ToArray())
            {
                while (child.Messages.TryDequeue(out string? line))
                {
                    if (line == "READY") { child.Attached = true; RefreshSegment(child); EmitReady(); }
                    if (line == "CAPTURED") { child.Captured = true; child.CapturedAt = now; RefreshSegment(child); }
                }
                if (stopped is not null) child.Stop(stopped.Cancelled); // stdin exists before EventPipe startup begins.
                if (stopped?.Cancelled == true) child.Kill("recording cancelled");
                if (!child.Process.HasExited)
                {
                    child.Process.Refresh();
                    string? failure = full ? "session disk budget reached" :
                        child.Process.WorkingSet64 > 1536L * 1024 * 1024 ? "segment worker exceeded 1.5 GiB memory budget" :
                        child.Captured && (now - child.CapturedAt).TotalSeconds > 90 ? "segment parsing exceeded 90 seconds" :
                        !child.Captured && (now - child.LaunchedAt).TotalSeconds > request.SegmentSeconds + 40 ? "segment capture/rundown timed out" :
                        child.StopSentAt is { } stopAt && !child.Captured && (now - stopAt).TotalSeconds > 30 ? "segment stop timed out" : null;
                    if (failure is not null) child.Kill(failure);
                }
                if (child.Process.HasExited) await Complete(child);
            }

            if (stopped is null && children.Count < 2 && !children.Any(c => !c.Captured) && failures < 3 && now >= retryAt)
            {
                // Bound potential raw growth. Parsing is also watched against the total directory cap.
                long available = budget - WorkerFiles.Size(directory) - Math.Min(4 * 1024 * 1024, budget / 8);
                int maxTraceMb = (int)Math.Min(request.MaxTraceMegabytes, available / (8 * 1024 * 1024));
                if (maxTraceMb < 1) stopped = new SessionStop("session disk budget reached", now);
                else StartSegment(maxTraceMb);
            }
            if (failures > 0) EmitReady(); // Monitoring-only recording is still a usable recording.
            if (stopped is not null)
            {
                info.Status = "finalizing";
                info.StopReason = stopped.Reason;
                info.StoppedUtc = stopped.StoppedUtc;
                foreach (var child in children) child.Stop(stopped.Cancelled);
                if (children.Count == 0) { Snapshot(); break; }
            }
            if (now >= nextSnapshot) { Snapshot(); nextSnapshot = now.AddSeconds(1); }
            await Task.Delay(200);
        }
    }

    private void PublishStop()
    {
        if (stopped is not null && !File.Exists(Path.Combine(directory, "stop.json")))
            WorkerFiles.Write(Path.Combine(directory, "stop.json"), stopped, overwrite: false);
    }

    private async Task AwaitJournal()
    {
        string closedPath = Path.Combine(directory, "journal.closed.json");
        // stop.json requests shutdown; journal.closed.json acknowledges detached/flushed journaling.
        for (int attempt = 0; attempt < 50 && !File.Exists(closedPath) && WorkerFiles.ParentAlive(request); attempt++)
            await Task.Delay(200);
        if (ReadStop() is { } finalStop) stopped = finalStop with { Cancelled = finalStop.Cancelled || stopped?.Cancelled == true };
        while (commands.TryDequeue(out bool cancelled))
            if (cancelled) stopped = stopped! with { Reason = "cancelled", Cancelled = true };
        if (File.Exists(closedPath))
        {
            var closed = WorkerFiles.Read<JournalClosed>(closedPath);
            stopped = stopped! with { DroppedBatches = Math.Max(stopped!.DroppedBatches, closed.DroppedBatches) };
            if (!string.IsNullOrEmpty(closed.Error)) AddNote("The game journal reported an error during finalization: " + closed.Error);
        }
        else if (WorkerFiles.ParentAlive(request))
            AddNote("Journal-close acknowledgment was not received within ten seconds; only journal records readable at finalization are available.");
        RefreshCancellation();
    }

    private void RefreshCancellation()
    {
        if (ReadStop() is { Cancelled: true } cancellation)
            stopped = cancellation with { DroppedBatches = Math.Max(cancellation.DroppedBatches, stopped?.DroppedBatches ?? 0) };
        while (commands.TryDequeue(out bool cancelled))
            if (cancelled) stopped = stopped! with { Reason = "cancelled", Cancelled = true };
    }

    private int CommitCancellation()
    {
        info.Status = "cancelled";
        info.StopReason = stopped!.Reason;
        info.StoppedUtc = stopped.StoppedUtc;
        info.DroppedBatches = stopped.DroppedBatches;
        Snapshot();
        WorkerFiles.Write(Path.Combine(directory, "result.json"), new SessionResult("cancelled", null, null, DateTimeOffset.UtcNow));
        Cleanup(directory);
        WorkerFiles.Emit("CANCELLED");
        return 0;
    }

    private sealed record JournalClosed(DateTimeOffset ClosedUtc, long DroppedBatches, long BytesWritten, string? Error);

    private void StartSegment(int maxTraceMb)
    {
        int index = info.Segments.Count == 0 ? 1 : info.Segments.Max(s => s.Index) + 1;
        string relative = $"segments/{index:D6}/profile.json";
        string work = Path.GetDirectoryName(Path.Combine(directory, relative))!;
        Directory.CreateDirectory(work);
        var segment = new ProfileSegment { Index = index, StartedUtc = DateTimeOffset.UtcNow, FileName = relative };
        WorkerFiles.Write(Path.Combine(work, "segment.json"), segment);
        // Serialize a per-segment copy so decreasing available disk never mutates the durable request.
        var copy = WorkerFiles.Read<SessionRequest>(Path.Combine(directory, "request.json"));
        copy.MaxTraceMegabytes = maxTraceMb;
        WorkerFiles.Write(Path.Combine(work, "request.json"), copy);
        info.Segments.Add(segment);
        try { children.Add(new Child(Process.Start(WorkerFiles.StartInfo("--segment-private", work)) ?? throw new IOException("Could not launch segment worker."), work)); }
        catch (Exception ex)
        {
            segment.Status = "failed"; segment.Error = ex.Message; segment.EndedUtc = DateTimeOffset.UtcNow;
            AttachFailure(ex.Message);
        }
    }

    private void RefreshSegment(Child child)
    {
        var saved = WorkerFiles.Read<ProfileSegment>(Path.Combine(child.Directory, "segment.json"));
        int at = info.Segments.FindIndex(s => s.Index == saved.Index);
        if (at >= 0) info.Segments[at] = saved;
    }

    private async Task Complete(Child child)
    {
        await child.FinishReaders();
        try
        {
            var saved = WorkerFiles.Read<ProfileSegment>(Path.Combine(child.Directory, "segment.json"));
            if (child.Process.ExitCode != 0 || child.Failure is not null)
            {
                saved.Status = "failed";
                saved.Error = child.Failure ?? saved.Error ?? child.Error;
                if (saved.EndedUtc == default) saved.EndedUtc = DateTimeOffset.UtcNow;
            }
            info.Segments[info.Segments.FindIndex(s => s.Index == saved.Index)] = saved;
            if (saved.Status == "completed") failures = 0;
            else if (!child.Attached && stopped is null) AttachFailure(saved.Error ?? "EventPipe attach failed.");
            else if (stopped is null)
            {
                failures++;
                retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                AddNote("Some stack segments failed; only successfully parsed intervals count toward stack coverage.");
            }
        }
        finally { children.Remove(child); child.Process.Dispose(); }
    }

    private void AttachFailure(string error)
    {
        failures++;
        retryAt = DateTimeOffset.UtcNow.AddSeconds(failures * 5);
        AddNote("EventPipe attach failed; metric/frame journaling continues with bounded retries. " + error);
        if (failures >= 3) AddNote("Stack capture retries exhausted; the rest of this recording is monitoring-only.");
    }

    private async Task RecoverSegments()
    {
        string root = Path.Combine(directory, "segments");
        if (!Directory.Exists(root)) return;
        // Discover checkpoint directories even if the supervisor died before its next snapshot.
        foreach (string work in Directory.EnumerateDirectories(root).Order())
        {
            if (!File.Exists(Path.Combine(work, "segment.json"))) continue;
            // An orphaned segment notices its dead supervisor and stops. Do not parse its raw file
            // concurrently with that stop/rundown or an in-flight parser.
            FileStream? ownership = null;
            for (int attempt = 0; attempt < 200 && ownership is null; attempt++)
            {
                try { ownership = new FileStream(Path.Combine(work, "segment.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { await Task.Delay(200); }
            }
            if (ownership is null) { AddNote("An orphaned segment worker still owns its checkpoint; that segment was not recovered."); continue; }
            using var segmentOwnership = ownership;
            ProfileSegment segment;
            try { segment = WorkerFiles.Read<ProfileSegment>(Path.Combine(work, "segment.json")); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { AddNote("Unreadable segment checkpoint: " + ex.Message); continue; }
            int at = info.Segments.FindIndex(s => s.Index == segment.Index);
            if (at < 0) info.Segments.Add(segment); else info.Segments[at] = segment;
            string profilePath = Path.Combine(work, "profile.json");
            StackProfile? profile = null;
            if (File.Exists(profilePath))
            {
                try { profile = WorkerFiles.Read<StackProfile>(profilePath); }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    File.Move(profilePath, profilePath + ".partial", overwrite: true);
                }
            }
            if (profile is null && File.Exists(Path.Combine(work, "capture.nettrace")))
            {
                // Do not retry conversion under an already-exhausted disk budget.
                long rawBytes = new FileInfo(Path.Combine(work, "capture.nettrace")).Length;
                if (rawBytes == 0 || WorkerFiles.Size(directory) + Math.Max(rawBytes * 2, 4 * 1024 * 1024) >= budget)
                {
                    segment.Status = "failed";
                    segment.Error = "Partial segment recovery skipped: insufficient remaining disk budget or empty trace.";
                    continue;
                }
                try
                {
                    var capture = WorkerFiles.Read<CaptureInfo>(Path.Combine(work, "capture.json"));
                    if (capture.TraceBytes == 0)
                    {
                        var end = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(work, "capture.nettrace")));
                        if (info.StoppedUtc is { } stop && end > stop) end = stop;
                        capture = capture with { TraceBytes = rawBytes, EndedUtc = end, Truncated = true };
                        WorkerFiles.Write(Path.Combine(work, "capture.json"), capture);
                    }
                    var watch = Stopwatch.StartNew();
                    using var limit = new CancellationTokenSource();
                    Task parse = Program.ParseInChild(work, request.MaxTraceMegabytes, limit.Token);
                    while (!parse.IsCompleted)
                    {
                        if (WorkerFiles.Size(directory) >= budget - Math.Min(16 * 1024 * 1024, budget / 16)) limit.Cancel();
                        await Task.WhenAny(parse, Task.Delay(200));
                    }
                    await parse;
                    profile = WorkerFiles.Read<StackProfile>(profilePath);
                    segment.ParseMilliseconds = watch.Elapsed.TotalMilliseconds;
                    AddNote("Recovered a retained segment after interruption; an incomplete trace may omit stacks or symbols.");
                }
                catch (Exception ex)
                {
                    segment.Status = "failed";
                    segment.Error = "Partial trace could not be recovered: " + ex.Message;
                }
            }
            if (profile is not null)
            {
                segment.Status = "completed";
                segment.FileName = Path.GetRelativePath(directory, profilePath).Replace('\\', '/');
                segment.StartedUtc = profile.StartedUtc;
                segment.EndedUtc = profile.StartedUtc.AddSeconds(profile.UsableDurationSeconds ?? profile.DurationSeconds);
                segment.TraceBytes = profile.TraceBytes;
                segment.ObservedSamples = profile.ObservedStackSamples;
                segment.RetainedSamples = profile.Samples.Count;
            }
            else if (segment.Status != "failed")
            {
                segment.Status = "failed";
                segment.Error ??= "No recoverable profile was present.";
            }
            if (profile is not null) WorkerFiles.CommitSegment(work, profile, segment);
            else WorkerFiles.Write(Path.Combine(work, "segment.json"), segment);
        }
    }

    private SessionStop? ReadStop()
    {
        string path = Path.Combine(directory, "stop.json");
        return File.Exists(path) ? WorkerFiles.Read<SessionStop>(path) : null;
    }

    private void EmitReady() { if (!ready) { ready = true; WorkerFiles.Emit("READY"); } }
    private void AddNote(string note) { if (!info.Notes.Contains(note) && info.Notes.Count < 100) info.Notes.Add(note); }

    private void Snapshot()
    {
        UpdateCoverage();
        WorkerFiles.Write(Path.Combine(directory, "session.json"), info);
        WorkerFiles.Write(Path.Combine(directory, "status.json"), info);
    }

    private void UpdateCoverage()
    {
        // The assembler validates the actual profile files. Metadata-only interval union must not
        // restore credit to a segment it rejected during merge.
        if (assembledCoverage) return;
        DateTimeOffset end = info.StoppedUtc ?? DateTimeOffset.UtcNow;
        info.RecordedSeconds = Math.Max(0, (end - info.StartedUtc).TotalSeconds);
        double covered = 0;
        var cursor = info.StartedUtc;
        foreach (var segment in info.Segments.Where(s => s.Status == "completed").OrderBy(s => s.StartedUtc))
        {
            var start = segment.StartedUtc > cursor ? segment.StartedUtc : cursor;
            var finish = segment.EndedUtc < end ? segment.EndedUtc : end;
            if (finish <= start) continue;
            covered += (finish - start).TotalSeconds;
            cursor = finish;
        }
        info.StackCoverageSeconds = covered;
        info.StackCoveragePercent = info.RecordedSeconds > 0 ? 100 * covered / info.RecordedSeconds : 0;
        AddNote("Stack coverage includes only successfully parsed segment intervals. Attach, rundown, parser backpressure and retry gaps have no stack coverage; journaled metrics/frames may still cover those gaps.");
    }

    private static void Cleanup(string directory)
    {
        try
        {
            string segments = Path.Combine(directory, "segments");
            if (Directory.Exists(segments)) Directory.Delete(segments, recursive: true);
            foreach (string name in new[] { "batches.ndjson", "seed.json", "final.json" }) File.Delete(Path.Combine(directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { WorkerFiles.Emit("Export committed; some recording files could not be removed: " + ex.Message, error: true); }
    }

    private async Task ShutdownChildren()
    {
        foreach (var child in children.ToArray())
        {
            child.Kill("supervisor shutting down");
            try { await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            child.Process.Dispose();
            children.Remove(child);
        }
    }

    private sealed class Child
    {
        public Process Process { get; }
        public string Directory { get; }
        public ConcurrentQueue<string> Messages { get; } = new();
        public DateTimeOffset LaunchedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset CapturedAt { get; set; }
        public DateTimeOffset? StopSentAt { get; private set; }
        public bool Captured { get; set; }
        public bool Attached { get; set; }
        public string? Failure { get; private set; }
        public string Error { get; private set; } = "";
        private readonly Task stdout;
        private readonly Task stderr;

        public Child(Process process, string directory)
        {
            Process = process; Directory = directory;
            stdout = Read(process.StandardOutput, line => { if (line is "READY" or "CAPTURED" or "DONE") Messages.Enqueue(line); });
            stderr = Read(process.StandardError, line => { if (Error.Length < 8192) Error += line[..Math.Min(line.Length, 8192 - Error.Length)] + "\n"; });
        }

        public void Stop(bool cancelled)
        {
            if (StopSentAt is not null || Process.HasExited) return;
            StopSentAt = DateTimeOffset.UtcNow;
            try { Process.StandardInput.WriteLine(cancelled ? "cancel" : "stop"); Process.StandardInput.Flush(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }

        public void Kill(string reason)
        {
            Failure ??= reason;
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }

        public async Task FinishReaders() => await Task.WhenAll(stdout, stderr);

        private static async Task Read(StreamReader reader, Action<string> onLine)
        {
            // Bound even third-party diagnostics that omit newlines.
            char[] buffer = new char[1024];
            var line = new System.Text.StringBuilder();
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer)) != 0)
                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] == '\n') { onLine(line.ToString().TrimEnd('\r')); line.Clear(); }
                        else if (line.Length < 8192) line.Append(buffer[i]);
                    }
                if (line.Length > 0) onLine(line.ToString());
            }
            catch (IOException) { }
        }
    }
}
