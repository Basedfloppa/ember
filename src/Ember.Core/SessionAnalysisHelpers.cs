using System.Text;
using System.Text.Json;

namespace Ember.Core;

internal sealed class Reservoir<T>(int capacity, int seed = 1729)
{
    private readonly Random random = new(seed);
    public List<T> Items { get; } = [];
    public long Observed { get; private set; }
    public void Add(T item)
    {
        Observed++;
        if (Items.Count < capacity) Items.Add(item);
        else if (capacity > 0)
        {
            long slot = random.NextInt64(Observed);
            if (slot < capacity) Items[(int)slot] = item;
        }
    }
}

internal static class SessionFiles
{
    public static T? Read<T>(string path, long maxBytes = 256L * 1024 * 1024)
    {
        if (!File.Exists(path)) return default;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > maxBytes) throw new InvalidDataException($"{Path.GetFileName(path)} exceeds the {maxBytes} byte read bound.");
        return JsonSerializer.Deserialize<T>(stream, Json.Options);
    }

    public static string SafeSegmentPath(string directory, string file)
    {
        if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file) || file.Contains(':') ||
            file.Split('/', '\\').Any(p => p is ".." or "."))
            throw new InvalidDataException("Segment path must be relative and cannot traverse directories.");
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, file));
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Segment path escapes recording directory.");
        for (string? p = full; p is not null && p.Length >= root.Length; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Segment path contains a symbolic link/reparse point.");
        return full;
    }

    // ReadLine allocates without a bound. Discard oversized lines through their newline instead.
    public static IEnumerable<(string? Text, bool Terminated)> Lines(string path, int maxChars = 16 * 1024 * 1024)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var line = new StringBuilder();
        var buffer = new char[8192];
        bool oversized = false;
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < count; i++)
            {
                char c = buffer[i];
                if (c == '\n')
                {
                    yield return (oversized ? null : line.ToString(), true);
                    line.Clear(); oversized = false;
                }
                else if (!oversized)
                {
                    if (line.Length >= maxChars) { oversized = true; line.Clear(); }
                    else line.Append(c);
                }
            }
        if (line.Length > 0 || oversized) yield return (oversized ? null : line.ToString(), false);
    }

    public static double UnionMilliseconds(IEnumerable<(double Start, double End)> intervals)
    {
        double total = 0, end = double.NegativeInfinity;
        foreach (var x in intervals.Where(x => x.End > x.Start).OrderBy(x => x.Start))
        {
            total += Math.Max(0, x.End - Math.Max(x.Start, end));
            end = Math.Max(end, x.End);
        }
        return total;
    }
}
