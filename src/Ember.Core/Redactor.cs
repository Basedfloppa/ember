using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ember.Core;

public static partial class Redactor
{
    [GeneratedRegex(@"(?i)(password|passwd|secret|token|api.?key|authorization|credential|connectionstring|privatekey|sessionkey|signature|cookie)")]
    private static partial Regex SecretKey();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b")]
    private static partial Regex Ip();
    [GeneratedRegex(@"(?i)(?:[a-z]:[\\/]|\\\\[^\\\s]+\\|/(?:home|Users|var|mnt|tmp)/)[^\r\n\""<>|]*")]
    private static partial Regex PathText();
    [GeneratedRegex(@"(?i)(password|passwd|secret|token|api.?key|authorization)\s*[:=]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s,;]+)")]
    private static partial Regex SecretText();
    [GeneratedRegex(@"(?i)\bauthorization\s*[:=]\s*(?:Bearer|Basic)\s+[^\s,;]+")]
    private static partial Regex AuthText();
    [GeneratedRegex(@"(?i)\[[0-9a-f:]+(?:%[\w]+)?\](?::\d+)?|\b(?:[0-9a-f]{0,4}:){2,}[0-9a-f:]{0,39}(?:%\w+)?")]
    private static partial Regex Ipv6();
    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Email();
    public static string Text(string? input)
    {
        string text = input ?? "";
        if (text.Length > 4000) text = text[..4000] + " [truncated]";
        text = AuthText().Replace(text, "Authorization=[redacted]");
        text = SecretText().Replace(text, "$1=[redacted]");
        text = PathText().Replace(text, "[path]");
        return Email().Replace(Ipv6().Replace(Ip().Replace(text, "[ip]"), "[ip]"), "[email]");
    }
    public static JsonNode? Config(JsonNode? node, int depth = 0)
    {
        if (depth > 16) return JsonValue.Create("[depth limit]");
        if (node is JsonObject obj)
        {
            var clean = new JsonObject();
            foreach (var (key, value) in obj)
                clean[key] = SecretKey().IsMatch(key) || key.Contains("player", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("address", StringComparison.OrdinalIgnoreCase) || key.Contains("username", StringComparison.OrdinalIgnoreCase)
                    ? JsonValue.Create("[redacted]") : Config(value, depth + 1);
            return clean;
        }
        if (node is JsonArray arr) return new JsonArray(arr.Take(1000).Select(x => Config(x, depth + 1)).ToArray());
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return JsonValue.Create(Text(s));
        return node?.DeepClone();
    }
}
