using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ember.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public sealed class EmberConfig
{
    public int HistorySeconds { get; set; } = 900;
    public int MaxTickRecords { get; set; } = 120000;
    public int CensusIntervalSeconds { get; set; } = 10;
    public int MaxCensusEntities { get; set; } = 50000;
    public int MaxLogEntries { get; set; } = 200;
    public int DefaultProfileSeconds { get; set; } = 30;
    public int MaxTraceMegabytes { get; set; } = 256;
    public double SlowTickMilliseconds { get; set; } = 50;
    public int KeepExports { get; set; } = 20;
    public bool IncludeModConfigs { get; set; } = false;
    public bool HashModFiles { get; set; } = true;
    public int SessionSegmentSeconds { get; set; } = 60;
    public int SessionMaxSeconds { get; set; } = 14400;
    public int SessionDiskBudgetMegabytes { get; set; } = 2048;
    public int SessionSegmentSamples { get; set; } = 50000;
    public bool EnableGpuTiming { get; set; } = true;
    public bool OpenFolderWhenFinished { get; set; } = true;
    // Heap dumps are never automatic: this only allows or refuses an explicit .ember heapdump.
    public bool EnableHeapDump { get; set; } = true;
    public int HeapDumpTimeoutSeconds { get; set; } = 60;
    public int HeapDumpBufferMegabytes { get; set; } = 4096;
    public int HeapDumpMaxNodes { get; set; } = 40_000_000;
    public bool HeapDumpSampledAllocations { get; set; } = true;
    // Heap dumps are never automatic: this only allows or refuses an explicit .ember heapdump.
    public void Normalize()
    {
        HistorySeconds = Math.Clamp(HistorySeconds, 30, 3600);
        MaxTickRecords = Math.Clamp(MaxTickRecords, 1000, 500000);
        CensusIntervalSeconds = Math.Clamp(CensusIntervalSeconds, 5, 120);
        MaxCensusEntities = Math.Clamp(MaxCensusEntities, 100, 200000);
        MaxLogEntries = Math.Clamp(MaxLogEntries, 0, 2000);
        DefaultProfileSeconds = Math.Clamp(DefaultProfileSeconds, 5, 300);
        MaxTraceMegabytes = Math.Clamp(MaxTraceMegabytes, 16, 1024);
        SlowTickMilliseconds = double.IsFinite(SlowTickMilliseconds) ? Math.Clamp(SlowTickMilliseconds, 1, 10000) : 50;
        KeepExports = Math.Clamp(KeepExports, 1, 100);
        SessionSegmentSeconds = Math.Clamp(SessionSegmentSeconds, 10, 120);
        SessionMaxSeconds = Math.Clamp(SessionMaxSeconds, 60, 28800);
        SessionDiskBudgetMegabytes = Math.Clamp(SessionDiskBudgetMegabytes, 128, 8192);
        SessionSegmentSamples = Math.Clamp(SessionSegmentSamples, 10000, 200000);
        HeapDumpTimeoutSeconds = Math.Clamp(HeapDumpTimeoutSeconds, 10, 300);
        // The circular buffer has to absorb the whole dump burst, because the runtime outruns the
        // trace consumer at the start. Too small a buffer overwrites the oldest events of the walk,
        // which costs the roots and makes most of the heap read as unreachable; completeness is
        // verified after every dump, but the buffer is the first line of defence. The walk needs
        // roughly as many bytes as the heap it walks (measured: 1.05 GiB of trace for a 1.43 GiB
        // client heap, 0.86 GiB for a 0.74 GiB synthetic one), so the default leaves room for a
        // modded client, and the ceiling bounds how much memory a dump may add to the game. The
        // ceiling costs nothing until the walk fills it: the runtime allocates ring buffers as the
        // burst arrives (measured: identical target RSS at 1024, 2048 and 4096 MiB caps).
        HeapDumpBufferMegabytes = Math.Clamp(HeapDumpBufferMegabytes, 64, 8192);
        HeapDumpMaxNodes = Math.Clamp(HeapDumpMaxNodes, 1_000_000, 100_000_000);
    }
}

