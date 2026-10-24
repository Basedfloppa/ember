namespace Ember.Core;

/// <summary>
/// Answers "which mod's code is this sampled frame" without conflating two different statements:
/// what module the trace says the code belongs to, and which mod owns that module. The trace's own
/// statement wins whenever it exists; a frame the runtime never associated with a module is left
/// unowned rather than assigned to a plausible assembly. The one heuristic here — matching the
/// method's root namespace against a declared assembly name — is recorded on the frame as
/// <see cref="TypeNamespaceMatch"/> so a reader can tell it apart from the runtime's own answer.
/// </summary>
public static class ProfileOwnership
{
    /// <summary>TraceEvent's synthetic module for managed code whose module events were not converted.</summary>
    public const string UnidentifiedModule = "ManagedModule";
    public const string UnknownModule = "unknown";
    /// <summary>Frame marker: the mod came from the method's root namespace, not from a module.</summary>
    public const string TypeNamespaceMatch = "type-namespace";

    /// <summary>True when the string names an actual module rather than a placeholder for "not known".</summary>
    public static bool IsIdentified(string? module) =>
        !string.IsNullOrWhiteSpace(module) &&
        !string.Equals(module, UnidentifiedModule, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(module, UnknownModule, StringComparison.OrdinalIgnoreCase);

    /// <summary>File name of a module path without its extension, which is how the assembly map is keyed.</summary>
    public static string ModuleKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string name = path.Replace('\\', '/').Split('/')[^1];
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4] : name;
    }

    /// <summary>
    /// Root namespace of a method's declaring type, which is the only ownership signal left when the
    /// runtime reported no module. "VintageHorizons.LodWorker.CaptureLoop(...)" yields
    /// "VintageHorizons"; a name with no dot yields its whole head, which simply will not match.
    /// </summary>
    public static string? TypeRoot(string? methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName)) return null;
        int call = methodName.IndexOf('(');
        string head = call >= 0 ? methodName[..call] : methodName;
        int dot = head.IndexOf('.');
        string root = dot > 0 ? head[..dot] : head;
        return root.Length == 0 ? null : root;
    }

    /// <summary>Mods declaring an assembly, in a stable order, or an empty array.</summary>
    public static string[] Owners(string module, IReadOnlyDictionary<string, string[]> assemblyToMods)
    {
        if (!IsIdentified(module)) return [];
        return assemblyToMods.TryGetValue(ModuleKey(module), out var owners) && owners.Length > 0
            ? owners.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    /// <summary>
    /// Ownership of one frame. <paramref name="recoveredModule"/> is the module a second pass over the
    /// raw trace found for this code address, or null; it is consulted only when the converted trace
    /// did not name a module, and it is reported as recovered so the report can say where the name came
    /// from. A frame whose module is known but belongs to no mod is deliberately not guessed at by
    /// namespace: a known vanilla module is an answer, and a namespace is not.
    /// </summary>
    public static FrameOwnership Resolve(string? methodName, string traceModule, string? recoveredModule,
        IReadOnlyDictionary<string, string[]> assemblyToMods)
    {
        bool identified = IsIdentified(traceModule);
        bool recovered = false;
        string module = traceModule;
        if (!identified && IsIdentified(recoveredModule))
        {
            module = ModuleKey(recoveredModule);
            identified = recovered = true;
        }
        string[] owners = Owners(module, assemblyToMods);
        if (identified) return new(module, owners, null, true, recovered);
        // No module: the declaring type's root namespace, and only when it names an assembly some mod
        // declares. A miss leaves the frame unowned, which is the truthful answer.
        string? root = TypeRoot(methodName);
        if (root is not null && assemblyToMods.TryGetValue(root, out var byType) && byType.Length > 0)
            return new(module, byType.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                TypeNamespaceMatch, false, false);
        return new(module, [], null, false, false);
    }

    /// <summary>
    /// Samples tallied by owning mod. A sample counts once for every mod present anywhere in its stack
    /// (inclusive) and for the mods of its leaf frame (self, which is the frame that was executing).
    /// Inclusives overlap by construction and must not be summed; a frame owned by two mods counts for
    /// both, and an unowned leaf is counted separately rather than assigned to "other".
    /// </summary>
    public static (List<ModSample> Mods, int UnattributedSelfSamples) Tally(
        IEnumerable<ProfileSample> samples, IReadOnlyList<ProfileFrame> frames)
    {
        var self = new Dictionary<string, int>(StringComparer.Ordinal);
        var inclusive = new Dictionary<string, int>(StringComparer.Ordinal);
        var managedSelf = new Dictionary<string, int>(StringComparer.Ordinal);
        int unattributed = 0;
        foreach (var sample in samples)
        {
            if (sample?.Stack is not { Length: > 0 } stack) continue;
            string[] leafOwners = OwnersOf(stack[^1]);
            if (leafOwners.Length == 0) unattributed++;
            foreach (string mod in leafOwners)
            {
                self[mod] = self.GetValueOrDefault(mod) + 1;
                if (sample.Managed) managedSelf[mod] = managedSelf.GetValueOrDefault(mod) + 1;
            }
            foreach (int id in stack.Distinct())
                foreach (string mod in OwnersOf(id))
                    inclusive[mod] = inclusive.GetValueOrDefault(mod) + 1;
        }
        var mods = self.Keys.Concat(inclusive.Keys).Distinct(StringComparer.Ordinal)
            .Select(mod => new ModSample(mod, self.GetValueOrDefault(mod), inclusive.GetValueOrDefault(mod), managedSelf.GetValueOrDefault(mod)))
            .OrderByDescending(m => m.ManagedSelfSamples).ThenByDescending(m => m.SelfSamples).ThenBy(m => m.Mod, StringComparer.Ordinal)
            .ToList();
        return (mods, unattributed);

        string[] OwnersOf(int id)
        {
            var frame = id >= 0 && id < frames.Count ? frames[id] : null;
            // A frame can carry the same mod twice only through a malformed file; counting once keeps
            // the tally a sample count rather than a frame count.
            return frame?.Mods is { Length: > 0 } owners ? owners.Distinct(StringComparer.Ordinal).ToArray() : [];
        }
    }

    /// <summary>
    /// One sentence a reader can check the mod table against, stating how much of the attribution is
    /// the runtime's own statement and how much is a fallback elsewhere in this class.
    /// </summary>
    public static string OwnershipNote(int frames, int withoutModule, int recoveredFromRawTrace, int matchedByTypeName)
    {
        string head = frames <= 0
            ? "Frame ownership: no distinct frames were retained."
            : $"Frame ownership: {frames:N0} distinct frames, {frames - withoutModule:N0} of them named a module and {withoutModule:N0} did not.";
        var parts = new List<string>();
        if (recoveredFromRawTrace > 0)
            parts.Add($"{recoveredFromRawTrace:N0} of those were recovered from the raw trace's method-load events, the same source the heap-dump owners use");
        if (matchedByTypeName > 0)
            parts.Add($"{matchedByTypeName:N0} carry a mod only because the method's root namespace names an assembly that mod declares (profile.frames[].match=\"{TypeNamespaceMatch}\")");
        if (parts.Count > 0) head += " " + string.Join("; ", parts) + ".";
        head += " A frame the runtime never associated with a module stays unowned rather than being guessed at, and an owner is a lookup aid, not proof of cause.";
        return head;
    }

    public static string TallyNote(int mods, int unattributedSelfSamples, int samples)
    {
        return $"Mod attribution covers {mods:N0} mod(s) over {samples:N0} retained observations. Inclusive counts a sample once per mod anywhere in its stack and overlaps, so those columns must not be summed; self counts the executing leaf frame and does not overlap. {unattributedSelfSamples:N0} retained observations have no known mod on the leaf frame — framework, vanilla or unidentified code, not \"other mods\".";
    }
}

/// <summary>What is known about one frame's ownership, and how it was learned.</summary>
public sealed record FrameOwnership(string Module, string[] Mods, string? Match, bool Identified, bool Recovered);

