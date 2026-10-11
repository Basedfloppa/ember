namespace Ember.Core;

// What a dump cost the client, in the two currencies a reader asks about: the hitch the walk itself
// caused, and the stack coverage the hold around it cost. Neither is the walk's own span, and they
// are not the same cost: the walk stops the client once, when the runtime performs it during a
// blocking collection, and the hold on new stack capture lasts as long as reading and reducing the
// trace, during which the segment that was already attached keeps recording.
//
// Measured on one real client, three dumps: the longest completed frame interval began 0.101 s,
// 0.103 s and 0.109 s after the request was consumed and lasted 2,345 ms, 2,806 ms and 2,818 ms,
// against a walk span of 1,465 ms, 1,376 ms and 1,416 ms. The same client had two hitches of
// 1,587 ms and 1,405 ms inside those holds, at +35 s and +48 s, and they were its own collections:
// the attach window below is what keeps them out of the dump's price. The 189.0 s the three holds
// lasted cost the recording 77.3 s of coverage, of which 74.8 s fell inside them and the rest was
// segment rotation - so the hold is not the same thing as the gap either.
public static class HeapDumpImpact
{
    // Wide enough for a slower worker start than the 0.10 s the client measured, and narrow enough
    // that a hitch later in the hold is not charged to the dump.
    public const int AttachWindowMilliseconds = 5000;
    // The frame the attach interrupts may already have been in flight when the request was consumed.
    // Incident discovery merges candidates within 250 ms and the same tolerance is used here.
    public const int AttachLeadMilliseconds = 250;

    /// <summary>
    /// Measure every dump's hitch from the frame journal, decompose the session's stack coverage gap
    /// and attach each dump's growth against its predecessor. Called by the assembler once the dumps,
    /// the frames and the timeline are all present.
    /// </summary>
    public static void Measure(DiagnosticReport report)
    {
        long omitted = report.Session?.FramesOmitted ?? 0;
        foreach (var dump in report.HeapDumps) MeasureFreeze(dump, report.Ticks, report.Timeline, omitted);
        HeapDumpDelta.Attach(report.HeapDumps);
        if (report.Session is { } session) MeasureCoverageGap(session, report.HeapDumps);
    }

