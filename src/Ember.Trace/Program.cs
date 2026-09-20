using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text.Json;
using Ember.Core;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Ember.Trace;

internal static class Program
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] is "--session" or "--recover")
            return await SessionWorker.Run(args[1], args[0] == "--recover");
        if (args.Length == 2 && args[0] == "--segment-private")
            return await SessionSegment.Run(args[1]);
        // Conversion is isolated so cancellation/time/resource limits can terminate TraceLog itself.
        if (args.Length == 2 && args[0] == "--parse-private")
        {
            try { ProfileParser.Parse(args[1]); return 0; }
            catch (Exception ex) { WorkerFiles.Emit($"Trace parsing failed: {ex.Message}", error: true); return 1; }
        }
        string? temporary = null;
        using var cancel = new CancellationTokenSource();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); stop.TrySetResult(); };
        Console.CancelKeyPress += handler;
        try
        {
            Options options = Options.Parse(args);
            Directory.CreateDirectory(options.Output);
            string destination = Path.Combine(options.Output, "profile.json");
            if (File.Exists(destination)) throw new IOException("The output directory already contains profile.json; use a fresh work directory.");
            temporary = Path.Combine(options.Output, ".ember-trace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            _ = Task.Run(async () =>
            {
                // Read fixed-size commands, not unbounded lines supplied over stdin.
                var command = new System.Text.StringBuilder();
                var buffer = new char[1];
                bool overflow = false;
                try
                {
                    while (await Console.In.ReadAsync(buffer.AsMemory(), cancel.Token) != 0)
                    {
                        char c = buffer[0];
                        if (c == '\n')
                        {
                            string line = command.ToString().Trim();
                            if (!overflow && line.Equals("cancel", StringComparison.OrdinalIgnoreCase)) { cancel.Cancel(); stop.TrySetResult(); return; }
                            if (!overflow && line.Equals("stop", StringComparison.OrdinalIgnoreCase)) stop.TrySetResult();
                            command.Clear(); overflow = false;
                        }
                        else if (command.Length < 64) command.Append(c);
                        else overflow = true;
                    }
                    if (!overflow && command.ToString().Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase)) { cancel.Cancel(); stop.TrySetResult(); }
                    else if (!overflow && command.ToString().Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) stop.TrySetResult();
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            });
            CaptureInfo info = await Capture(options, temporary, stop.Task, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(temporary, "capture.json"), Json.Write(info), cancel.Token);
            await ParseInChild(temporary, options.MaxMb, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            File.Move(Path.Combine(temporary, "profile.json"), destination);
            WorkerFiles.Emit("DONE");
            return 0;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return 2; }
        catch (Exception ex)
        {
            if (cancel.IsCancellationRequested) return 2;
            WorkerFiles.Emit($"Ember.Trace: {ex.Message}", error: true);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            // The stdin reader runs on a background thread; do not wait for a terminal read.
            if (temporary is not null)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { Directory.Delete(temporary, recursive: true); break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (attempt == 4) WorkerFiles.Emit($"Temporary trace cleanup failed: {ex.Message}", error: true);
                        else await Task.Delay(100);
                    }
                }
            }
        }
    }

    internal static async Task<CaptureInfo> Capture(Options options, string directory, Task stop, CancellationToken cancel,
        Action? ready = null, bool preservePartial = false)
    {
        long limit = options.MaxMb * 1024L * 1024;
        // Reserve a quarter of the byte budget for stop-time CLR rundown.
        long stopAt = limit * 3 / 4;
        var sizeStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long bytes = 0;
        bool truncated = false;
        var providers = new[]
        {
            new EventPipeProvider(SampleProfilerTraceEventParser.ProviderName, EventLevel.Informational),
            new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Verbose,
                (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
                       ClrTraceEventParser.Keywords.Exception | ClrTraceEventParser.Keywords.Contention |
                       ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader))
        };
        using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        startTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var startupWatch = new CancellationTokenSource();
        _ = CancelStartupOnStop();
        using var session = await new DiagnosticsClient(options.Pid).StartEventPipeSessionAsync(providers,
            requestRundown: true, circularBufferMB: Math.Min(64, options.MaxMb), token: startTimeout.Token);
        startupWatch.Cancel();
        using var copyCancellation = new CancellationTokenSource();
        await using var output = new FileStream(Path.Combine(directory, "capture.nettrace"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Task copy = Copy();
        if (ready is null) WorkerFiles.Emit("READY");
        else ready();
        try
        {
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(options.Seconds), cancel), stop, sizeStop.Task, copy);
            bool budgetTriggeredStop = sizeStop.Task.IsCompleted;
            var ended = DateTimeOffset.UtcNow;
            // Stop and stream draining MUST overlap: rundown can exceed the IPC pipe buffer.
            using var shutdown = new CancellationTokenSource(cancel.IsCancellationRequested ? TimeSpan.FromSeconds(5) : ShutdownTimeout);
            Task stopping = session.StopAsync(shutdown.Token);
            try
            {
                await Task.WhenAll(stopping, copy).WaitAsync(shutdown.Token);
            }
            catch (Exception) when (cancel.IsCancellationRequested) { throw new OperationCanceledException(cancel); }
            catch (Exception) when (preservePartial && bytes > 0)
            {
                truncated = true;
                copyCancellation.Cancel();
                session.EventStream.Dispose();
                try { await copy.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            catch (OperationCanceledException) { throw new TimeoutException("EventPipe stop/rundown did not finish within 20 seconds."); }
            await output.FlushAsync();
            return new(options.Pid, ended, bytes, budgetTriggeredStop || truncated, truncated, options.Map, options.MaxSamples);
        }

        finally
        {
            copyCancellation.Cancel();
            session.EventStream.Dispose();
            try { await copy.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* Original capture failure is reported by Main. */ }
        }

        async Task CancelStartupOnStop()
        {
            try
            {
                await stop.WaitAsync(startupWatch.Token);
                startTimeout.Cancel();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        async Task Copy()
        {
            byte[] buffer = new byte[65536];
            while (true)
            {
                int count = await session.EventStream.ReadAsync(buffer, copyCancellation.Token);
                if (count == 0) break;
                int keep = (int)Math.Min(count, limit - bytes);
                if (keep > 0) await output.WriteAsync(buffer.AsMemory(0, keep), copyCancellation.Token);
                bytes += keep;
                if (bytes >= stopAt) sizeStop.TrySetResult();
                if (keep != count) truncated = true;
                // Continue draining after the hard file cap so stop/rundown cannot deadlock.
            }
        }
    }

    internal static async Task ParseInChild(string directory, int maxMb, CancellationToken cancel)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the worker executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--parse-private");
        start.ArgumentList.Add(directory);
        using var child = Process.Start(start) ?? throw new IOException("Could not launch the trace parser.");
        // Read continuously, but keep only bounded diagnostics from third-party conversion code.
        Task<string> errors = ReadBounded(child.StandardError);
        Task<string> stdout = ReadBounded(child.StandardOutput);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            Task exited = child.WaitForExitAsync(timeout.Token);
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(100, timeout.Token));
                timeout.Token.ThrowIfCancellationRequested();
                child.Refresh();
                if (!child.HasExited && child.WorkingSet64 > 1536L * 1024 * 1024)
                    throw new IOException("Trace parsing exceeded its 1.5 GiB memory budget.");
                long disk = Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);
                if (disk > Math.Max(256L, maxMb * 4L) * 1024 * 1024)
                    throw new IOException("Trace parsing exceeded its temporary file budget.");
            }
            await exited;
            string diagnostic = await errors;
            await stdout;
            if (child.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(diagnostic) ? "Trace parser failed." : diagnostic.Trim());
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("Trace parsing did not finish within 90 seconds.");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader)
    {
        var text = new System.Text.StringBuilder();
        char[] buffer = new char[1024];
        int n;
        while ((n = await reader.ReadAsync(buffer)) != 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(n, 8192 - text.Length));
        return text.ToString();
    }
}
