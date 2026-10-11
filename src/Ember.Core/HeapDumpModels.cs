namespace Ember.Core;

// The EventPipe ring buffer is the first thing a heap walk can outgrow. It holds what the dump
// worker has not read yet and discards the oldest events when it fills; a heap walk emits the roots
// and the objects they point at first, so a full buffer costs exactly the part that makes the rest
// readable. Measured against one target and one 735 MiB heap, changing only the cap: a trace at 84%
// of the cap kept every event and the snapshot was complete, and traces at 98% of the cap lost the
// beginning of the walk and reported 100% of the heap as unreachable. The real client run that
// motivated this sat at 102% of a 1024 MiB cap and reported 62% unreachable. The separator is
// therefore drawn between the measured clean and lost points, and a trace at or above it is
// reported as a lower bound rather than as a heap.
public static class HeapDumpBuffer
{
    public const int SaturatedPercent = 90;

    public static bool Saturated(long traceBytes, int bufferMegabytes) =>
        traceBytes > 0 && bufferMegabytes > 0 &&
        traceBytes * 100 >= (long)bufferMegabytes * 1048576L * SaturatedPercent;

    // Why a dump stopped before it could be analysed, in the terms the reader has to act on. Two
    // independent limits can stop one dump - the capture's buffer and the analysis node budget - and
    // the first real client failure hit both at once while the text named only the node limit, which
    // is the wrong fix for the other one: a full buffer discards the walk's oldest events, and those
    // are the roots, so no node budget makes that trace readable.
    public static string FailureMessage(long traceBytes, int bufferMegabytes, int maxNodes)
    {
        string nodes = $"The trace also carried more than {maxNodes:N0} walked objects, which is this build's analysis budget for one dump; a repeated walk counts every copy.";
        if (Saturated(traceBytes, bufferMegabytes))
            return $"The recording reached {traceBytes / 1048576.0:F1} MiB against a {bufferMegabytes} MiB EventPipe buffer, so the buffer filled while the walk was emitted and discarded its oldest events. A heap walk emits the roots and the objects they point at first, so what survives has no strong root for most of its objects, and raising HeapDumpMaxNodes alone cannot repair that. Raise HeapDumpBufferMegabytes above {Math.Ceiling(traceBytes / 1048576.0):F0} and repeat the dump. {nodes}";
        return $"The trace carried more than {maxNodes:N0} walked objects, which is this build's analysis budget; nothing was analysed. {nodes} Raise HeapDumpMaxNodes to cover it, and repeat the dump while no new stack segment attaches: it is a new attach, not another collection, that adds a whole walk.";
    }

    // The note a dump that still produced an aggregate carries when its trace filled the buffer. It
    // is the same reading as the failure message above for the case where enough of the walk arrived
    // to analyse; HeapDumpReport classifies a dump by this text, so both live here.
    public static string SaturationNote(long traceBytes, int bufferMegabytes) =>
        $"The recorded trace is {traceBytes / 1048576.0:F1} MiB against a {bufferMegabytes} MiB EventPipe buffer, so the buffer filled while the walk was being emitted and discarded its oldest events. A heap walk emits the roots and the objects they point at first, so that is what a full buffer costs: the objects that survive keep no strong root in the trace. Every number below is a lower bound. Raise HeapDumpBufferMegabytes above {Math.Ceiling(traceBytes / 1048576.0):F0} and repeat the dump.";
}

// Why a dump stopped before it produced a heap, as a value rather than only as prose. The chat line,
// the exported summary and the findings list have to name the same cause, and the client failures on
// record stopped for different reasons - one filled its buffer and hit the object budget at once,
// another ran out of time with nothing recorded at all. Storing the kind on the dump means a reader
// of report.json does not have to match message text to find out which limit was the real one.
public static class HeapDumpFailure
{
    public const string Buffer = "buffer";
    public const string NodeLimit = "node-limit";
    public const string TimeBudget = "time-budget";
    public const string NoWalk = "no-walk";
    public const string Interrupted = "interrupted";
    public const string SupervisorGone = "supervisor-gone";
    public const string NotStarted = "not-started";
    public const string Refused = "refused";
}

