using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ember.Core;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace Ember.Mod;

internal static class ClientSnapshot
{
    // Deliberately enumerate performance settings rather than serialize settings containing login tokens.
    private static readonly string[] SettingNames = ["ViewDistance", "LodBias", "LodBiasFar", "MaxFPS", "VsyncMode",
        "SSAA", "FXAA", "SSAOQuality", "ShadowMapQuality", "SmoothShadows", "GodRayQuality", "Bloom",
        "WavingFoliage", "LiquidFoamAndShinyEffect", "CloudRenderMode", "TransparentRenderPass", "MipMapLevel",
        "MaxTextureAtlasWidth", "MaxTextureAtlasHeight", "MaxQuadParticles", "MaxCubeParticles", "MaxAsyncQuadParticles",
        "MaxAsyncCubeParticles", "RenderParticles", "AmbientParticles", "HighQualityAnimations", "Occlusionculling",
        "AllowSSBOs", "OptimizeRamMode", "ChunkVerticesUploadRateLimiter", "GraphicsPresetId", "GameWindowMode",
        "ScreenWidth", "ScreenHeight", "FieldOfView", "GUIScale", "GameTickFrameRate", "PauseGameOnLostFocus",
        "DynamicColorGrading", "GlDebugMode", "GlErrorChecking", "ExtendedDebugInfo", "UseServerTextures"];
    private static readonly string[] DebugKeys = ["fps", "mem", "triangles", "occludedchunks", "gpumemfrag",
        "chunkstats", "entitycount", "quadparticlepool", "cubeparticlepool", "incomingbytes"];

    public static Census Census(ICoreClientAPI api, EmberConfig config)
    {
        double start = Collector.ClockMs;
        Dictionary<string, int> types = [], domains = [];
        int examined = 0;
        foreach (var entity in api.World.LoadedEntities.Values)
        {
            if (examined >= config.MaxCensusEntities) break;
            examined++;
            string code = entity.Code?.ToString() ?? "unknown";
            string domain = entity.Code?.Domain ?? "unknown";
            types[code] = types.GetValueOrDefault(code) + 1;
            domains[domain] = domains.GetValueOrDefault(domain) + 1;
        }
        int? chunks = null;
        Dictionary<string, double?> counters = [];
        // Get count under the engine's own lock, avoiding cloning every chunk for a census.
        if (api.World is ClientMain game)
        {
            object? map = ReadMember(game, "WorldMap");
            object? chunkLock = map is null ? null : ReadMember(map, "chunksLock");
            if (chunkLock is not null && map is not null)
                lock (chunkLock) { if (ReadMember(map, "chunks") is ICollection collection) chunks = collection.Count; }
            foreach (var name in new[] { "dirtyChunks", "dirtyChunksPriority", "dirtyChunksLast", "tesselatedChunks", "tesselatedChunksPriority" })
            {
                object? queueLock = ReadMember(game, name + "Lock");
                if (queueLock is null) continue;
                lock (queueLock)
                {
                    object? queue = ReadMember(game, name);
                    object? count = queue is null ? null : ReadMember(queue, "Count");
                    counters[name] = count is int n ? n : null;
                }
            }
            if (game.networkProc is not null)
            {
                counters["receivedTcpBytesModulo2Pow32"] = ReadMember(game.networkProc, "totalByteCount") is int tcp ? unchecked((uint)tcp) : null;
                counters["receivedUdpBytesModulo2Pow32"] = ReadMember(game.networkProc, "totalUdpByteCount") is int udp ? unchecked((uint)udp) : null;
            }
        }
        double[] ping = api.World.Player is ClientPlayer player && player.Ping >= 0 ? [player.Ping * 1000.0] : [];
        return new(Collector.UnixMs, api.World.AllOnlinePlayers?.Length ?? 0, api.World.LoadedEntities.Count,
            chunks, types, domains, examined, examined < api.World.LoadedEntities.Count,
            Distribution.Of(ping), Collector.ClockMs - start, counters);
    }

