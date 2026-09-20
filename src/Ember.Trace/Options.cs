using System.Text.Json;

namespace Ember.Trace;

internal sealed record Options(int Pid, int Seconds, string Output, int MaxMb, Dictionary<string, string[]> Map, int MaxSamples = 200_000)
{
    public static Options Parse(string[] args)
    {
        const string usage = "Usage: dotnet Ember.Trace.dll --pid N --seconds 5..300 --output absolute-workdir --max-mb 16..1024 --map assembly-mod-map.json [--max-samples 1..200000], or --session absolute-directory, or --recover absolute-directory";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || args[i] is not ("--pid" or "--seconds" or "--output" or "--max-mb" or "--map" or "--max-samples") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException(usage);
        }
        if (new[] { "--pid", "--seconds", "--output", "--max-mb", "--map" }.Any(key => !values.ContainsKey(key))) throw new ArgumentException(usage);
        int Number(string name, int min, int max) => int.TryParse(values[name], out int n) && n >= min && n <= max
            ? n : throw new ArgumentException($"{name} must be between {min} and {max}. {usage}");
        int pid = Number("--pid", 1, int.MaxValue);
        if (pid == Environment.ProcessId) throw new ArgumentException("The target PID must be a different process.");
        if (!Path.IsPathFullyQualified(values["--output"])) throw new ArgumentException("--output must be an absolute directory path.");
        string output = Path.GetFullPath(values["--output"]);
        var mapFile = new FileInfo(values["--map"]);
        if (!mapFile.Exists || mapFile.Length > 4 * 1024 * 1024) throw new ArgumentException("The assembly map must exist and be at most 4 MiB.");
        using var stream = mapFile.OpenRead();
        var source = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
            ?? throw new ArgumentException("The assembly map must be a JSON object of string arrays.");
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (module, owners) in source)
        {
            if (owners is null || owners.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Assembly map owners must be nonempty strings.");
            string key = ModuleKey(module);
            map[key] = (map.GetValueOrDefault(key) ?? []).Concat(owners).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        return new(pid, Number("--seconds", 5, 300), output, Number("--max-mb", 16, 1024), map,
            values.ContainsKey("--max-samples") ? Number("--max-samples", 1, 200_000) : 200_000);
    }

    internal static string ModuleKey(string path)
    {
        string name = path.Replace('\\', '/').Split('/')[^1];
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4] : name;
    }
}

internal sealed record CaptureInfo(int Pid, DateTimeOffset EndedUtc, long TraceBytes,
    bool SizeLimitReached, bool Truncated, Dictionary<string, string[]> Map, int MaxSamples = 200_000,
    int MaxConversionEvents = 2_000_000);
