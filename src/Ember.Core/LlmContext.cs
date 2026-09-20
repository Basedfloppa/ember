namespace Ember.Core;

/// <summary>A context-sized index into the full evidence, without duplicating hundreds of thousands of stacks.</summary>
public static class LlmContext
{
    public static object Create(DiagnosticReport r)
    {
        var selected = r.Timeline.OrderByDescending(p => p.IntervalMs.Max).Take(15)
            .Concat(r.Timeline.OrderByDescending(p => p.Runtime.GcPauseMilliseconds).Take(10))
            .Concat(r.Timeline.OrderByDescending(p => p.Runtime.AllocationBytesPerSecond).Take(5))
            .Concat(r.Timeline.OrderByDescending(p => p.Runtime.CpuOneCorePercent).Take(5))
            .Concat(r.Timeline.Take(3)).Concat(r.Timeline.TakeLast(3))
            .DistinctBy(p => p.UnixMs).OrderBy(p => p.UnixMs).ToArray();
        var profile = r.Profile;
        object? profileContext = profile is null ? null : new
        {
            profile.Kind, profile.StartedUtc, profile.DurationSeconds, profile.UsableDurationSeconds, profile.EventsLost,
            knownUsablePrefixTruncation = ProfileMerge.HasKnownTruncation(profile),
            profile.SamplesWithoutStack, profile.SamplesDroppedByLimit, profile.TraceBytes, profile.SizeLimitReached, profile.ObservedStackSamples,
            retainedSamples = profile.Samples.Count,
            managedSamples = profile.Samples.Count(s => s.Managed),
            threads = profile.Samples.GroupBy(s => s.ThreadId).Select(g => new
            {
                threadId = g.Key, samples = g.Count(), managedSamples = g.Count(s => s.Managed)
            }).OrderByDescending(t => t.managedSamples).Take(50),
            hotspots = profile.Hotspots.OrderByDescending(h => h.ManagedSelfSamples).Take(100)
                .Concat(profile.Hotspots.OrderByDescending(h => h.InclusiveSamples).Take(50)).DistinctBy(h => h.Frame)
                .Select(h => new { frame = profile.Frames[h.Frame], h.SelfSamples, h.InclusiveSamples, h.ManagedSelfSamples }),
            representativeManagedStacks = profile.Samples.Where(s => s.Managed)
                .GroupBy(s => s.Stack, StackComparer.Instance)
                .OrderByDescending(g => g.Count()).Take(20)
                .Select(g => new { observations = g.Count(), rootFirstFrames = g.First().Stack.TakeLast(48).Select(i => profile.Frames[i]),
                    omittedRootFrames = Math.Max(0, g.First().Stack.Length - 48) }),
            allocations = profile.Allocations.Take(40), profile.ExceptionTypes, profile.ContentionEvents,
            profile.ContentionTotalMs, profile.Notes
        };
        return new
        {
            r.SchemaVersion, r.Producer, r.Id, r.CreatedUtc, r.Side, r.GameVersion, r.Reason,
            scope = "Compact index into report.json. Selected windows and top-N lists are intentionally incomplete; omitted data are not zero.",
            r.Environment, r.Settings, r.Mods,
            session = r.Session,
            contextHistory = r.ContextHistory.Take(1).Concat(r.ContextHistory.TakeLast(1))
                .Concat(r.Incidents.Select(i => r.ContextHistory.LastOrDefault(c => c.UnixMs <= i.StartUnixMs)).OfType<ContextSnapshot>()).DistinctBy(c => c.UnixMs),
            incidents = r.Incidents.Select(i => new
            {
                i.Id, i.Title, i.StartUnixMs, i.EndUnixMs, i.ContextStartUnixMs, i.ContextEndUnixMs,
                i.PeakFrameMs, i.SlowFrames, i.Confidence, i.StackCoveragePercent,
                i.Evidence, i.CounterEvidence, i.Hypotheses, i.NextExperiments, i.Notes,
                windows = i.Windows.Take(3).Concat(i.Windows.OrderByDescending(w => w.IntervalMs.Max).Take(5)).Concat(i.Windows.TakeLast(3)).DistinctBy(w => w.UnixMs).OrderBy(w => w.UnixMs),
                retainedIncidentSamples = i.Samples.Count,
                representativeStacks = profile is null ? null : i.Samples.GroupBy(s =>
                    profile.StartedUtc.ToUnixTimeMilliseconds() + s.RelativeMs < i.StartUnixMs ? "before" :
                    profile.StartedUtc.ToUnixTimeMilliseconds() + s.RelativeMs > i.EndUnixMs ? "after" : "during")
                    .Select(g => new { phase = g.Key, retainedObservations = g.Count(), stacks = g.GroupBy(s => s.ThreadId).SelectMany(t => t.Take(2)).Take(12).Select(s => new
                    { s.RelativeMs, s.ThreadId, s.Managed, rootFirstFrames = s.Stack.TakeLast(32).Select(id => new { id, frame = profile.Frames[id] }) }) })
            }),
            modConfigFilesIncluded = r.ModConfigs.Keys,
            world = r.World is null ? null : new
            {
                r.World.UnixMs, r.World.Players, r.World.Entities, r.World.Chunks,
                entityTypes = r.World.EntityTypes.OrderByDescending(p => p.Value).Take(100).ToDictionary(p => p.Key, p => p.Value),
                r.World.EntityDomains, r.World.EntitiesExamined, r.World.Truncated, r.World.PingMs, r.World.CollectionMs, r.World.Counters
            },
            retainedWindowCount = r.Timeline.Length,
            runtimeWindowSeconds = r.Timeline.Sum(t => t.WindowSeconds),
            retainedFrameCount = r.Ticks.Length,
            frameDistributionScope = r.Session?.FramesOmitted > 0 ? "Uniform frame reservoir estimates, not exact session percentiles; incidents discovered before sampling." : "All retained frames; consult capture scope for omissions.",
            frameIntervalMs = Distribution.Of(r.Ticks.Where(t => t.IntervalMs > 0).Select(t => t.IntervalMs)),
            renderSubmissionMs = Distribution.Of(r.Ticks.Select(t => t.WorkMs)),
            selectedWindowRule = "Union of top 15 max frame intervals, top 10 GC-pause windows, top 5 allocation/CPU windows, first/last 3 windows; chronological order, no resampling.",
            selectedWindows = selected,
            patchMethodCount = r.Patches.Count,
            patchOwners = r.Patches.SelectMany(p => p.Owners).GroupBy(x => x).Select(g => new { owner = g.Key, patchedMethods = g.Count() }).OrderByDescending(x => x.patchedMethods),
            recentLogs = r.Logs.TakeLast(50), recentEvents = r.Events.TakeLast(100),
            profile = profileContext, r.Findings, r.Limitations, r.Metrics,
            fullEvidence = new { file = "report.json", fields = new[] { "timeline", "ticks", "profile.samples", "profile.frames", "patches", "assemblies", "modConfigs", "logs", "world" } }
        };
    }

    private sealed class StackComparer : IEqualityComparer<int[]>
    {
        public static readonly StackComparer Instance = new();
        public bool Equals(int[]? x, int[]? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));
        public int GetHashCode(int[] value)
        {
            var hash = new HashCode();
            foreach (int frame in value) hash.Add(frame);
            return hash.ToHashCode();
        }
    }
}
