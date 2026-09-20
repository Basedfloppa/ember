namespace Ember.Core;

public static class IncidentAnalysis
{
    internal sealed class Discovery(double configuredThreshold, double initialBaseline = 0)
    {
        private readonly double threshold = double.IsFinite(configuredThreshold) && configuredThreshold > 0 ? configuredThreshold : 50;
        private readonly Reservoir<double> baselineWindows = new(512);
        private double baseline = initialBaseline;
        private readonly List<PerformanceIncident> candidates = [];
        public void Observe(TickRecord frame)
        {
            if (frame.IntervalMs < Math.Max(threshold, baseline * 2)) return;
            Add(frame.EndUnixMs - (long)Math.Min(frame.IntervalMs, 86400000), frame.EndUnixMs, frame.IntervalMs, 1, false);
        }
        public void Observe(MetricPoint window)
        {
            if (window.LoopRate > 0 && window.IntervalMs.Count > 0 && double.IsFinite(window.IntervalMs.P50))
            {
                baselineWindows.Add(window.IntervalMs.P50);
                if (baselineWindows.Observed <= 16 || baselineWindows.Observed % 16 == 0)
                    baseline = Distribution.Of(baselineWindows.Items).P50;
            }
            if (window.WindowSeconds > 0 && window.LoopRate == 0 && window.IntervalMs.Count == 0)
                Add(window.UnixMs - (long)(window.WindowSeconds * 1000), window.UnixMs, window.WindowSeconds * 1000, 0, true);
        }
        public void ObserveAggregateOnly(MetricPoint window)
        {
            if (window.IntervalMs.Count == 0 || window.IntervalMs.Max < Math.Max(threshold, baseline * 2)) return;
            Add(window.UnixMs - (long)(window.WindowSeconds * 1000), window.UnixMs, window.IntervalMs.Max, Math.Max(0, window.SlowTicks), false, aggregate: true);
        }
        private void Add(long start, long end, double peak, int slow, bool stall, bool aggregate = false)
        {
            var matches = candidates.Where(c => c.StartUnixMs <= end + 250 && c.EndUnixMs >= start - 250).ToArray();
            var incident = new PerformanceIncident { StartUnixMs = start, EndUnixMs = end, PeakFrameMs = stall ? 0 : peak,
                SlowFrames = slow, Title = stall ? "No completed frames observed" : aggregate ? "Slow frame window (aggregate timing)" : "Slow frame cluster" };
            foreach (var match in matches)
            {
                incident.StartUnixMs = Math.Min(incident.StartUnixMs, match.StartUnixMs);
                incident.EndUnixMs = Math.Max(incident.EndUnixMs, match.EndUnixMs);
                incident.PeakFrameMs = Math.Max(incident.PeakFrameMs, match.PeakFrameMs);
                incident.SlowFrames += match.SlowFrames;
                if (match.Title.Contains("No completed")) incident.Title = match.Title;
                else if (match.Title.Contains("aggregate") && !stall) incident.Title = match.Title;
                candidates.Remove(match);
            }
            candidates.Add(incident);
            if (candidates.Count > 64)
            {
                // Prefer distinct regions when the candidate budget is full, rather than allowing
                // many near-identical peaks in one short interval to evict the rest of the session.
                var ranked = candidates.OrderByDescending(Score).ToArray();
                candidates.Clear();
                foreach (var candidate in ranked)
                {
                    if (candidates.Any(c => c.StartUnixMs - 2000 <= candidate.EndUnixMs + 2000 && c.EndUnixMs + 2000 >= candidate.StartUnixMs - 2000)) continue;
                    candidates.Add(candidate);
                    if (candidates.Count == 64) break;
                }
            }
        }
        private static double Score(PerformanceIncident i) => Math.Max(i.PeakFrameMs, i.Title.Contains("No completed") ? i.EndUnixMs - i.StartUnixMs : 0);
        public List<PerformanceIncident> Finish(DiagnosticReport report)
        {
            double baseline = Distribution.Of(report.Timeline.Where(w => w.LoopRate > 0).Select(w => w.IntervalMs.P50)).P50;
            long start = report.Session?.StartedUtc.ToUnixTimeMilliseconds() ?? long.MinValue + 2000;
            long end = report.Session?.StoppedUtc?.ToUnixTimeMilliseconds() ?? long.MaxValue - 2000;
            var result = new List<PerformanceIncident>();
            foreach (var candidate in candidates.OrderByDescending(Score))
            {
                if (candidate.PeakFrameMs > 0 && candidate.PeakFrameMs < Math.Max(threshold, baseline * 2) && !candidate.Title.Contains("No completed")) continue;
                candidate.StartUnixMs = Math.Max(start, candidate.StartUnixMs);
                candidate.EndUnixMs = Math.Min(end, candidate.EndUnixMs);
                if (candidate.EndUnixMs < candidate.StartUnixMs) continue;
                candidate.ContextStartUnixMs = Math.Max(start, candidate.StartUnixMs - 2000);
                candidate.ContextEndUnixMs = Math.Min(end, candidate.EndUnixMs + 2000);
                if (result.Any(i => i.ContextStartUnixMs <= candidate.ContextEndUnixMs && i.ContextEndUnixMs >= candidate.ContextStartUnixMs)) continue;
                candidate.Notes.Add($"Discovery uses configured threshold {threshold:F1}ms and 2× a streaming runtime-window p50 baseline; final selection baseline={baseline:F1}ms. Top distinct clusters ranked by peak interval or no-frame duration. Maximum 12 incidents, bounded candidate set 64; absence from this list is not absence of a slowdown.");
                result.Add(candidate);
                if (result.Count == 12) break;
            }
            result = result.OrderBy(i => i.StartUnixMs).ToList();
            for (int i = 0; i < result.Count; i++) result[i].Id = $"incident-{i + 1:00}";
            return result;
        }
    }

