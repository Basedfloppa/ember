using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ember.Core;

public static class Exporter
{
    public static string Export(DiagnosticReport report, string directory, int keepExports = 20)
    {
        Directory.CreateDirectory(directory);
        IncidentAnalysis.Analyze(report);
        report.Findings = Analysis.Findings(report);
        string safeId = new string(report.Id.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        string stem = $"ember-client-{report.CreatedUtc:yyyyMMdd-HHmmss}-{safeId}";
        string work = Path.Combine(directory, "." + stem + ".tmp");
        string zip = Path.Combine(directory, stem + ".zip");
        Directory.CreateDirectory(work);
        try
        {
            string reportPath = Path.Combine(work, "report.json");
            using (var stream = File.Create(reportPath)) JsonSerializer.Serialize(stream, report, Json.Options);
            File.WriteAllText(Path.Combine(work, "llm-context.json"), Json.Write(LlmContext.Create(report)));
            File.WriteAllText(Path.Combine(work, "SUMMARY.md"), Analysis.Summary(report));
            File.WriteAllText(Path.Combine(work, "ASK-AN-LLM.md"), Analysis.LlmInstructions);
            File.WriteAllText(Path.Combine(work, "GPT-REPORT.md"), GptReport.Create(report));
            using (var incidents = File.Create(Path.Combine(work, "incidents.json"))) JsonSerializer.Serialize(incidents, report.Incidents, Json.Options);
            using (var metrics = new StreamWriter(Path.Combine(work, "metrics.csv"))) MetricsCsv(report, metrics);
            using (var frames = new StreamWriter(Path.Combine(work, "frames.csv"))) FramesCsv(report, frames);
            using (var incidents = new StreamWriter(Path.Combine(work, "incidents.csv")))
            {
                incidents.WriteLine("id,start_unix_ms,end_unix_ms,context_start_unix_ms,context_end_unix_ms,peak_frame_ms,slow_frames,confidence,stack_coverage_pct,retained_thread_observations");
                foreach (var i in report.Incidents) incidents.WriteLine(string.Join(",", new object[] { i.Id, i.StartUnixMs, i.EndUnixMs,
                    i.ContextStartUnixMs, i.ContextEndUnixMs, i.PeakFrameMs, i.SlowFrames, i.Confidence, i.StackCoveragePercent, i.Samples.Count }.Select(Format)));
            }
            using var template = new StreamReader(typeof(Exporter).Assembly.GetManifestResourceStream("Ember.Report.html")!);
            string html = template.ReadToEnd();
            const string marker = "/*EMBER_DATA*/null";
            int markerAt = html.IndexOf(marker, StringComparison.Ordinal);
            if (markerAt < 0) throw new InvalidDataException("Embedded viewer is missing its data marker.");
            // Stream the large payload rather than allocating a second report-sized HTML string.
            // JSON's default encoder escapes <, > and &, including hostile </script> sequences.
            using (var htmlFile = File.Create(Path.Combine(work, "report.html")))
            {
                htmlFile.Write(Encoding.UTF8.GetBytes(html[..markerAt]));
                using (var data = File.OpenRead(reportPath)) data.CopyTo(htmlFile);
                htmlFile.Write(Encoding.UTF8.GetBytes(html[(markerAt + marker.Length)..]));
            }
            if (report.Profile is { } profile)
            {
                using var speedscope = File.Create(Path.Combine(work, "profile.speedscope.json"));
                JsonSerializer.Serialize(speedscope, SpeedscopeData(profile), Json.Options);
            }
            var files = Directory.GetFiles(work).Order().Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new { name = Path.GetFileName(path), bytes = stream.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
            }).ToArray();
            File.WriteAllText(Path.Combine(work, "manifest.json"), Json.Write(new { schemaVersion = "1.0.0", report.Id, files }));
            ZipFile.CreateFromDirectory(work, zip + ".tmp", CompressionLevel.Optimal, false);
            File.Move(zip + ".tmp", zip);
            foreach (var old in new DirectoryInfo(directory).GetFiles("ember-client-*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keepExports))
                try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return zip;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Delete(zip + ".tmp"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static string Speedscope(StackProfile p) => Json.Write(SpeedscopeData(p));

    private static object SpeedscopeData(StackProfile p)
    {
        var profiles = p.Samples.GroupBy(s => s.ThreadId).OrderByDescending(g => g.Count()).Select(g => new
        {
            type = "sampled", name = $"Thread {g.Key} (thread observations)", unit = "none",
            startValue = 0, endValue = g.Count(), samples = g.Select(s => s.Stack).ToArray(),
            weights = g.Select(_ => 1).ToArray()
        }).ToArray();
        return new Dictionary<string, object>
        {
            ["$schema"] = "https://www.speedscope.app/file-format-schema.json", ["name"] = "Ember sampled thread activity",
            ["exporter"] = "Ember 1.1.0", ["activeProfileIndex"] = 0,
            ["shared"] = new { frames = p.Frames.Select(f => new { name = $"{f.Name} [{f.Module}]" }).ToArray() },
            ["profiles"] = profiles
        };
    }

    private static void MetricsCsv(DiagnosticReport r, TextWriter b)
    {
        b.WriteLine("unix_ms,window_s,fps,render_mean_ms,render_p99_ms,frame_p50_ms,frame_p95_ms,frame_p99_ms,frame_max_ms,cpu_one_core_pct,cpu_machine_pct,working_set_bytes,managed_bytes,allocation_bytes_per_s,gc_pause_ms,gen0,gen1,gen2,entities,chunks,census_age_s,collector_ms,gpu_elapsed_mean_ms,gpu_elapsed_p95_ms,gpu_elapsed_max_ms");
        foreach (var t in r.Timeline)
            b.WriteLine(string.Join(",", new object?[] { t.UnixMs, t.WindowSeconds, t.LoopRate, t.WorkMs.Mean, t.WorkMs.P99,
                t.IntervalMs.P50, t.IntervalMs.P95, t.IntervalMs.P99, t.IntervalMs.Max, t.Runtime.CpuOneCorePercent,
                t.Runtime.CpuMachinePercent, t.Runtime.WorkingSetBytes, t.Runtime.ManagedBytes, t.Runtime.AllocationBytesPerSecond,
                t.Runtime.GcPauseMilliseconds, t.Runtime.Gen0, t.Runtime.Gen1, t.Runtime.Gen2, t.Entities, t.Chunks, t.CensusAgeSeconds, t.CollectorMs,
                t.GpuElapsedMs?.Mean, t.GpuElapsedMs?.P95, t.GpuElapsedMs?.Max }.Select(Format)));
    }
    private static void FramesCsv(DiagnosticReport r, TextWriter b)
    {
        b.WriteLine("end_unix_ms,end_monotonic_ms,render_submission_ms,frame_interval_ms");
        foreach (var t in r.Ticks) b.WriteLine(string.Join(",", new object[] { t.EndUnixMs, t.EndMonotonicMs, t.WorkMs, t.IntervalMs }.Select(Format)));
    }
    private static string Format(object? o) => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
}
