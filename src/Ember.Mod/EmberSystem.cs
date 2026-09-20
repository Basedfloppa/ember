using System.Diagnostics;
using Ember.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Ember.Mod;

public sealed class EmberSystem : ModSystem
{
    private ICoreClientAPI? api;
    private EmberConfig config = new();
    private Collector? collector;
    private SessionRecording? session;
    private GpuFrameTimer? gpu;
    private readonly List<(IRenderer Renderer, EnumRenderStage Stage)> observers = [];
    private string output = "", sessionsDirectory = "", configDirectory = "", tracerPath = "";
    private long censusListener;
    private double frameStart, previousFrameEnd;
    private int exporting, recovering;
    private volatile bool disposed, leaving;
    private bool previousPause;
    private bool? previousFocus;
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        api = capi;
        try { config = capi.LoadModConfig<EmberConfig>("emberdiagnostics.json") ?? new(); }
        catch (Exception e) { capi.Logger.Warning("[Ember] Could not read configuration; using defaults: {0}", e.Message); }
        config.Normalize();
        capi.StoreModConfig(config, "emberdiagnostics.json");
        output = capi.GetOrCreateDataPath("Ember/exports");
        sessionsDirectory = capi.GetOrCreateDataPath("Ember/sessions");
        configDirectory = capi.GetOrCreateDataPath("ModConfig");
        tracerPath = Path.Combine(Path.GetDirectoryName(typeof(EmberSystem).Assembly.Location)!, "tracer", "Ember.Trace.dll");
        collector = new(config);
        capi.Logger.EntryAdded += OnLog;
        collector.Mark("world", "Client collector started; no server component is used.");
        foreach (var stage in Enum.GetValues<EnumRenderStage>())
        {
            double stageStart = 0;
            var first = new Observer(-10000, () =>
            {
                if (leaving) return;
                stageStart = Collector.ClockMs;
                if (stage != EnumRenderStage.Before) return;
                frameStart = stageStart;
                bool paused = capi.IsGamePaused;
                if (paused != previousPause) { previousPause = paused; collector.Mark("pause", paused ? "Game paused" : "Game resumed"); }
                if (capi.World is Vintagestory.Client.NoObf.ClientMain game)
                {
                    bool focused = game.Platform.IsFocused;
                    if (previousFocus != focused) { previousFocus = focused; collector.Mark("focus", focused ? "Window focused" : "Window unfocused"); }
                }
                if (config.EnableGpuTiming)
                {
                    gpu ??= new GpuFrameTimer(collector.RecordGpu, message => collector.Mark("gpu-timing", message));
                    gpu.BeginFrame();
                }
            });
            var last = new Observer(10000, () =>
            {
                if (leaving) return;
                double end = Collector.ClockMs;
                if (stageStart > 0) collector.RecordStage(stage.ToString(), end - stageStart);
                if (stage != EnumRenderStage.Done || frameStart <= 0) return;
                gpu?.EndFrame();
                collector.Record(end - frameStart, previousFrameEnd > 0 ? end - previousFrameEnd : 0);
                previousFrameEnd = end;
                frameStart = 0;
            });
            foreach (var observer in new[] { first, last })
            {
                capi.Event.RegisterRenderer(observer, stage, "ember-observer");
                observers.Add((observer, stage));
            }
        }
        censusListener = capi.Event.RegisterGameTickListener(_ => Census(), config.CensusIntervalSeconds * 1000);
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;
        RegisterCommands();
        _ = Recover();
        capi.Logger.Notification("[Ember] Ready. Use .ember start, play normally, then .ember stop or leave the world. Exports: {0}", output);
    }

    private void OnLevelFinalize() { Census(); collector?.Mark("world", "Client world loaded."); }
    private void OnLeaveWorld()
    {
        if (leaving) return;
        collector?.Mark("world", "Leaving world; active session will finalize automatically.");
        FinishSession("left world or server");
        leaving = true;
        gpu?.Dispose();
    }
    private void Census()
    {
        if (disposed || leaving || api is null || collector is null) return;
        try
        {
            collector.SetCensus(ClientSnapshot.Census(api, config));
            if (session?.IsRecording == true)
            {
                var context = ClientSnapshot.Context(api);
                AddGpu(context.Environment);
                collector.SetContext(context);
            }
        }
        catch (Exception e) { collector.Mark("census-error", e.GetType().Name + ": " + e.Message); }
    }
    private void AddGpu(Dictionary<string, object?> environment)
    {
        environment["gpuTimingAvailable"] = gpu?.IsAvailable ?? false;
        environment["gpuTimingStatus"] = gpu?.Status ?? (config.EnableGpuTiming ? "Not initialized" : "Disabled by configuration");
        environment["gpuTimingSubmitted"] = gpu?.Submitted ?? 0;
        environment["gpuTimingCompleted"] = gpu?.Completed ?? 0;
        environment["gpuTimingSkipped"] = gpu?.Skipped ?? 0;
    }
    private void OnLog(EnumLogType type, string message, params object[] args)
    {
        if (type is not (EnumLogType.Warning or EnumLogType.Error or EnumLogType.Fatal)) return;
        try { collector?.Log(type.ToString(), args.Length > 0 ? string.Format(message, args) : message); }
        catch (FormatException) { collector?.Log(type.ToString(), message); }
        catch { /* Diagnostics must never interrupt the game's logger. */ }
    }

    private void RegisterCommands()
    {
        var p = api!.ChatCommands.Parsers;
        var command = api.ChatCommands.Create("ember").WithDescription("Record a complete client performance session.")
            .HandleWith(_ => TextCommandResult.Success(Help));
        command.BeginSubCommand("help").WithDescription("Show Ember commands").HandleWith(_ => TextCommandResult.Success(Help)).EndSubCommand();
        command.BeginSubCommand("start").WithDescription("Start recording; automatically save on stop or world exit")
            .HandleWith(_ => StartSession()).EndSubCommand();
        command.BeginSubCommand("profile").WithDescription("Start a session, optionally with a duration in seconds")
            .WithArgs(p.OptionalIntRange("seconds", 5, 28800, 0)).HandleWith(args => StartSession((int)args[0])).EndSubCommand();
        command.BeginSubCommand("stop").WithDescription("Finish recording and save one complete report")
            .HandleWith(_ => TextCommandResult.Success(FinishSession("manual stop") ? "Finishing the complete session. The report is being prepared in the background…" : "No recording to stop. Use .ember start.")).EndSubCommand();
        command.BeginSubCommand("cancel").WithDescription("Discard the current recording")
            .HandleWith(_ => TextCommandResult.Success(FinishSession("cancelled by user", true) ? "Cancelling this recording…" : "No recording to cancel.")).EndSubCommand();
        command.BeginSubCommand("status").WithDescription("Show recording status").HandleWith(_ => TextCommandResult.Success(Status())).EndSubCommand();
        command.BeginSubCommand("health").WithDescription("Client FPS, CPU, memory and recording status").HandleWith(_ => Health()).EndSubCommand();
        command.BeginSubCommand("export").WithDescription("Save a recent monitoring snapshot without stopping an active session")
            .HandleWith(_ => StartExport()).EndSubCommand();
        command.BeginSubCommand("mark").WithDescription("Optional note about what happened")
            .WithArgs(p.All("description")).HandleWith(args => { collector?.Mark("user", (string)args[0]); return TextCommandResult.Success("Timeline marker saved."); }).EndSubCommand();
        command.BeginSubCommand("recover").WithDescription("Retry unfinished recordings after a crash or export failure")
            .HandleWith(args => { _ = Recover(); return TextCommandResult.Success("Checking saved recordings. Completed reports remain in the export folder."); }).EndSubCommand();
        command.BeginSubCommand("folder").WithDescription("Open finished reports")
            .HandleWith(_ =>
            {
                try { Process.Start(new ProcessStartInfo(output) { UseShellExecute = true }); }
                catch { }
                return TextCommandResult.Success("Reports: " + output);
            }).EndSubCommand();
    }
    private const string Help = "Ember — record once, play normally\n.ember start — start a full session\n.ember stop — finish and save one report\nLeaving the world/server also stops and saves automatically.\n.ember profile [seconds] — same recording, with an optional automatic timer\n.ember status / .ember health — recording status and current performance\n.ember mark <description> — optional note\n.ember folder — open reports; give GPT the included GPT-REPORT.md\n.ember export — recent monitoring snapshot\n.ember cancel — discard this recording\n.ember recover — retry an unfinished export\nNo server component or server permissions needed.";

    private string Status() => session is null ? "Not recording. Use .ember start, play, then .ember stop or leave the world."
        : $"Session {session.Request.Id[..8]}: {session.State} · {session.Elapsed:hh\\:mm\\:ss}\nMonitoring history is saved for the session automatically. Exports: {output}";
    private TextCommandResult Health()
    {
        var report = new DiagnosticReport();
        collector?.Fill(report);
        var point = report.Timeline.LastOrDefault();
        if (point is null) return TextCommandResult.Success("Ember is warming up; retry in a few seconds.\n" + Status());
        return TextCommandResult.Success($"{point.LoopRate:F1} FPS | frame p99 {point.IntervalMs.P99:F1} ms | render submission {point.WorkMs.Mean:F1} ms\nCPU {point.Runtime.CpuOneCorePercent:F0}% of one core / {point.Runtime.CpuMachinePercent:F1}% normalized | RAM {point.Runtime.WorkingSetBytes / 1048576:F0} MiB | allocations {point.Runtime.AllocationBytesPerSecond / 1048576:F1} MiB/s\nGC pause {point.Runtime.GcPauseMilliseconds:F1} ms/window | client entities {point.Entities} | chunks {point.Chunks?.ToString() ?? "unavailable"}\n" + Status());
    }
    private (DiagnosticReport Report, Dictionary<string, string> Paths) Snapshot(string reason)
    {
        Census();
        var captured = ClientSnapshot.Capture(api!, collector!, config, output, reason);
        AddGpu(captured.Report.Environment);
        return captured;
    }
    private TextCommandResult StartSession(int durationSeconds = 0)
    {
        if (leaving || disposed) return TextCommandResult.Error("The world is closing.");
        if (session?.IsActive == true) return TextCommandResult.Error("A session is already recording or finishing. " + Status());
        if (Volatile.Read(ref exporting) != 0) return TextCommandResult.Error("A snapshot is being exported; retry when it finishes.");
        try
        {
            var snapshot = Snapshot(durationSeconds > 0 ? $"{durationSeconds}s session" : "complete gameplay session");
            session = new SessionRecording(tracerPath, sessionsDirectory, output, collector!, snapshot.Report,
                snapshot.Paths, config, configDirectory, Notify, durationSeconds);
            collector!.SetContext(ClientSnapshot.Context(api!));
            return TextCommandResult.Success("Starting the profiler. Play normally—stop with .ember stop or simply leave the world/server. One complete report will be saved automatically.");
        }
        catch (Exception e) { return TextCommandResult.Error("Could not start recording: " + Redactor.Text(e.Message)); }
    }
    private bool FinishSession(string reason, bool cancel = false)
    {
        if (session?.IsRecording != true) return false;
        DiagnosticReport? final = null;
        if (!cancel && !disposed)
        {
            try { final = Snapshot(reason).Report; }
            catch (Exception e) { collector?.Mark("final-context", "Using last saved context: " + e.GetType().Name); }
        }
        return session.Stop(reason, cancel, final);
    }
    private TextCommandResult StartExport()
    {
        if (Interlocked.CompareExchange(ref exporting, 1, 0) != 0) return TextCommandResult.Error("A snapshot export is already in progress.");
        try
        {
            collector?.Mark("export", "Monitoring snapshot requested");
            var snapshot = Snapshot("manual monitoring snapshot");
            _ = Task.Run(() =>
            {
                try
                {
                    ClientSnapshot.EnrichFiles(snapshot.Report, snapshot.Paths, config, configDirectory);
                    string file = Exporter.Export(snapshot.Report, output, config.KeepExports);
                    Notify("Snapshot saved: " + file + (session?.IsRecording == true ? "\nYour full session is still recording." : ""));
                }
                catch (Exception e) { Notify("Snapshot export failed: " + Redactor.Text(e.Message)); }
                finally { Volatile.Write(ref exporting, 0); }
            });
            return TextCommandResult.Success("Saving a monitoring snapshot in the background…");
        }
        catch (Exception e) { Volatile.Write(ref exporting, 0); return TextCommandResult.Error("Snapshot failed: " + Redactor.Text(e.Message)); }
    }
    private async Task Recover()
    {
        if (Interlocked.CompareExchange(ref recovering, 1, 0) != 0) return;
        try { await Task.Run(() => SessionRecording.RecoverOrphans(tracerPath, sessionsDirectory, Notify)); }
        finally { Volatile.Write(ref recovering, 0); }
    }
    private void Notify(string message)
    {
        if (disposed || leaving || api is null) return;
        try
        {
            api.Logger.Notification("[Ember] " + message);
            api.Event.EnqueueMainThreadTask(() => { if (!disposed && !leaving) api.ShowChatMessage("[Ember] " + message); }, "ember-notification");
        }
        catch (ObjectDisposedException) { }
    }
    public override void Dispose()
    {
        if (disposed) return;
        if (!leaving) FinishSession("client disposed or shutting down");
        disposed = true;
        if (api is not null)
        {
            api.Logger.EntryAdded -= OnLog;
            api.Event.LevelFinalize -= OnLevelFinalize;
            api.Event.LeaveWorld -= OnLeaveWorld;
            api.Event.UnregisterGameTickListener(censusListener);
            foreach (var (renderer, stage) in observers) api.Event.UnregisterRenderer(renderer, stage);
        }
        gpu?.Dispose();
        collector?.Dispose();
        // Do not kill the supervisor: it must finish the report after disconnect or process exit.
        base.Dispose();
    }
    private sealed class Observer(double order, Action callback) : IRenderer
    {
        public double RenderOrder => order;
        public int RenderRange => int.MaxValue;
        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => callback();
        public void Dispose() { }
    }
}
