namespace Ember.Core;

// Files in a recording directory form a durable protocol shared by the client and standalone worker.
// All protocol JSON uses Json.Options, except batches.ndjson (compact JSON, one SessionBatch per line).
public sealed class SessionRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int ProcessId { get; set; }
    public DateTimeOffset ProcessStartedUtc { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string ExportDirectory { get; set; } = "";
    public int SegmentSeconds { get; set; } = 60;
    public int MaxSeconds { get; set; } = 14400;
    public int MaxDiskMegabytes { get; set; } = 2048;
    public int MaxTraceMegabytes { get; set; } = 128;
    public int SegmentSamples { get; set; } = 50000;
    public int MaxReportSamples { get; set; } = 200000;
    public int MaxReportFrames { get; set; } = 500000;
    public int KeepExports { get; set; } = 20;
    public bool OpenFolderWhenFinished { get; set; }
    public Dictionary<string, string[]> ModMap { get; set; } = [];
}

public sealed record SessionStop(string Reason, DateTimeOffset StoppedUtc, bool Cancelled = false, long DroppedBatches = 0);
public sealed record SessionResult(string Status, string? ExportPath, string? Error, DateTimeOffset CompletedUtc);
public sealed record ContextSnapshot(long UnixMs, Dictionary<string, object?> Settings, Dictionary<string, object?> Environment);
public sealed record SessionBatch(long Sequence, MetricPoint? Metric, TickRecord[] Frames, DiagnosticEvent[] Events,
    LogRecord[] Logs, Census? World, ContextSnapshot? Context = null);

public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "recording";
    public string StopReason { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? StoppedUtc { get; set; }
    public int SupervisorProcessId { get; set; }
    public DateTimeOffset? SupervisorStartedUtc { get; set; }
    public double RecordedSeconds { get; set; }
    public double StackCoverageSeconds { get; set; }
    public double StackCoveragePercent { get; set; }
    public long FramesObserved { get; set; }
    public long FramesOmitted { get; set; }
    public long DroppedBatches { get; set; }
    public int IncompleteJournalLines { get; set; }
    public bool Recovered { get; set; }
    public List<ProfileSegment> Segments { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public sealed class ProfileSegment
{
    public int Index { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset EndedUtc { get; set; }
    public string Status { get; set; } = "capturing";
    public string? FileName { get; set; }
    public long TraceBytes { get; set; }
    public long ObservedSamples { get; set; }
    public int RetainedSamples { get; set; }
    public double ParseMilliseconds { get; set; }
    public string? Error { get; set; }
}

public sealed class PerformanceIncident
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public long StartUnixMs { get; set; }
    public long EndUnixMs { get; set; }
    public long ContextStartUnixMs { get; set; }
    public long ContextEndUnixMs { get; set; }
    public double PeakFrameMs { get; set; }
    public int SlowFrames { get; set; }
    public string Confidence { get; set; } = "low";
    public double StackCoveragePercent { get; set; }
    public MetricPoint[] Windows { get; set; } = [];
    public List<ProfileSample> Samples { get; set; } = [];
    public List<Hotspot> Hotspots { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> CounterEvidence { get; set; } = [];
    public List<Finding> Hypotheses { get; set; } = [];
    public List<string> NextExperiments { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}
