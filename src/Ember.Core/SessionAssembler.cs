using System.Text.Json;

namespace Ember.Core;

public static class SessionAssembler
{
    public static DiagnosticReport Assemble(string directory)
    {
        var request = SessionFiles.Read<SessionRequest>(Path.Combine(directory, "request.json"), 4 * 1024 * 1024)
            ?? throw new InvalidDataException("Recording request.json is missing.");
        var notes = new List<string>();
        T? Optional<T>(string name)
        {
            try { return SessionFiles.Read<T>(Path.Combine(directory, name)); }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { notes.Add($"Could not read {name}: {e.Message}"); return default; }
        }
        var seed = Optional<DiagnosticReport>("seed.json") ?? new DiagnosticReport();
        var report = Optional<DiagnosticReport>("final.json") ?? seed;
        report.Environment = seed.Environment.Concat(report.Environment).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        report.Id = request.Id;
        report.Session = Optional<SessionInfo>("session.json") ?? new SessionInfo { Recovered = true };
        var info = report.Session;
        info.Id = request.Id;
        info.StartedUtc = request.StartedUtc;
        var stop = Optional<SessionStop>("stop.json");
        if (stop is not null)
        {
            info.StoppedUtc = stop.StoppedUtc; info.StopReason = stop.Reason;
            info.DroppedBatches = Math.Max(info.DroppedBatches, stop.DroppedBatches);
            info.Status = stop.Cancelled ? "cancelled" : "completed";
        }
        else if (info.Status == "recording") { info.Status = "recovered"; info.Recovered = true; }
        var frames = new Reservoir<TickRecord>(Math.Clamp(request.MaxReportFrames, 0, 500000));
        var windows = new Queue<MetricPoint>();
        var logs = new Queue<LogRecord>();
        var events = new Queue<DiagnosticEvent>();
        var contexts = new Queue<ContextSnapshot>();
        long omittedWindows = 0, omittedLogs = 0, omittedEvents = 0, omittedContexts = 0;
        void Tail<T>(Queue<T> queue, T value, int limit, ref long omitted)
        { if (queue.Count == limit) { queue.Dequeue(); omitted++; } queue.Enqueue(value); }
        var candidates = new IncidentAnalysis.Discovery(report.SlowTickThresholdMs, Distribution.Of(seed.Timeline.Where(w => w.LoopRate > 0).Select(w => w.IntervalMs.P50)).P50);
        long lastSequence = -1, sequenceGaps = 0, lastUnix = request.StartedUtc.ToUnixTimeMilliseconds();
        report.World = seed.World;
        var initialContext = new ContextSnapshot(lastUnix, new(seed.Settings), new(seed.Environment));
        string journal = Path.Combine(directory, "batches.ndjson");
        if (File.Exists(journal))
        {
            foreach (var line in SessionFiles.Lines(journal))
            {
                if (line.Text is not null && string.IsNullOrWhiteSpace(line.Text)) continue;
                SessionBatch? batch;
                try { batch = line.Text is null ? null : JsonSerializer.Deserialize<SessionBatch>(line.Text, Json.Options); }
                catch (JsonException) { batch = null; }
                if (batch is null || batch.Frames is null || batch.Events is null || batch.Logs is null ||
                    batch.Metric is { } m && (m.Runtime is null || m.IntervalMs is null || m.WorkMs is null ||
                    !double.IsFinite(m.WindowSeconds) || m.WindowSeconds <= 0 || m.WindowSeconds > 86400 || m.RenderStagesMs is null))
                { info.IncompleteJournalLines++; info.Recovered = true; continue; }
                if (batch.Sequence <= lastSequence)
                { info.IncompleteJournalLines++; info.Recovered = true; continue; }
                if (lastSequence >= 0 && batch.Sequence > lastSequence + 1)
                    sequenceGaps += batch.Sequence - lastSequence - 1;
                lastSequence = batch.Sequence;
                if (batch.Metric is { } metric)
                {
                    Tail(windows, metric, 20000, ref omittedWindows);
                    candidates.Observe(metric); lastUnix = Math.Max(lastUnix, metric.UnixMs);
                    if (batch.Frames.Length == 0) candidates.ObserveAggregateOnly(metric);
                }
                foreach (var frame in batch.Frames)
                {
                    if (frame is null || !double.IsFinite(frame.IntervalMs) || frame.IntervalMs < 0 || !double.IsFinite(frame.WorkMs)) continue;
                    frames.Add(frame); candidates.Observe(frame); lastUnix = Math.Max(lastUnix, frame.EndUnixMs);
                }
                foreach (var log in batch.Logs) if (log is not null) Tail(logs, log, 20000, ref omittedLogs);
                foreach (var ev in batch.Events) if (ev is not null) Tail(events, ev, 20000, ref omittedEvents);
                if (batch.World is { } world) report.World = world;
                if (batch.Context is { } context) Tail(contexts, context, 1999, ref omittedContexts);
            }
        }
        else { notes.Add("batches.ndjson is missing; no runtime timeline could be recovered."); info.Recovered = true; }
        info.DroppedBatches = Math.Max(info.DroppedBatches, sequenceGaps);
        info.StoppedUtc ??= DateTimeOffset.FromUnixTimeMilliseconds(lastUnix);
        info.RecordedSeconds = Math.Max(0, (info.StoppedUtc.Value - request.StartedUtc).TotalSeconds);
        info.FramesObserved = frames.Observed;
        info.FramesOmitted = frames.Observed - frames.Items.Count;
        report.Ticks = frames.Items.OrderBy(t => t.EndUnixMs).ToArray();
        report.Timeline = windows.OrderBy(t => t.UnixMs).ToArray();
        report.Logs = logs.ToArray(); report.Events = events.ToArray(); report.ContextHistory = new[] { initialContext }.Concat(contexts).ToList();
        report.Incidents = candidates.Finish(report);
        if (info.FramesOmitted > 0)
            notes.Add($"Frames: {info.FramesObserved} observed across {info.RecordedSeconds:F1}s; {report.Ticks.Length} retained by uniform reservoir, {info.FramesOmitted} omitted. Frame percentiles are estimates from the uniform sample; incidents were discovered before sampling.");
        if (omittedWindows + omittedLogs + omittedEvents + omittedContexts > 0)
            notes.Add($"Bounded tails omitted {omittedWindows} runtime windows, {omittedLogs} logs, {omittedEvents} events, {omittedContexts} context snapshots. Missing earlier values are unknown, not zero.");
        if (info.IncompleteJournalLines > 0) notes.Add($"Skipped {info.IncompleteJournalLines} corrupt, oversized, truncated or out-of-order journal lines; subsequent complete lines were recovered.");
        if (info.DroppedBatches > 0) notes.Add($"Journal reports {info.DroppedBatches} missing/dropped batches; gaps were not interpolated.");
        info.Notes.AddRange(notes);
        report.Profile = ProfileMerge.Merge(directory, info, Math.Clamp(request.MaxReportSamples, 0, 200000), report.Incidents);
        IncidentAnalysis.Analyze(report);
        report.Findings = Analysis.Findings(report);
        return report;
    }
}