    public static (DiagnosticReport Report, Dictionary<string, string> ModPaths) Capture(ICoreClientAPI api, Collector collector,
        EmberConfig config, string output, string reason)
    {
        var report = new DiagnosticReport
        {
            GameVersion = GameVersion.ShortGameVersion, Reason = reason,
            Environment = RuntimeReader.Describe(output), TargetIntervalMs = ClientSettings.MaxFPS > 0 ? 1000.0 / ClientSettings.MaxFPS : 0,
            Limitations = [
                "Client only: remote server CPU, TPS, server-only mods, chunk/entity totals and hardware are unavailable.",
                "Process metrics and stack samples include the integrated server in singleplayer and all other in-process background work.",
                "Render submission/stage timings are CPU-side wall time. Optional GPU timestamp elapsed times include pipeline bubbles and exclude present; GPU utilization and VRAM usage are not measured.",
                "Frame intervals include VSync, FPS limits, pause, focus changes, OS scheduling and loading. Session performance settings are sampled periodically; changes between snapshots may be missed.",
                "World census describes client-loaded entities/chunks, is periodically refreshed, and may be stale during stalls. Counts do not measure entity cost.",
                "Mod ownership is assembly-based, may be shared or unknown. Asset-only mods and Harmony patches may cause work in vanilla/framework frames.",
                "Profile thread IDs are OS thread IDs; managed main/tessellation thread IDs in the environment are a different identifier space.",
                "Logs retain bounded warning/error/fatal entries since the collector started. Redaction is best-effort; review exports before sharing.",
                "Monitoring, census, export and profiling have overhead. CollectorMs and census.CollectionMs measure only their respective phases.",
                "No heap dump, network packet contents, world seed, player coordinates, player names, login credentials or server address are intentionally collected."
            ]
        };
        report.Environment["singleplayer"] = api.IsSinglePlayer;
        report.Environment["snapshotUtc"] = DateTimeOffset.UtcNow;
        report.Environment["capabilities"] = new Dictionary<string, object?>
        {
            ["frameIntervals"] = true, ["renderStageWallTimes"] = true, ["processCpuAndMemory"] = true,
            ["managedThreadStacks"] = "on-demand EventPipe", ["clientMods"] = true,
            ["remoteServerCpu"] = false, ["remoteServerTps"] = false, ["serverOnlyMods"] = false,
            ["gpuExecutionTime"] = false, ["gpuUtilization"] = false, ["vramUsage"] = false,
            ["networkPacketContents"] = false, ["retainedObjectHeapDump"] = false
        };
        report.Environment["gamePaused"] = api.IsGamePaused;
        report.Environment["frameWidth"] = api.Render.FrameWidth;
        report.Environment["frameHeight"] = api.Render.FrameHeight;
        report.Environment["tessellationManagedThreadId"] = api.TesselationThreadId;
        report.Environment["mainManagedThreadId"] = System.Environment.CurrentManagedThreadId;
        report.Environment["mainOsThreadId"] = NativeThread.CurrentId();
        try
        {
            report.Environment["gpuRenderer"] = GL.GetString(StringName.Renderer);
            report.Environment["gpuVendor"] = GL.GetString(StringName.Vendor);
            report.Environment["openGlVersion"] = GL.GetString(StringName.Version);
            report.Environment["shaderLanguageVersion"] = GL.GetString(StringName.ShadingLanguageVersion);
        }
        catch (Exception e) { report.Limitations.Add("GPU identity unavailable: " + e.GetType().Name); }
        foreach (string name in SettingNames)
        {
            try { report.Settings[name] = typeof(ClientSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null); }
            catch { report.Settings[name] = null; }
        }
        if (api.World is ClientMain main)
        {
            Dictionary<string, string> debug = [];
            foreach (string key in DebugKeys)
                if (main.DebugScreenInfo.TryGetValue(key, out string? value)) debug[key] = Redactor.Text(value);
            report.Settings["gameDebugCounters"] = debug;
            report.Environment["serverGameVersionReported"] = ReadMember(main, "ServerGameVersion")?.ToString();
            report.Environment["serverNetworkVersionReported"] = main.ServerNetworkVersion;
        }
        if (api.World is ClientMain cm)
        {
            report.Environment["windowFocused"] = cm.Platform.IsFocused;
        }
        Dictionary<string, string> modPaths = [];
        var loadedAssemblyNames = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in api.ModLoader.Mods)
        {
            if (mod.Info is null) continue;
            string id = mod.Info.ModID ?? "unknown";
            var systems = mod.Systems ?? [];
            var assemblies = systems.Select(s => s.GetType().Assembly.GetName().Name ?? "unknown").ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ReadMember(mod, "Assembly") is Assembly primary) assemblies.Add(primary.GetName().Name ?? "unknown");
            if (ReadMember(mod, "AssemblyFiles") is IEnumerable<string> assemblyFiles)
                foreach (string file in assemblyFiles)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (loadedAssemblyNames.Contains(name)) assemblies.Add(name);
                }
            report.Mods.Add(new(id, mod.Info.Name ?? id, mod.Info.Version, mod.Info.Side.ToString(),
                mod.SourceType.ToString(), Path.GetFileName(mod.FileName ?? ""),
                systems.Select(s => s.GetType().FullName ?? s.GetType().Name).Order().ToArray(),
                assemblies.Order().ToArray(),
                mod.Info.Dependencies.GroupBy(d => d.ModID).ToDictionary(g => g.Key, g => g.First().Version),
                Enabled: ReadMember(mod, "Enabled") as bool?));
            if (File.Exists(mod.SourcePath)) modPaths[id] = mod.SourcePath;
        }
        report.Mods = report.Mods.OrderBy(m => m.Id).ToList();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
        {
            try
            {
                string name = asm.GetName().Name ?? "unknown";
                report.Assemblies.Add(new(name, asm.GetName().Version?.ToString() ?? "", asm.ManifestModule.ModuleVersionId.ToString(),
                    report.Mods.Where(m => m.Assemblies.Contains(name, StringComparer.OrdinalIgnoreCase)).Select(m => m.Id).ToArray()));
            }
            catch { /* Dynamic assemblies can lack a manifest module. */ }
        }
        try
        {
            foreach (var method in Harmony.GetAllPatchedMethods().ToArray())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info is null) continue;
                string Describe(Patch p) => $"{p.owner}: {p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name}";
                report.Patches.Add(new($"{method.DeclaringType?.FullName}.{method}", info.Owners.ToArray(),
                    info.Prefixes.Select(Describe).ToArray(), info.Postfixes.Select(Describe).ToArray(),
                    info.Transpilers.Select(Describe).ToArray(), info.Finalizers.Select(Describe).ToArray()));
            }
        }
        catch (Exception e) { report.Limitations.Add("Patch inventory incomplete: " + e.GetType().Name); }
        collector.Fill(report);
        return (report, modPaths);
    }

    public static ContextSnapshot Context(ICoreClientAPI api)
    {
        Dictionary<string, object?> settings = [];
        foreach (string name in SettingNames)
        {
            try { settings[name] = typeof(ClientSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null); }
            catch { settings[name] = null; }
        }
        Dictionary<string, object?> environment = new()
        {
            ["gamePaused"] = api.IsGamePaused, ["singleplayer"] = api.IsSinglePlayer,
            ["frameWidth"] = api.Render.FrameWidth, ["frameHeight"] = api.Render.FrameHeight
        };
        if (api.World is ClientMain game) environment["windowFocused"] = game.Platform.IsFocused;
        return new(Collector.UnixMs, settings, environment);
    }

    public static void EnrichFiles(DiagnosticReport report, Dictionary<string, string> paths, EmberConfig config, string configDirectory)
    {
        if (config.HashModFiles)
        {
            for (int i = 0; i < report.Mods.Count; i++)
            {
                var mod = report.Mods[i];
                if (!paths.TryGetValue(mod.Id, out string? path)) continue;
                try
                {
                    using var stream = File.OpenRead(path);
                    report.Mods[i] = mod with { Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
                }
                catch (Exception e) { report.Limitations.Add($"Cannot hash mod {mod.Id}: {e.GetType().Name}"); }
            }
        }
        if (!config.IncludeModConfigs)
        {
            report.Limitations.Add("Mod configuration bodies omitted (IncludeModConfigs=false). Performance-relevant client settings are included.");
            return;
        }
        if (!Directory.Exists(configDirectory)) return;
        long total = 0;
        foreach (string file in Directory.GetFiles(configDirectory, "*.json").Order().Take(200))
        {
            try
            {
                long bytes = new FileInfo(file).Length;
                if (bytes > 256 * 1024 || total + bytes > 2 * 1024 * 1024) { report.Limitations.Add("Mod config size limit reached: " + Path.GetFileName(file)); continue; }
                total += bytes;
                report.ModConfigs[Path.GetFileName(file)] = Redactor.Config(JsonNode.Parse(File.ReadAllText(file)));
            }
            catch (Exception e) { report.Limitations.Add("Cannot read config " + Path.GetFileName(file) + ": " + e.GetType().Name); }
        }
    }

    private static object? ReadMember(object instance, string name)
    {
        try
        {
            Type? type = instance.GetType();
            while (type is not null)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                var field = type.GetField(name, flags);
                if (field is not null) return field.GetValue(instance);
                var property = type.GetProperty(name, flags);
                if (property is not null && property.GetIndexParameters().Length == 0) return property.GetValue(instance);
                type = type.BaseType;
            }
        }
        catch { }
        return null;
    }
}