// One dump pays for three things in a row - reading the walk out of the runtime, waiting for the
// EventPipe session to stop, and reducing the trace to an aggregate - and each is bounded here so the
// budgets cannot drift apart again. On the first real client failure they had: the worker's own
// ceiling sat fifteen seconds under the supervisor's kill, the reduction could not be cancelled at
// all, and a dump that overran was killed with nothing recorded. The supervisor's kill is now a
// backstop behind the worker's own deadlines rather than the deadline itself, and the reduction has a
// budget of its own instead of living in the gap between the other two.
public static class HeapDumpBudget
{
    // The capture loop stops the session at TimeoutSeconds and keeps draining until TimeoutSeconds+10.
    public const int CaptureGraceSeconds = 10;
    // What the capture waits for when it is not waiting for data: the session teardown.
    public const int StopGraceSeconds = 20;
    // The reduction reads a trace as large as the heap and then runs the dominator pass over it.
    // Measured on the real client: 1.36 GiB of trace reduced in 11.6 s, about 8.5 s per GiB. The floor
    // below therefore covers a trace that filled the default 4096 MiB buffer with room to spare, and
    // the budget follows the dump timeout up to a ceiling that covers the largest ring this build
    // accepts (8192 MiB) rather than merely the default one.
    public const int MinimumAnalysisSeconds = 45;
    public const int MaximumAnalysisSeconds = 120;
    // The worker writes its aggregate after its last deadline fires; the supervisor's kill waits for
    // that instead of racing it.
    public const int PublishGraceSeconds = 15;

    // The two stages a deadline can interrupt, named as values so the message, the advice and the
    // classification are chosen by identity rather than by matching prose.
    public const string CaptureStage = "reading the walk out of the runtime";
    public const string AnalysisStage = "reducing the walk to an aggregate";

    /// <summary>Hardest the capture can take: the loop's own stop plus the bounded session teardown.</summary>
    public static int CaptureSeconds(int timeoutSeconds) => timeoutSeconds + CaptureGraceSeconds + StopGraceSeconds;

    /// <summary>
    /// What the reduction is allowed once it starts. It follows the dump's own timeout so raising
    /// that timeout really does buy more time, and it is bounded so one dump cannot hold the stack
    /// capture indefinitely.
    /// </summary>
    public static int AnalysisSeconds(int timeoutSeconds, int requestedSeconds = 0) =>
        requestedSeconds > 0 ? requestedSeconds
        : Math.Clamp(timeoutSeconds, MinimumAnalysisSeconds, MaximumAnalysisSeconds);

    /// <summary>
    /// When the worker stops itself: the capture ceiling it enforces itself plus the reduction's
    /// budget. After this the worker only writes its aggregate.
    /// </summary>
    public static int WorkerSeconds(int timeoutSeconds, int requestedAnalysisSeconds = 0) =>
        CaptureSeconds(timeoutSeconds) + AnalysisSeconds(timeoutSeconds, requestedAnalysisSeconds);

    /// <summary>When the supervisor kills a worker that did not stop itself, never before.</summary>
    public static int KillSeconds(int timeoutSeconds, int requestedAnalysisSeconds = 0) =>
        WorkerSeconds(timeoutSeconds, requestedAnalysisSeconds) + PublishGraceSeconds;

    // A dump that runs out of time is not a heap measurement, and it is not reported as one: the
    // partial walk is discarded rather than published, because an interrupted walk describes neither
    // a complete heap nor a coherent one. What it does report is everything that was measured before
    // the deadline, which is what tells a reader whether the heap or the budget was the problem.
    public static string TimeoutMessage(string stage, int timeoutSeconds, int analysisSeconds,
        long traceBytes, int bufferMegabytes, int readObjects)
    {
        string captured = traceBytes > 0
            ? $"the trace stood at {traceBytes / 1048576.0:F1} MiB" +
              (bufferMegabytes > 0 ? $" of its {bufferMegabytes} MiB buffer" : "")
            : "no trace had been written yet";
        string read = readObjects > 0 ? $", and {readObjects:N0} walked objects had already been read" : "";
        int budget = AnalysisSeconds(timeoutSeconds, analysisSeconds);
        string advice = stage == AnalysisStage
            ? "Repeat the dump when the world is quieter; raising HeapDumpTimeoutSeconds extends this budget too if it fails again."
            : "Repeat the dump when the world is quieter, or raise HeapDumpTimeoutSeconds, which extends both the capture and the reduction.";
        return $"The dump ran out of time while {stage}: {captured}{read}. One dump is allowed {timeoutSeconds} s to capture, " +
            $"{CaptureGraceSeconds} s to stop the session and {budget} s to reduce the walk, and a partial walk is not published " +
            $"as a heap because it would describe neither a complete heap nor a coherent one. {advice}";
    }
}

