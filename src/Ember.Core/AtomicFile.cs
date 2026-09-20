using System.Text.Json;

namespace Ember.Core;

public static class AtomicFile
{
    public static void WriteJson<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json.Options);
                stream.Flush(true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path, true);
                    break;
                }
                catch (Exception e) when (attempt < 20 && e is IOException or UnauthorizedAccessException)
                { Thread.Sleep(10); }
            }
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public static T? ReadJson<T>(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, Json.Options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
}
