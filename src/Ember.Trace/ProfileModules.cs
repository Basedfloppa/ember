using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Ember.Trace;

/// <summary>
/// Module identity for sampled code addresses, read straight from a segment's raw EventPipe file.
/// <para>
/// The converted trace (ETLX) associates a code address with a module, and when the module-load events
/// were not converted it answers with TraceEvent's synthetic "ManagedModule" for every managed frame —
/// measured on a real 24-minute client session, 1404 of 1404 frames. The raw file still holds the
/// stop-time rundown, so this pass rebuilds the same association from the events themselves: method
/// load records give a code-address range, module records give that module's path. Measured on two
/// synthetic segments, every sampled address fell inside a recovered range and the recovered name
/// agreed with the ETLX name on 4018/4018 and 4001/4001 frames where the ETLX had one.
/// </para>
/// <para>
/// Only <c>ModuleDCStop</c>/<c>MethodDCStopVerbose</c> variants were present in the measured traces,
/// and the method records arrive before the module records, so the two are joined after the pass
/// rather than during it.
/// </para>
/// </summary>
internal sealed class ProfileModules
{
    private const int MaxMethods = 1_000_000;
    private const int MaxModules = 100_000;
    private readonly (ulong Start, int Size, string Module)[] ranges;

    private ProfileModules((ulong Start, int Size, string Module)[] ranges) => this.ranges = ranges;

    public int Count => ranges.Length;

    /// <summary>
    /// Reads the raw trace. Returns null when nothing could be recovered; <paramref name="note"/>
    /// says why, because "no modules" and "no readable trace" are different answers.
    /// </summary>
    public static ProfileModules? Read(string nettrace, out string? note)
    {
        note = null;
        if (!File.Exists(nettrace)) { note = "its raw trace is no longer present"; return null; }
        var modules = new Dictionary<long, string>();
        var methods = new List<(long ModuleId, ulong Start, int Size)>();
        bool methodsCapped = false, modulesCapped = false;
        try
        {
            using var source = new EventPipeEventSource(nettrace);
            var clr = new ClrTraceEventParser(source);
            var rundown = new ClrRundownTraceEventParser(source);
            Action<ModuleLoadUnloadTraceData> onModule = data =>
            {
                string path = !string.IsNullOrEmpty(data.ModuleILPath) ? data.ModuleILPath
                    : !string.IsNullOrEmpty(data.ModuleNativePath) ? data.ModuleNativePath : "";
                if (path.Length == 0) return;
                if (modules.Count >= MaxModules && !modules.ContainsKey(data.ModuleID)) { modulesCapped = true; return; }
                modules[data.ModuleID] = path;
            };
            // Live load events cover modules loaded during the capture; the rundown covers the ones
            // already loaded when it started, which is every mod assembly in a running game.
            clr.LoaderModuleLoad += onModule;
            clr.LoaderModuleDCStopV2 += onModule;
            rundown.LoaderModuleDCStop += onModule;
            rundown.LoaderModuleDCStart += onModule;
            Action<MethodLoadUnloadVerboseTraceData> onMethod = data =>
            {
                if (methods.Count >= MaxMethods) { methodsCapped = true; return; }
                if (data.MethodSize <= 0 || data.MethodStartAddress == 0) return;
                methods.Add((data.ModuleID, data.MethodStartAddress, data.MethodSize));
            };
            clr.MethodLoadVerbose += onMethod;
            clr.MethodDCStartVerboseV2 += onMethod;
            rundown.MethodDCStopVerbose += onMethod;
            rundown.MethodDCStartVerbose += onMethod;
            source.Process();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            note = $"its raw trace could not be read again ({e.GetType().Name})";
            return null;
        }
        // Joined here, not in the callback: the measured rundown emits method records before module
        // records, so a lookup during the pass found no module for any method.
        var ranges = new List<(ulong Start, int Size, string Module)>(methods.Count);
        int orphaned = 0;
        foreach (var (moduleId, start, size) in methods)
        {
            if (!modules.TryGetValue(moduleId, out string? path) || path.Length == 0) { orphaned++; continue; }
            ranges.Add((start, size, path));
        }
        if (ranges.Count == 0)
        {
            note = methods.Count == 0
                ? "it carried no verbose method-load records"
                : $"none of its {methods.Count:N0} method-load records named a module";
            return null;
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var parts = new List<string> { $"recovered {ranges.Count:N0} code-address ranges over {modules.Count:N0} modules" };
        if (orphaned > 0) parts.Add($"{orphaned:N0} method records named no module and were dropped");
        if (methodsCapped) parts.Add($"the {MaxMethods:N0}-method bound was reached, so later methods are unmapped");
        if (modulesCapped) parts.Add($"the {MaxModules:N0}-module bound was reached");
        note = string.Join("; ", parts);
        return new ProfileModules(ranges.ToArray());
    }

    /// <summary>Module path whose code-address range contains the address, or null.</summary>
    public string? Lookup(ulong address)
    {
        int low = 0, high = ranges.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            ref readonly var range = ref ranges[middle];
            if (address < range.Start) high = middle - 1;
            else if (address - range.Start >= (ulong)range.Size) low = middle + 1;
            else return range.Module;
        }
        return null;
    }
}