// The reader-facing reading of a settled dump. Kept in Core so the chat line, the exported summary
// and a test all name the same cause instead of drifting apart.
public static class HeapDumpReport
{
    /// <summary>
    /// Why a dump produced no heap, and the one thing a reader can do about it. The kind is recorded
    /// on the dump; a record written before that existed is classified from its text the same way, so
    /// an older bundle reads identically.
    /// </summary>
    public static (string Cause, string NextStep) Failure(HeapDump dump)
    {
        return Kind(dump) switch
        {
            HeapDumpFailure.Buffer => (
                "the capture filled its EventPipe buffer, which discards the oldest events of the walk first, so the roots are what it lost",
                "Raise HeapDumpBufferMegabytes above the size the failure names and repeat the dump while no other stack capture attaches."),
            HeapDumpFailure.NodeLimit => (
                "the trace carried more walked objects than this build analyses in one dump",
                "Raise HeapDumpMaxNodes, and repeat the dump while no new stack segment attaches: it is a new attach, not another collection, that adds a whole walk."),
            HeapDumpFailure.TimeBudget => (
                "the dump did not finish inside its own time budget",
                "Repeat the dump in a quieter window and let it settle; raise HeapDumpTimeoutSeconds if it fails again, which extends both the capture and the reduction."),
            HeapDumpFailure.NoWalk => (
                "the capture ended before the runtime emitted the heap walk",
                "Repeat the dump once the world has loaded and settled."),
            HeapDumpFailure.Interrupted => (
                "the dump was stopped before it could finish, rather than running to its own result",
                "Repeat the dump and let it report in chat before stopping the recording; if it was stopped for its budget, raise HeapDumpTimeoutSeconds."),
            HeapDumpFailure.SupervisorGone => (
                "the recording process exited while this dump was running",
                "Keep the game running until the dump reports its result in chat."),
            HeapDumpFailure.Refused => (
                "the request was refused before a dump was started, so nothing was measured",
                "Read the refusal, wait for the dump in progress or the attach failures to clear, then request again."),
            _ => (
                "the dump worker ended without publishing an aggregate",
                "Repeat the dump; if it fails the same way, keep the recording and its heapdumps folder for inspection."),
        };
    }

    private static string Kind(HeapDump dump)
    {
        if (!string.IsNullOrEmpty(dump.FailureKind)) return dump.FailureKind;
        if (dump.Status == "refused") return HeapDumpFailure.Refused;
        string text = dump.Error ?? "";
        if (text.Contains("EventPipe buffer", StringComparison.Ordinal)) return HeapDumpFailure.Buffer;
        if (text.Contains("HeapDumpMaxNodes", StringComparison.Ordinal)) return HeapDumpFailure.NodeLimit;
        if (text.Contains("ran out of time", StringComparison.Ordinal) ||
            text.Contains("time budget", StringComparison.Ordinal)) return HeapDumpFailure.TimeBudget;
        if (text.Contains("heap-walk node events", StringComparison.Ordinal)) return HeapDumpFailure.NoWalk;
        return HeapDumpFailure.NotStarted;
    }

    /// <summary>
    /// Why a completed dump's totals are a lower bound, or null when it describes the whole heap.
    /// The cases are checked in the order of how much they explain: a broken byte accounting means
    /// events are missing outright, and a full buffer is the one cause that names itself, so it is
    /// reported ahead of the generic "roots and objects do not describe one moment".
    /// </summary>
    public static string? Incompleteness(HeapDump dump)
    {
        if (dump.Status != "completed" || dump.Complete) return null;
        if (dump.Notes.Any(n => n.StartsWith("Event accounting is broken", StringComparison.Ordinal)))
            return "the trace lost events, so the byte accounting does not balance";
        if (dump.Notes.Any(n => n.Contains("EventPipe buffer, so the buffer filled", StringComparison.Ordinal)))
            return "the capture filled its EventPipe buffer, which discarded the oldest events of the walk first";
        if (dump.Notes.Any(n => n.Contains("no matching strong root", StringComparison.Ordinal)))
            return "much of the heap had no matching strong root, so the objects and the roots do not describe one moment";
        return "the byte closure does not balance";
    }
}

