using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ember.Core;

namespace Ember.Mod;

internal sealed class TraceController : IDisposable
{
    private readonly object gate = new();
    private Process? process;
    private bool disposed;
    public bool Running { get { lock (gate) return process is not null; } }

    public async Task<StackProfile?> Capture(string tracer, string directory, int seconds, int maxMb,
        Dictionary<string, string[]> modMap, Action<string> status)
    {
        string work = Path.Combine(directory, "capture-" + Guid.NewGuid().ToString("N"));
        Process child;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TraceController));
            if (process is not null) throw new InvalidOperationException("A profile is already running.");
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, "map.json"), Json.Write(modMap));
            var start = new ProcessStartInfo(FindDotnet())
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(tracer)!
            };
            foreach (var arg in new[] { tracer, "--pid", Environment.ProcessId.ToString(), "--seconds", seconds.ToString(),
                "--output", work, "--max-mb", maxMb.ToString(), "--map", Path.Combine(work, "map.json") }) start.ArgumentList.Add(arg);
            child = new Process { StartInfo = start };
            try { child.Start(); }
            catch { child.Dispose(); Directory.Delete(work, true); throw; }
            process = child;
        }
        try
        {
            Task<string> errors = child.StandardError.ReadToEndAsync();
            Task output = Task.Run(async () =>
            {
                while (await child.StandardOutput.ReadLineAsync() is { } line)
                    if (line.StartsWith("READY", StringComparison.Ordinal)) status("Stack capture started. Reproduce the problem now.");
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 180));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { child.Kill(true); await child.WaitForExitAsync(); throw new TimeoutException("Profiler timed out while collecting or resolving symbols."); }
            await output;
            string error = await errors;
            if (child.ExitCode == 2) return null;
            if (child.ExitCode != 0) throw new InvalidOperationException($"Profiler exited {child.ExitCode}: {Redactor.Text(error)}");
            string path = Path.Combine(work, "profile.json");
            if (!File.Exists(path)) throw new IOException("Profiler did not produce profile.json.");
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<StackProfile>(stream, Json.Options)
                ?? throw new IOException("Profiler produced an empty result.");
        }
        finally
        {
            lock (gate) { process = null; child.Dispose(); }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Directory.Delete(work, true); break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 4) status("Temporary capture cleanup failed: " + e.GetType().Name);
                    else await Task.Delay(100);
                }
            }
        }
    }

    public bool Request(string command)
    {
        lock (gate)
        {
            if (process is null || process.HasExited) return false;
            try { process.StandardInput.WriteLine(command); process.StandardInput.Flush(); return true; }
            catch (IOException) { return false; }
        }
    }
    internal static string FindDotnet()
    {
        string name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (root is not null && File.Exists(Path.Combine(root, name))) return Path.Combine(root, name);
        // Runtime path is <dotnet-root>/shared/Microsoft.NETCore.App/<version>/.
        var dir = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        for (int i = 0; i < 5 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, name))) return Path.Combine(dir.FullName, name);
        return name;
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            if (process is null) return;
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }
}
