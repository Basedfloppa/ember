using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Ember.Core;

public sealed class Ring<T>(int capacity)
{
    private readonly T[] items = new T[Math.Max(1, capacity)];
    private int next, count;
    public long Overwritten { get; private set; }
    public void Add(T item)
    {
        items[next] = item;
        next = (next + 1) % items.Length;
        if (count == items.Length) Overwritten++; else count++;
    }
    public T[] Snapshot()
    {
        var result = new T[count];
        for (int i = 0; i < count; i++) result[i] = items[(next - count + i + items.Length) % items.Length];
        return result;
    }
}

public sealed class Collector : IDisposable
{
    private readonly object gate = new();
    private readonly EmberConfig config;
    private readonly Ring<TickRecord> ticks;
    private readonly Ring<MetricPoint> timeline;
    private readonly Ring<DiagnosticEvent> events = new(200);
    private readonly Ring<LogRecord> logs;
    private readonly List<TickRecord> pending = [];
    private readonly Dictionary<string, List<double>> stages = [];
    private readonly List<double> gpuElapsed = [];
    private readonly List<DiagnosticEvent> sessionEvents = [];
    private readonly List<LogRecord> sessionLogs = [];
    private Action<SessionBatch>? sessionSink;
    private long sessionSequence;
    private Census? journalCensus;
    private ContextSnapshot? pendingContext;
    private readonly RuntimeReader runtime = new();
    private readonly Timer timer;
    private double lastPoll = ClockMs;
    private bool disposed;
    private Census? census;
    public static double ClockMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    public static long UnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Collector(EmberConfig config)
    {
        this.config = config;
        ticks = new(config.MaxTickRecords);
        timeline = new(config.HistorySeconds);
        logs = new(config.MaxLogEntries);
        timer = new Timer(_ => Poll(), null, 1000, 1000);
    }

    public void Record(double workMs, double intervalMs)
    {
        if (!double.IsFinite(workMs) || workMs < 0 || !double.IsFinite(intervalMs) || intervalMs < 0) return;
        lock (gate)
        {
            if (disposed) return;
            var tick = new TickRecord(UnixMs, ClockMs, workMs, intervalMs);
            ticks.Add(tick);
            if (pending.Count < 10000) pending.Add(tick);
        }
    }
    public void SetCensus(Census value) { lock (gate) census = value; }
    public void SetContext(ContextSnapshot value) { lock (gate) pendingContext = value; }
    public void RecordGpu(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (gate) { if (!disposed && gpuElapsed.Count < 10000) gpuElapsed.Add(milliseconds); }
    }
    public void RecordStage(string stage, double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (gate)
        {
            if (!stages.TryGetValue(stage, out var values)) stages[stage] = values = [];
            if (values.Count < 10000) values.Add(milliseconds);
        }
    }
    public void Mark(string kind, string message)
    {
        lock (gate)
        {
            var entry = new DiagnosticEvent(UnixMs, kind, Redactor.Text(message));
            events.Add(entry);
            if (sessionSink is not null && sessionEvents.Count < 1000) sessionEvents.Add(entry);
        }
    }
    public void Log(string level, string message)
    {
        if (config.MaxLogEntries == 0) return;
        lock (gate)
        {
            var entry = new LogRecord(UnixMs, level, Redactor.Text(message));
            logs.Add(entry);
            if (sessionSink is not null && sessionLogs.Count < 1000) sessionLogs.Add(entry);
        }
    }
    /// <summary>The sink must only enqueue: it runs under the collector lock, never performs disk IO.</summary>
    public void BeginRecording(Action<SessionBatch> sink)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessionSink is not null) throw new InvalidOperationException("A recording is already attached.");
            PollLocked(true); // Close even a sub-millisecond pre-session window before changing ownership.
            sessionSequence = 0;
            journalCensus = null;
            sessionSink = sink;
            Mark("session-start", "Session recording started; runtime and frame history now journaled to disk.");
        }
    }
    public void EndRecording()
    {
        lock (gate)
        {
            if (sessionSink is null) return;
            if (!disposed) PollLocked(true);
            sessionSink = null;
            sessionEvents.Clear();
            sessionLogs.Clear();
            pendingContext = null;
        }
    }
    private void Poll()
    {
        lock (gate)
        {
            if (disposed) return;
            PollLocked();
        }
    }
    private void PollLocked(bool force = false)
    {
        try
        {
                double start = ClockMs, elapsed = (start - lastPoll) / 1000;
                if (elapsed < .001 && !force) return;
                elapsed = Math.Max(.000001, elapsed);
                lastPoll = start;
                var values = pending.ToArray();
                pending.Clear();
                var metrics = runtime.Read(elapsed);
                var point = new MetricPoint(UnixMs, start, elapsed, values.Length / Math.Max(.001, elapsed),
                    Distribution.Of(values.Select(t => t.WorkMs)), Distribution.Of(values.Where(t => t.IntervalMs > 0).Select(t => t.IntervalMs)),
                    values.Count(t => t.IntervalMs >= config.SlowTickMilliseconds), metrics,
                    census?.Players ?? 0, census?.Entities ?? 0, census?.Chunks,
                    census is null ? -1 : Math.Max(0, (UnixMs - census.UnixMs) / 1000.0), ClockMs - start,
                    stages.ToDictionary(p => p.Key, p => Distribution.Of(p.Value)),
                    gpuElapsed.Count > 0 ? Distribution.Of(gpuElapsed) : null);
                timeline.Add(point);
                foreach (var valuesList in stages.Values) valuesList.Clear();
                gpuElapsed.Clear();
                if (sessionSink is not null)
                {
                    var world = ReferenceEquals(census, journalCensus) ? null : census;
                    var batch = new SessionBatch(sessionSequence++, point, values, sessionEvents.ToArray(), sessionLogs.ToArray(), world, pendingContext);
                    sessionSink(batch);
                    journalCensus = census;
                    pendingContext = null;
                    sessionEvents.Clear();
                    sessionLogs.Clear();
                }
        }
        catch (Exception e) { events.Add(new(UnixMs, "collector-error", e.GetType().Name + ": " + Redactor.Text(e.Message))); }
    }
    public void Fill(DiagnosticReport report)
    {
        lock (gate)
        {
            long since = UnixMs - config.HistorySeconds * 1000L;
            report.Timeline = timeline.Snapshot().Where(p => p.UnixMs >= since).ToArray();
            report.Ticks = ticks.Snapshot().Where(t => t.EndUnixMs >= since).ToArray();
            report.Events = events.Snapshot();
            report.Logs = logs.Snapshot();
            report.World = census;
            report.Settings["retentionSeconds"] = config.HistorySeconds;
            report.Settings["tickRingOverwrittenSinceStart"] = ticks.Overwritten;
            report.Settings["logRingOverwrittenSinceStart"] = logs.Overwritten;
            report.SlowTickThresholdMs = config.SlowTickMilliseconds;
        }
    }
    public void Dispose()
    {
        lock (gate) { EndRecording(); disposed = true; timer.Dispose(); runtime.Dispose(); }
    }
}