// A heap dump is requested by hand, once per request, and answers one question:
// which owner *retains* the bytes. It is evidence for a person to read, never an
// automatic accusation: no field names a culprit, and the viewer does not rank one.
public sealed class HeapDumpRequest
{
    public DateTimeOffset RequestedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Reason { get; set; } = "";
}

// Supervisor-visible acknowledgement of a request. The game replaces this file by request index
// and reports transitions in chat; states are "pausing", "running", "completed", "refused" and
// "failed". "pausing" is not terminal: the dump has taken the target and is about to start its worker.
public sealed record HeapDumpStatus(int Index, string State, string Message, DateTimeOffset UpdatedUtc);

// Per-request job file written by the supervisor into the dump work directory and read by the
// isolated dump worker. It carries its own limits so a dump can never inherit session budgets.
public sealed class HeapDumpJob
{
    public int Index { get; set; }
    public int ProcessId { get; set; }
    public DateTimeOffset ProcessStartedUtc { get; set; }
    public DateTimeOffset RequestedUtc { get; set; }
    public string Reason { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public int BufferMegabytes { get; set; } = 4096;
    public int MaxNodes { get; set; } = 40_000_000;
    // The reduction's own budget, 0 to derive it from TimeoutSeconds. It travels with the job for the
    // same reason the other limits do: a dump carries its own budgets, so the path a deadline takes
    // can be exercised at a small scale without shortening the capture it is measuring.
    public int AnalysisSeconds { get; set; }
    public bool SampledAllocations { get; set; } = true;
    public Dictionary<string, string[]> ModMap { get; set; } = [];
}

// Ownership of retained bytes is a partition: every reachable byte is attributed to exactly one
// holder root. Bytes that no requested GC root reaches are reported separately, never folded in.
public sealed record HeapDumpHolder(string Module, string[] Mods, long Bytes, double Percent, int Nodes);
public sealed record HeapDumpRoot(string Module, string Type, string[] Mods, long RetainedBytes, long ShallowBytes, int Nodes);
public sealed record HeapDumpType(string Type, string Module, long ShallowBytes, int Nodes);
public sealed record HeapDumpAllocation(string Module, string[] Mods, long SampledBytes, int Events);

public sealed class HeapDump
{
    public int Index { get; set; }
    // requested | running | completed | refused | failed
    public string Status { get; set; } = "requested";
    public string? Error { get; set; }
    // Which limit stopped this dump, as a value: one of HeapDumpFailure. Null whenever the dump
    // produced an aggregate. It is separate from the message so the summary, the findings and the
    // viewer name the same cause without matching prose, and so a bundle can be classified without
    // reading the text at all.
    public string? FailureKind { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset RequestedUtc { get; set; }
    public DateTimeOffset? CapturedUtc { get; set; }
    // When the attempt ended, whether it produced a heap or not. A dump that was refused or that
    // failed has no capture time, so this is what places its outcome in the event timeline.
    public DateTimeOffset? CompletedUtc { get; set; }
    public int TimeoutSeconds { get; set; }
    // Span between the first and last heap-walk event in the trace. When the walk is emitted as
    // one burst this is its cost; when the runtime delivers it across several collections the span
    // can reach tens of seconds while the game keeps running. It is not a measured suspension:
    // the recorded frame intervals at the same moment are the authority on impact.
    public double SpanMilliseconds { get; set; }
    public double CaptureMilliseconds { get; set; }
    public double ParseMilliseconds { get; set; }
    // Whether the dump held new stack capture back, and for how long. It covers the walk's capture
    // and ends when the worker closes its session, not when the dump is published: the reduction
    // reads a closed file and needs no quiet target. A segment that was already attached kept
    // capturing throughout, so this interval is not by itself a hole in stack coverage - the
    // session's own segment intervals say whether one fell in it. Measured on one deterministic
    // heap: a segment that stayed attached for the whole walk left 0.09% of the heap unattributable
    // against 0.04% with nothing attached, so a steadily attached session does not justify a pause.
    // A session that attaches during the walk does: the runtime answers every attach with a whole
    // walk of the heap, and two-second segments rotating through one walk produced about 15 copies
    // of it, which the analysis collapses but the trace, the capture and the reduction all pay for.
    public bool PausedStackCapture { get; set; }
    public DateTimeOffset? PausedFromUtc { get; set; }
    public DateTimeOffset? ResumedUtc { get; set; }
    public double PauseMilliseconds { get; set; }
    // What the walk cost the client, measured from the recorded frame intervals rather than from the
    // walk's own span: the runtime performs the walk during a blocking collection, so the cost is a
    // stopped client, and the frame journal is the only witness to it that is not a report about
    // itself. The span above is the walk's event window and is not a suspension.
    public double FreezeMilliseconds { get; set; }
    public DateTimeOffset? FreezeStartUtc { get; set; }
    // Runtime GC pause recorded in the timeline windows the freeze falls in. It is temporal overlap,
    // not an attribution: the pause is a blocking collection, and a collection in the same window
    // may have had another cause.
    public double FreezeGcPauseMilliseconds { get; set; }
    // Growth against the previous completed dump in the same recording. A dump answers what is held
    // at one moment; consecutive dumps answer what changed, and that difference is what a recording
    // with more than one dump is for. Null on the first completed dump and on every later one that
    // has no completed predecessor.
    public HeapDumpDelta? DeltaSincePrevious { get; set; }
    // The raw trace is deleted as soon as the aggregate below exists; this is its size. The buffer
    // is the cap that size had to fit in, and a trace that reached it lost the oldest events of the
    // walk, so the two are kept together: a reader cannot judge completeness from either alone.
    public long TraceBytes { get; set; }
    public int BufferMegabytes { get; set; }
    public long TotalBytes { get; set; }
    public long AttributedBytes { get; set; }
    public long UnreachableBytes { get; set; }
    public long UnattributedBytes { get; set; }
    // Objects in the graph the aggregate describes: one per distinct address in the walk. A trace
    // that repeats the walk is collapsed onto the first copy before any of this is measured, so this
    // is the heap once, and WalkedNodeCount below is how many objects the trace actually carried.
    public int NodeCount { get; set; }
    // Objects the trace carried before the repeated walks were collapsed. The runtime answers every
    // EventPipe session that attaches while a walk is being captured with a whole walk of the heap,
    // broadcast to every session that asked for one, so this can be several times NodeCount. The
    // difference is why the dump took as long and its trace was as large as they were; it is not
    // more heap. Measured: 15 two-second segments rotating through one unchanged 300k-object walk
    // produced 4,553,580 walked objects carrying 303,949 distinct addresses.
    public int WalkedNodeCount { get; set; }
    // References between the objects above. A reference to an object the walk did not include is not
    // counted: it is not part of the graph the dominator tree measures.
    public long EdgeCount { get; set; }
    public int TypeCount { get; set; }
    public int ModuleCount { get; set; }
    public int RootCount { get; set; }
    // How many GC roots of each kind the trace reported, so a reader can see what was counted.
    public Dictionary<string, int> RootKinds { get; set; } = [];
    // True only when the node/edge accounting and the byte closure both balance. A false value
    // means the trace lost events and every number below is a lower bound, not the whole heap.
    public bool Complete { get; set; }
    public long SampledAllocationBytes { get; set; }
    public int SampledAllocationEvents { get; set; }
    public List<HeapDumpHolder> Holders { get; set; } = [];
    public List<HeapDumpRoot> Roots { get; set; } = [];
    public List<HeapDumpType> Types { get; set; } = [];
    public List<HeapDumpAllocation> Allocations { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

// What changed between two dumps of one recording. A single dump answers what is held at one
// moment; only a pair answers what changed, which is the question a recording with several dumps
// invites. Retained bytes are a partition of the heap, so the totals subtract directly; the owner
// and type lists say where the difference went, and are cut to the largest changes rather than
// repeated in full - the dumps keep their own tables.
public sealed class HeapDumpDelta
{
    public int PreviousIndex { get; set; }
    // Capture to capture. The interval is what turns a difference into a rate, and the timeline
    // between two dumps is deliberately not interpolated.
    public double SecondsBetween { get; set; }
    public long TotalBytes { get; set; }
    public long AttributedBytes { get; set; }
    public long UnreachableBytes { get; set; }
    public long UnattributedBytes { get; set; }
    public int NodeCount { get; set; }
    public int RootCount { get; set; }
    // True when either dump is a lower bound, in which case the difference is a lower bound too and
    // must not be read as growth. Saturation or dropped events make a dump incomplete, not empty.
    public bool LowerBound { get; set; }
    public List<HeapDumpDeltaEntry> Holders { get; set; } = [];
    public List<HeapDumpDeltaEntry> Types { get; set; } = [];

    // Enough to name the direction of the change without turning the report into two dumps' tables
    // subtracted from each other.
    public const int MaxEntries = 10;

    public static HeapDumpDelta Between(HeapDump previous, HeapDump current) => new()
    {
        PreviousIndex = previous.Index,
        SecondsBetween = Math.Max(0, ((current.CapturedUtc ?? current.RequestedUtc) - (previous.CapturedUtc ?? previous.RequestedUtc)).TotalSeconds),
        TotalBytes = current.TotalBytes - previous.TotalBytes,
        AttributedBytes = current.AttributedBytes - previous.AttributedBytes,
        UnreachableBytes = current.UnreachableBytes - previous.UnreachableBytes,
        UnattributedBytes = current.UnattributedBytes - previous.UnattributedBytes,
        NodeCount = current.NodeCount - previous.NodeCount,
        RootCount = current.RootCount - previous.RootCount,
        LowerBound = !current.Complete || !previous.Complete,
        Holders = Entries(previous.Holders.Select(h => (Name: h.Module, Detail: string.Join(", ", h.Mods), h.Bytes, h.Nodes)),
            current.Holders.Select(h => (Name: h.Module, Detail: string.Join(", ", h.Mods), h.Bytes, h.Nodes))),
        Types = Entries(previous.Types.Select(t => (Name: t.Type, Detail: t.Module, t.ShallowBytes, t.Nodes)),
            current.Types.Select(t => (Name: t.Type, Detail: t.Module, t.ShallowBytes, t.Nodes)))
    };

    /// <summary>
    /// Give every completed dump its growth against the previous completed one, in index order. A
    /// dump that was refused or that failed measures nothing, so it neither gets a delta nor becomes
    /// one: the pair would compare a heap with nothing.
    /// </summary>
    public static void Attach(IEnumerable<HeapDump> dumps)
    {
        HeapDump? previous = null;
        foreach (var dump in dumps.Where(d => d.Status == "completed").OrderBy(d => d.Index))
        {
            if (previous is not null) dump.DeltaSincePrevious = Between(previous, dump);
            previous = dump;
        }
    }

    // Ownership is matched by name - module for owners, type for types - because that is the identity
    // the aggregate itself carries; an owner that disappeared has a zero predecessor rather than an
    // absent row, so a reader can see that it went away as well as that something grew.
    private static List<HeapDumpDeltaEntry> Entries(
        IEnumerable<(string Name, string Detail, long Bytes, int Nodes)> before,
        IEnumerable<(string Name, string Detail, long Bytes, int Nodes)> after)
    {
        var previous = new Dictionary<string, (string Detail, long Bytes, int Nodes)>(StringComparer.Ordinal);
        foreach (var entry in before) previous[entry.Name] = (entry.Detail, entry.Bytes, entry.Nodes);
        var names = new List<string>();
        var current = new Dictionary<string, (string Detail, long Bytes, int Nodes)>(StringComparer.Ordinal);
        foreach (var entry in after)
        {
            current[entry.Name] = (entry.Detail, entry.Bytes, entry.Nodes);
            names.Add(entry.Name);
        }
        foreach (string name in previous.Keys)
            if (!current.ContainsKey(name)) names.Add(name);
        return names
            .Select(name =>
            {
                current.TryGetValue(name, out var now);
                previous.TryGetValue(name, out var was);
                return new HeapDumpDeltaEntry(name, now.Detail ?? was.Detail ?? "", now.Bytes, was.Bytes, now.Nodes, was.Nodes);
            })
            .Where(e => e.Bytes != e.PreviousBytes || e.Nodes != e.PreviousNodes)
            .OrderByDescending(e => Math.Abs(e.Bytes - e.PreviousBytes))
            .ThenByDescending(e => Math.Abs(e.Nodes - e.PreviousNodes))
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Take(MaxEntries)
            .ToList();
    }
}

// One owner or type, as it is now and as it was in the previous dump. Both sides are kept so a
// reader can see a fall as well as a rise, and so a row that is new or gone says which it is.
public sealed record HeapDumpDeltaEntry(string Name, string Detail, long Bytes, long PreviousBytes, int Nodes, int PreviousNodes);
