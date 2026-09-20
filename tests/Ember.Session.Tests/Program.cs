using System.IO.Compression;
using System.Text.Json;
using Ember.Core;

var tests = new (string Name, Action Run)[]
{
    ("30-minute journal survives frame reservoir and preserves early incidents", WholeSession),
    ("fragmented/corrupt journal recovery and final inventory precedence", Recovery),
    ("variable segment density, recursion, temporal alignment and union coverage", Merge),
    ("known usable prefix clips coverage independently of reservoir endpoints", UsablePrefix),
    ("segment traversal, absolute paths and corrupt files rejected", BadPaths),
    ("incident pre/during/post stacks and causal guardrails are idempotent", Incidents),
    ("missing telemetry is not zero and stalls are discoverable", Missing),
    ("bounded journal tails preserve seed context and report omissions", Bounds),
    ("standalone export and streamed CSV manifest", Export)
};
foreach (var test in tests) { test.Run(); Console.WriteLine("PASS " + test.Name); }
Console.WriteLine($"{tests.Length} session analysis checks passed.");

static DateTimeOffset Origin() => DateTimeOffset.Parse("2026-09-19T12:00:00Z");
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Save<T>(string directory, string name, T value) => File.WriteAllText(Path.Combine(directory, name), Json.Write(value));
static SessionRequest Init(string path, int frames = 500000)
{
    var request = new SessionRequest { Id = "session-test", StartedUtc = Origin(), MaxReportFrames = frames };
    Save(path, "request.json", request);
    Save(path, "seed.json", new DiagnosticReport { Id = "seed", SlowTickThresholdMs = 50, TargetIntervalMs = 16.67,
        Environment = new() { ["startOnly"] = true, ["phase"] = "seed" }, Settings = new() { ["fpsCap"] = 60 } });
    return request;
}
static MetricPoint Window(int second, double max = 17, bool zero = false, double gc = 0, double cpu = 20)
{
    var d = zero ? Distribution.Of([]) : new Distribution(60, 17, 17, 17, 17, max);
    return new(Origin().ToUnixTimeMilliseconds() + second * 1000, second * 1000, 1, zero ? 0 : 60,
        d, d, max >= 50 ? 1 : 0, new(cpu, cpu / 8, 100, 100, 100, 100, 0, 1000, 10000, gc, 0, 0, 0, 10, 2, 0, 0),
        1, 2, 3, 0, 0, []);
}
static void WholeSession()
{
    using var dir = new Scratch();
    Init(dir.Path, 1000);
    var compact = new JsonSerializerOptions(Json.Options) { WriteIndented = false };
    using (var writer = new StreamWriter(Path.Combine(dir.Path, "batches.ndjson")))
        for (int s = 1; s <= 1800; s++)
        {
            var frames = Enumerable.Range(0, 60).Select(f => new TickRecord(Origin().ToUnixTimeMilliseconds() + (s - 1) * 1000 + f * 16,
                s * 1000 + f * 16, 2, s == 10 && f == 30 ? 900 : 16.67)).ToArray();
            writer.WriteLine(JsonSerializer.Serialize(new SessionBatch(s, Window(s, s == 10 ? 900 : 17), frames, [], [], null), compact));
        }
    Save(dir.Path, "stop.json", new SessionStop("duration", Origin().AddMinutes(30)));
    var report = SessionAssembler.Assemble(dir.Path);
    Assert(report.Timeline.Length == 1800 && report.Timeline[0].UnixMs == Window(1).UnixMs, "15-minute truncation returned");
    Assert(report.Session!.RecordedSeconds == 1800 && report.Session.FramesObserved == 108000, "whole duration/count incorrect");
    Assert(report.Ticks.Length == 1000 && report.Session.FramesOmitted == 107000, "frame budget incorrect");
    Assert(report.Ticks[0].EndUnixMs < Window(300).UnixMs && report.Ticks[^1].EndUnixMs > Window(1500).UnixMs, "reservoir not whole-session");
    Assert(report.Incidents.Any(i => i.PeakFrameMs == 900 && i.StartUnixMs < Window(10).UnixMs), "early incident lost before downsampling");
    Assert(Analysis.Summary(report).Contains("estimates from a uniform reservoir"), "biased/exact percentile claim");
    var request = new SessionRequest { Id = "session-test", StartedUtc = Origin(), MaxReportFrames = 500000 };
    Save(dir.Path, "request.json", request);
    Assert(SessionAssembler.Assemble(dir.Path).Ticks.Length == 108000, "all raw frames must be retained when they fit");
}
static void Recovery()
{
    using var dir = new Scratch();
    Init(dir.Path);
    Save(dir.Path, "final.json", new DiagnosticReport { Id = "must-not-win", GameVersion = "final-game",
        Environment = new() { ["phase"] = "final", ["finalOnly"] = true }, Settings = new() { ["fpsCap"] = 30 }, Timeline = [Window(999)] });
    var compact = new JsonSerializerOptions(Json.Options) { WriteIndented = false };
    var a = new SessionBatch(1, Window(1), [], [], [], null);
    var b = new SessionBatch(3, Window(3), [], [], [], new(Window(3).UnixMs, 1, 99, 3, [], [], 99, false, Distribution.Of([]), 1),
        new(Window(3).UnixMs, new() { ["focused"] = false }, []));
    File.WriteAllText(Path.Combine(dir.Path, "batches.ndjson"), JsonSerializer.Serialize(a, compact) + "\n{broken}\n" + JsonSerializer.Serialize(b, compact) + "\n{\"sequence\":4");
    Save(dir.Path, "stop.json", new SessionStop("stopped", Origin().AddSeconds(4), DroppedBatches: 1));
    var report = SessionAssembler.Assemble(dir.Path);
    Assert(report.Id == "session-test" && report.GameVersion == "final-game", "request identity/final inventory lost");
    Assert(report.Environment.ContainsKey("startOnly") && report.Environment["phase"]!.ToString() == "final", "seed/final environment merge incorrect");
    Assert(report.Timeline.Length == 2 && report.Timeline[^1].UnixMs == Window(3).UnixMs, "final ring leaked into journal timeline");
    Assert(report.Session!.IncompleteJournalLines == 2 && report.Session.DroppedBatches == 1, "corruption/gap counts incorrect or doubled");
    Assert(report.ContextHistory.Count == 2 && report.ContextHistory[0].Settings["fpsCap"]!.ToString() == "60" && report.World?.Entities == 99, "start context/last census incorrect");
    // A complete final JSON record without newline is safe to recover.
    File.WriteAllText(Path.Combine(dir.Path, "batches.ndjson"), JsonSerializer.Serialize(a, compact));
    Assert(SessionAssembler.Assemble(dir.Path).Timeline.Length == 1, "valid unterminated last line rejected");
}
static StackProfile Profile(DateTimeOffset start, long observed, int thread)
{
    var profile = new StackProfile { StartedUtc = start, DurationSeconds = 60, ObservedStackSamples = observed,
        Frames = [new("M(System.Int32)", "mod.dll", ["owner"]), new("Root()", "core.dll", [])],
        Allocations = [new("Thing", 100, 2)], ExceptionTypes = new() { ["Example"] = 2 }, ContentionEvents = 3, ContentionTotalMs = 4 };
    profile.Samples = Enumerable.Range(0, 100).Select(n => new ProfileSample(n * 600, thread, true, [0, 1, 0])).ToList();
    return profile;
}
static void Merge()
{
    using var dir = new Scratch();
    var session = new SessionInfo { StartedUtc = Origin(), StoppedUtc = Origin().AddSeconds(120), RecordedSeconds = 120,
        Segments = [new() { Index = 0, Status = "completed", FileName = "a.json", StartedUtc = Origin(), EndedUtc = Origin().AddSeconds(60) },
            new() { Index = 1, Status = "completed", FileName = "b.json", StartedUtc = Origin().AddSeconds(30), EndedUtc = Origin().AddSeconds(90) }] };
    Save(dir.Path, "a.json", Profile(Origin(), 1000, 11));
    Save(dir.Path, "b.json", Profile(Origin().AddSeconds(30), 200, 22));
    var origin = Origin().ToUnixTimeMilliseconds();
    var incident = new PerformanceIncident { StartUnixMs = origin + 4500, EndUnixMs = origin + 5500, ContextStartUnixMs = origin + 2500, ContextEndUnixMs = origin + 7500 };
    var p = ProfileMerge.Merge(dir.Path, session, 120, [incident])!;
    Assert(p.Samples.Count == 120 && p.Samples.Count(s => s.ThreadId == 11) == 100 && p.Samples.Count(s => s.ThreadId == 22) == 20, "segment sample density weighting wrong");
    Assert(p.Frames.Count == 2 && p.Hotspots.Single(h => p.Frames[h.Frame].Name.StartsWith("M(")).InclusiveSamples == 120, "identity dedup or recursion inclusive count wrong");
    Assert(p.Samples.Where(s => s.ThreadId == 22).All(s => s.RelativeMs >= 30000 && s.RelativeMs < 90000), "segment timestamps not aligned");
    Assert(session.StackCoverageSeconds == 90 && session.StackCoveragePercent == 75, "coverage should be clipped union, not sum");
    Assert(p.Allocations.Single().SampledBytes == 200 && p.ExceptionTypes["Example"] == 4 && p.ContentionEvents == 6 && p.ContentionTotalMs == 8, "metadata not summed");
    Assert(incident.Samples.Any(s => s.RelativeMs < 4500) && incident.Samples.Any(s => s.RelativeMs is >= 4500 and <= 5500) && incident.Samples.Any(s => s.RelativeMs > 5500), "incident phase stacks missing");
    Assert(incident.Samples.All(s => s.Stack.All(f => f < p.Frames.Count)), "incident frames not globally remapped");
    Assert(ProfileMerge.Merge(dir.Path, session, 1000)!.Samples.Count == 120, "sparsest segment retention ratio must constrain global sampling even with spare budget");
    var b = Profile(Origin().AddSeconds(30), 200, 22); b.Frames[0] = new("M(System.String)", "mod.dll", ["owner"]);
    Save(dir.Path, "b.json", b);
    Assert(ProfileMerge.Merge(dir.Path, session, 120)!.Frames.Count == 3, "overloaded signatures collapsed");
    b.Frames[0] = new("M(System.Int32)", "different.dll", ["owner"]);
    Save(dir.Path, "b.json", b);
    Assert(ProfileMerge.Merge(dir.Path, session, 120)!.Frames.Count == 3, "different modules collapsed");
    b.Frames[0] = new("M(System.Int32)", "mod.dll", ["different-owner"]);
    Save(dir.Path, "b.json", b);
    Assert(ProfileMerge.Merge(dir.Path, session, 120)!.Frames.Count == 3, "different ownership sets collapsed");
    Assert(ProfileMerge.Merge(dir.Path, session, 0, [incident])!.Samples.Count == 0 && incident.Samples.Count > 0, "incident capture depends on global budget");
}
static void BadPaths()
{
    using var dir = new Scratch();
    var session = new SessionInfo { StartedUtc = Origin(), RecordedSeconds = 120 };
    foreach (string path in new[] { "../outside.json", "..\\outside.json", "C:\\outside.json", "broken.json" })
        session.Segments.Add(new() { Index = session.Segments.Count, Status = "completed", FileName = path, StartedUtc = Origin(), EndedUtc = Origin().AddSeconds(60) });
    File.WriteAllText(Path.Combine(dir.Path, "broken.json"), "{incomplete");
    Assert(ProfileMerge.Merge(dir.Path, session) is null && session.StackCoverageSeconds == 0, "unsafe or invalid profile accepted");
    Assert(session.Notes.Count(n => n.Contains("rejected")) == 4, "missing path/corruption rejection evidence");
}
static void UsablePrefix()
{
    using var dir = new Scratch();
    var session = new SessionInfo { StartedUtc = Origin(), StoppedUtc = Origin().AddSeconds(60), RecordedSeconds = 60,
        Segments = [new() { Index = 0, Status = "completed", FileName = "prefix.json", StartedUtc = Origin(), EndedUtc = Origin().AddSeconds(60) }] };
    var profile = Profile(Origin(), 1000, 11);
    profile.UsableDurationSeconds = 40;
    profile.Samples = [new(1000, 11, true, [0]), new(20000, 11, true, [0]), new(50000, 11, true, [0])];
    Save(dir.Path, "prefix.json", profile);
    var unix = Origin().ToUnixTimeMilliseconds();
    var incident = new PerformanceIncident { StartUnixMs = unix + 49500, EndUnixMs = unix + 50500,
        ContextStartUnixMs = unix + 47500, ContextEndUnixMs = unix + 52500 };
    var merged = ProfileMerge.Merge(dir.Path, session, 200000, [incident])!;
    Assert(session.StackCoverageSeconds == 40 && Math.Abs(session.StackCoveragePercent - 100 * 40.0 / 60) < .001,
        "nominal metadata or last reservoir sample incorrectly determined usable coverage");
    Assert(incident.StackCoveragePercent == 0 && incident.Samples.Count == 0, "unprocessed suffix fabricated incident coverage or retained an invalid sample");
    Assert(merged.Samples.Count == 2 && merged.Samples[^1].RelativeMs == 20000 && merged.DurationSeconds == 60,
        "usable endpoint must not be inferred from the last retained sample or replace nominal duration");
    Assert(merged.UsableDurationSeconds is null, "merged interval union must not be represented as a single usable prefix");
    Assert(session.Notes.Any(n => n.Contains("stack observations rejected")), "out-of-prefix sample rejection not disclosed");
    var report = new DiagnosticReport { Profile = merged, Session = session };
    Assert(Analysis.Findings(report).Any(f => f.Severity == "warning" && f.Title == "Known stack-profile processing truncation"),
        "merged truncation needs a warning even with zero lost events and no byte limit");
    Assert(Analysis.Findings(report).Any(f => f.Severity == "info" && f.Title == "Stack samples intentionally downsampled"),
        "deliberate reservoir omissions must retain informational severity");
    using (var context = JsonDocument.Parse(Json.Write(LlmContext.Create(report))))
        Assert(context.RootElement.GetProperty("profile").GetProperty("knownUsablePrefixTruncation").GetBoolean(), "merged truncation absent from LLM context");
    report.Profile = profile; report.Session = null;
    report.Ticks = [new(unix + 50000, 50000, 1, 500)];
    IncidentAnalysis.Analyze(report);
    Assert(report.Incidents.Single().StackCoveragePercent == 0 && report.Incidents.Single().Samples.Count == 0,
        "manual profile incident credited an unprocessed suffix");
    Assert(Analysis.Findings(report).Any(f => f.Title == "Known stack-profile processing truncation"), "manual truncation warning missing");
    using (var context = JsonDocument.Parse(Json.Write(LlmContext.Create(report))))
        Assert(context.RootElement.GetProperty("profile").GetProperty("usableDurationSeconds").GetDouble() == 40, "usable duration absent from LLM context");
    profile.Samples.RemoveAt(2);
    profile.UsableDurationSeconds = null;
    Save(dir.Path, "prefix.json", profile);
    merged = ProfileMerge.Merge(dir.Path, session, 200000, [incident])!;
    Assert(session.StackCoverageSeconds == 60 && incident.StackCoveragePercent == 100,
        "normal reservoir must retain full nominal interval coverage even when last sample is at 20s");
    Assert(!Analysis.Findings(new() { Profile = merged }).Any(f => f.Title == "Known stack-profile processing truncation"), "normal reservoir mislabeled as processing truncation");
    foreach (double invalid in new[] { -1.0, 61.0 })
    {
        profile.UsableDurationSeconds = invalid;
        Save(dir.Path, "prefix.json", profile);
        Assert(ProfileMerge.Merge(dir.Path, session) is null && session.StackCoverageSeconds == 0, "invalid usable duration was accepted");
    }
}
static DiagnosticReport IncidentReport()
{
    var p = Profile(Origin(), 100, 42); p.DurationSeconds = 10;
    p.Samples = Enumerable.Range(0, 100).Select(n => new ProfileSample(n * 100, 42, true, [0, 1, 0])).ToList();
    return new DiagnosticReport { SlowTickThresholdMs = 50, TargetIntervalMs = 17, Profile = p,
        Ticks = [new(Origin().ToUnixTimeMilliseconds() + 5000, 5000, 20, 500)],
        Timeline = Enumerable.Range(1, 10).Select(n => Window(n, n == 5 ? 500 : 17, gc: n == 5 ? 100 : 0, cpu: n == 5 ? 120 : 20)).ToArray(),
        Events = [new(Origin().ToUnixTimeMilliseconds() + 4900, "focus", "window lost focus")],
        Mods = [new("innocent", "Installed only", "1", "client", "code", "mod.zip", [], [], [])] };
}
static void Incidents()
{
    var report = IncidentReport();
    IncidentAnalysis.Analyze(report);
    var incident = report.Incidents.Single();
    Assert(incident.Samples.Any(s => s.RelativeMs < 4500) && incident.Samples.Any(s => s.RelativeMs is >= 4500 and <= 5000) && incident.Samples.Any(s => s.RelativeMs > 5000), "manual incident phases not attached");
    Assert(incident.Confidence == "low" && incident.CounterEvidence.Any(e => e.Contains("lost focus")), "focus contradiction ignored");
    Assert(incident.Hypotheses.Any(h => h.Title.Contains("GC")) && incident.Hypotheses.All(h => !h.Title.Contains("innocent")), "GC evidence missed or installed mod blamed");
    Assert(incident.Evidence.Any(e => e.Contains("Thread IDs alone")), "thread role guessed");
    string first = Json.Write(report.Incidents);
    IncidentAnalysis.Analyze(report);
    Assert(Json.Write(report.Incidents) == first, "analysis is not idempotent");
}
static void Missing()
{
    var report = new DiagnosticReport { Timeline = [Window(1, zero: true), Window(2, zero: true)] };
    IncidentAnalysis.Analyze(report);
    Assert(report.Incidents.Count == 1 && report.Incidents[0].PeakFrameMs == 0 && report.Incidents[0].SlowFrames == 0, "stall fabricated a completed frame");
    Assert(report.Incidents[0].CounterEvidence.Any(e => e.Contains("No retained stacks")), "missing stacks not disclosed");
    report = new DiagnosticReport { Ticks = [new(Origin().ToUnixTimeMilliseconds(), 0, 1, 500)] };
    IncidentAnalysis.Analyze(report);
    Assert(report.Incidents.Single().CounterEvidence.Any(e => e.Contains("unavailable, not zero")), "missing telemetry treated as zero");
}
static void Export()
{
    using var dir = new Scratch();
    string path = Exporter.Export(IncidentReport(), dir.Path);
    using var zip = ZipFile.OpenRead(path);
    foreach (var name in new[] { "GPT-REPORT.md", "incidents.json", "incidents.csv", "metrics.csv", "frames.csv", "report.html", "manifest.json" })
        Assert(zip.GetEntry(name) is not null, "missing export " + name);
    using var reader = new StreamReader(zip.GetEntry("GPT-REPORT.md")!.Open());
    string gpt = reader.ReadToEnd();
    Assert(gpt.Contains("Installed only") && gpt.Contains("before") && gpt.Contains("CounterEvidence", StringComparison.OrdinalIgnoreCase), "one-file handoff missing inventory/incident evidence");
    using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
    Assert(manifest.RootElement.GetProperty("files").EnumerateArray().Any(f => f.GetProperty("name").GetString() == "GPT-REPORT.md"), "new file absent from manifest");
}
static void Bounds()
{
    using var dir = new Scratch();
    Init(dir.Path);
    var compact = new JsonSerializerOptions(Json.Options) { WriteIndented = false };
    using (var writer = new StreamWriter(Path.Combine(dir.Path, "batches.ndjson")))
    {
        // Oversized malformed line must be discarded without losing the following valid batch.
        writer.WriteLine(new string('x', 16 * 1024 * 1024 + 10));
        for (int n = 1; n <= 20005; n++)
            writer.WriteLine(JsonSerializer.Serialize(new SessionBatch(n, Window(n), [],
                [new(n, "test", "event")], [new(n, "info", "log")], null,
                n <= 2005 ? new(n, [], []) : null), compact));
    }
    var report = SessionAssembler.Assemble(dir.Path);
    Assert(report.Timeline.Length == 20000 && report.Timeline[0].UnixMs == Window(6).UnixMs, "runtime window tail bound incorrect");
    Assert(report.Logs.Length == 20000 && report.Logs[0].UnixMs == 6 && report.Events.Length == 20000, "event/log tail bound incorrect");
    Assert(report.ContextHistory.Count == 2000 && report.ContextHistory[0].UnixMs == Origin().ToUnixTimeMilliseconds(), "initial context must survive bounded context history");
    Assert(report.Session!.IncompleteJournalLines == 1 && report.Session.Notes.Any(n => n.Contains("5 runtime windows, 5 logs, 5 events, 6 context snapshots")), "oversized line/tail omissions not reported");
}

sealed class Scratch : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? System.IO.Path.GetTempPath(), "Temp", "opencode", "ember-session-tests-" + Guid.NewGuid().ToString("N"));
    public Scratch() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
