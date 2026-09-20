using System.Diagnostics;
using Ember.Core;

namespace Ember.Trace;

internal static class SessionSegment
{
    // One process captures and then parses. The supervisor may overlap the next capture with this parse.
    public static async Task<int> Run(string directory)
    {
        using var segmentLock = new FileStream(Path.Combine(directory, "segment.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var input = new CancellationTokenSource();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => WorkerFiles.ReadCommands(_ => stop.TrySetResult(), input.Token));
        var segment = WorkerFiles.Read<ProfileSegment>(Path.Combine(directory, "segment.json"));
        var request = WorkerFiles.Read<SessionRequest>(Path.Combine(directory, "request.json"));
        string sessionDirectory = Path.GetFullPath(Path.Combine(directory, "..", ".."));
        var supervisor = WorkerFiles.Read<SessionInfo>(Path.Combine(sessionDirectory, "session.json"));
        _ = WatchSupervisor();
        var options = new Options(request.ProcessId, request.SegmentSeconds, directory, request.MaxTraceMegabytes,
            request.ModMap, request.SegmentSamples);
        string checkpoint = Path.Combine(directory, "capture.json");
        try
        {
            // Save enough to attempt conversion if this worker or the target dies during capture.
            WorkerFiles.Write(checkpoint, new CaptureInfo(request.ProcessId, DateTimeOffset.UtcNow, 0,
                false, true, request.ModMap, request.SegmentSamples));
            CaptureInfo info = await Program.Capture(options, directory, stop.Task, CancellationToken.None, () =>
            {
                segment.StartedUtc = DateTimeOffset.UtcNow;
                WorkerFiles.Write(Path.Combine(directory, "segment.json"), segment);
                WorkerFiles.Emit("READY");
            }, preservePartial: true);
            WorkerFiles.Write(checkpoint, info);
            segment.EndedUtc = info.EndedUtc;
            segment.TraceBytes = info.TraceBytes;
            segment.Status = "parsing";
            WorkerFiles.Write(Path.Combine(directory, "segment.json"), segment);
            WorkerFiles.Emit("CAPTURED");
            var watch = Stopwatch.StartNew();
            ProfileParser.Parse(directory);
            segment.ParseMilliseconds = watch.Elapsed.TotalMilliseconds;
            var profile = WorkerFiles.Read<StackProfile>(Path.Combine(directory, "profile.json"));
            segment.StartedUtc = profile.StartedUtc;
            segment.EndedUtc = profile.StartedUtc.AddSeconds(profile.UsableDurationSeconds ?? profile.DurationSeconds);
            segment.ObservedSamples = profile.ObservedStackSamples;
            segment.RetainedSamples = profile.Samples.Count;
            segment.Status = "completed";
            WorkerFiles.CommitSegment(directory, profile, segment);
            WorkerFiles.Emit("DONE");
            return 0;
        }
        catch (Exception ex)
        {
            segment.Status = "failed";
            if (segment.EndedUtc == default) segment.EndedUtc = DateTimeOffset.UtcNow;
            segment.Error = ex.Message;
            WorkerFiles.Write(Path.Combine(directory, "segment.json"), segment);
            WorkerFiles.Emit(ex.Message, error: true);
            return 1;
        }
        finally { input.Cancel(); }

        async Task WatchSupervisor()
        {
            try
            {
                var identity = new SessionRequest { ProcessId = supervisor.SupervisorProcessId, ProcessStartedUtc = supervisor.SupervisorStartedUtc ?? default };
                while (!input.IsCancellationRequested)
                {
                    if (!WorkerFiles.ParentAlive(identity)) { stop.TrySetResult(); return; }
                    await Task.Delay(500, input.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
