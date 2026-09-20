namespace Ember.Core;

public static class MetricCatalog
{
    public static Dictionary<string, MetricDefinition> Create() => new()
    {
        ["timeline.loopRate"] = new("frames/s", "monotonic observation window", "Completed rendered frames divided by actual window, not 1000 / work time. Includes frame pacing; not server TPS."),
        ["timeline.workMs"] = new("ms", "Render Before (first) to Done (last)", "Render submission wall time including driver waits; excludes earlier game tick callbacks and later buffer swap/pacing. Not GPU execution time."),
        ["timeline.intervalMs"] = new("ms", "Stopwatch between completed frames", "Includes callbacks, scheduling, frame pacing and stalls. Nearest-rank percentiles; 1000/p99 is a percentile-derived low FPS, not an average of the slowest 1%."),
        ["timeline.renderStagesMs"] = new("ms", "First and last IRenderer observers per render stage", "CPU-side stage callback wall time, possibly including driver waits. Does not include engine work between stages; stages may be absent or repeated depending on settings."),
        ["timeline.gpuElapsedMs"] = new("ms", "Asynchronous GL timestamp pairs, Before to Done", "GPU-clock elapsed render-submission window, including pipeline bubbles; not GPU utilization/busy time and excludes present. Readback is nonblocking and delayed by frames, so samples belong approximately to earlier work, not the exact polling frame. Null means unavailable or no completed query."),
        ["timeline.slowTicks"] = new("frames/window", "frame interval threshold", "Number of completed frame intervals at or above slowTickThresholdMs. Legacy JSON field name; this is a client frame count, not server ticks."),
        ["timeline.runtime.cpuOneCorePercent"] = new("% of one logical CPU", "Process.TotalProcessorTime delta / monotonic elapsed", "100 means one logical CPU fully used; can exceed 100 on a multithreaded process."),
        ["timeline.runtime.cpuMachinePercent"] = new("% of available CPUs", "cpuOneCorePercent / Environment.ProcessorCount", "Normalized to processors visible to the process; not whole-system CPU and not necessarily physical-core count."),
        ["timeline.runtime.allocationBytesPerSecond"] = new("bytes/s", "GC.GetTotalAllocatedBytes(false) delta", "Process-wide managed allocations, including diagnostics overhead; not retained heap growth."),
        ["timeline.runtime.gcPauseMilliseconds"] = new("ms/window", "GC.GetTotalPauseDuration delta", "Cumulative runtime GC pauses within this observation window."),
        ["timeline.runtime.gen0/gen1/gen2"] = new("collections/window", "GC.CollectionCount delta", "Process-wide collection counts; higher generation collections can also increment lower generations."),
        ["timeline.runtime.managedBytes"] = new("bytes", "GC.GetTotalMemory(false)", "Approximate current allocated managed memory; no forced GC."),
        ["timeline.runtime.heapBytes/fragmentedBytes"] = new("bytes", "GC.GetGCMemoryInfo", "Heap size and fragmentation at the last GC, not instantaneous live-object size."),
        ["timeline.runtime.availableMemoryBytes"] = new("bytes", "GC.GetGCMemoryInfo.TotalAvailableMemoryBytes", "Runtime memory budget (may reflect container limit); not current free RAM."),
        ["timeline.runtime.workingSetBytes/privateBytes"] = new("bytes", "System.Diagnostics.Process", "Process-resident and private memory; distinct from managed heap."),
        ["timeline.censusAgeSeconds"] = new("s", "last main-thread census UTC", "World counts are periodically refreshed and can be stale during a main-thread stall."),
        ["timeline.collectorMs"] = new("ms", "Stopwatch", "Runtime/window collection wall duration, not total profiler overhead. Census has its own collectionMs."),
        ["world.pingMs"] = new("ms", "ClientPlayer.Ping (seconds) * 1000", "Server-reported round-trip ping for this client when available. Not a direct measurement of server tick time."),
        ["world.counters"] = new("counts or bytes", "Version-specific read-only client fields", "Meshing queue lengths are point-in-time values. TCP/UDP received counters are cumulative unsigned 32-bit totals (wrap at 4 GiB); not rates. Null means unavailable. No packet contents are collected."),
        ["profile.samples"] = new("samples", "Microsoft-DotNETCore-SampleProfiler", "All-thread statistical stack observations. Managed/external is runtime classification, not busy/idle. Inclusive values overlap; do not sum them as CPU time."),
        ["profile.observedStackSamples"] = new("observations", "Resolved stack events processed before reservoir selection", "Observed within usable parsed data, not an exhaustive original-event count when conversion or stream truncation occurred. Retained sample count can be much smaller."),
        ["session.stackCoveragePercent"] = new("percent of session wall time", "Union of validated usable segment intervals", "Attachment, rundown, parsing gaps and known truncated tails receive no coverage credit. Interval coverage is not sample density, uninterrupted event delivery or CPU utilization."),
        ["incidents.samples"] = new("samples", "Independent per-incident pre/during/post reservoirs", "Selected from segment-retained samples before whole-session downsampling. These supplement, and may overlap, profile.samples; do not add their counts to the global sample total."),
        ["profile.allocations"] = new("sampled bytes", "GC/AllocationTick AllocationAmount64", "Approximate allocation volume by sampled type. Not object counts, retained memory, or a heap dump."),
        ["profile.contentionTotalMs"] = new("ms across threads", "Contention/Stop DurationNs", "Managed lock contention duration. Concurrent waits overlap and can exceed capture wall time.")
    };
}
