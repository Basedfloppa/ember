namespace Ember.Core;

public static class ProfileMerge
{
    internal const string TruncationNotePrefix = "Known usable-prefix truncation:";
    internal static bool HasKnownTruncation(StackProfile p) => p.UsableDurationSeconds is not null ||
        p.Notes.Any(n => n.StartsWith(TruncationNotePrefix, StringComparison.Ordinal));
    internal static double EffectiveDuration(StackProfile p) => Math.Min(p.DurationSeconds, p.UsableDurationSeconds ?? p.DurationSeconds);
    private sealed record Input(ProfileSegment Segment, string Path, long Observed, int Retained, double Start, double End);

    public static StackProfile? Merge(string directory, SessionInfo session, int budget = 200000,
        List<PerformanceIncident>? incidents = null)
    {
        budget = Math.Clamp(budget, 0, 200000);
        var inputs = new List<Input>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        double origin = session.StartedUtc.ToUnixTimeMilliseconds();
        double end = origin + session.RecordedSeconds * 1000;
        foreach (var segment in session.Segments.OrderBy(s => s.Index))
        {
            if (segment.Status is not ("completed" or "complete") || segment.FileName is null)
            { session.Notes.Add($"Segment {segment.Index}: {segment.Status}; no stack coverage credited. {segment.Error}"); continue; }
            try
            {
                string path = SessionFiles.SafeSegmentPath(directory, segment.FileName);
                if (!paths.Add(path)) throw new InvalidDataException("Duplicate segment profile path; observations must not be counted twice.");
                var p = SessionFiles.Read<StackProfile>(path) ?? throw new InvalidDataException("Profile file missing.");
                Validate(p);
                int retained = p.Samples.Count(s => Valid(s, p));
                if (retained != p.Samples.Count)
                    session.Notes.Add($"Segment {segment.Index}: {p.Samples.Count - retained} invalid/out-of-profile-range stack observations rejected; this is data loss, not reservoir sampling.");
                long observed = Math.Max(retained, Math.Max(p.ObservedStackSamples, segment.ObservedSamples));
                if (p.ObservedStackSamples == 0 && segment.ObservedSamples == 0)
                    observed = Math.Max(observed, (long)p.Samples.Count + Math.Max(0, p.SamplesDroppedByLimit));
                double start = Math.Max(origin, Math.Max(segment.StartedUtc.ToUnixTimeMilliseconds(), p.StartedUtc.ToUnixTimeMilliseconds()));
                double finish = Math.Min(end, Math.Min(segment.EndedUtc.ToUnixTimeMilliseconds(), p.StartedUtc.ToUnixTimeMilliseconds() + EffectiveDuration(p) * 1000));
                if (!double.IsFinite(finish) || finish < start) finish = start;
                inputs.Add(new(segment, path, observed, retained, start, finish));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
            { session.Notes.Add($"Segment {segment.Index} rejected: {e.Message}"); }
        }
        if (inputs.Count == 0) { session.StackCoverageSeconds = 0; session.StackCoveragePercent = 0; return null; }
        long total = inputs.Sum(i => i.Observed);
        double q = total > 0 ? Math.Min(1, (double)budget / total) : 0;
        foreach (var i in inputs.Where(i => i.Observed > 0)) q = Math.Min(q, (double)i.Retained / i.Observed);
        var result = new StackProfile { StartedUtc = session.StartedUtc, DurationSeconds = session.RecordedSeconds, ObservedStackSamples = total };
        var frameIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var allocations = new Dictionary<string, (long Bytes, int Events)>();
        incidents ??= [];
        var incidentSamples = incidents.Select(_ => new[] { new Reservoir<ProfileSample>(667), new Reservoir<ProfileSample>(666), new Reservoir<ProfileSample>(667) }).ToArray();
        var validIntervals = new List<(double Start, double End)>();
        int frameLimitHits = 0;
        int Map(ProfileFrame frame)
        {
            string key = Json.Write(new { frame.Name, frame.Module, Mods = frame.Mods.Order(StringComparer.Ordinal).ToArray(), frame.Match });
            if (frameIds.TryGetValue(key, out int id)) return id;
            if (result.Frames.Count >= 199999)
            {
                frameLimitHits++;
                if (result.Frames.Count == 199999) result.Frames.Add(new("[frame identity omitted: cardinality bound]", "unknown", []));
                return 199999;
            }
            id = result.Frames.Count; result.Frames.Add(frame); frameIds.Add(key, id); return id;
        }
        foreach (var input in inputs)
        {
            try
            {
                var p = SessionFiles.Read<StackProfile>(input.Path) ?? throw new InvalidDataException("Profile disappeared during merge.");
                Validate(p);
                if (p.UsableDurationSeconds is { } usable)
                    result.Notes.Add($"{TruncationNotePrefix} segment {input.Segment.Index}, startedUtc={p.StartedUtc:O}, nominal durationSeconds={p.DurationSeconds:F3}, usableDurationSeconds={usable:F3}. Conversion/stream processing stopped at a known usable prefix; the remainder is unobserved, independently of event-loss and byte-limit counters. Coverage uses the usable endpoint, not the last reservoir-retained sample. Merged coverage is an interval union, not a single usable prefix.");
                int quota = (int)Math.Min(input.Retained, Math.Floor(input.Observed * q));
                var selected = new Reservoir<ProfileSample>(quota, input.Segment.Index + 1729);
                // Segment files are consumed one at a time; incident observations are selected before the global reservoir.
                var map = new Dictionary<int, int>();
                ProfileSample Remap(ProfileSample s)
                {
                    var stack = s.Stack.TakeLast(1024).Select(id =>
                    {
                        if (!map.TryGetValue(id, out int mapped)) map[id] = mapped = Map(p.Frames[id]);
                        return mapped;
                    }).ToArray();
                    return new((p.StartedUtc - session.StartedUtc).TotalMilliseconds + s.RelativeMs, s.ThreadId, s.Managed, stack);
                }
                foreach (var sample in p.Samples)
                {
                    if (!Valid(sample, p)) continue;
                    selected.Add(sample);
                    double unix = p.StartedUtc.ToUnixTimeMilliseconds() + sample.RelativeMs;
                    for (int j = 0; j < incidents.Count; j++)
                    {
                        var incident = incidents[j];
                        if (unix < incident.ContextStartUnixMs || unix > incident.ContextEndUnixMs) continue;
                        int phase = unix < incident.StartUnixMs ? 0 : unix > incident.EndUnixMs ? 2 : 1;
                        incidentSamples[j][phase].Add(Remap(sample));
                    }
                }
                result.Samples.AddRange(selected.Items.Select(Remap));
                validIntervals.Add((input.Start, input.End));
                result.EventsLost = Add(result.EventsLost, p.EventsLost);
                result.SamplesWithoutStack = Add(result.SamplesWithoutStack, p.SamplesWithoutStack);
                result.TraceBytes += p.TraceBytes;
                result.SizeLimitReached |= p.SizeLimitReached;
                result.FramesWithoutModule = Add(result.FramesWithoutModule, p.FramesWithoutModule);
                result.FramesRecoveredFromRawTrace = Add(result.FramesRecoveredFromRawTrace, p.FramesRecoveredFromRawTrace);
                result.FramesMatchedByTypeName = Add(result.FramesMatchedByTypeName, p.FramesMatchedByTypeName);
                result.ContentionEvents = Add(result.ContentionEvents, p.ContentionEvents);
                result.ContentionTotalMs += p.ContentionTotalMs;
                foreach (var a in p.Allocations)
                {
                    string key = allocations.ContainsKey(a.Type) || allocations.Count < 20000 ? a.Type : "[other allocation types: bound]";
                    allocations.TryGetValue(key, out var prev);
                    allocations[key] = (prev.Bytes + a.SampledBytes, Add(prev.Events, a.Events));
                }
                foreach (var e in p.ExceptionTypes)
                {
                    string key = result.ExceptionTypes.ContainsKey(e.Key) || result.ExceptionTypes.Count < 20000 ? e.Key : "[other exception types: bound]";
                    result.ExceptionTypes.TryGetValue(key, out int prev); result.ExceptionTypes[key] = Add(prev, e.Value);
                }
                foreach (string note in p.Notes.Take(10))
                    if (result.Notes.Count < 2000) result.Notes.Add($"Segment {input.Segment.Index}: {note}");
                if (p.Samples.Any(s => s?.Stack?.Length > 1024)) result.Notes.Add($"Segment {input.Segment.Index}: stacks deeper than 1024 frames retain their leaf-side 1024 frames.");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            { result.Notes.Add($"Segment {input.Segment.Index} became unreadable during merge: {e.Message}; its quota is missing."); }
        }
        result.Samples = result.Samples.OrderBy(s => s.RelativeMs).ToList();
        result.SamplesDroppedByLimit = (int)Math.Clamp(total - result.Samples.Count, 0, int.MaxValue);
        result.Hotspots = Hotspots(result.Samples);
        (result.ModSamples, result.UnattributedSelfSamples) = ProfileOwnership.Tally(result.Samples, result.Frames);
        result.Allocations = allocations.Select(a => new AllocationType(a.Key, a.Value.Bytes, a.Value.Events)).OrderByDescending(a => a.SampledBytes).ToList();
        session.StackCoverageSeconds = SessionFiles.UnionMilliseconds(validIntervals) / 1000;
        session.StackCoveragePercent = session.RecordedSeconds > 0 ? 100 * session.StackCoverageSeconds / session.RecordedSeconds : 0;
        result.Notes.Add($"Whole-session equal-probability segment sampling: q={q:G6}; observed={total}, retained={result.Samples.Count}. Each segment quota=floor(observed*q), constrained by its retained/observed ratio. Reservoir omissions are not EventPipe event loss. Flooring can omit very sparse segments.");
        result.Notes.Add($"Valid trace interval union covers {session.StackCoverageSeconds:F2}/{session.RecordedSeconds:F2}s ({session.StackCoveragePercent:F1}%). Segment gaps are unobserved; coverage is not sample density or proof of uninterrupted events.");
        if (frameLimitHits > 0) result.Notes.Add($"Frame identity bound reached ({frameLimitHits} mappings collapsed into explicitly unknown frame).");
        for (int j = 0; j < incidents.Count; j++)
        {
            var incident = incidents[j];
            incident.Samples = incidentSamples[j].SelectMany(r => r.Items).OrderBy(s => s.RelativeMs).ToList();
            incident.StackCoveragePercent = Coverage(incident, validIntervals);
            incident.Notes.Add("Incident stacks selected independently before global downsampling, with pre/during/post quotas 667/666/667. Counts are thread observations, not CPU time; compare phase rates and sample availability, not raw unequal-duration totals.");
            incident.Notes.Add("Available segment-retained pre/during/post observations: " + string.Join("/", incidentSamples[j].Select(r => r.Observed)) + "; omitted by incident quota: " + string.Join("/", incidentSamples[j].Select(r => r.Observed - r.Items.Count)) + ".");
        }
        return result;
    }

    private static void Validate(StackProfile p)
    {
        if (p.Frames is null || p.Samples is null || p.Allocations is null || p.ExceptionTypes is null || p.Notes is null ||
            !double.IsFinite(p.DurationSeconds) || p.DurationSeconds < 0 ||
            p.UsableDurationSeconds is { } usable && (!double.IsFinite(usable) || usable < 0 || usable > p.DurationSeconds) ||
            p.Frames.Any(f => f is null || f.Name is null || f.Module is null || f.Mods is null) ||
            p.Allocations.Any(a => a is null || a.Type is null))
            throw new InvalidDataException("Profile contains missing required fields or an invalid duration.");
    }
    internal static bool Valid(ProfileSample s, StackProfile p) => s is not null && double.IsFinite(s.RelativeMs) && s.RelativeMs >= 0 &&
        s.RelativeMs <= EffectiveDuration(p) * 1000 && s.Stack is { Length: > 0 } && s.Stack.All(i => i >= 0 && i < p.Frames.Count);
    internal static double Coverage(PerformanceIncident i, IEnumerable<(double Start, double End)> intervals) =>
        100 * SessionFiles.UnionMilliseconds(intervals.Select(x => (Math.Max(x.Start, i.ContextStartUnixMs), Math.Min(x.End, i.ContextEndUnixMs)))) /
        Math.Max(1, i.ContextEndUnixMs - i.ContextStartUnixMs);
    private static int Add(int a, int b) => (int)Math.Clamp((long)a + b, 0, int.MaxValue);
    public static List<Hotspot> Hotspots(IEnumerable<ProfileSample> samples)
    {
        var counts = new Dictionary<int, (int Self, int Inclusive, int Managed)>();
        foreach (var s in samples)
        {
            if (s.Stack.Length == 0) continue;
            foreach (int id in s.Stack.Distinct())
            { counts.TryGetValue(id, out var c); counts[id] = (c.Self, c.Inclusive + 1, c.Managed); }
            int leaf = s.Stack[^1]; var value = counts[leaf];
            counts[leaf] = (value.Self + 1, value.Inclusive, value.Managed + (s.Managed ? 1 : 0));
        }
        return counts.Select(c => new Hotspot(c.Key, c.Value.Self, c.Value.Inclusive, c.Value.Managed)).OrderByDescending(h => h.InclusiveSamples).ToList();
    }
}