public sealed class RuntimeReader : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    private TimeSpan cpu;
    private TimeSpan pause = GC.GetTotalPauseDuration();
    private long allocated = GC.GetTotalAllocatedBytes(false), completed = ThreadPool.CompletedWorkItemCount;
    private int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    public RuntimeReader() { cpu = process.TotalProcessorTime; }
    public RuntimeMetrics Read(double seconds)
    {
        process.Refresh();
        var nowCpu = process.TotalProcessorTime;
        var nowPause = GC.GetTotalPauseDuration();
        var memory = GC.GetGCMemoryInfo();
        long nowAllocated = GC.GetTotalAllocatedBytes(false), nowCompleted = ThreadPool.CompletedWorkItemCount;
        int n0 = GC.CollectionCount(0), n1 = GC.CollectionCount(1), n2 = GC.CollectionCount(2);
        double oneCore = Math.Max(0, (nowCpu - cpu).TotalSeconds / Math.Max(.001, seconds) * 100);
        var result = new RuntimeMetrics(oneCore, oneCore / Environment.ProcessorCount, process.WorkingSet64,
            process.PrivateMemorySize64, GC.GetTotalMemory(false), memory.HeapSizeBytes, memory.FragmentedBytes,
            memory.TotalAvailableMemoryBytes, Math.Max(0, nowAllocated - allocated) / Math.Max(.001, seconds),
            Math.Max(0, (nowPause - pause).TotalMilliseconds), n0 - g0, n1 - g1, n2 - g2,
            process.Threads.Count, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, nowCompleted - completed);
        cpu = nowCpu; pause = nowPause; allocated = nowAllocated; completed = nowCompleted;
        g0 = n0; g1 = n1; g2 = n2;
        return result;
    }
    public static Dictionary<string, object?> Describe(string dataDirectory)
    {
        var result = new Dictionary<string, object?>
        {
            ["os"] = RuntimeInformation.OSDescription, ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription, ["logicalProcessors"] = Environment.ProcessorCount,
            ["serverGc"] = GCSettings.IsServerGC, ["gcLatencyMode"] = GCSettings.LatencyMode.ToString(),
            ["processId"] = Environment.ProcessId, ["stopwatchFrequency"] = Stopwatch.Frequency,
            ["processUptimeSeconds"] = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
            ["dynamicCodeSupported"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
        };
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                result["cpuModel"] = key?.GetValue("ProcessorNameString")?.ToString()?.Trim();
            }
            else if (File.Exists("/proc/cpuinfo"))
                result["cpuModel"] = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2).Last().Trim();
        }
        catch { result["cpuModel"] = null; }
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataDirectory))!);
            result["dataVolumeFreeBytes"] = drive.AvailableFreeSpace;
            result["dataVolumeTotalBytes"] = drive.TotalSize;
        }
        catch { result["dataVolumeFreeBytes"] = null; }
        return result;
    }
    public void Dispose() => process.Dispose();
}