public sealed record TickRecord(long EndUnixMs, double EndMonotonicMs, double WorkMs, double IntervalMs);
public sealed record Distribution(int Count, double Mean, double P50, double P95, double P99, double Max)
{
    public static Distribution Of(IEnumerable<double> source)
    {
        var sorted = source.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return new(0, 0, 0, 0, 0, 0);
        double P(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(sorted.Length, sorted.Average(), P(.5), P(.95), P(.99), sorted[^1]);
    }
}
public sealed record RuntimeMetrics(double CpuOneCorePercent, double CpuMachinePercent, long WorkingSetBytes,
    long PrivateBytes, long ManagedBytes, long HeapBytes, long FragmentedBytes, long AvailableMemoryBytes,
    double AllocationBytesPerSecond, double GcPauseMilliseconds, int Gen0, int Gen1, int Gen2,
    int OsThreads, int ThreadPoolThreads, long ThreadPoolPending, long ThreadPoolCompleted);
public sealed record MetricPoint(long UnixMs, double MonotonicMs, double WindowSeconds, double LoopRate,
    Distribution WorkMs, Distribution IntervalMs, int SlowTicks, RuntimeMetrics Runtime,
    int Players, int Entities, int? Chunks, double CensusAgeSeconds, double CollectorMs,
    Dictionary<string, Distribution> RenderStagesMs, Distribution? GpuElapsedMs = null);
public sealed record DiagnosticEvent(long UnixMs, string Kind, string Message);
public sealed record LogRecord(long UnixMs, string Level, string Message);
public sealed record ModInfoRecord(string Id, string Name, string Version, string Side, string SourceType,
    string FileName, string[] Systems, string[] Assemblies, Dictionary<string, string> Dependencies,
    string? Sha256 = null, bool? Enabled = null);
public sealed record AssemblyRecord(string Name, string Version, string ModuleId, string[] Mods);
public sealed record PatchRecord(string Method, string[] Owners, string[] Prefixes, string[] Postfixes, string[] Transpilers, string[] Finalizers);
public sealed record Census(long UnixMs, int Players, int Entities, int? Chunks,
    Dictionary<string, int> EntityTypes, Dictionary<string, int> EntityDomains, int EntitiesExamined,
    bool Truncated, Distribution PingMs, double CollectionMs, Dictionary<string, double?>? Counters = null);
public sealed record Finding(string Severity, string Title, string Evidence, string NextStep);
public sealed record MetricDefinition(string Unit, string Source, string Meaning);

public sealed class DiagnosticReport
{
    public string SchemaVersion { get; set; } = "1.2.0";
    public string Producer { get; set; } = "Ember Diagnostics 1.2.0";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Side { get; set; } = "client";
    public string GameVersion { get; set; } = "unknown";
    public double TargetIntervalMs { get; set; }
    public double SlowTickThresholdMs { get; set; }
    public string Reason { get; set; } = "manual";
    public Dictionary<string, object?> Environment { get; set; } = [];
    public Dictionary<string, object?> Settings { get; set; } = [];
    public Dictionary<string, object?> ModConfigs { get; set; } = [];
    public List<ModInfoRecord> Mods { get; set; } = [];
    public List<AssemblyRecord> Assemblies { get; set; } = [];
    public List<PatchRecord> Patches { get; set; } = [];
    public Census? World { get; set; }
    public MetricPoint[] Timeline { get; set; } = [];
    public TickRecord[] Ticks { get; set; } = [];
    public LogRecord[] Logs { get; set; } = [];
    public DiagnosticEvent[] Events { get; set; } = [];
    public StackProfile? Profile { get; set; }
    public SessionInfo? Session { get; set; }
    // One entry per .ember heapdump request, in request order. Empty unless the player asked.
    public List<HeapDump> HeapDumps { get; set; } = [];
    public List<PerformanceIncident> Incidents { get; set; } = [];
    public List<ContextSnapshot> ContextHistory { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public Dictionary<string, MetricDefinition> Metrics { get; set; } = MetricCatalog.Create();
}

// Match is set only when the mod came from something other than a module the trace named; see
// ProfileOwnership. Null therefore means "the runtime said which module this is".
public sealed record ProfileFrame(string Name, string Module, string[] Mods, string? Match = null);
public sealed record ProfileSample(double RelativeMs, int ThreadId, bool Managed, int[] Stack);
public sealed record Hotspot(int Frame, int SelfSamples, int InclusiveSamples, int ManagedSelfSamples);
public sealed record ModSample(string Mod, int SelfSamples, int InclusiveSamples, int ManagedSelfSamples);
public sealed record AllocationType(string Type, long SampledBytes, int Events);
public sealed class StackProfile
{
    public string Kind { get; set; } = "EventPipe sampled thread stacks (not exact on-CPU time)";
    public DateTimeOffset StartedUtc { get; set; }
    public double DurationSeconds { get; set; }
    public int EventsLost { get; set; }
    public int SamplesWithoutStack { get; set; }
    public int SamplesDroppedByLimit { get; set; }
    public long TraceBytes { get; set; }
    public bool SizeLimitReached { get; set; }
    public long ObservedStackSamples { get; set; }
    // Null for a complete trace. Known conversion/stream truncation can shorten the usable
    // prefix without changing the actual capture duration or the timestamp origin.
    public double? UsableDurationSeconds { get; set; }
    public List<ProfileFrame> Frames { get; set; } = [];
    public List<ProfileSample> Samples { get; set; } = [];
    public List<Hotspot> Hotspots { get; set; } = [];
    public List<ModSample> ModSamples { get; set; } = [];
    // Samples whose leaf frame belongs to no known mod. Kept as the denominator of every mod share:
    // an unowned leaf is framework, vanilla or unidentified code, not a mod, and not "other".
    public int UnattributedSelfSamples { get; set; }
    // How frame ownership was learned, so a report can state its own quality instead of implying that
    // an empty mod list means "no mod was executing". Counts are distinct frames, not samples.
    public int FramesWithoutModule { get; set; }
    public int FramesRecoveredFromRawTrace { get; set; }
    public int FramesMatchedByTypeName { get; set; }
    public List<AllocationType> Allocations { get; set; } = [];
    public Dictionary<string, int> ExceptionTypes { get; set; } = [];
    public int ContentionEvents { get; set; }
    public double ContentionTotalMs { get; set; }
    public List<string> Notes { get; set; } = [];
}
