using System.Text.Json;
using Ember.Core;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;

namespace Ember.Trace;

internal static class ProfileParser
{
    private const int MaxFrames = 100_000;
    private const int MaxStackDepth = 512;
    private const int MaxStackEntries = 8_000_000;
    private const int MaxTypes = 20_000;

    public static void Parse(string directory)
    {
        var info = JsonSerializer.Deserialize<CaptureInfo>(File.ReadAllText(Path.Combine(directory, "capture.json")), Json.Options)
            ?? throw new InvalidDataException("Missing capture metadata.");
        var map = new Dictionary<string, string[]>(info.Map, StringComparer.OrdinalIgnoreCase);
        int maxSamples = Math.Clamp(info.MaxSamples, 1, 200_000);
        bool conversionLimited = false;
        int lost = 0;
        string etlx = Path.Combine(directory, "capture.etlx");
        TraceLog.CreateFromEventPipeDataFile(Path.Combine(directory, "capture.nettrace"), etlx, new TraceLogOptions
        {
            ContinueOnError = info.Truncated,
            // Private checkpoint override supports real parser regression tests; production keeps
            // the two-million-event default, and no checkpoint may raise that hard bound.
            MaxEventCount = Math.Clamp(info.MaxConversionEvents, 1, 2_000_000),
            ConversionLog = TextWriter.Null,
            LocalSymbolsOnly = true,
            ShouldResolveSymbols = _ => false,
            OnLostEvents = (truncated, eventsLost, _) => { conversionLimited |= truncated; lost = Math.Max(lost, eventsLost); }
        });
        using var log = new TraceLog(etlx);
        var started = new DateTimeOffset(log.SessionStartTime.ToUniversalTime());
        double duration = Math.Max(0, (info.EndedUtc - started).TotalSeconds);
        // Trace session timestamps, rather than process uptime or the first sample, are the origin.
        var profile = new StackProfile
        {
            StartedUtc = started,
            DurationSeconds = duration,
            TraceBytes = info.TraceBytes,
            SizeLimitReached = info.SizeLimitReached,
            EventsLost = Math.Max(lost, log.EventsLost)
        };
        profile.Notes.Add("Thread samples are statistical observations of managed/external execution, not exact on-CPU time; external samples may include waiting threads.");
        profile.Notes.Add("AllocationTick bytes are sampled allocation amounts attributed to the tick's type, not an exact per-type allocation census. Contention time sums completed waits and may overlap across threads.");
        if (info.SizeLimitReached) profile.Notes.Add("The trace byte budget triggered an early stop or hard-limit truncation; soft-limit stops reserve space for CLR rundown.");
        if (info.Truncated) profile.Notes.Add("The hard trace byte cap truncated the stream; stacks, symbols and aggregates may be incomplete.");
        if (conversionLimited) profile.Notes.Add("TraceLog's event conversion limit was reached; aggregates describe only retained events.");
        var frames = new Dictionary<(string Module, string Name), int>();
        var counts = new List<int[]>();
        var allocations = new Dictionary<string, (long Bytes, int Events)>(StringComparer.Ordinal);
        int entries = 0;
        int seenStackSamples = 0;
        double lastResolvedStackMs = 0;
        var reservoirRandom = new Random(0xE8B3);
        int unclassified = 0;
        bool depthLimited = false, frameLimited = false, typesLimited = false;
        using var source = log.Events.GetSource();
        var samples = new SampleProfilerTraceEventParser(source);
        samples.ThreadSample += data =>
        {
            if (!InCapture(data.ProcessID, data.TimeStampRelativeMSec)) return;
            if (data.Type is not (ClrThreadSampleType.Managed or ClrThreadSampleType.External)) { unclassified++; return; }
            var stack = data.CallStack();
            if (stack is null) { profile.SamplesWithoutStack++; return; }
            // Observe every resolved event before any reservoir/frame/stack-entry rejection.
            // A retained reservoir's last sample is not the end of the processed trace prefix.
            lastResolvedStackMs = Math.Max(lastResolvedStackMs, data.TimeStampRelativeMSec);
            seenStackSamples++;
            // Reservoir sampling keeps the entire capture represented when many idle threads
            // produce more observations than the output can retain, rather than losing its tail.
            int slot = profile.Samples.Count < maxSamples ? profile.Samples.Count : reservoirRandom.Next(seenStackSamples);
            if (slot >= maxSamples) { profile.SamplesDroppedByLimit++; return; }
            ProfileSample? replaced = slot < profile.Samples.Count ? profile.Samples[slot] : null;
            // TraceCallStack starts at the leaf; its Caller chain runs toward the root.
            var ids = new List<int>(Math.Min(stack.Depth, MaxStackDepth));
            for (; stack is not null && ids.Count < MaxStackDepth; stack = stack.Caller)
            {
                var address = stack.CodeAddress;
                string module = Options.ModuleKey(address.ModuleFilePath ?? address.ModuleName ?? "unknown");
                if (string.IsNullOrEmpty(module)) module = "unknown";
                string name = address.FullMethodName;
                if (string.IsNullOrEmpty(name)) name = $"{module}!0x{address.Address:x}";
                if (!frames.TryGetValue((module, name), out int id))
                {
                    if (frames.Count >= MaxFrames) { frameLimited = true; profile.SamplesDroppedByLimit++; return; }
                    id = profile.Frames.Count;
                    frames.Add((module, name), id);
                    profile.Frames.Add(new(name, module, map.GetValueOrDefault(module) ?? []));
                    counts.Add(new int[3]);
                }
                ids.Add(id);
            }
            if (stack is not null) depthLimited = true;
            if (entries + ids.Count - (replaced?.Stack.Length ?? 0) > MaxStackEntries) { profile.SamplesDroppedByLimit++; return; }
            ids.Reverse();
            bool managed = data.Type == ClrThreadSampleType.Managed;
            int[] rootFirst = ids.ToArray();
            var sample = new ProfileSample(data.TimeStampRelativeMSec, data.ThreadID, managed, rootFirst);
            if (replaced is not null)
            {
                profile.SamplesDroppedByLimit++;
                entries -= replaced.Stack.Length;
                counts[replaced.Stack[^1]][0]--;
                if (replaced.Managed) counts[replaced.Stack[^1]][2]--;
                foreach (int id in replaced.Stack.Distinct()) counts[id][1]--;
                profile.Samples[slot] = sample;
            }
            else profile.Samples.Add(sample);
            entries += ids.Count;
            int leaf = rootFirst[^1];
            counts[leaf][0]++;
            if (managed) counts[leaf][2]++;
            // Recursion counts a frame only once inclusively in each sample.
            foreach (int id in rootFirst.Distinct()) counts[id][1]++;
        };
        source.Clr.GCAllocationTick += data =>
        {
            if (!InCapture(data.ProcessID, data.TimeStampRelativeMSec)) return;
            string type = string.IsNullOrWhiteSpace(data.TypeName) ? "[unknown allocation type]" : data.TypeName;
            type = BoundedType(allocations.ContainsKey(type), allocations.Count, type);
            var previous = allocations.GetValueOrDefault(type);
            long bytes = Math.Max(0, data.AllocationAmount64 != 0 ? data.AllocationAmount64 : data.AllocationAmount);
            allocations[type] = (previous.Bytes > long.MaxValue - bytes ? long.MaxValue : previous.Bytes + bytes, previous.Events + 1);
        };
        source.Clr.ExceptionStart += data =>
        {
            if (!InCapture(data.ProcessID, data.TimeStampRelativeMSec)) return;
            string type = string.IsNullOrWhiteSpace(data.ExceptionType) ? "[unknown exception type]" : data.ExceptionType;
            type = BoundedType(profile.ExceptionTypes.ContainsKey(type), profile.ExceptionTypes.Count, type);
            profile.ExceptionTypes[type] = profile.ExceptionTypes.GetValueOrDefault(type) + 1;
        };
        source.Clr.ContentionStop += data =>
        {
            if (!InCapture(data.ProcessID, data.TimeStampRelativeMSec)) return;
            profile.ContentionEvents++;
            if (double.IsFinite(data.DurationNs) && data.DurationNs >= 0) profile.ContentionTotalMs += data.DurationNs / 1_000_000;
        };
        source.Process();
        if (conversionLimited || info.Truncated)
        {
            profile.UsableDurationSeconds = Math.Clamp(lastResolvedStackMs / 1000, 0, duration);
            profile.Notes.Add($"Nominal capture duration is {duration:F3}s; usable stack coverage is limited to the first {profile.UsableDurationSeconds.Value:F3}s, ending at the last actually processed resolved stack event before reservoir selection. The remaining tail is unobserved because conversion was limited or the raw trace was truncated; zero resolved events give zero usable coverage.");
        }
        profile.ObservedStackSamples = seenStackSamples;
        profile.Samples.Sort((a, b) => a.RelativeMs.CompareTo(b.RelativeMs));
        for (int i = 0; i < counts.Count; i++)
            if (counts[i][1] != 0) profile.Hotspots.Add(new(i, counts[i][0], counts[i][1], counts[i][2]));
        profile.Hotspots = profile.Hotspots.OrderByDescending(x => x.InclusiveSamples).ThenByDescending(x => x.SelfSamples).ToList();
        profile.Allocations = allocations.Select(x => new AllocationType(x.Key, x.Value.Bytes, x.Value.Events)).OrderByDescending(x => x.SampledBytes).ToList();
        if (seenStackSamples > maxSamples) profile.Notes.Add($"A deterministic uniform reservoir retains up to {maxSamples:N0} stack observations across the entire capture, then restores chronological order. Hotspots describe retained observations, not every original sample.");
        if (profile.SamplesDroppedByLimit > 0) profile.Notes.Add("Samples were omitted by reservoir downsampling or the 8,000,000-stack-entry / 100,000-frame processing limits.");
        if (depthLimited) profile.Notes.Add("Stacks deeper than 512 frames retain the leaf-most 512 frames, in root-first order.");
        if (frameLimited) profile.Notes.Add("The unique frame limit was reached.");
        if (typesLimited) profile.Notes.Add("Type cardinality exceeded 20,000; additional types are aggregated as [other types: limit].");
        if (unclassified > 0) profile.Notes.Add($"Omitted {unclassified} profiler samples with an unknown/error execution classification.");
        using var output = new FileStream(Path.Combine(directory, "profile.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(output, profile, Json.Options);

        bool InCapture(int pid, double relativeMs) => pid == info.Pid && relativeMs >= 0 && relativeMs <= duration * 1000;
        string BoundedType(bool exists, int count, string type)
        {
            if (exists || count < MaxTypes) return type;
            typesLimited = true;
            return "[other types: limit]";
        }
    }
}