    /// <summary>
    /// The hitch the walk caused, as the longest completed frame interval that began in the window
    /// around the request. Returns false when the journal holds no frame there, in which case the
    /// cost is unmeasured rather than zero and the dump says so.
    /// </summary>
    public static bool MeasureFreeze(HeapDump dump, IReadOnlyList<TickRecord> frames, IReadOnlyList<MetricPoint> windows, long framesOmitted = 0)
    {
        if (dump.Status != "completed" || dump.PausedFromUtc is not { } from) return false;
        long windowStart = from.ToUnixTimeMilliseconds() - AttachLeadMilliseconds;
        long windowEnd = from.ToUnixTimeMilliseconds() + AttachWindowMilliseconds;
        double longest = 0;
        long longestEnd = 0;
        foreach (var frame in frames)
        {
            if (!double.IsFinite(frame.IntervalMs) || frame.IntervalMs <= longest) continue;
            long began = frame.EndUnixMs - (long)Math.Min(frame.IntervalMs, 86400000);
            if (began < windowStart || began > windowEnd) continue;
            longest = frame.IntervalMs;
            longestEnd = frame.EndUnixMs;
        }
        dump.FreezeMilliseconds = longest;
        dump.FreezeStartUtc = longest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(windowStart, longestEnd - (long)longest)) : null;
        long freezeEnd = longest > 0 ? longestEnd : windowEnd;
        long freezeStart = longest > 0 ? Math.Max(windowStart, longestEnd - (long)longest) : from.ToUnixTimeMilliseconds();
        dump.FreezeGcPauseMilliseconds = longest > 0
            ? windows.Where(w => Overlaps(w, freezeStart, freezeEnd)).Sum(w => w.Runtime.GcPauseMilliseconds)
            : 0;
        // A journal that omitted frames kept a uniform sample of them, so the longest retained interval
        // can be shorter than the longest one recorded, and the difference has to be said rather than
        // left for a reader to assume the sample was complete.
        string sampled = framesOmitted > 0
            ? $" Frame intervals were reservoir-sampled ({framesOmitted:N0} observed frames omitted), so this is the longest retained interval, not necessarily the longest one recorded."
            : "";
        string note = longest > 0
            ? $"The walk stopped the client for {longest:F0} ms, the longest completed frame interval that began within {AttachWindowMilliseconds / 1000} s of the request, with {dump.FreezeGcPauseMilliseconds:F0} ms of runtime GC pause recorded in the same timeline windows. That pause is the runtime performing the walk during a blocking collection, and it is this dump's price; the {dump.PauseMilliseconds / 1000:F1} s hold on new stack capture is not a second cost, because the segment that was already attached kept recording through it.{sampled}"
            : $"No{(framesOmitted > 0 ? " retained" : "")} completed frame interval was recorded within {AttachWindowMilliseconds / 1000} s of this request, so the hitch the walk caused is unmeasured in this journal - it is unknown, not zero.{sampled}";
        if (!dump.Notes.Contains(note)) dump.Notes.Add(note);
        return longest > 0;
    }

    /// <summary>
    /// Split the session's uncovered seconds into the part that falls inside the intervals dumps held
    /// new stack capture back and the part that does not. A hold is not a gap by itself - a segment
    /// that was already attached keeps recording through it - so the two numbers are different and
    /// the summary has to name which is which rather than leave a reader to guess.
    /// </summary>
    public static void MeasureCoverageGap(SessionInfo session, IReadOnlyList<HeapDump> dumps)
    {
        session.UncoveredSeconds = Math.Max(0, session.RecordedSeconds - session.StackCoverageSeconds);
        session.HeapDumpHoldSeconds = 0;
        session.UncoveredDuringHeapDumpHoldsSeconds = 0;
        if (session.RecordedSeconds <= 0) return;
        var covered = CoveredIntervals(session);
        var holds = new List<(DateTimeOffset From, DateTimeOffset To)>();
        foreach (var dump in dumps)
        {
            if (dump.PausedFromUtc is not { } from || dump.ResumedUtc is not { } to || to <= from) continue;
            var start = from < session.StartedUtc ? session.StartedUtc : from;
            var end = session.StoppedUtc is { } stopped && to > stopped ? stopped : to;
            if (end <= start) continue;
            holds.Add((start, end));
            session.HeapDumpHoldSeconds += (end - start).TotalSeconds;
            session.UncoveredDuringHeapDumpHoldsSeconds += (end - start).TotalSeconds - Covered(covered, start, end);
        }
        // The holds are measured against segment intervals and the gap is what the tracer reported;
        // a rounding difference between them must not produce more gap inside the holds than exists.
        session.UncoveredDuringHeapDumpHoldsSeconds = Math.Min(session.UncoveredDuringHeapDumpHoldsSeconds, session.UncoveredSeconds);
        string line = HoldsLine(session, holds.Count);
        if (!session.Notes.Contains(line)) session.Notes.Add(line);
    }

    public static string HoldsLine(SessionInfo session, int holds)
    {
        double outside = Math.Max(0, session.UncoveredSeconds - session.UncoveredDuringHeapDumpHoldsSeconds);
        string share = session.UncoveredSeconds > 0
            ? $"{100 * session.UncoveredDuringHeapDumpHoldsSeconds / session.UncoveredSeconds:F0}%"
            : "0%";
        return $"Stack coverage gap: {session.UncoveredSeconds:F1} s of {session.RecordedSeconds:F1} s ({session.StackCoveragePercent:F1}% covered). " +
            $"{session.UncoveredDuringHeapDumpHoldsSeconds:F1} s ({share}) of that gap falls inside the intervals {holds} heap dump(s) held new stack capture back, and {outside:F1} s of it is segment rotation and the start of the recording. " +
            $"Those holds totalled {session.HeapDumpHoldSeconds:F1} s, so {Math.Max(0, session.HeapDumpHoldSeconds - session.UncoveredDuringHeapDumpHoldsSeconds):F1} s of them were still covered by the segment that was already attached: a hold is not by itself a gap.";
    }

    // The same union the tracer reports coverage from: successfully parsed segments only, taken in
    // start order and not allowed to overlap, because a reader has to be able to reconcile the split
    // with the coverage percentage that sits beside it.
    private static List<(DateTimeOffset From, DateTimeOffset To)> CoveredIntervals(SessionInfo session)
    {
        var covered = new List<(DateTimeOffset, DateTimeOffset)>();
        var cursor = session.StartedUtc;
        var end = session.StoppedUtc ?? DateTimeOffset.UtcNow;
        foreach (var segment in session.Segments.Where(s => s.Status == "completed").OrderBy(s => s.StartedUtc))
        {
            var start = segment.StartedUtc > cursor ? segment.StartedUtc : cursor;
            var finish = segment.EndedUtc < end ? segment.EndedUtc : end;
            if (finish <= start) continue;
            covered.Add((start, finish));
            cursor = finish;
        }
        return covered;
    }

    private static double Covered(List<(DateTimeOffset From, DateTimeOffset To)> covered, DateTimeOffset from, DateTimeOffset to)
    {
        double seconds = 0;
        foreach (var (start, finish) in covered)
        {
            var overlapStart = start > from ? start : from;
            var overlapEnd = finish < to ? finish : to;
            if (overlapEnd > overlapStart) seconds += (overlapEnd - overlapStart).TotalSeconds;
        }
        return seconds;
    }

    private static bool Overlaps(MetricPoint window, long start, long end) =>
        window.UnixMs >= start && window.UnixMs - (long)(window.WindowSeconds * 1000) <= end;
}
