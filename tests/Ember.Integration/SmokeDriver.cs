using System.Diagnostics;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Ember.Integration;

public sealed class SmokeDriver : ModSystem, IRenderer
{
    private ICoreClientAPI api = null!;
    private string evidence = "", exports = "";
    private readonly Stopwatch elapsed = new();
    private readonly Stopwatch commandsElapsed = new();
    private long listener;
    private int step;
    private bool finished;
    private bool confirmedCharacter;
    private readonly int profileSeconds = int.TryParse(Environment.GetEnvironmentVariable("EMBER_SMOKE_SECONDS"), out int seconds) ? Math.Clamp(seconds, 5, 60) : 8;
    public double RenderOrder => 20000;
    public int RenderRange => int.MaxValue;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        api = capi;
        evidence = api.GetOrCreateDataPath("EmberSmoke");
        exports = api.GetOrCreateDataPath("Ember/exports");
        Record("driver-start", "Client-only driver initialized");
        api.Event.LevelFinalize += Loaded;
        api.Event.RegisterRenderer(this, EnumRenderStage.Done, "ember-smoke-first-run");
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (confirmedCharacter || !elapsed.IsRunning) return;
        var dialog = api.Gui.OpenedGuis.FirstOrDefault(d => d.GetType().Name == "GuiDialogCreateCharacter");
        if (dialog is null) return;
        // Fresh creative worlds pause at character creation; confirm the default selection
        // through the game's own button handler, without sending input to unrelated windows.
        var confirm = dialog.GetType().GetMethod("OnConfirm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (confirm is null) { Record("driver-error", "Character confirmation handler not found"); return; }
        confirm.Invoke(dialog, null);
        confirmedCharacter = true;
        elapsed.Restart();
        Record("character-confirmed", "Default appearance/class accepted in isolated fresh world");
    }

    private void Loaded()
    {
        Record("level-finalize", "Graphical client world loaded");
        var ember = api.ModLoader.GetModSystem("Ember.Mod.EmberSystem");
        Record("side-check", new { client = ember?.ShouldLoad(EnumAppSide.Client), server = ember?.ShouldLoad(EnumAppSide.Server) });
        elapsed.Start();
        listener = api.Event.RegisterGameTickListener(Tick, 500);
    }

    private void Tick(float dt)
    {
        if (finished) return;
        try
        {
            double seconds = elapsed.Elapsed.TotalSeconds;
            if (step == 0 && seconds >= 8) { Execute("mark smoke"); Execute("health"); Execute("profile " + profileSeconds); commandsElapsed.Start(); step = 1; }
            if (step == 1 && Directory.GetFiles(exports, "*.zip").Length > 0)
            {
                Record("profile-export", Directory.GetFiles(exports, "*.zip").Select(Path.GetFileName).ToArray());
                Execute("health");
                Execute("export");
                step = 2;
            }
            if (step == 2 && Directory.GetFiles(exports, "*.zip").Length >= 2)
            {
                Record("complete", new { elapsedSeconds = seconds, exports = Directory.GetFiles(exports, "*.zip").Select(Path.GetFileName).ToArray() });
                File.WriteAllText(Path.Combine(evidence, "done.json"), JsonSerializer.Serialize(new { success = true, elapsedSeconds = seconds }));
                finished = true;
            }
            if (commandsElapsed.Elapsed.TotalSeconds > 150)
            {
                Record("timeout", "No two completed exports within 150 seconds after profile command");
                File.WriteAllText(Path.Combine(evidence, "done.json"), "{\"success\":false,\"reason\":\"driver timeout\"}");
                finished = true;
            }
        }
        catch (Exception ex) { Record("driver-error", ex.ToString()); finished = true; File.WriteAllText(Path.Combine(evidence, "done.json"), "{\"success\":false,\"reason\":\"driver exception\"}"); }
    }

    private void Execute(string args)
    {
        api.ChatCommands.Execute("ember", new TextCommandCallingArgs
        {
            // Match ChatCommandApi's IClientPlayer overload, which grants local command
            // privileges; this is unrelated to server-side player/admin privileges.
            Caller = new Caller { Player = api.World.Player, CallerPrivileges = ["*"] },
            RawArgs = new CmdArgs(args),
            LanguageCode = "en"
        }, result => Record("command", new { command = ".ember " + args, status = result.Status.ToString(), message = result.StatusMessage }));
    }

    private void Record(string kind, object detail) => File.AppendAllText(Path.Combine(evidence, "events.jsonl"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, kind, detail }) + Environment.NewLine);
    public override void Dispose()
    {
        if (api is not null) { api.Event.LevelFinalize -= Loaded; api.Event.UnregisterGameTickListener(listener); api.Event.UnregisterRenderer(this, EnumRenderStage.Done); }
        base.Dispose();
    }
}
