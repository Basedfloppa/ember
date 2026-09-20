using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Ember.Session.GameTests;

// No Ember reference: this tests the installed package's commands and durable protocol.
public sealed class SessionDriver : ModSystem, IRenderer
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private ICoreClientAPI api = null!;
    private string evidence = "", scenario = "", phase = "loading";
    private int generation, step;
    private long listener, lastFrame;
    private bool characterConfirmed, finished, spiked;
    private readonly Stopwatch elapsed = new(), recording = new();
    private readonly List<double> baselineFrames = [], recordingFrames = [];
    private readonly List<object> baselineWindows = [];
    private double duration;
    private long baselineStart;
    public double RenderOrder => 20000;
    public int RenderRange => int.MaxValue;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        api = capi;
        evidence = api.GetOrCreateDataPath("EmberSessionTest");
        scenario = Environment.GetEnvironmentVariable("EMBER_SESSION_SCENARIO") ?? throw new InvalidOperationException("Scenario missing");
        duration = double.Parse(Environment.GetEnvironmentVariable("EMBER_SESSION_DURATION") ?? "35", System.Globalization.CultureInfo.InvariantCulture);
        generation = Directory.GetFiles(evidence, "world-*.json").Length + 1;
        Write($"world-{generation}.json", new { generation, processId = Environment.ProcessId, utc = DateTimeOffset.UtcNow });
        Record("driver-start", new { generation, scenario });
        api.Event.LevelFinalize += Loaded;
        api.Event.LeaveWorld += Leaving;
        api.Event.RegisterRenderer(this, EnumRenderStage.Done, "ember-session-test");
        Status("loading");
    }

    private void Loaded()
    {
        var ember = api.ModLoader.GetModSystem("Ember.Mod.EmberSystem");
        Record("world-loaded", new {
            singleplayer = api.IsSinglePlayer,
            role = Member(api.World.Player, "RoleCode"),
            privileges = api.World.Player.Privileges,
            emberClient = ember?.ShouldLoad(EnumAppSide.Client), emberServer = ember?.ShouldLoad(EnumAppSide.Server)
        });
        elapsed.Restart();
        listener = api.Event.RegisterGameTickListener(Tick, 100);
        Status("warming");
    }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        try
        {
            if (!elapsed.IsRunning || finished) return;
            if (!characterConfirmed)
            {
                var dialog = api.Gui.OpenedGuis.FirstOrDefault(d => d.GetType().Name == "GuiDialogCreateCharacter");
                if (dialog is not null)
                {
                    var confirm = dialog.GetType().GetMethod("OnConfirm", Members) ?? throw new MissingMethodException("Character confirmation handler");
                    confirm.Invoke(dialog, null);
                    characterConfirmed = true;
                    elapsed.Restart();
                    Record("character-confirmed", new { });
                }
            }
            long now = Stopwatch.GetTimestamp();
            if (lastFrame != 0)
            {
                double ms = (now - lastFrame) * 1000d / Stopwatch.Frequency;
                if (phase == "baseline") baselineFrames.Add(ms);
                if (phase == "recording") recordingFrames.Add(ms);
            }
            lastFrame = now;
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void Tick(float dt)
    {
        if (finished) return;
        try
        {
            if (step == 0 && elapsed.Elapsed.TotalSeconds >= 8)
            {
                baselineStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Status("baseline"); step = 1;
            }
            if (step == 1 && elapsed.Elapsed.TotalSeconds >= 18)
            {
                CaptureBaselineCollector();
                Record("capture-start-request", new { command = scenario == "profile" ? "profile" : "start" });
                Execute(scenario == "profile" ? "profile" : "start");
                if (finished) return;
                recording.Restart(); step = 2;
                Status("recording");
                if (scenario == "double-start") Execute("start", allowError: true);
                // Intentionally no wait for request.json or worker startup: exercise the race.
                if (scenario == "immediate") FinishCapture();
            }
            if (step != 2) return;
            if (!spiked && recording.Elapsed.TotalSeconds >= Math.Min(12, duration / 2))
            {
                spiked = true;
                Record("spike-begin", new { sleepMs = 120, allocatedBytes = 16 * 1024 * 1024, managedThreadId = Environment.CurrentManagedThreadId });
                Execute("mark session-test-spike");
                InjectMainThreadSpike();
                Record("spike-end", new { });
            }
            if (recording.Elapsed.TotalSeconds >= duration) FinishCapture();
        }
        catch (Exception ex) { Fail(ex); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void InjectMainThreadSpike()
    {
        Thread.Sleep(120);
        var burst = new byte[16][];
        for (int i = 0; i < burst.Length; i++)
        {
            burst[i] = new byte[1024 * 1024];
            for (int page = 0; page < burst[i].Length; page += 4096) burst[i][page] = (byte)i;
        }
        GC.KeepAlive(burst);
    }

    private void FinishCapture()
    {
        step = 3;
        Write($"performance-{generation}.json", new {
            baseline = Stats(baselineFrames), recording = Stats(recordingFrames), baselineCollector = baselineWindows,
            note = "Descriptive same-world sequential comparison with Ember monitoring enabled in both phases. Recording includes injected stall/allocation and warmup/scene effects; this is not a causal overhead estimate."
        });
        Record("capture-end-request", new { scenario, recordedWallSeconds = recording.Elapsed.TotalSeconds });
        if (scenario == "leave")
        {
            Status("leaving"); finished = true;
            api.Event.EnqueueMainThreadTask(() => {
                try
                {
                    var method = api.World.GetType().GetMethods(Members).Single(m => m.Name == "DestroyGameSession" && m.GetParameters().Length == 2);
                    Type mode = method.GetParameters()[1].ParameterType;
                    // VS 1.22.7's normal GUI leave uses SoftExit; never guess enum numeric values.
                    method.Invoke(api.World, [false, Enum.Parse(mode, "SoftExit")]);
                }
                catch (Exception ex) { Fail(ex); }
            }, "ember-test-leave");
        }
        else if (scenario == "reconnect" && generation == 1)
        {
            Status("reconnecting"); finished = true;
            (api.World.GetType().GetMethod("DoReconnect", Members) ?? throw new MissingMethodException("DoReconnect")).Invoke(api.World, null);
        }
        else if (scenario == "shutdown")
        {
            // The host responds with CloseMainWindow; no stop command is sent.
            Status("close-requested"); finished = true;
        }
        else
        {
            Execute(scenario == "cancel" ? "cancel" : "stop");
            if (finished) return;
            Status("finalizing"); finished = true;
        }
    }

    private void CaptureBaselineCollector()
    {
        var ember = api.ModLoader.GetModSystem("Ember.Mod.EmberSystem") ?? throw new InvalidOperationException("Installed Ember missing");
        var collector = Member(ember, "collector") ?? throw new MissingMemberException("Ember collector");
        var fill = collector.GetType().GetMethod("Fill") ?? throw new MissingMethodException("Collector.Fill");
        object report = Activator.CreateInstance(fill.GetParameters()[0].ParameterType)!;
        fill.Invoke(collector, [report]);
        foreach (object point in (IEnumerable)Member(report, "Timeline")!)
        {
            if (Convert.ToInt64(Member(point, "UnixMs")) < baselineStart) continue;
            baselineWindows.Add(new { unixMs = Member(point, "UnixMs"), collectorMs = Member(point, "CollectorMs"), intervalMs = Member(point, "IntervalMs") });
        }
        if (baselineWindows.Count < 5) throw new InvalidOperationException("Insufficient baseline collector observations");
    }

    private void Execute(string args, bool allowError = false)
    {
        api.ChatCommands.Execute("ember", new TextCommandCallingArgs {
            // Matches the game's local client command caller; server role is checked separately.
            Caller = new Caller { Player = api.World.Player, CallerPrivileges = ["*"] },
            RawArgs = new CmdArgs(args), LanguageCode = "en"
        }, result => {
            Record("command", new { command = ".ember " + args, status = result.Status.ToString(), message = result.StatusMessage });
            if (!allowError && result.Status.ToString() != "Success") Fail(new InvalidOperationException("Command failed: .ember " + args));
        });
    }

    private static object? Member(object obj, string name) => obj.GetType().GetProperty(name, Members)?.GetValue(obj) ?? obj.GetType().GetField(name, Members)?.GetValue(obj);
    private static object Stats(List<double> values)
    {
        var sorted = values.Order().ToArray();
        double P(double percentile) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
        return new { count = sorted.Length, meanMs = sorted.Length == 0 ? 0 : sorted.Average(), p50Ms = P(.5), p95Ms = P(.95), p99Ms = P(.99), maxMs = P(1) };
    }
    private void Leaving() => Record("leave-world", new { });
    private void Status(string value) { phase = value; Write("status.json", new { phase, generation, scenario, utc = DateTimeOffset.UtcNow }); }
    private void Write(string name, object value)
    {
        string path = Path.Combine(evidence, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, true);
    }
    private void Record(string kind, object detail) => File.AppendAllText(Path.Combine(evidence, "events.ndjson"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, generation, kind, detail }, Json) + "\n");
    private void Fail(Exception ex)
    {
        finished = true;
        Record("driver-error", new { type = ex.GetType().Name, message = ex.Message });
        Status("failed");
    }
    public override void Dispose()
    {
        if (api is not null)
        {
            Record("driver-dispose", new { });
            api.Event.LevelFinalize -= Loaded;
            api.Event.LeaveWorld -= Leaving;
            api.Event.UnregisterGameTickListener(listener);
            api.Event.UnregisterRenderer(this, EnumRenderStage.Done);
        }
        base.Dispose();
    }
}
