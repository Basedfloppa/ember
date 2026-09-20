using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Ember.Core;

namespace Ember.Trace;

internal static class WorkerFiles
{
    public static T Read<T>(string path)
    {
        // Protocol snapshots are atomically replaced while other processes read them.
        // Windows readers must permit replacement, otherwise an ordinary snapshot can fail
        // with a sharing violation and abort an otherwise healthy recording.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream, Json.Options)
                    ?? throw new InvalidDataException($"Missing JSON value in {Path.GetFileName(path)}.");
            }
            catch (Exception e) when (attempt < 20 && e is IOException or UnauthorizedAccessException)
            { Thread.Sleep(10); } // NTFS replacement can briefly expose a delete-pending/missing name.
        }
    }

    public static void Write<T>(string path, T value, bool overwrite = true)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json.Options);
                stream.Flush(flushToDisk: true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (overwrite && File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path, overwrite);
                    break;
                }
                catch (IOException) when (!overwrite && File.Exists(path)) { break; }
                catch (Exception e) when (attempt < 20 && e is IOException or UnauthorizedAccessException)
                { Thread.Sleep(10); }
            }
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    // The game can disappear with redirected pipes still installed. Logging must not prevent export.
    public static void Emit(string message, bool error = false)
    {
        try { (error ? Console.Error : Console.Out).WriteLine(message); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public static void CommitSegment(string directory, StackProfile profile, ProfileSegment segment)
    {
        // The parsed profile replaces the raw trace as the recovery payload only after both files
        // have been atomically published and flushed to disk, in that order.
        Write(Path.Combine(directory, "profile.json"), profile);
        Write(Path.Combine(directory, "segment.json"), segment);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Path.GetFileName(path) is "profile.json" or "segment.json" or "segment.lock") continue;
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Emit("Completed segment artifact cleanup failed: " + ex.Message, error: true); }
        }
    }

    public static ProcessStartInfo StartInfo(params string[] args)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Cannot locate worker executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    public static bool ParentAlive(SessionRequest request)
    {
        try
        {
            using var parent = Process.GetProcessById(request.ProcessId);
            return !parent.HasExited && Math.Abs((parent.StartTime.ToUniversalTime() - request.ProcessStartedUtc.UtcDateTime).TotalMilliseconds) < 10;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static long Size(string directory)
    {
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(path).Length; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return total;
    }

    public static async Task ReadCommands(Action<bool> stop, CancellationToken cancel)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[1];
        bool overflow = false;
        try
        {
            while (await Console.In.ReadAsync(buffer.AsMemory(), cancel) != 0)
            {
                if (buffer[0] == '\n') { Dispatch(); text.Clear(); overflow = false; }
                else if (text.Length < 64) text.Append(buffer[0]);
                else overflow = true;
            }
            Dispatch(); // EOF alone is deliberately not a stop: the parent may have exited.
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        void Dispatch()
        {
            if (overflow) return;
            string command = text.ToString().Trim();
            if (command.Equals("stop", StringComparison.OrdinalIgnoreCase)) stop(false);
            if (command.Equals("cancel", StringComparison.OrdinalIgnoreCase)) stop(true);
        }
    }
}
