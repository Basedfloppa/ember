using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ember.Core;

namespace Ember.Tests;

internal static class Program
{
    private const string Hostile = "</script><script>globalThis.EMBER_INJECTED=true</script><img src=x onerror=alert(1)>";
    private static string output = "";

    public static async Task<int> Main(string[] args)
    {
        output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(RepositoryRoot(), "test-runs", "core"));
        Directory.CreateDirectory(output);
        (string Name, Func<Task> Run)[] tests =
        [
            Sync("ring chronological ordering, boundedness and overwrite accounting", RingOrdering),
            Sync("ring snapshot independence and minimum capacity", RingSnapshots),
            Sync("nearest-rank percentiles and finite filtering", Percentiles),
            Sync("configuration normalization", ConfigNormalization),
            Sync("collector frame, log and event ring retention", CollectorRetention),
            ("collector real timed sampling, census and stage window reset", CollectorSampling),
            ("collector pending frame and stage sampling caps", CollectorSamplingCaps),
            ("collector history expiration and disposal", CollectorExpiration),
            ("recording journal separates sessions and flushes partial windows", RecordingLifecycle),
            ("journal queue is bounded and accounts for every submitted batch", JournalBackpressure),
            ("journal disk limit degrades without throwing on render callbacks", JournalDiskLimit),
            Sync("runtime counters and machine description are sane", RuntimeCounters),
            Sync("text redaction: secrets, Windows paths, IPv4 and email", TextRedaction),
            Sync("text redaction: bearer authorization does not leak credentials", BearerRedaction),
            Sync("text redaction: UNC paths and IPv6 endpoints", AdditionalAddresses),
            Sync("nested JSON redaction preserves structure and original input", JsonRedaction),
            Sync("JSON redaction depth and array bounds", JsonRedactionBounds),
            Sync("Speedscope JSON, sample counts, weights and frame indices", Speedscope),
            Sync("Speedscope empty profile remains valid JSON", EmptySpeedscope),
            Sync("findings do not blame merely installed or sampled mods", FindingAttribution),
            Sync("findings report measured pressure and data-quality limits", FindingEvidence),
            Sync("mod ownership: module first, raw recovery second, type namespace last", ModOwnershipResolution),
            Sync("mod sample tally counts self, inclusive and unowned leaves honestly", ModSampleTally),
            Sync("mod attribution quality reaches summary, findings and JSON", ModAttributionReporting),
            Sync("compact LLM context preserves extremes without raw sample duplication", CompactContext),
            Sync("export ZIP manifest hashes, hostile HTML and round-trip JSON", ExportBundle),
            Sync("export without profile and export retention", ExportRetention),
            Sync("heap dump settings normalization", HeapDumpConfig),
            Sync("heap dump buffer saturation boundary", HeapDumpBufferThreshold),
            Sync("heap dump budgets keep the kill behind the worker", HeapDumpBudgetOrdering),
            Sync("heap dump failures are classified by the limit that stopped them", HeapDumpFailureKinds),
            Sync("heap dump requests are assembled, refusals included", HeapDumpAssembly),
            Sync("heap dump impact: walk freeze, coverage gap and growth between dumps", HeapDumpImpactAndDeltas)
        ];
        var results = new List<object>();
        int failures = 0;
        var total = Stopwatch.StartNew();
        foreach (var test in tests)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name} ({watch.ElapsedMilliseconds} ms)");
                results.Add(new { test.Name, passed = true, elapsedMs = watch.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}\n{ex}");
                results.Add(new { test.Name, passed = false, elapsedMs = watch.ElapsedMilliseconds, error = ex.ToString() });
            }
        }
        File.WriteAllText(Path.Combine(output, "results.json"), Json.Write(new
        {
            createdUtc = DateTimeOffset.UtcNow, total = tests.Length, passed = tests.Length - failures,
            failed = failures, elapsedMs = total.ElapsedMilliseconds, results
        }));
        Console.WriteLine($"\n{tests.Length - failures}/{tests.Length} passed; artifacts: {output}");
        return failures == 0 ? 0 : 1;
    }

    private static (string, Func<Task>) Sync(string name, Action action) => (name, () => { action(); return Task.CompletedTask; });

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "Ember.Core", "Ember.Core.csproj"))) return dir.FullName;
        throw new InvalidOperationException("Cannot find repository root; pass an explicit artifact directory.");
    }

    private static void RingOrdering()
    {
        var ring = new Ring<int>(3);
        Equal(0, ring.Snapshot().Length);
        for (int i = 0; i < 100; i++)
        {
            ring.Add(i);
            Sequence(Enumerable.Range(Math.Max(0, i - 2), Math.Min(i + 1, 3)), ring.Snapshot());
            Equal((long)Math.Max(0, i - 2), ring.Overwritten);
        }
    }

    private static void RingSnapshots()
    {
        var ring = new Ring<int>(2);
        ring.Add(1);
        var snapshot = ring.Snapshot();
        snapshot[0] = 99;
        ring.Add(2);
        ring.Add(3);
        Sequence(new[] { 2, 3 }, ring.Snapshot());
        Equal(99, snapshot[0]);
        foreach (int capacity in new[] { 0, -10, 1 })
        {
            var tiny = new Ring<int>(capacity);
            tiny.Add(1);
            tiny.Add(2);
            Sequence(new[] { 2 }, tiny.Snapshot());
            Equal(1L, tiny.Overwritten);
        }
    }

    private static void Percentiles()
    {
        var d = Distribution.Of(Enumerable.Range(1, 100).Reverse().Select(x => (double)x)
            .Concat(new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }));
        Equal(100, d.Count);
        Near(50.5, d.Mean);
        Near(50, d.P50);
        Near(95, d.P95);
        Near(99, d.P99);
        Near(100, d.Max);
        Equal(new Distribution(0, 0, 0, 0, 0, 0), Distribution.Of([double.NaN, double.NegativeInfinity]));
        Equal(new Distribution(1, 7, 7, 7, 7, 7), Distribution.Of([7]));
        Equal(new Distribution(4, 3, 2, 6, 6, 6), Distribution.Of([2, 6, 2, 2]));
    }

    private static void ConfigNormalization()
    {
        var c = new EmberConfig
        {
            HistorySeconds = -1, MaxTickRecords = int.MaxValue, CensusIntervalSeconds = 0,
            MaxCensusEntities = 0, MaxLogEntries = -1, DefaultProfileSeconds = 10000,
            MaxTraceMegabytes = 0, SlowTickMilliseconds = double.NaN, KeepExports = 0
        };
        c.Normalize();
        Equal(30, c.HistorySeconds);
        Equal(500000, c.MaxTickRecords);
        Equal(5, c.CensusIntervalSeconds);
        Equal(100, c.MaxCensusEntities);
        Equal(0, c.MaxLogEntries);
        Equal(300, c.DefaultProfileSeconds);
        Equal(16, c.MaxTraceMegabytes);
        Near(50, c.SlowTickMilliseconds);
        Equal(1, c.KeepExports);
    }

    private static void CollectorRetention()
    {
        using var c = new Collector(new EmberConfig { MaxTickRecords = 3, MaxLogEntries = 2 });
        for (int i = 1; i <= 7; i++) c.Record(i, i * 2);
        foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            c.Record(invalid, 1);
            c.Record(1, invalid);
        }
        for (int i = 0; i < 4; i++) c.Log("Warning", $"entry {i}: password=log-secret");
        for (int i = 0; i < 203; i++) c.Mark("test", $"event {i}: person@example.org");
        var r = Snapshot(c);
        Sequence(new double[] { 5, 6, 7 }, r.Ticks.Select(t => t.WorkMs));
        Equal(4L, (long)r.Settings["tickRingOverwrittenSinceStart"]!);
        Equal(2L, (long)r.Settings["logRingOverwrittenSinceStart"]!);
        Equal(2, r.Logs.Length);
        True(r.Logs[0].Message.StartsWith("entry 2:", StringComparison.Ordinal), "Logs must retain newest entries in chronological order.");
        True(r.Logs.All(l => !l.Message.Contains("log-secret", StringComparison.Ordinal)), "Collector log secrets leaked.");
        Equal(200, r.Events.Length);
        True(r.Events[0].Message.StartsWith("event 3:", StringComparison.Ordinal), "Event ring chronology incorrect.");
        True(r.Events.All(e => !e.Message.Contains("person@example.org", StringComparison.Ordinal)), "Event email leaked.");
        True(r.Ticks.Zip(r.Ticks.Skip(1)).All(pair => pair.First.EndMonotonicMs <= pair.Second.EndMonotonicMs), "Frame timestamps must be monotonic.");
        using var disabled = new Collector(new EmberConfig { MaxLogEntries = 0 });
        disabled.Log("Error", "must not be retained");
        Equal(0, Snapshot(disabled).Logs.Length);
    }

    private static async Task CollectorSampling()
    {
        using var c = new Collector(new EmberConfig { SlowTickMilliseconds = 50 });
        var census = new Census(Collector.UnixMs, 2, 42, 7, new() { ["game:hare"] = 42 },
            new() { ["game"] = 42 }, 42, false, Distribution.Of([20, 30]), .25);
        c.SetCensus(census);
        c.Record(0, 0);
        c.Record(10, 20);
        c.Record(60, 70);
        c.RecordStage("opaque", 2);
        c.RecordStage("opaque", 6);
        c.RecordStage("opaque", double.NaN);
        c.RecordStage("opaque", -1);
        c.RecordStage("invalid", double.PositiveInfinity);
        var first = await WaitFor(c, r => r.Timeline.Length >= 1);
        var point = first.Timeline[0];
        Equal(3, point.WorkMs.Count);
        Near(70.0 / 3, point.WorkMs.Mean);
        Equal(2, point.IntervalMs.Count);
        Equal(1, point.SlowTicks);
        True(point.WindowSeconds > 0 && double.IsFinite(point.WindowSeconds), "Timed window duration must be finite and positive.");
        Near(3 / point.WindowSeconds, point.LoopRate);
        Equal(new Distribution(2, 4, 2, 6, 6, 6), point.RenderStagesMs["opaque"]);
        True(!point.RenderStagesMs.ContainsKey("invalid"), "Invalid stage values must not create a sample.");
        Equal(2, point.Players);
        Equal(42, point.Entities);
        Equal((int?)7, point.Chunks);
        True(point.CensusAgeSeconds >= 0, "Census age must be nonnegative.");
        Equal(census, first.World);
        c.Record(5, 16);
        c.RecordStage("opaque", 9);
        var second = await WaitFor(c, r => r.Timeline.Length >= 2);
        Equal(1, second.Timeline[1].WorkMs.Count);
        Equal(1, second.Timeline[1].RenderStagesMs["opaque"].Count);
        Near(9, second.Timeline[1].RenderStagesMs["opaque"].Mean);
        Equal(4, second.Ticks.Length);
        Equal(2, second.Timeline[0].RenderStagesMs["opaque"].Count);
        var third = await WaitFor(c, r => r.Timeline.Length >= 3);
        Equal(0, third.Timeline[2].WorkMs.Count);
        Near(0, third.Timeline[2].LoopRate);
        True(!third.Timeline[2].RenderStagesMs.TryGetValue("opaque", out var stage) || stage.Count == 0,
            "Stage samples must reset every runtime window.");
        True(third.Events.All(e => e.Kind != "collector-error"), "Timer swallowed a collector exception.");
    }

    private static async Task CollectorSamplingCaps()
    {
        using var c = new Collector(new EmberConfig { MaxTickRecords = 4 });
        for (int i = 0; i < 10025; i++)
        {
            c.Record(1, 2);
            c.RecordStage("opaque", 3);
        }
        var r = await WaitFor(c, x => x.Timeline.Length > 0);
        Equal(10000, r.Timeline[0].WorkMs.Count);
        Equal(10000, r.Timeline[0].RenderStagesMs["opaque"].Count);
        Equal(4, r.Ticks.Length);
        Equal(10021L, (long)r.Settings["tickRingOverwrittenSinceStart"]!);
    }

    private static async Task CollectorExpiration()
    {
        // Deliberately bypass Normalize to exercise a real expiry without a 30-second test delay.
        using var c = new Collector(new EmberConfig { HistorySeconds = 1 });
        c.Record(1, 2);
        await WaitFor(c, r => r.Ticks.Length == 0 && r.Timeline.Length == 1);
        var before = Snapshot(c);
        c.Dispose();
        c.Record(100, 100);
        await Task.Delay(1100);
        var after = Snapshot(c);
        Equal(0, after.Ticks.Length);
        True(after.Timeline.All(p => before.Timeline.Any(old => old.MonotonicMs == p.MonotonicMs)), "Disposed collector sampled another runtime window.");
    }

    private static void RuntimeCounters()
    {
        using var reader = new RuntimeReader();
        var allocation = new byte[1024 * 1024];
        allocation[0] = 17;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 30) Thread.SpinWait(1000);
        var metrics = reader.Read(watch.Elapsed.TotalSeconds);
        using var json = JsonDocument.Parse(Json.Write(metrics));
        foreach (var property in json.RootElement.EnumerateObject())
            True(double.IsFinite(property.Value.GetDouble()) && property.Value.GetDouble() >= 0, $"Runtime metric {property.Name} must be finite and nonnegative.");
        True(metrics.WorkingSetBytes > 0 && metrics.PrivateBytes > 0 && metrics.ManagedBytes > 0, "Process memory counters should be positive.");
        True(metrics.OsThreads > 0, "At least one OS thread must exist.");
        Near(metrics.CpuOneCorePercent / Environment.ProcessorCount, metrics.CpuMachinePercent);
        var zeroWindow = reader.Read(0);
        True(double.IsFinite(zeroWindow.CpuOneCorePercent) && double.IsFinite(zeroWindow.AllocationBytesPerSecond), "Zero-duration read must avoid division by zero.");
        var description = RuntimeReader.Describe(output);
        Equal(Environment.ProcessorCount, (int)description["logicalProcessors"]!);
        Equal(Environment.ProcessId, (int)description["processId"]!);
        True((long)description["stopwatchFrequency"]! > 0, "Stopwatch frequency missing.");
        True((double)description["processUptimeSeconds"]! >= 0, "Uptime must be nonnegative.");
        GC.KeepAlive(allocation);
    }

    private static async Task RecordingLifecycle()
    {
        string directory = Path.Combine(output, "journal-" + Guid.NewGuid().ToString("N"));
        await using var journal = new SessionJournal(directory);
        using var c = new Collector(new EmberConfig());
        c.Record(1, 11);
        c.Mark("user", "before recording");
        c.BeginRecording(journal.Enqueue);
        c.Record(2, 22);
        c.RecordGpu(3);
        c.Mark("user", "inside recording");
        c.SetContext(new(Collector.UnixMs, new() { ["ViewDistance"] = 128 }, []));
        await Task.Delay(40);
        c.EndRecording();
        c.Record(4, 44);
        await journal.CompleteAsync();
        var batches = File.ReadLines(Path.Combine(directory, "batches.ndjson")).Select(l => JsonSerializer.Deserialize<SessionBatch>(l, Json.Options)!).ToArray();
        Sequence(new[] { 22.0 }, batches.SelectMany(b => b.Frames).Select(f => f.IntervalMs));
        True(batches.SelectMany(b => b.Events).Any(e => e.Message == "inside recording"), "Final partial window lost its marker.");
        True(!batches.SelectMany(b => b.Events).Any(e => e.Message == "before recording"), "Pre-session marker leaked into session.");
        Near(3, batches.Single().Metric!.GpuElapsedMs!.Mean);
        Equal(128, ((JsonElement)batches.Single().Context!.Settings["ViewDistance"]!).GetInt32());
        List<SessionBatch> next = [];
        c.BeginRecording(next.Add);
        c.Record(5, 55);
        await Task.Delay(20);
        c.EndRecording();
        Sequence(new[] { 55.0 }, next.SelectMany(b => b.Frames).Select(f => f.IntervalMs));
        Equal(0L, next[0].Sequence);
    }

    private static async Task JournalBackpressure()
    {
        string directory = Path.Combine(output, "queue-" + Guid.NewGuid().ToString("N"));
        await using var journal = new SessionJournal(directory, queueCapacity: 1);
        for (int i = 0; i < 500; i++) journal.Enqueue(new(i, Point(), [], [], [], null));
        await journal.CompleteAsync();
        var batches = File.ReadLines(Path.Combine(directory, "batches.ndjson")).Select(l => JsonSerializer.Deserialize<SessionBatch>(l, Json.Options)!).ToArray();
        Equal(500L, batches.Length + journal.DroppedBatches);
        True(batches.Length > 0, "Journal did not commit any submitted data.");
        Sequence(batches.Select(b => b.Sequence).Distinct().Order(), batches.Select(b => b.Sequence));
        Equal(new FileInfo(Path.Combine(directory, "batches.ndjson")).Length, journal.BytesWritten);
        True(journal.Error is null, "Normal backpressure should not be a writer exception.");
    }

    private static async Task JournalDiskLimit()
    {
        string directory = Path.Combine(output, "disk-limit-" + Guid.NewGuid().ToString("N"));
        await using var journal = new SessionJournal(directory, maxBytes: 1024);
        journal.Enqueue(new(0, Point(), Enumerable.Range(0, 500).Select(i => new TickRecord(i, i, 1, 16)).ToArray(), [], [], null));
        await journal.CompleteAsync();
        True(journal.Error?.Contains("disk budget", StringComparison.Ordinal) == true, "Disk limit was not reported.");
        Equal(1L, journal.DroppedBatches);
        True(new FileInfo(Path.Combine(directory, "batches.ndjson")).Length <= 1024, "Journal exceeded hard byte limit.");
    }

    private static void TextRedaction()
    {
        (string Input, string Secret)[] cases =
        [
            ("password=hunter-secret", "hunter-secret"), ("passwd: 'two words'", "two words"),
            ("SECRET = \"private value\"", "private value"), ("token=abcd-token", "abcd-token"),
            ("api_key: key-secret", "key-secret"), ("authorization=credential-secret", "credential-secret"),
            (@"at C:\Users\PrivatePerson\Game\server.json", "PrivatePerson"),
            ("at D:/Games/PrivatePerson/data", "PrivatePerson"), ("connected 192.168.1.10:42420", "192.168.1.10"),
            ("contact Person.Name+tag@example.org", "Person.Name+tag@example.org")
        ];
        foreach (var (input, secret) in cases) True(!Redactor.Text(input).Contains(secret, StringComparison.OrdinalIgnoreCase), $"Redaction leaked {secret}: {Redactor.Text(input)}");
        Equal("", Redactor.Text(null));
        Equal("game:entity count=42", Redactor.Text("game:entity count=42"));
        True(Redactor.Text(new string('x', 5000)).Length < 4100, "Unbounded diagnostic text.");
    }

    private static void BearerRedaction()
    {
        const string credential = "eyJhbGciOiJIUzI1NiJ9.private.signature";
        string clean = Redactor.Text("Authorization: Bearer " + credential);
        True(!clean.Contains(credential, StringComparison.Ordinal), "Bearer credential leaked after its scheme: " + clean);
    }

    private static void AdditionalAddresses()
    {
        var failures = new List<string>();
        foreach (var (input, secret) in new[]
        {
            (@"loaded \\private-server\PrivateShare\config.json", "private-server"),
            ("connected [2001:db8:85a3::8a2e:370:7334]:42420", "2001:db8:85a3::8a2e:370:7334")
        })
            if (Redactor.Text(input).Contains(secret, StringComparison.OrdinalIgnoreCase)) failures.Add(Redactor.Text(input));
        True(failures.Count == 0, "Private endpoint/path leaked: " + string.Join("; ", failures));
    }

    private static void JsonRedaction()
    {
        var original = JsonNode.Parse("""
            {"enabled":true,"count":42,"nullable":null,"nested":{"apiKey":"key-private",
            "database":{"connectionString":"db-private","privateKey":"pem-private"},
            "items":[{"password":"password-private","playerName":"player-private","username":"user-private","serverAddress":"host-private"},
            "mail person@example.org", "C:\\Users\\PrivatePerson\\config.json", 7, false]},"normal":"retained"}
            """)!;
        string before = original.ToJsonString();
        var clean = Redactor.Config(original)!;
        string serialized = clean.ToJsonString();
        foreach (string secret in new[] { "key-private", "db-private", "pem-private", "password-private", "player-private", "user-private", "host-private", "person@example.org", "PrivatePerson" })
            True(!serialized.Contains(secret, StringComparison.Ordinal), "Nested JSON leaked " + secret);
        Equal(before, original.ToJsonString());
        Equal(true, clean["enabled"]!.GetValue<bool>());
        Equal(42, clean["count"]!.GetValue<int>());
        True(clean["nullable"] is null, "Null changed during redaction.");
        Equal(7, clean["nested"]!["items"]![3]!.GetValue<int>());
        Equal(false, clean["nested"]!["items"]![4]!.GetValue<bool>());
        Equal("retained", clean["normal"]!.GetValue<string>());
        clean["normal"] = "changed";
        Equal("retained", original["normal"]!.GetValue<string>());
        True(Redactor.Config(null) is null, "Root null must remain null.");
    }

    private static void JsonRedactionBounds()
    {
        var array = new JsonArray(Enumerable.Range(0, 1005).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        Equal(1000, ((JsonArray)Redactor.Config(array)!).Count);
        Equal(1005, array.Count);
        JsonNode deep = JsonValue.Create("too-deep")!;
        for (int i = 0; i < 20; i++) deep = new JsonObject { ["child"] = deep };
        string clean = Redactor.Config(deep)!.ToJsonString();
        True(clean.Contains("[depth limit]", StringComparison.Ordinal), "Depth bound not enforced.");
        True(!clean.Contains("too-deep", StringComparison.Ordinal), "Deep value retained beyond bound.");
    }

    private static StackProfile Profile() => new()
    {
        StartedUtc = DateTimeOffset.UtcNow, DurationSeconds = 2.5,
        Frames = [new("Game.Render", "Game", []), new("InstalledMod.Update " + Hostile, "Shared", ["innocent-mod", "other-mod"]), new("System.Wait", "System.Private.CoreLib", [])],
        Samples = [new(1, 11, true, [0, 1]), new(2, 22, false, [2]), new(3, 11, true, [0, 1]), new(4, 22, true, [0, 2]), new(5, 11, true, [0])],
        Hotspots = [new(0, 1, 4, 1), new(1, 2, 2, 2), new(2, 2, 2, 1)]
    };

    private static void Speedscope()
    {
        var source = Profile();
        using var doc = JsonDocument.Parse(Exporter.Speedscope(source));
        var root = doc.RootElement;
        Equal("https://www.speedscope.app/file-format-schema.json", root.GetProperty("$schema").GetString());
        var frames = root.GetProperty("shared").GetProperty("frames");
        Equal(source.Frames.Count, frames.GetArrayLength());
        True(frames[1].GetProperty("name").GetString()!.Contains(Hostile, StringComparison.Ordinal), "Frame labels must survive JSON round trip.");
        var profiles = root.GetProperty("profiles");
        Equal(2, profiles.GetArrayLength());
        int active = root.GetProperty("activeProfileIndex").GetInt32();
        True(active >= 0 && active < profiles.GetArrayLength(), "Invalid active profile index.");
        var expectedGroups = source.Samples.GroupBy(s => s.ThreadId).OrderByDescending(g => g.Count()).ToArray();
        int total = 0;
        for (int i = 0; i < profiles.GetArrayLength(); i++)
        {
            var p = profiles[i];
            Equal("sampled", p.GetProperty("type").GetString());
            Equal("none", p.GetProperty("unit").GetString());
            var samples = p.GetProperty("samples");
            var weights = p.GetProperty("weights");
            Equal(expectedGroups[i].Count(), samples.GetArrayLength());
            Equal(samples.GetArrayLength(), weights.GetArrayLength());
            Near(0, p.GetProperty("startValue").GetDouble());
            Near(samples.GetArrayLength(), p.GetProperty("endValue").GetDouble());
            int j = 0;
            foreach (var expected in expectedGroups[i])
            {
                var stack = samples[j].EnumerateArray().Select(x => x.GetInt32()).ToArray();
                Sequence(expected.Stack, stack);
                True(stack.All(index => index >= 0 && index < frames.GetArrayLength()), "Out-of-range frame index.");
                Equal(1, weights[j].GetInt32());
                j++;
            }
            total += samples.GetArrayLength();
        }
        Equal(source.Samples.Count, total);
    }

    private static void EmptySpeedscope()
    {
        using var doc = JsonDocument.Parse(Exporter.Speedscope(new StackProfile()));
        Equal(0, doc.RootElement.GetProperty("profiles").GetArrayLength());
        Equal(0, doc.RootElement.GetProperty("shared").GetProperty("frames").GetArrayLength());
    }

    private static void FindingAttribution()
    {
        var baseline = Fixture();
        baseline.Mods.Clear();
        baseline.Profile = null;
        var installed = Fixture();
        installed.Profile = null;
        Sequence(Analysis.Findings(baseline), Analysis.Findings(installed));
        installed.Profile = Profile();
        string findings = Json.Write(Analysis.Findings(installed));
        True(!findings.Contains("innocent-mod", StringComparison.Ordinal), "Installed/sample ownership alone must not blame a mod.");
        True(!findings.Contains("other-mod", StringComparison.Ordinal), "Shared assembly ownership must not imply fault.");
        True(Analysis.LlmInstructions.Contains("Never recommend removing a mod solely because it is installed", StringComparison.Ordinal), "Attribution guardrail missing from analyst instructions.");
        installed.Findings = Analysis.Findings(installed);
        string summary = Analysis.Summary(installed);
        True(summary.Contains("not exact CPU-time percentages", StringComparison.Ordinal), "Summary must distinguish stack samples from CPU time.");
        True(summary.Contains("Public-server CPU", StringComparison.Ordinal), "Remote server scope limitation missing.");
    }

    private static void ModOwnershipResolution()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["VintageHorizons"] = ["vintagehorizons"],
            ["VintagestoryLib"] = [],
            ["Shared"] = ["beta", "alpha"]
        };
        // A module the trace named is the answer, and a known module that belongs to no mod is an
        // answer too: the method's namespace must not override it.
        var named = ProfileOwnership.Resolve("Vintagestory.GameContent.BlockEntityX.Tick(float)", "VintagestoryLib", "/mods/VintageHorizons.dll", map);
        Equal("VintagestoryLib", named.Module);
        Equal(0, named.Mods.Length);
        True(named.Identified, "A named module must count as identified.");
        True(!named.Recovered, "A named module must not be reported as recovered.");
        Equal(null, named.Match);
        var ownedButGuessed = ProfileOwnership.Resolve("VintageHorizons.LodWorker.Tick()", "VintagestoryLib", null, map);
        Equal(0, ownedButGuessed.Mods.Length);
        Equal(null, ownedButGuessed.Match);
        // TraceEvent's placeholder for managed code is not a module.
        True(!ProfileOwnership.IsIdentified("ManagedModule"), "The synthetic managed module must not count as identified.");
        True(!ProfileOwnership.IsIdentified(""), "An empty module must not count as identified.");
        True(!ProfileOwnership.IsIdentified(null), "A null module must not count as identified.");
        True(!ProfileOwnership.IsIdentified("unknown"), "The unknown placeholder must not count as identified.");
        True(ProfileOwnership.IsIdentified("VintagestoryLib"), "A real module name must count as identified.");
        // The raw trace names the module the conversion lost.
        var recovered = ProfileOwnership.Resolve("VintageHorizons.LodWorker.CaptureLoop()", "ManagedModule",
            "/home/user/.config/VintagestoryData/Mods/vintagehorizons-0.3.1/VintageHorizons.dll", map);
        Equal("VintageHorizons", recovered.Module);
        Sequence(new[] { "vintagehorizons" }, recovered.Mods);
        True(recovered.Recovered, "A raw-trace module must be reported as recovered.");
        Equal(null, recovered.Match);
        // Nothing names it: the root namespace, and only against an assembly a mod declares.
        var typed = ProfileOwnership.Resolve("VintageHorizons.LodWorker.MeshLoop()", "ManagedModule", null, map);
        Sequence(new[] { "vintagehorizons" }, typed.Mods);
        Equal(ProfileOwnership.TypeNamespaceMatch, typed.Match);
        True(!typed.Identified, "A type-namespace match is not a module statement.");
        Equal("ManagedModule", typed.Module);
        var none = ProfileOwnership.Resolve("System.Threading.Thread.Sleep(int32)", "ManagedModule", null, map);
        Equal(0, none.Mods.Length);
        Equal(null, none.Match);
        // Owners of a shared assembly come back in a stable order, whatever the map's order was.
        var shared = ProfileOwnership.Resolve("Shared.Thing.Run()", "Shared", null, map);
        Sequence(new[] { "alpha", "beta" }, shared.Mods);
        Equal("VintageHorizons", ProfileOwnership.TypeRoot("VintageHorizons.LodWorker.CaptureLoop()"));
        Equal("dynamicClass", ProfileOwnership.TypeRoot("dynamicClass.lambda_method"));
        Equal("VintageHorizons", ProfileOwnership.ModuleKey(@"C:\Mods\vintagehorizons\VintageHorizons.dll"));
        Equal("VintageHorizons", ProfileOwnership.ModuleKey("/mods/VintageHorizons.exe"));
        Equal("", ProfileOwnership.ModuleKey(null));
    }

    private static void ModSampleTally()
    {
        var frames = new List<ProfileFrame>
        {
            new("A.Run()", "ModA", ["alpha"]),
            new("B.Run()", "Shared", ["beta", "gamma"]),
            new("System.Wait()", "System.Private.CoreLib", []),
            new("D.Run()", "ManagedModule", ["delta"], ProfileOwnership.TypeNamespaceMatch)
        };
        var samples = new List<ProfileSample>
        {
            new(0, 1, true, [0, 1]),
            new(1, 1, true, [1]),
            new(2, 1, false, [2]),
            new(3, 1, true, [3]),
            new(4, 1, true, [0])
        };
        var (mods, unattributed) = ProfileOwnership.Tally(samples, frames);
        Equal(1, unattributed);
        Equal(4, mods.Count);
        var alpha = mods.Single(m => m.Mod == "alpha");
        Equal(1, alpha.SelfSamples);
        Equal(2, alpha.InclusiveSamples);
        Equal(1, alpha.ManagedSelfSamples);
        var beta = mods.Single(m => m.Mod == "beta");
        Equal(2, beta.SelfSamples);
        Equal(2, beta.InclusiveSamples);
        Equal(2, beta.ManagedSelfSamples);
        Equal(1, mods.Single(m => m.Mod == "delta").SelfSamples);
        Equal(1, mods.Single(m => m.Mod == "delta").ManagedSelfSamples);
        // A frame index outside the frame table is not invented as an owner.
        var (empty, allUnattributed) = ProfileOwnership.Tally([new ProfileSample(0, 1, true, [99])], frames);
        Equal(0, empty.Count);
        Equal(1, allUnattributed);
        string note = ProfileOwnership.OwnershipNote(10, 4, 3, 1);
        True(note.Contains("recovered from the raw trace", StringComparison.Ordinal), "Ownership note must name the recovery source.");
        True(note.Contains(ProfileOwnership.TypeNamespaceMatch, StringComparison.Ordinal), "Ownership note must name the heuristic marker.");
    }

    private static void ModAttributionReporting()
    {
        var report = Fixture();
        var profile = new StackProfile
        {
            DurationSeconds = 10,
            Frames =
            [
                new("VintageHorizons.LodWorker.CaptureLoop()", "ManagedModule", ["vintagehorizons"], ProfileOwnership.TypeNamespaceMatch),
                new("Vintagestory.Client.NoObf.ClientMain.Run()", "VintagestoryLib", []),
                new("Work.Burn(int32)", "Ember.Trace.Session.Tests", [])
            ],
            Samples = [new(0, 1, true, [2, 0]), new(1, 1, true, [2]), new(2, 1, false, [1])],
            FramesWithoutModule = 1, FramesMatchedByTypeName = 1
        };
        profile.Hotspots = ProfileMerge.Hotspots(profile.Samples);
        (profile.ModSamples, profile.UnattributedSelfSamples) = ProfileOwnership.Tally(profile.Samples, profile.Frames);
        report.Profile = profile;
        string summary = Analysis.Summary(report);
        True(summary.Contains("### Samples by owning mod", StringComparison.Ordinal), "Summary must answer whose code the samples are.");
        True(summary.Contains("vintagehorizons", StringComparison.Ordinal), "Summary must name the owning mod.");
        True(summary.Contains("(mod by type name)", StringComparison.Ordinal), "A namespace-derived owner must be marked in the hotspot table.");
        True(summary.Contains("no known mod on the leaf frame", StringComparison.Ordinal), "The unowned denominator must be stated.");
        string[] titles = Analysis.Findings(report).Select(f => f.Title).ToArray();
        True(titles.Contains("Mod attribution is incomplete"), "Frames without a module must be reported, not silently unowned.");
        using (var doc = JsonDocument.Parse(Json.Write(report)))
        {
            var p = doc.RootElement.GetProperty("profile");
            Equal("type-namespace", p.GetProperty("frames")[0].GetProperty("match").GetString());
            Equal(1, p.GetProperty("modSamples").GetArrayLength());
            Equal(2, p.GetProperty("unattributedSelfSamples").GetInt32());
            Equal(1, p.GetProperty("framesMatchedByTypeName").GetInt32());
        }
        // A frame with a real module carries no marker at all: the field exists only for fallbacks.
        using (var doc = JsonDocument.Parse(Json.Write(report)))
        {
            var second = doc.RootElement.GetProperty("profile").GetProperty("frames")[1];
            True(!second.TryGetProperty("match", out _), "A module-derived owner must not claim a heuristic match.");
        }
        // Once every frame has a module the data-quality finding is withdrawn rather than repeated.
        profile.FramesWithoutModule = 0;
        profile.FramesMatchedByTypeName = 0;
        True(!Analysis.Findings(report).Any(f => f.Title == "Mod attribution is incomplete"),
            "A fully resolved profile must not be reported as incomplete.");
        using var context = JsonDocument.Parse(Json.Write(LlmContext.Create(report)));
        Equal(1, context.RootElement.GetProperty("profile").GetProperty("modSamples").GetArrayLength());
        True(context.RootElement.GetProperty("profile").TryGetProperty("framesWithoutModule", out _),
            "Compact context must carry the ownership quality counters.");
    }

    private static void FindingEvidence()
    {
        var r = Fixture();
        r.Ticks = [new(Collector.UnixMs, Collector.ClockMs, 5, 200)];
        r.Timeline = [Point() with { Runtime = Runtime() with { GcPauseMilliseconds = 50 }, CensusAgeSeconds = 30, LoopRate = 0 }];
        r.Logs = [new(Collector.UnixMs, "Error", "Synthetic error")];
        r.Profile = new StackProfile { EventsLost = 3, SamplesWithoutStack = 5, SamplesDroppedByLimit = 2, SizeLimitReached = true };
        string[] titles = Analysis.Findings(r).Select(f => f.Title).ToArray();
        foreach (string title in new[] { "Frame pacing spikes", "GC pause pressure", "Client loop stopped producing frames", "Client errors recorded", "Profile is incomplete", "No resolved stack samples", "Remote server internals are unobserved" })
            True(titles.Contains(title), "Expected evidence-based finding: " + title);
    }

    private static RuntimeMetrics Runtime() => new(30, 30.0 / Environment.ProcessorCount,
        10000000, 12000000, 5000000, 6000000, 100000, 1000000000, 4096, 0, 1, 0, 0, 8, 2, 0, 3);

    private static MetricPoint Point() => new(Collector.UnixMs, Collector.ClockMs, 1, 60,
        Distribution.Of([1.5, 2.5]), Distribution.Of([16.5, 17.5]), 0, Runtime(), 1, 42, null, 1, .125,
        new() { ["opaque"] = Distribution.Of([.5, 1.5]) });

    private static void CompactContext()
    {
        var report = Fixture();
        report.Timeline = Enumerable.Range(0, 500).Select(i => Point() with
        {
            UnixMs = 1000 + i * 1000,
            IntervalMs = Distribution.Of([i == 123 ? 1000 : 16.0]),
            Runtime = Runtime() with { GcPauseMilliseconds = i == 321 ? 250 : 0 }
        }).ToArray();
        using var doc = JsonDocument.Parse(Json.Write(LlmContext.Create(report)));
        var root = doc.RootElement;
        Equal(500, root.GetProperty("retainedWindowCount").GetInt32());
        var windows = root.GetProperty("selectedWindows").EnumerateArray().ToArray();
        True(windows.Length <= 41, "Compact context must have a bounded selection of windows.");
        var timestamps = windows.Select(w => w.GetProperty("unixMs").GetInt64()).ToArray();
        Sequence(timestamps.Distinct().Order(), timestamps);
        True(timestamps.Contains(124000) && timestamps.Contains(322000), "Frame and GC extremes must be retained.");
        True(!root.GetProperty("profile").TryGetProperty("samples", out _), "Raw samples should not be duplicated in compact context.");
        var stacks = root.GetProperty("profile").GetProperty("representativeManagedStacks");
        True(stacks.EnumerateArray().Any(s => s.GetProperty("observations").GetInt32() == 2), "Identical stacks must group by content, not array identity.");
        Equal(report.Mods.Count, root.GetProperty("mods").GetArrayLength());
    }

    private static DiagnosticReport Fixture() => new()
    {
        Reason = "Synthetic fixture " + Hostile, GameVersion = "test-1.0", TargetIntervalMs = 1000.0 / 60,
        Environment = new() { ["fixture"] = true }, Settings = new() { ["testValue"] = 3.5 },
        Mods = [new("innocent-mod", "Innocent " + Hostile, "1.0", "Client", "Zip", "innocent.zip", ["Innocent.System"], ["Shared"], [])],
        Timeline = [Point()], Ticks = [new(Collector.UnixMs, Collector.ClockMs, 2.5, 16.5)],
        Profile = Profile(), Limitations = ["Synthetic regression fixture; no real game capture."]
    };

    private static void HeapDumpConfig()
    {
        var c = new EmberConfig { HeapDumpTimeoutSeconds = 0, HeapDumpBufferMegabytes = 99999, HeapDumpMaxNodes = 1 };
        c.Normalize();
        Equal(10, c.HeapDumpTimeoutSeconds);
        Equal(8192, c.HeapDumpBufferMegabytes);
        Equal(1_000_000, c.HeapDumpMaxNodes);
        var d = new EmberConfig { HeapDumpTimeoutSeconds = 999, HeapDumpBufferMegabytes = 1, HeapDumpMaxNodes = int.MaxValue };
        d.Normalize();
        Equal(300, d.HeapDumpTimeoutSeconds);
        Equal(64, d.HeapDumpBufferMegabytes);
        Equal(100_000_000, d.HeapDumpMaxNodes);
        True(d.EnableHeapDump && d.HeapDumpSampledAllocations, "Heap dumps stay available but never automatic: the default is only a permission.");
        Equal(4096, new EmberConfig().HeapDumpBufferMegabytes);
    }

    // The boundary is the measured one, not a guess: the same target and the same heap, with only
    // the cap changed, kept every event at 84% of it and lost the beginning of the walk at 98%.
    // The real client dump that reached this code sat at 102% of a 1024 MiB cap.
    private static void HeapDumpBufferThreshold()
    {
        True(!HeapDumpBuffer.Saturated(899_880_866, 1024), "a trace at 86% of its cap is not treated as truncated");
        True(HeapDumpBuffer.Saturated(864_087_610, 840), "the measured lost trace at 98% of its cap is truncated");
        True(HeapDumpBuffer.Saturated(526_478_382, 512), "the measured lost trace at 98% of a smaller cap is truncated");
        True(HeapDumpBuffer.Saturated(1_096_359_742, 1024), "the real client dump that reported 62% unreachable is truncated");
        True(!HeapDumpBuffer.Saturated(0, 1024) && !HeapDumpBuffer.Saturated(899_880_866, 0),
            "a missing trace size or cap makes no claim about truncation");
        // The first real client failure hit both limits at once while the text named only the node
        // limit. Raising that budget cannot make a trace whose oldest events - the roots - were
        // overwritten readable, so the buffer has to be named first and the second limit still told.
        string both = HeapDumpBuffer.FailureMessage(4_272_926_726, 4096, 40_000_000);
        True(both.Contains("HeapDumpBufferMegabytes above 4075", StringComparison.Ordinal) &&
             both.Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
            "a dump that filled its buffer as well as its object budget names the buffer as the fix and still reports the object budget");
        string nodes = HeapDumpBuffer.FailureMessage(1_500_000_000, 4096, 40_000_000);
        True(nodes.Contains("Raise HeapDumpMaxNodes", StringComparison.Ordinal) &&
             !nodes.Contains("HeapDumpBufferMegabytes above", StringComparison.Ordinal),
            "a dump that only exceeded the object budget keeps that budget as the fix");
        True(HeapDumpBuffer.SaturationNote(4_272_926_726, 4096).Contains("the buffer filled", StringComparison.Ordinal),
            "the saturation note says the buffer filled, not merely that the trace is large");
    }

    // The first real client failure was not one limit but an ordering: the supervisor's kill sat
    // fifteen seconds under the worker's own ceiling, the reduction could not be interrupted at all,
    // and the dump was killed with nothing recorded - so the heap and the budget could not be told
    // apart afterwards. These assertions are that ordering, stated as arithmetic.
    private static void HeapDumpBudgetOrdering()
    {
        foreach (int timeout in new[] { 10, 60, 300 })
        {
            int capture = HeapDumpBudget.CaptureSeconds(timeout);
            int worker = HeapDumpBudget.WorkerSeconds(timeout);
            int kill = HeapDumpBudget.KillSeconds(timeout);
            True(capture > timeout, $"the capture allowance exceeds the dump timeout at {timeout} s");
            True(worker > capture, $"the reduction gets a budget of its own at {timeout} s");
            True(kill >= worker + HeapDumpBudget.PublishGraceSeconds,
                $"the kill leaves the worker its publication grace at {timeout} s: worker {worker} s, kill {kill} s");
        }
        // The reduction follows the dump timeout, so raising that timeout really does buy time at
        // either stage, and it is capped so one dump cannot hold the stack capture indefinitely.
        Equal(HeapDumpBudget.MinimumAnalysisSeconds, HeapDumpBudget.AnalysisSeconds(10));
        Equal(60, HeapDumpBudget.AnalysisSeconds(60));
        Equal(HeapDumpBudget.MaximumAnalysisSeconds, HeapDumpBudget.AnalysisSeconds(300));
        Equal(1, HeapDumpBudget.AnalysisSeconds(60, 1));
        // The measured shape of that client dump: about 70 s of capture, then the reduction of a
        // 4 GiB trace at the measured 8.5 s/GiB. The old kill left 45 s for both.
        True(HeapDumpBudget.WorkerSeconds(60) >= 70 + 45,
            "the worker is allowed the capture the client really took plus the reduction it needs");
        // A dump that ran out of time reports what it measured before the deadline, because that is
        // what separates a heap that outgrew the budget from a budget too small for the heap.
        string late = HeapDumpBudget.TimeoutMessage(HeapDumpBudget.AnalysisStage, 60, 0, 4_272_926_726, 4096, 31_000_000);
        True(late.Contains("reducing the walk to an aggregate", StringComparison.Ordinal) &&
             late.Contains("4075", StringComparison.Ordinal) &&
             late.Contains("walked objects had already been read", StringComparison.Ordinal),
            "the reduction deadline says which stage it hit, how large the trace was and how far it got");
        True(late.Contains("HeapDumpTimeoutSeconds", StringComparison.Ordinal) &&
             !late.Contains("HeapDumpBufferMegabytes", StringComparison.Ordinal) &&
             !late.Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
            "a dump that ran out of time is not sent to a limit that cannot help it");
        string early = HeapDumpBudget.TimeoutMessage(HeapDumpBudget.CaptureStage, 60, 0, 0, 4096, 0);
        True(early.Contains("no trace had been written yet", StringComparison.Ordinal),
            "a capture that never produced a trace says so instead of reporting a heap of zero bytes");
    }

    // A filled EventPipe buffer, an exhausted object budget and a runtime budget that ran out are
    // three different problems with three different fixes, and naming the wrong one sends a reader
    // to raise a limit that cannot help. The kind is stored on the dump; an older record that carries
    // only its text is classified the same way, so a bundle exported before this reads identically.
    private static void HeapDumpFailureKinds()
    {
        string Next(HeapDump dump) => HeapDumpReport.Failure(dump).NextStep;
        True(Next(new HeapDump { Status = "failed", FailureKind = HeapDumpFailure.Buffer }).Contains("HeapDumpBufferMegabytes", StringComparison.Ordinal),
            "a filled buffer is answered with the buffer cap");
        True(Next(new HeapDump { Status = "failed", FailureKind = HeapDumpFailure.NodeLimit }).Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
            "an exhausted object budget is answered with the object budget");
        string time = Next(new HeapDump { Status = "failed", FailureKind = HeapDumpFailure.TimeBudget });
        True(time.Contains("HeapDumpTimeoutSeconds", StringComparison.Ordinal) &&
             !time.Contains("HeapDumpBufferMegabytes", StringComparison.Ordinal) &&
             !time.Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
            "a dump that ran out of time is answered with the time budget alone: " + time);
        True(Next(new HeapDump { Status = "failed", Error = HeapDumpBuffer.FailureMessage(4_272_926_726, 4096, 40_000_000) })
                .Contains("HeapDumpBufferMegabytes", StringComparison.Ordinal),
            "a record written before the kind existed still classifies from its text");
        True(Next(new HeapDump { Status = "failed", Error = HeapDumpBuffer.FailureMessage(1_500_000_000, 4096, 40_000_000) })
                .Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
            "a legacy record that only hit the object budget keeps that budget as the fix");
        True(Next(new HeapDump
        {
            Status = "failed",
            Error = "Heap dump 2 failed: the heap dump exceeded its time budget, and it published nothing within 15 s of being asked to stop."
        }).Contains("HeapDumpTimeoutSeconds", StringComparison.Ordinal),
            "the supervisor's kill message classifies as a time budget without the stored kind");
        True(Next(new HeapDump { Status = "refused", Error = "Heap dump 1 is still running." })
                .Contains("request again", StringComparison.Ordinal),
            "a refusal is answered with when to ask again");
    }

    private static void HeapDumpAssembly()
    {
        string dir = Path.Combine(output, "heapdump-assembly", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var request = new SessionRequest
        {
            Id = "heapdump-assembly", ProcessId = Environment.ProcessId,
            ProcessStartedUtc = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime()),
            StartedUtc = started, ExportDirectory = Path.Combine(dir, "exports")
        };
        File.WriteAllText(Path.Combine(dir, "request.json"), Json.Write(request));
        long now = started.ToUnixTimeMilliseconds();
        var batch = new SessionBatch(1, null, [new(now, 10, 12, 16)], [new(now, "test", "journal event")], [new(now, "info", "journal log")], null);
        File.WriteAllText(Path.Combine(dir, "batches.ndjson"),
            JsonSerializer.Serialize(batch, new JsonSerializerOptions(Json.Options) { WriteIndented = false }) + "\n");
        Dump(dir, new HeapDump
        {
            Index = 1, Status = "completed", Complete = true, NodeCount = 4, EdgeCount = 5, TotalBytes = 100,
            AttributedBytes = 80, UnreachableBytes = 20, TraceBytes = 4096, SpanMilliseconds = 12,
            CapturedUtc = started.AddMinutes(1),
            Holders = [new("ExampleMod.dll", ["example"], 80, 80, 3)], Notes = ["Retained bytes come from a dominator tree."]
        });
        Dump(dir, new HeapDump { Index = 2, Status = "refused", Error = "Heap dump 1 is still running.", CompletedUtc = started.AddSeconds(30) });
        // A dump whose worker died mid-flight must be reported as failed, not omitted. It carries no
        // settled time, so its timeline mark falls back to when it was requested.
        Dump(dir, new HeapDump { Index = 3, Status = "running", RequestedUtc = started.AddSeconds(40) });
        // A dump that had to hold new stack capture back carries the interval, and the timeline marks
        // both ends: no new segment covers that point, which is deliberate and has to be readable.
        Dump(dir, new HeapDump
        {
            Index = 4, Status = "completed", Complete = true, NodeCount = 2, TotalBytes = 50,
            AttributedBytes = 50, CapturedUtc = started.AddMinutes(2).AddSeconds(3),
            PausedStackCapture = true, PausedFromUtc = started.AddMinutes(2),
            ResumedUtc = started.AddMinutes(2).AddSeconds(4.5), PauseMilliseconds = 4500,
            Holders = [new("ExampleMod.dll", ["example"], 50, 100, 2)]
        });
        // The first real client failure: a 4075 MiB trace against a 4096 MiB buffer and more walked
        // objects than the analysis budget allows. The aggregate records both, and the failure text
        // has to name the buffer because that is the limit the other one cannot substitute for.
        Dump(dir, new HeapDump
        {
            Index = 5, Status = "failed", FailureKind = HeapDumpFailure.Buffer,
            TraceBytes = 4_272_926_726, BufferMegabytes = 4096,
            Error = HeapDumpBuffer.FailureMessage(4_272_926_726, 4096, 40_000_000),
            Notes = [HeapDumpBuffer.FailureMessage(4_272_926_726, 4096, 40_000_000)],
            CompletedUtc = started.AddMinutes(3)
        });
        // A dump that produced an aggregate but whose trace filled the buffer: the totals exist and
        // are lower bounds, which the summary has to say without a reader opening report.json.
        Dump(dir, new HeapDump
        {
            Index = 6, Status = "completed", Complete = false, NodeCount = 12, TotalBytes = 300_000_000,
            AttributedBytes = 280_000_000, UnreachableBytes = 20_000_000,
            TraceBytes = 4_272_926_726, BufferMegabytes = 4096, CapturedUtc = started.AddMinutes(3),
            Holders = [new("ExampleMod.dll", ["example"], 280_000_000, 93.3, 10)],
            Notes = [HeapDumpBuffer.SaturationNote(4_272_926_726, 4096)]
        });
        // The second real client failure: a dump that ran out of time and produced nothing at all, not
        // even a partial aggregate. It still has to appear where it happened, and its finding has to
        // send a reader to the time budget rather than to a limit that cannot help.
        Dump(dir, new HeapDump
        {
            Index = 7, Status = "failed", FailureKind = HeapDumpFailure.TimeBudget,
            TraceBytes = 4_272_926_726, BufferMegabytes = 4096, WalkedNodeCount = 31_000_000,
            RequestedUtc = started.AddMinutes(4), CompletedUtc = started.AddMinutes(4).AddSeconds(45),
            Error = HeapDumpBudget.TimeoutMessage(HeapDumpBudget.AnalysisStage, 60, 0, 4_272_926_726, 4096, 31_000_000)
        });
        var report = SessionAssembler.Assemble(dir);
        Equal("1.2.0", report.SchemaVersion);
        Equal(7, report.HeapDumps.Count);
        Sequence(new[] { 1, 2, 3, 4, 5, 6, 7 }, report.HeapDumps.Select(d => d.Index));
        Equal("completed", report.HeapDumps[0].Status);
        Equal(80L, report.HeapDumps[0].AttributedBytes);
        Equal("ExampleMod.dll", report.HeapDumps[0].Holders.Single().Module);
        True(report.HeapDumps[0].Complete, "A balanced dump keeps its completeness flag.");
        Equal("refused", report.HeapDumps[1].Status);
        Equal("Heap dump 1 is still running.", report.HeapDumps[1].Error);
        Equal("failed", report.HeapDumps[2].Status);
        True(report.HeapDumps[2].Error!.Contains("ended before its aggregate"), "An unfinished dump is reported as failed rather than omitted.");
        Equal(0, new DiagnosticReport().HeapDumps.Count);
        True(report.Events.Any(e => e.Kind == "heapdump"), "Each dump is marked in the event timeline.");
        // One event for the walk, plus a park and a release for the dump that needed the target quiet,
        // plus one for the dump whose aggregate exists even though its trace filled the buffer, plus
        // one each for the three dumps and the refusal that produced no heap at all. A request that
        // produced nothing is the one an unexplained interval is most likely to be, so leaving it out
        // of the timeline would point a reader at the game instead of at the dump.
        Equal(9, report.Events.Count(e => e.Kind == "heapdump"));
        True(report.Events.Any(e => e.Kind == "heapdump" && e.Message.Contains("Heap dump 7: failed", StringComparison.Ordinal)),
            "A dump that produced no heap marks the timeline where it ended.");
        True(report.Events.Any(e => e.Kind == "heapdump" && e.Message.Contains("Heap dump 2: refused", StringComparison.Ordinal)),
            "A refusal marks the timeline too.");
        True(report.Events.Any(e => e.Kind == "heapdump" && e.Message.Contains("Heap dump 3: failed", StringComparison.Ordinal)),
            "A dump that never settled falls back to when it was requested.");
        var parked = report.HeapDumps.Single(d => d.Index == 4);
        True(parked.PausedStackCapture, "A dump that held new capture back says so.");
        True(Math.Abs(parked.PauseMilliseconds - 4500) < 0.001, "The held-back interval survives into the report.");
        True(parked.ResumedUtc > parked.PausedFromUtc, "The held-back interval has both ends.");
        True(report.Events.Any(e => e.Message.Contains("held back so the walk describes")), "The timeline marks where new capture was held back.");
        True(report.Events.Any(e => e.Message.Contains("released after 4.5 s")), "The timeline marks the release and states its cost.");
        True(report.Events.Zip(report.Events.Skip(1)).All(p => p.First.UnixMs <= p.Second.UnixMs), "Timeline marks stay ordered.");
        using var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, report.HeapDumps[0], Json.Options);
        True(Encoding.UTF8.GetString(stream.ToArray()).Contains("dominator tree"), "Dump notes travel into the exported report.");
        // The failed dump carries both limits, so a reader is not sent to raise the one budget that
        // cannot help, and the incomplete one is classified rather than merely flagged.
        var failedDump = report.HeapDumps.Single(d => d.Index == 5);
        True(failedDump.Error!.Contains("HeapDumpBufferMegabytes above 4075", StringComparison.Ordinal) &&
             failedDump.Error.Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
            "a failed dump names the buffer first and still reports the object budget it hit");
        Equal("the capture filled its EventPipe buffer, which discarded the oldest events of the walk first",
            HeapDumpReport.Incompleteness(report.HeapDumps.Single(d => d.Index == 6)));
        True(HeapDumpReport.Incompleteness(report.HeapDumps.Single(d => d.Index == 1)) is null,
            "a complete dump has no incompleteness reason");
        // The summary is the first thing a person opens, so a dump that produced nothing and a dump
        // that produced a lower bound both have to appear in it.
        string summary = Analysis.Summary(report);
        True(summary.Contains("## Heap dumps", StringComparison.Ordinal), "The summary lists heap dumps at all.");
        True(summary.Contains("| 5 | failed |", StringComparison.Ordinal) && summary.Contains("no analysis", StringComparison.Ordinal),
            "A dump that produced no analysis is a row in the summary, not an omission.");
        True(summary.Contains("Heap dump 5 was failed", StringComparison.Ordinal), "The summary carries the failure reason.");
        True(summary.Contains("Heap dump 6 is incomplete", StringComparison.Ordinal), "The summary marks a lower bound as one.");
        True(summary.Contains("held back 4.5 s", StringComparison.Ordinal), "The summary states how long a dump held new capture back.");
        string[] dumpFindings = Analysis.Findings(report).Select(f => f.Title).ToArray();
        True(dumpFindings.Contains("Heap dump 5 was not taken") && dumpFindings.Contains("Heap dump 6 is incomplete"),
            "A dump that failed or fell short is a finding, not a silent section.");
        // The next step is chosen by the limit that stopped the dump: a filled buffer, an exhausted
        // object budget and a runtime budget that ran out are three different problems, and naming
        // the wrong one sends a reader to raise a limit that cannot help.
        var findings = Analysis.Findings(report);
        string Next(string title) => findings.Single(f => f.Title == title).NextStep;
        True(Next("Heap dump 5 was not taken").Contains("HeapDumpBufferMegabytes", StringComparison.Ordinal),
            "The finding for a dump that filled its buffer names the buffer cap.");
        True(Next("Heap dump 7 was not taken").Contains("HeapDumpTimeoutSeconds", StringComparison.Ordinal) &&
             !Next("Heap dump 7 was not taken").Contains("HeapDumpBufferMegabytes", StringComparison.Ordinal) &&
             !Next("Heap dump 7 was not taken").Contains("HeapDumpMaxNodes", StringComparison.Ordinal),
            "The finding for a dump that ran out of time names the time budget alone: " + Next("Heap dump 7 was not taken"));
        True(Next("Heap dump 2 was not taken").Contains("request again", StringComparison.Ordinal),
            "The finding for a refusal says when to ask again.");
    }

    // A dump's cost has two shapes that a reader conflates unless the report separates them: the hitch
    // the walk itself causes, measured from the frame journal, and the gap in stack coverage left by
    // the hold around it, which is smaller because a segment that was already attached keeps
    // recording. Both are measured here against a fixture whose timings are exact, together with the
    // growth between dumps, because all three come out of the same assembly step.
    private static void HeapDumpImpactAndDeltas()
    {
        string dir = Path.Combine(output, "heapdump-impact", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        long t0 = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(t0 + ms);
        File.WriteAllText(Path.Combine(dir, "request.json"), Json.Write(new SessionRequest
        {
            Id = "heapdump-impact", ProcessId = Environment.ProcessId,
            ProcessStartedUtc = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime()),
            StartedUtc = At(0), ExportDirectory = Path.Combine(dir, "exports")
        }));
        File.WriteAllText(Path.Combine(dir, "stop.json"), Json.Write(new SessionStop("manual stop", At(200_000))));
        // Two real segment profiles, because the assembler credits coverage from the files that exist
        // rather than from the interval a session claims: the gap below has to be the one the tracer
        // would report, or the split against the dump holds would be measured against a fiction.
        var segments = new[]
        {
            new ProfileSegment { Index = 1, StartedUtc = At(40_000), EndedUtc = At(70_000), Status = "completed", FileName = "profile-1.json", ObservedSamples = 1 },
            new ProfileSegment { Index = 2, StartedUtc = At(120_000), EndedUtc = At(160_000), Status = "completed", FileName = "profile-2.json", ObservedSamples = 1 }
        };
        foreach (var segment in segments)
            File.WriteAllText(Path.Combine(dir, segment.FileName!), Json.Write(new StackProfile
            {
                StartedUtc = segment.StartedUtc, DurationSeconds = (segment.EndedUtc - segment.StartedUtc).TotalSeconds,
                ObservedStackSamples = 1, Frames = [new ProfileFrame("Main", "Game", [])],
                Samples = [new ProfileSample(0, 1, true, [0])], Notes = []
            }));
        File.WriteAllText(Path.Combine(dir, "session.json"), Json.Write(new SessionInfo
        {
            Id = "heapdump-impact", Status = "completed", StartedUtc = At(0), StoppedUtc = At(200_000),
            RecordedSeconds = 200, StackCoverageSeconds = 70, StackCoveragePercent = 35, Segments = [.. segments]
        }));
        // Frames every 100 ms, plus the two the dumps caused and one the game caused inside the third
        // dump's hold: the last one is what must not be charged to the dump that happened to be running.
        var frames = new List<TickRecord>();
        for (long ms = 100; ms <= 200_000; ms += 100) frames.Add(new TickRecord(t0 + ms, ms, 5, 16));
        frames.Add(new TickRecord(t0 + 12_500, 12_500, 9, 2300));   // dump 1's walk: began 200 ms into its hold
        frames.Add(new TickRecord(t0 + 100_500, 100_500, 9, 400));  // dump 3's walk: began 100 ms into its hold
        frames.Add(new TickRecord(t0 + 106_700, 106_700, 9, 1500)); // the game's own collection, 5.2 s into that hold
        MetricPoint Window(long endMs, double gc) => new(t0 + endMs, endMs, 1, 60, Distribution.Of([5.0]), Distribution.Of([16.0]), 0,
            new RuntimeMetrics(100, 12.5, 1_000_000_000, 1_100_000_000, 400_000_000, 500_000_000, 20_000_000, 16_000_000_000,
                5_000_000, gc, 1, 0, 1, 40, 4, 0, 100), 1, 20, null, 1, 0.2, new Dictionary<string, Distribution>());
        long sequence = 0;
        string Line(MetricPoint? metric, TickRecord[] batch) =>
            JsonSerializer.Serialize(new SessionBatch(++sequence, metric, batch, [], [], null),
                new JsonSerializerOptions(Json.Options) { WriteIndented = false });
        File.WriteAllLines(Path.Combine(dir, "batches.ndjson"),
        [
            Line(null, frames.ToArray()),
            Line(Window(12_000, 800), []),      // overlaps dump 1's freeze
            Line(Window(13_000, 100), []),      // also overlaps it
            Line(Window(40_000, 5_000), []),    // long after it: must not be charged to dump 1
            Line(Window(100_500, 200), []),     // overlaps dump 3's freeze
            Line(Window(106_700, 2_000), [])    // the game's hitch: no dump is charged for it
        ]);
        Dump(dir, new HeapDump
        {
            Index = 1, Status = "completed", Complete = true, RequestedUtc = At(9_000), CapturedUtc = At(20_000),
            TotalBytes = 300, NodeCount = 100, AttributedBytes = 200, UnreachableBytes = 100, RootCount = 10,
            PausedStackCapture = true, PausedFromUtc = At(10_000), ResumedUtc = At(20_000), PauseMilliseconds = 10_000,
            Holders = [new("ExampleMod.dll", ["example"], 200, 66.7, 80), new("GameLib.dll", [], 100, 33.3, 20)],
            Types = [new("System.Byte[]", "System.Private.CoreLib.dll", 150, 60), new("System.String", "System.Private.CoreLib.dll", 150, 40)]
        });
        Dump(dir, new HeapDump
        {
            Index = 2, Status = "completed", Complete = true, RequestedUtc = At(49_000), CapturedUtc = At(62_000),
            TotalBytes = 400, NodeCount = 150, AttributedBytes = 300, UnreachableBytes = 100, RootCount = 12,
            PausedStackCapture = true, PausedFromUtc = At(50_000), ResumedUtc = At(62_000), PauseMilliseconds = 12_000,
            Holders = [new("ExampleMod.dll", ["example"], 300, 75, 100), new("GameLib.dll", [], 90, 22.5, 15), new("NewMod.dll", ["newmod"], 10, 2.5, 5)],
            Types = [new("System.Byte[]", "System.Private.CoreLib.dll", 250, 90), new("System.String", "System.Private.CoreLib.dll", 150, 40)]
        });
        Dump(dir, new HeapDump
        {
            Index = 3, Status = "completed", Complete = true, RequestedUtc = At(99_000), CapturedUtc = At(112_000),
            TotalBytes = 420, NodeCount = 155, AttributedBytes = 320, UnreachableBytes = 100, RootCount = 12,
            PausedStackCapture = true, PausedFromUtc = At(100_000), ResumedUtc = At(112_000), PauseMilliseconds = 12_000,
            Holders = [new("ExampleMod.dll", ["example"], 320, 76.2, 110)],
            Types = [new("System.Byte[]", "System.Private.CoreLib.dll", 260, 95)]
        });
        var report = SessionAssembler.Assemble(dir);
        Equal(3, report.HeapDumps.Count);

        // The freeze is the longest completed frame interval that began around the request, and the GC
        // pause is read from the windows it falls in - not from the whole sixty-second hold, which
        // would hand the dump every collection the game made in the meantime.
        var first = report.HeapDumps[0];
        Near(2300, first.FreezeMilliseconds);
        Equal(At(10_200), first.FreezeStartUtc);
        Near(900, first.FreezeGcPauseMilliseconds);
        Near(16, report.HeapDumps[1].FreezeMilliseconds);
        var third = report.HeapDumps[2];
        Near(400, third.FreezeMilliseconds);
        Equal(At(100_100), third.FreezeStartUtc);
        Near(200, third.FreezeGcPauseMilliseconds);
        True(first.Notes.Any(n => n.Contains("stopped the client for 2300 ms", StringComparison.Ordinal) &&
                                  n.Contains("blocking collection", StringComparison.Ordinal)),
            "the dump states the hitch the walk caused and where the number comes from: " + string.Join(" | ", first.Notes));
        // A journal with no frame in the window says the hitch is unknown, which is not the same answer
        // as a hitch of zero, and a dump that produced no heap has no hitch to measure at all.
        var unmeasured = new HeapDump { Status = "completed", PausedFromUtc = At(0), ResumedUtc = At(1_000) };
        True(!HeapDumpImpact.MeasureFreeze(unmeasured, [], []) && unmeasured.FreezeStartUtc is null && unmeasured.FreezeMilliseconds == 0 &&
             unmeasured.Notes.Any(n => n.Contains("unmeasured in this journal", StringComparison.Ordinal)),
            "a dump whose recording kept no frames reports an unknown hitch rather than a free one");
        True(!HeapDumpImpact.MeasureFreeze(new HeapDump { Status = "failed", PausedFromUtc = At(0) }, frames, []),
            "a dump that produced no heap gets no freeze");

        // The hold is not the gap: only the first and third holds fell outside a segment, and one of
        // those was covered because a segment was still attached through it.
        var session = report.Session!;
        Near(130, session.UncoveredSeconds);
        Near(22, session.UncoveredDuringHeapDumpHoldsSeconds);
        Near(34, session.HeapDumpHoldSeconds);
        True(session.Notes.Any(n => n.Contains("Stack coverage gap", StringComparison.Ordinal) &&
                                    n.Contains("22.0 s", StringComparison.Ordinal) &&
                                    n.Contains("34.0 s", StringComparison.Ordinal)),
            "the coverage gap names the part the dump holds cost and how long those holds were: " + string.Join(" | ", session.Notes));

        // Growth between consecutive dumps: totals subtract, owners and types are matched by name, a
        // vanished row keeps a zero predecessor and an unchanged row is not repeated.
        True(report.HeapDumps[0].DeltaSincePrevious is null, "the first dump has nothing to compare with");
        var delta = report.HeapDumps[1].DeltaSincePrevious!;
        Equal(1, delta.PreviousIndex);
        Near(42, delta.SecondsBetween);
        Equal(100L, delta.TotalBytes);
        Equal(50, delta.NodeCount);
        Equal(2, delta.RootCount);
        True(!delta.LowerBound, "two complete dumps compare directly");
        Sequence(new[] { "ExampleMod.dll", "GameLib.dll", "NewMod.dll" }, delta.Holders.Select(h => h.Name));
        Equal(300L, delta.Holders[0].Bytes);
        Equal(200L, delta.Holders[0].PreviousBytes);
        Equal(0L, delta.Holders[2].PreviousBytes);
        Equal("newmod", delta.Holders[2].Detail);
        Sequence(new[] { "System.Byte[]" }, delta.Types.Select(t => t.Name));
        Equal(2, report.HeapDumps[2].DeltaSincePrevious!.PreviousIndex);
        True(HeapDumpDelta.Between(new HeapDump { Index = 1, Status = "completed" }, report.HeapDumps[1]).LowerBound,
            "a difference against a dump that is a lower bound is itself a lower bound");

        // The incident that contains the walk has to name it, and the game's own collection inside the
        // same hold has to stay unowned.
        var walk = report.Incidents.Single(i => i.HeapDumpIndex == 1);
        True(walk.Evidence.Any(e => e.Contains("is the heap walk the capture requested", StringComparison.Ordinal)),
            "the incident that contains the walk says so instead of leaving an unexplained stall: " + string.Join(" | ", walk.Evidence));
        True(walk.Hypotheses.Count > 0 && walk.Hypotheses[0].Title.Contains("Heap dump 1", StringComparison.Ordinal),
            "the dump is the first hypothesis for its own incident rather than a mod: " + string.Join(" | ", walk.Hypotheses.Select(h => h.Title)));
        True(report.Incidents.Any(i => i.HeapDumpIndex is null && i.PeakFrameMs > 1000),
            "the game's own collection inside a hold is not charged to the dump that was running");

        // What the client pays, and what changed, both have to be readable without opening report.json.
        string title = "Heap dump 1 stopped the client for 2.30 s";
        True(report.Findings.Any(f => f.Title == title && f.Evidence.Contains("incident-", StringComparison.Ordinal)),
            "the finding prices the dump and points at the incident that names it: " + string.Join(" | ", report.Findings.Select(f => f.Title)));
        True(!report.Findings.Any(f => f.Title == "Heap dump 2 stopped the client for 0.02 s"),
            "a walk that did not stop the client is measured on the dump but is not raised as a finding");
        string summary = Analysis.Summary(report);
        True(summary.Contains("| Client freeze |", StringComparison.Ordinal) && summary.Contains("2.30 s", StringComparison.Ordinal),
            "the summary table prices the freeze, not only the hold");
        True(summary.Contains("### Change since the previous dump", StringComparison.Ordinal) &&
             summary.Contains("| 1 → 2 | 42 s | +100 B | +50 objects | ExampleMod.dll +100 B", StringComparison.Ordinal),
            "the summary states the growth between two dumps and where it went: " + summary);
        True(summary.Contains("Stack coverage gap: 130.0s uncovered, 22.0s of it inside heap-dump holds", StringComparison.Ordinal),
            "the summary separates the coverage gap from the holds, which are not the same number");
    }

    private static void Dump(string directory, HeapDump dump)
    {
        string work = Path.Combine(directory, "heapdumps", dump.Index.ToString());
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "heapdump.json"), Json.Write(dump));
    }

    private static void ExportBundle()
    {
        RequireViewer();
        var report = Fixture();
        var oldCulture = CultureInfo.CurrentCulture;
        string path;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            path = Exporter.Export(report, Path.Combine(output, "bundles"));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        string[] expected = ["report.json", "llm-context.json", "report.html", "SUMMARY.md", "GPT-REPORT.md", "ASK-AN-LLM.md", "incidents.json", "incidents.csv", "metrics.csv", "frames.csv", "profile.speedscope.json", "manifest.json"];
        Sequence(expected.Order(), entries.Keys.Order());
        True(entries.Keys.All(name => !name.Contains('/') && !name.Contains('\\') && !name.Contains("..", StringComparison.Ordinal)), "ZIP entry path must be a safe flat filename.");
        using var manifest = JsonDocument.Parse(ReadEntry(entries["manifest.json"]));
        Equal(report.Id, manifest.RootElement.GetProperty("id").GetString());
        var listed = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Equal(entries.Count - 1, listed.Length);
        Sequence(entries.Keys.Where(name => name != "manifest.json").Order(), listed.Select(f => f.GetProperty("name").GetString()!).Order());
        foreach (var file in listed)
        {
            string name = file.GetProperty("name").GetString()!;
            using var stream = entries[name].Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Equal(file.GetProperty("bytes").GetInt64(), bytes.Length);
            Equal(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
        }
        string rawJson = ReadEntry(entries["report.json"]);
        using var parsed = JsonDocument.Parse(rawJson);
        Equal(report.Reason, parsed.RootElement.GetProperty("reason").GetString());
        Equal(report.Ticks.Length, parsed.RootElement.GetProperty("ticks").GetArrayLength());
        var roundTrip = JsonSerializer.Deserialize<DiagnosticReport>(rawJson, Json.Options)!;
        Equal(report.Id, roundTrip.Id);
        Equal(report.Profile!.Samples.Count, roundTrip.Profile!.Samples.Count);
        string html = ReadEntry(entries["report.html"]);
        True(!html.Contains(Hostile, StringComparison.Ordinal), "Hostile report content escaped the inline script.");
        True(!rawJson.Contains("</script", StringComparison.OrdinalIgnoreCase), "JSON serializer emitted an executable script terminator.");
        True(!html.Contains("/*EMBER_DATA*/null", StringComparison.Ordinal), "Viewer data placeholder was not replaced.");
        True(html.Contains(rawJson, StringComparison.Ordinal), "Viewer must contain the complete serialized capture.");
        var scripts = Regex.Matches(html, @"<script\b[^>]*>([\s\S]*?)</script\s*>", RegexOptions.IgnoreCase);
        True(scripts.Cast<Match>().Any(m => m.Groups[1].Value.Contains(rawJson, StringComparison.Ordinal)), "Complete report JSON must be contained within one script element.");
        using var profile = JsonDocument.Parse(ReadEntry(entries["profile.speedscope.json"]));
        Equal(report.Profile.Samples.Count, profile.RootElement.GetProperty("profiles").EnumerateArray().Sum(p => p.GetProperty("samples").GetArrayLength()));
        string[] framesCsv = ReadEntry(entries["frames.csv"]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Equal(2, framesCsv.Length);
        Equal(4, framesCsv[1].Trim().Split(',').Length);
        True(framesCsv[1].Contains(",2.5,16.5", StringComparison.Ordinal), "CSV numbers must use invariant decimal points.");
        string[] metricsCsv = ReadEntry(entries["metrics.csv"]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Equal(2, metricsCsv.Length);
        Equal(metricsCsv[0].Split(',').Length, metricsCsv[1].Split(',').Length);
        True(!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Any(p => p.EndsWith(".tmp", StringComparison.Ordinal)), "Export left temporary files behind.");
    }

    private static void ExportRetention()
    {
        RequireViewer();
        string dir = Path.Combine(output, "retention", Guid.NewGuid().ToString("N"));
        var first = Fixture();
        first.Profile = null;
        string old = Exporter.Export(first, dir, 1);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-2));
        var second = Fixture();
        second.Profile = null;
        string latest = Exporter.Export(second, dir, 1);
        Sequence(new[] { latest }, Directory.GetFiles(dir, "*.zip"));
        using var zip = ZipFile.OpenRead(latest);
        True(zip.GetEntry("profile.speedscope.json") is null, "Monitoring-only bundle should omit stack profile file.");
        using var doc = JsonDocument.Parse(ReadEntry(zip.GetEntry("report.json")!));
        True(!doc.RootElement.TryGetProperty("profile", out _), "Null profile must be omitted from report JSON.");
    }

    private static void RequireViewer()
    {
        True(typeof(Exporter).Assembly.GetManifestResourceNames().Contains("Ember.Report.html"),
            "Export prerequisite missing: Ember.Core must embed viewer/report.html as Ember.Report.html. Build again after the viewer is available.");
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static DiagnosticReport Snapshot(Collector collector)
    {
        var report = new DiagnosticReport();
        collector.Fill(report);
        return report;
    }

    private static async Task<DiagnosticReport> WaitFor(Collector c, Func<DiagnosticReport, bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            var report = Snapshot(c);
            if (report.Events.Any(e => e.Kind == "collector-error")) throw new InvalidOperationException(Json.Write(report.Events));
            if (predicate(report)) return report;
            await Task.Delay(20);
        }
        throw new TimeoutException("Collector did not produce the expected state within eight seconds.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}>; actual <{actual}>.");
    }

    private static void Near(double expected, double actual, double tolerance = 1e-8)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance * Math.Max(1, Math.Abs(expected)))
            throw new InvalidOperationException($"Expected approximately {expected:R}; actual {actual:R}.");
    }

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}]; actual [{string.Join(", ", actual)}].");
    }
}