    /// <summary>Idempotent evidence-based incident analysis, also usable for older manual exports.</summary>
    public static void Analyze(DiagnosticReport report)
    {
        if (report.Incidents.Count == 0)
        {
            var discovery = new Discovery(report.SlowTickThresholdMs, Distribution.Of(report.Timeline.Where(w => w.LoopRate > 0).Select(w => w.IntervalMs.P50)).P50);
            foreach (var frame in report.Ticks) discovery.Observe(frame);
            foreach (var window in report.Timeline)
            {
                discovery.Observe(window);
                if (report.Ticks.Length == 0) discovery.ObserveAggregateOnly(window);
            }
            report.Incidents = discovery.Finish(report);
        }
        var normal = report.Timeline.Where(w => w.LoopRate > 0 && !report.Incidents.Any(i => Overlaps(w, i.StartUnixMs, i.EndUnixMs))).ToArray();
        double normalCpu = Distribution.Of(normal.Select(w => w.Runtime.CpuOneCorePercent)).P50;
        double normalAllocation = Distribution.Of(normal.Select(w => w.Runtime.AllocationBytesPerSecond)).P50;
        int? mainOsThread = null;
        if (report.Environment.TryGetValue("mainOsThreadId", out var mainThreadValue) && int.TryParse(mainThreadValue?.ToString(), out int mainThread)) mainOsThread = mainThread;
        foreach (var i in report.Incidents)
        {
            i.Evidence.Clear(); i.CounterEvidence.Clear(); i.Hypotheses.Clear(); i.NextExperiments.Clear();
            i.Windows = report.Timeline.Where(w => Overlaps(w, i.ContextStartUnixMs, i.ContextEndUnixMs)).Take(20000).ToArray();
            var during = i.Windows.Where(w => Overlaps(w, i.StartUnixMs, i.EndUnixMs)).ToArray();
            var before = i.Windows.Where(w => w.UnixMs <= i.StartUnixMs).ToArray();
            var after = i.Windows.Where(w => w.UnixMs - w.WindowSeconds * 1000 >= i.EndUnixMs).ToArray();
            i.Evidence.Add($"incidents[{i.Id}]: Unix ms {i.StartUnixMs}–{i.EndUnixMs}; peak completed-frame interval={i.PeakFrameMs:F2}ms, slowFrames={i.SlowFrames}. Context ±2s is clipped at known session bounds.");
            if (i.Title.Contains("aggregate")) i.CounterEvidence.Add("Incident timing is an aggregate runtime window because matching raw frame records were unavailable; exact frame timestamp and sub-window ordering are unknown.");
            i.Evidence.Add($"timeline windows before/during/after={before.Length}/{during.Length}/{after.Length}; windows overlapping a boundary cannot resolve sub-window ordering. Normal comparison uses {normal.Length} non-incident windows: median cpuOneCorePercent={normalCpu:F1}, allocationBytesPerSecond={normalAllocation:F0}.");
            foreach (var phase in new[] { (Name: "before", Windows: before), (Name: "during", Windows: during), (Name: "after", Windows: after) })
                if (phase.Windows.Length > 0)
                    i.Evidence.Add($"{phase.Name} timeline unixMs={phase.Windows[0].UnixMs}–{phase.Windows[^1].UnixMs}: median intervalMs.p50={Distribution.Of(phase.Windows.Select(w => w.IntervalMs.P50)).P50:F2}ms, median cpuOneCorePercent={Distribution.Of(phase.Windows.Select(w => w.Runtime.CpuOneCorePercent)).P50:F1}, median allocationBytesPerSecond={Distribution.Of(phase.Windows.Select(w => w.Runtime.AllocationBytesPerSecond)).P50:F0}, gcPauseMilliseconds sum={phase.Windows.Sum(w => w.Runtime.GcPauseMilliseconds):F2}ms.");
            foreach (var w in during.Take(8))
                i.Evidence.Add($"timeline[unixMs={w.UnixMs}]: loopRate={w.LoopRate:F1}, intervalMs.max={w.IntervalMs.Max:F2}, runtime.cpuOneCorePercent={w.Runtime.CpuOneCorePercent:F1}, runtime.allocationBytesPerSecond={w.Runtime.AllocationBytesPerSecond:F0}, runtime.gcPauseMilliseconds={w.Runtime.GcPauseMilliseconds:F2}" +
                    (w.GpuElapsedMs is { } gpu ? $", gpuElapsedMs.p95={gpu.P95:F2} (GPU timestamp elapsed render window, not exact GPU busy time)" : ", gpuElapsedMs unavailable") + ".");
            double gc = during.Sum(w => w.Runtime.GcPauseMilliseconds);
            if (gc > 0)
                i.Hypotheses.Add(new("warning", "GC pause overlaps slowdown", $"timeline runtime.gcPauseMilliseconds sums to {gc:F2}ms in overlapping windows {string.Join(",", during.Where(w => w.Runtime.GcPauseMilliseconds > 0).Take(8).Select(w => w.UnixMs))}; temporal overlap does not establish the whole stall's cause.", "Reproduce the same route with one allocation-producing feature disabled; compare incident duration, gcPauseMilliseconds and allocationBytesPerSecond against normal windows."));
            else if (during.Length > 0) i.CounterEvidence.Add("Overlapping timeline.runtime.gcPauseMilliseconds is zero; recorded GC pauses do not explain this incident (missing events remain possible).");
            if (during.Any(w => w.Runtime.CpuOneCorePercent > Math.Max(80, normalCpu * 1.5)))
                i.Hypotheses.Add(new("info", "Elevated process CPU overlaps slowdown", $"timeline.runtime.cpuOneCorePercent peaks at {during.Max(w => w.Runtime.CpuOneCorePercent):F1} versus non-incident median {normalCpu:F1}; process CPU includes all threads and integrated server when present.", "Repeat the scene changing one CPU-side workload setting; compare intervalMs.max, process CPU and incident stack observation patterns by thread."));
            if (during.Any(w => w.GpuElapsedMs is { Count: > 0 } g && g.P95 > Math.Max(16, report.TargetIntervalMs)))
                i.Hypotheses.Add(new("info", "Long GPU timestamp render windows", "Overlapping timeline.gpuElapsedMs.p95 exceeds the target/fallback 16ms. Timestamp span can include dependencies and scheduling; GPU utilization and exact busy time are unmeasured.", "Repeat with lower resolution at the same scene and FPS cap; compare gpuElapsedMs.p95 and frame intervals. Keep other graphics settings fixed."));
            var contextEvents = report.Events.Where(e => e.UnixMs >= i.ContextStartUnixMs && e.UnixMs <= i.ContextEndUnixMs).ToArray();
            foreach (var ev in contextEvents.Where(e => IsContext(e.Kind + " " + e.Message)).Take(12))
                i.CounterEvidence.Add($"events[unixMs={ev.UnixMs},kind={ev.Kind}]: {Redactor.Text(ev.Message)}; focus/pause/loading state may explain intentional pacing or transitional work.");
            var snapshot = report.ContextHistory.LastOrDefault(c => c.UnixMs <= i.StartUnixMs);
            if (snapshot is not null)
                i.CounterEvidence.Add($"Consult contextHistory[unixMs={snapshot.UnixMs}].settings/environment for contemporaneous focus, pause, loading, VSync and FPS-cap state; snapshot values do not prove continuous state.");
            else i.CounterEvidence.Add("No contemporaneous context snapshot; focus, pause, loading, VSync and FPS-cap explanations are unresolved. Inspect settings and timestamped events.");
            if (report.Profile is { } p)
            {
                if (i.Samples.Count == 0 && report.Session is null)
                {
                    var phases = new[] { new Reservoir<ProfileSample>(667), new Reservoir<ProfileSample>(666), new Reservoir<ProfileSample>(667) };
                    foreach (var s in p.Samples.Where(s => ProfileMerge.Valid(s, p)))
                    {
                        double time = p.StartedUtc.ToUnixTimeMilliseconds() + s.RelativeMs;
                        if (time < i.ContextStartUnixMs || time > i.ContextEndUnixMs) continue;
                        phases[time < i.StartUnixMs ? 0 : time > i.EndUnixMs ? 2 : 1].Add(s);
                    }
                    i.Samples = phases.SelectMany(x => x.Items).OrderBy(s => s.RelativeMs).ToList();
                    i.StackCoveragePercent = ProfileMerge.Coverage(i, [(p.StartedUtc.ToUnixTimeMilliseconds(), p.StartedUtc.ToUnixTimeMilliseconds() + ProfileMerge.EffectiveDuration(p) * 1000)]);
                    const string note = "Manual profile: incident observations selected from already-retained profile samples; earlier reservoir omissions cannot be recovered. Coverage is profile interval overlap clipped to usableDurationSeconds when known, not event completeness or the last reservoir-retained timestamp.";
                    if (!i.Notes.Contains(note)) i.Notes.Add(note);
                }
                i.Hotspots = ProfileMerge.Hotspots(i.Samples);
                var groups = i.Samples.GroupBy(s => p.StartedUtc.ToUnixTimeMilliseconds() + s.RelativeMs < i.StartUnixMs ? "before" : p.StartedUtc.ToUnixTimeMilliseconds() + s.RelativeMs > i.EndUnixMs ? "after" : "during");
                foreach (var phase in groups)
                    i.Evidence.Add($"incidents[{i.Id}].samples {phase.Key}: {phase.Count()} thread observations, managed={phase.Count(s => s.Managed)}; thread IDs/counts={string.Join(", ", phase.GroupBy(s => s.ThreadId).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key}:{g.Count()}"))}. Thread IDs alone do not identify main/worker roles.");
                if (mainOsThread is { } main)
                    i.Evidence.Add($"environment.mainOsThreadId={main} explicitly identifies the main OS thread: {i.Samples.Count(s => s.ThreadId == main)} incident observations; {i.Samples.Count(s => s.ThreadId != main)} observations on other threads (worker roles not identified). Managed thread IDs are a separate identifier space.");
                foreach (var h in i.Hotspots.Take(5).Where(h => h.Frame >= 0 && h.Frame < p.Frames.Count))
                {
                    var frame = p.Frames[h.Frame];
                    i.Evidence.Add($"profile.frames[{h.Frame}]={frame.Name} [{frame.Module}], owners=[{string.Join(",", frame.Mods)}]: {h.InclusiveSamples} inclusive incident observations (recursion counted once/sample), {h.SelfSamples} leaf. Ownership and sampled presence alone do not establish mod blame.");
                }
            }
            if (i.Samples.Count == 0) i.CounterEvidence.Add("No retained stacks in this context; no stack-based cause or thread role can be established.");
            if (before.Length == 0 || after.Length == 0) i.CounterEvidence.Add("Before or after runtime windows are missing; recovery or baseline contrast is incomplete.");
            if (during.Length == 0) i.CounterEvidence.Add("No overlapping runtime windows; GC, allocation, CPU and GPU evidence is unavailable, not zero.");
            if (i.Hypotheses.Count == 0)
                i.Hypotheses.Add(new("info", "Cause unresolved from retained evidence", "Slow/absent completed frames are observable, but available runtime and stack observations do not uniquely identify a cause.", "Reproduce with continuous session capture; mark focus/pause/loading transitions and compare the same scene with a fixed FPS cap. Inspect incident before/during/after stacks and logs."));
            i.Confidence = during.Length > 0 && i.Samples.Count >= 20 && i.StackCoveragePercent >= 80 && !contextEvents.Any(e => IsContext(e.Kind + " " + e.Message)) ? "medium" : "low";
            i.NextExperiments.AddRange(i.Hypotheses.Select(h => h.NextStep));
            i.NextExperiments.Add("Use one controlled A/B change at a time; retain game version, mod versions, world/route and settings. Compare equivalent-duration windows and repeat to check variability. Installed mods, patches and shared-module ownership are investigation leads, not causal evidence.");
        }
    }
    private static bool Overlaps(MetricPoint w, long start, long end) => w.UnixMs >= start && w.UnixMs - w.WindowSeconds * 1000 <= end;
    private static bool IsContext(string text) => new[] { "focus", "pause", "load", "vsync", "fps cap" }.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
}
