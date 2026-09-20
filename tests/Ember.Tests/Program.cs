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
            Sync("compact LLM context preserves extremes without raw sample duplication", CompactContext),
            Sync("export ZIP manifest hashes, hostile HTML and round-trip JSON", ExportBundle),
            Sync("export without profile and export retention", ExportRetention)
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
