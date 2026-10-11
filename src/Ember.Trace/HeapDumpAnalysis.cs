using Ember.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Ember.Trace;

/// <summary>
/// Turns a GC heap-dump trace into the answer a person reads: who retains the bytes.
/// Attribution is a partition (every reachable byte belongs to exactly one holder root), and
/// nothing here ranks or accuses an owner: the numbers are evidence, not a verdict.
/// </summary>
internal static class HeapDumpAnalysis
{
    private const int TopEntries = 40;
    // Measured unattributable shares: 0.00% for a large coherent synthetic heap and 0.05% for a
    // live Vintage Story server, 11% for a tiny isolated heap (small heaps carry proportionally
    // more infrastructure objects outside the strong roots), and 62-100% when the walk arrived
    // truncated. The gap between them is wide, so the check is a separator, not a quality grade.
    private const double CoherentUnreachablePercent = 25;
    private const string Unreachable = "unreachable (no strong root in this trace)";
    // The measured mask: GCHeapSnapshot (GC|Type|GCHeapDump|GCHeapCollect|GCHeapAndTypeNames) plus
    // Loader, which is what makes module names arrive at all. Kept numeric because this is the exact
    // value verified end to end; a test asserts that the named keywords still add up to it.
    internal const long DumpKeywords = 0x1980009L;
    // The allocation-sampling keywords (GCSampledObjectAllocationHigh|Low, 0x2200000) are deliberately
    // absent from the mask above, and so is the .NET 10 event they were superseded by. Measured on
    // .NET 10.0.12 against a process allocating 1.5M objects in 45 s: those two bits alone produced a
    // trace holding one event (the session metadata), and no event with ids 20/32 arrived under any
    // keyword mask, including all 64 bits. The allocation stream the runtime does emit is
    // GCAllocationTick, which the GC keyword already enables. Its .NET 10 sibling AllocationSampled
    // (id 303, EventPipe-only keyword bit 0x0000080000000000) carries a type name and an object size
    // per sample and would be the better source, but this TraceEvent version cannot decode it.

    public static void Analyze(string tracePath, HeapDumpJob job, HeapDump dump, CancellationToken cancel)
    {
        var map = job.ModMap;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var source = new EventPipeEventSource(tracePath);
        var clr = new ClrTraceEventParser(source);
        var rundown = new ClrRundownTraceEventParser(source);

        var moduleNames = new Dictionary<ulong, string>();
        Action<ModuleLoadUnloadTraceData> onModule = data =>
            moduleNames[(ulong)data.ModuleID] = data.ModuleILPath ?? data.ModuleNativePath ?? "";
        clr.AddCallbackForEvents(onModule);
        // Module names for assemblies that were already loaded arrive as rundown events, which the
        // live parser never sees. Without this every owner would read "unresolved module".
        rundown.AddCallbackForEvents(onModule);

        var typeIndex = new Dictionary<ulong, int>();
        var typeNames = new List<string>();
        var typeModules = new List<ulong>();
        var addresses = new Growth<ulong>();
        var sizes = new Growth<ulong>();
        var nodes = new Growth<int>();
        var nodeEdges = new Growth<int>();
        var edgeTargets = new Growth<ulong>();
        var roots = new Growth<ulong>();
        var unresolved = new List<(int Node, ulong TypeId)>();
        var rootKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var sampled = new Dictionary<string, (long Bytes, long Count, ulong TypeId)>(StringComparer.Ordinal);

        long expectedEdges = 0, receivedEdges = 0, rootCount = 0, rootEntries = 0;
        double nodeFirst = double.MaxValue, nodeLast = double.MinValue, edgeFirst = double.MaxValue, edgeLast = double.MinValue;

        // Reading the trace is the longest phase of a dump - it is as large as the heap - so it is the
        // one a deadline has to be able to interrupt. A deadline that could only wait turned a slow
        // dump into no dump at all: the first real client failure was killed by the supervisor with
        // nothing recorded, and neither the heap nor the budget could be told apart afterwards. The
        // check runs in the bulk callbacks, which is where a walk spends its time, and it records how
        // far it got before it stops, because that partial count is the evidence a reader has left.
        bool stopped = false;
        bool Interrupted()
        {
            if (stopped) return true;
            if (!cancel.IsCancellationRequested) return false;
            stopped = true;
            dump.WalkedNodeCount = addresses.Count;
            source.StopProcessing();
            return true;
        }

        // The buffer is the cap the trace had to fit in, and its size is known before a single event
        // is read. It is evaluated here, not after the walk, because the node budget below can stop
        // the analysis first - and on the one real client failure that happened, both limits had
        // fired while only the node limit was reported.
        bool saturated = HeapDumpBuffer.Saturated(dump.TraceBytes, job.BufferMegabytes);

        clr.TypeBulkType += data =>
        {
            if (Interrupted()) return;
            for (int i = 0; i < data.Count; i++)
            {
                var v = data.Values(i);
                if (typeIndex.ContainsKey(v.TypeID)) continue;
                typeIndex[v.TypeID] = typeNames.Count;
                typeNames.Add(string.IsNullOrWhiteSpace(v.TypeName) ? $"[type 0x{v.TypeID:x}]" : v.TypeName);
                typeModules.Add(v.ModuleID);
            }
        };
        clr.GCBulkNode += data =>
        {
            if (Interrupted()) return;
            if (data.TimeStampRelativeMSec < nodeFirst) nodeFirst = data.TimeStampRelativeMSec;
            if (data.TimeStampRelativeMSec > nodeLast) nodeLast = data.TimeStampRelativeMSec;
            for (int i = 0; i < data.Count; i++)
            {
                var v = data.Values(i);
                if (addresses.Count >= job.MaxNodes)
                {
                    // The note is written before the throw so the failed aggregate on disk carries the
                    // same reason the operator reads: the worker records this same object. The kind is
                    // recorded with it, so the summary and the findings do not have to parse the text.
                    string reason = HeapDumpBuffer.FailureMessage(dump.TraceBytes, job.BufferMegabytes, job.MaxNodes);
                    dump.FailureKind = saturated ? HeapDumpFailure.Buffer : HeapDumpFailure.NodeLimit;
                    dump.Notes.Add(reason);
                    throw new InvalidOperationException(reason);
                }
                int at = addresses.Count;
                addresses.Add(v.Address);
                sizes.Add(v.Size);
                // A type can be declared after the nodes that use it, so an unresolved id is
                // remembered and resolved once the whole trace has been read.
                if (typeIndex.TryGetValue(v.TypeID, out int t)) nodes.Add(t);
                else { nodes.Add(-1); unresolved.Add((at, v.TypeID)); }
                nodeEdges.Add((int)v.EdgeCount);
                expectedEdges += v.EdgeCount;
            }
        };
        clr.GCBulkEdge += data =>
        {
            if (Interrupted()) return;
            if (data.TimeStampRelativeMSec < edgeFirst) edgeFirst = data.TimeStampRelativeMSec;
            if (data.TimeStampRelativeMSec > edgeLast) edgeLast = data.TimeStampRelativeMSec;
            for (int i = 0; i < data.Count; i++) { edgeTargets.Add(data.Values(i).Target); receivedEdges++; }
        };
        clr.GCBulkRootEdge += data =>
        {
            if (Interrupted()) return;
            for (int i = 0; i < data.Count; i++)
            {
                var v = data.Values(i);
                rootEntries++;
                string kind = v.GCRootKind.ToString();
                rootKinds[kind] = rootKinds.GetValueOrDefault(kind) + 1;
                // Weak references and finalizer-queue entries do not retain anything by themselves.
                if ((v.GCRootFlag & GCRootFlags.WeakRef) != 0) continue;
                roots.Add(v.RootedNodeAddress);
                rootCount++;
            }
        };
        // Allocation samples are ticks, not a per-type running total: the runtime raises one when
        // about 100 KB has been allocated and names the type and size of the object that crossed that
        // boundary, so the sizes are summed. The type arrives as a name, so a sample is kept even when
        // its type is not in the type table yet - that table is written by the walk, which lands after
        // the samples - and the id is carried only to resolve the module that owns the type.
        clr.GCAllocationTick += data =>
        {
            if (Interrupted()) return;
            if (!job.SampledAllocations) return;
            string type = string.IsNullOrWhiteSpace(data.TypeName) ? $"[type 0x{data.TypeID:x}]" : data.TypeName;
            long bytes = Math.Max(0, (long)data.ObjectSize);
            var previous = sampled.GetValueOrDefault(type);
            sampled[type] = (previous.Bytes + bytes, previous.Count + 1, data.TypeID);
        };
        source.Process();
        // An interrupted reduction publishes nothing: the count it recorded is where it stopped, and
        // a walk read only part way through describes neither a complete heap nor a coherent one.
        if (stopped) throw new OperationCanceledException(cancel);
        foreach (var (node, typeId) in unresolved)
            if (typeIndex.TryGetValue(typeId, out int resolved)) nodes.Items[node] = resolved;

        int n = addresses.Count;
        if (n == 0)
        {
            dump.FailureKind = HeapDumpFailure.NoWalk;
            throw new InvalidOperationException("The trace contains no heap-walk node events; the dump did not complete before the capture ended.");
        }
        dump.WalkedNodeCount = n;
        dump.TypeCount = typeNames.Count;
        dump.ModuleCount = moduleNames.Count;
        dump.RootCount = (int)rootCount;
        dump.RootKinds = rootKinds;
        if (nodeLast > nodeFirst)
        {
            double last = edgeLast > double.MinValue && edgeLast > nodeLast ? edgeLast : nodeLast;
            dump.SpanMilliseconds = last - nodeFirst;
        }
        dump.Complete = expectedEdges == receivedEdges;
        if (!dump.Complete)
            dump.Notes.Add($"Event accounting is broken: the heap walk declared {expectedEdges:N0} references but {receivedEdges:N0} arrived. EventPipe dropped events, so every number below describes a partial heap and is a lower bound. Raise HeapDumpBufferMegabytes and repeat the dump.");
        // A trace that reached its EventPipe buffer is the one case where losing events is not a
        // guess, and the case that made the feature's first real client run unreadable. The rule and
        // its measured basis live in Core so the boundary is covered by a test.
        if (saturated)
        {
            dump.Complete = false;
            dump.Notes.Add(HeapDumpBuffer.SaturationNote(dump.TraceBytes, job.BufferMegabytes));
        }

        string[] owners = OwnerNames(typeModules, moduleNames);

        // Address lookup is a sorted key array rather than a hash map: at tens of millions of nodes
        // a Dictionary<ulong,int> costs more than the rest of the analysis together.
        var keys = new ulong[n];
        Array.Copy(addresses.Items, keys, n);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(keys, order);
        int distinct = n == 0 ? 0 : 1;
        for (int i = 1; i < n; i++) if (keys[i] != keys[i - 1]) distinct++;

        // The runtime answers every EventPipe session that attaches while the walk is being captured
        // with a whole walk of the heap, broadcast to each session that asked for one, so one trace
        // can describe the same heap many times over. Counting those copies as more heap is what made
        // the first client dump that contained them unreadable, and the copies are not more heap. A
        // live object keeps its address for as long as it lives, so the copies carry the same
        // addresses: collapse them and analyse the heap once. The copy that is kept is the one that
        // arrived first, which is also the one whose edges are read first, so the graph below is a
        // snapshot from a single walk rather than a mixture of several. When nothing repeated - the
        // normal case - both mappings stay null and the arrays are used exactly as they arrived.
        int[]? kept = null;      // original node index -> index in the collapsed graph, -1 when dropped
        int[]? byAddress = null; // sorted address position -> index in the collapsed graph
        if (distinct < n)
        {
            kept = new int[n];
            Array.Fill(kept, -1);
            byAddress = new int[n];
            for (int start = 0; start < n;)
            {
                int end = start + 1;
                while (end < n && keys[end] == keys[start]) end++;
                int first = start;
                for (int i = start + 1; i < end; i++) if (order[i] < order[first]) first = i;
                kept[order[first]] = 1; // chosen; its rank is assigned below
                // Every position of the run resolves to the copy that arrived first, which is how a
                // root entry or a reference to any of the copies reaches the one that is kept.
                for (int i = start; i < end; i++) byAddress[i] = order[first];
                start = end;
            }
            // The collapsed index has to grow with the original one, because the edges are read in
            // arrival order and childStart below is a prefix sum over that order. Ranking the copies
            // by address instead would leave childStart unsorted and read the wrong edges.
            int next = 0;
            for (int i = 0; i < n; i++) if (kept[i] == 1) kept[i] = next++;
            for (int i = 0; i < n; i++) byAddress[i] = kept[byAddress[i]];
        }
        int count = kept is null ? n : distinct;
        ulong[] nodeSizes = sizes.Items;
        int[] nodeTypes = nodes.Items;
        if (kept is not null)
        {
            nodeSizes = new ulong[count];
            nodeTypes = new int[count];
            for (int i = 0; i < n; i++)
            {
                if (kept[i] < 0) continue;
                nodeSizes[kept[i]] = sizes[i];
                nodeTypes[kept[i]] = nodes[i];
            }
        }
        dump.NodeCount = count;
        long shallowTotal = 0;
        for (int i = 0; i < count; i++) shallowTotal += (long)nodeSizes[i];
        dump.TotalBytes = shallowTotal;

        var childStart = new int[count + 1];
        var children = new int[edgeTargets.Count + roots.Count];
        int written = 0, cursor = 0;
        for (int i = 0; i < n; i++)
        {
            int end = Math.Min(cursor + nodeEdges[i], edgeTargets.Count);
            int at = kept is null ? i : kept[i];
            if (at < 0) { cursor = end; continue; }
            childStart[at] = written;
            for (; cursor < end; cursor++)
            {
                int target = Array.BinarySearch(keys, edgeTargets[cursor]);
                if (target < 0) continue; // A reference to an object the dump did not include.
                children[written++] = kept is null ? order[target] : byAddress![target];
            }
        }
        childStart[count] = written;
        // References between the objects this graph describes, which is the count that pairs with the
        // object count: a reference to an object the walk did not include is not one of them, and the
        // copies of a repeated walk are not counted twice here either.
        dump.EdgeCount = written;
        for (int i = 0; i < roots.Count; i++)
        {
            int target = Array.BinarySearch(keys, roots[i]);
            if (target >= 0) children[written++] = kept is null ? order[target] : byAddress![target];
        }
        Array.Resize(ref children, written);
        // Release the raw targets and the sorted keys before the dominator pass, which allocates
        // several more arrays of its own.
        edgeTargets.Release();
        keys = null!;
        order = null!;
        byAddress = null;

        var (retained, idom) = Dominators(count, childStart, children, nodeSizes, cancel);
        long unreachable = 0;
        for (int i = 0; i < count; i++) if (idom[i] == -1) unreachable += (long)nodeSizes[i];
        dump.UnreachableBytes = unreachable;
        long closure = retained[count] + unreachable;
        if (closure != shallowTotal)
        {
            dump.Complete = false;
            dump.Notes.Add($"The byte closure does not balance: reachable {retained[count]:N0} + unreachable {unreachable:N0} = {closure:N0} against a shallow total of {shallowTotal:N0}.");
        }

        // Which module owns the block each node lives in. Attribution has to be a partition:
        // summing retained per module counts every ancestor chain once per link, and double counts
        // as soon as one module's object dominates another module's.
        var blockOwner = new string?[count];
        var path = new List<int>();
        string OwnerOf(int start)
        {
            path.Clear();
            int current = start;
            while (true)
            {
                if (current >= count)
                {
                    string owner = Owner(nodeTypes[path[^1]]);
                    foreach (int item in path) blockOwner[item] = owner;
                    return owner;
                }
                if (current < 0 || idom[current] == -1)
                {
                    foreach (int item in path) blockOwner[item] = Unreachable;
                    return Unreachable;
                }
                if (blockOwner[current] is { } known)
                {
                    foreach (int item in path) blockOwner[item] = known;
                    return known;
                }
                path.Add(current);
                if (idom[current] >= count) // current itself is a holder root
                {
                    string owner = Owner(nodeTypes[current]);
                    foreach (int item in path) blockOwner[item] = owner;
                    return owner;
                }
                current = idom[current];
            }
        }

        // A type id the trace never declared still has to land somewhere; it is shown as
        // unresolved instead of being folded into whichever owner happens to be adjacent.
        string Owner(int type) => type < 0 || type >= owners.Length ? "unresolved module" : owners[type];

        var holders = new Dictionary<string, (long Bytes, int Nodes)>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string owner = OwnerOf(i);
            var previous = holders.GetValueOrDefault(owner);
            holders[owner] = (previous.Bytes + (long)nodeSizes[i], previous.Nodes + 1);
        }
        // The partition keeps the unreachable bucket, because it has to be visible; attributed
        // bytes are what a real owner holds, so the bucket is subtracted rather than counted.
        long unattributedShortfall = holders.GetValueOrDefault(Unreachable).Bytes;
        long attributed = holders.Values.Sum(v => v.Bytes) - unattributedShortfall;
        dump.AttributedBytes = attributed;
        dump.UnattributedBytes = shallowTotal - attributed - unreachable;
        dump.Holders = holders.OrderByDescending(kv => kv.Value.Bytes).Take(TopEntries)
            .Select(kv => new HeapDumpHolder(kv.Key, Mods(map, kv.Key), kv.Value.Bytes,
                shallowTotal > 0 ? 100.0 * kv.Value.Bytes / shallowTotal : 0, kv.Value.Nodes)).ToList();

        dump.Roots = Enumerable.Range(0, count).Where(i => idom[i] == count).OrderByDescending(i => retained[i]).Take(TopEntries)
            .Select(i => new HeapDumpRoot(Owner(nodeTypes[i]), TypeName(typeNames, nodeTypes[i]), Mods(map, Owner(nodeTypes[i])),
                retained[i], (long)nodeSizes[i], 0)).ToList();

        var byType = new Dictionary<int, (long Bytes, int Nodes)>();
        for (int i = 0; i < count; i++)
        {
            var previous = byType.GetValueOrDefault(nodeTypes[i]);
            byType[nodeTypes[i]] = (previous.Bytes + (long)nodeSizes[i], previous.Nodes + 1);
        }
        dump.Types = byType.OrderByDescending(kv => kv.Value.Bytes).Take(TopEntries)
            .Select(kv => new HeapDumpType(kv.Key < 0 ? "[type id not declared in the trace]" : typeNames[kv.Key],
                Owner(kv.Key), kv.Value.Bytes, kv.Value.Nodes)).ToList();

        var allocations = new Dictionary<string, (long Bytes, int Events)>(StringComparer.Ordinal);
        foreach (var (type, value) in sampled)
        {
            // The owner is the assembly that declares the type, with the type id from the sample; a
            // type id the walk never declared still counts, under "unresolved module", because an
            // allocation the trace recorded is evidence even when its type name could not be resolved.
            string owner = typeIndex.TryGetValue(value.TypeId, out int index) ? Owner(index) : "unresolved module";
            var previous = allocations.GetValueOrDefault(owner);
            allocations[owner] = (previous.Bytes + value.Bytes, previous.Events + (int)value.Count);
        }
        dump.Allocations = allocations.OrderByDescending(kv => kv.Value.Bytes).Take(TopEntries)
            .Select(kv => new HeapDumpAllocation(kv.Key, Mods(map, kv.Key), kv.Value.Bytes, kv.Value.Events)).ToList();
        dump.SampledAllocationBytes = sampled.Values.Sum(v => v.Bytes);
        dump.SampledAllocationEvents = sampled.Values.Sum(v => (int)v.Count);

        dump.Notes.Add("Retained bytes come from a dominator tree over the object graph: they prove which owner holds an object, never why it exists or who caused it. Read them as evidence, and confirm any suspicion by changing one thing and repeating the dump.");
        dump.Notes.Add("Ownership is assembly-based: a shared library, a vanilla type held by a mod, or an object reachable from two mods is attributed to the holder root the GC reports, which can differ from the mod a person would name.");
        double unreachableShare = 100.0 * unreachable / Math.Max(1, shallowTotal);
        if (unreachable > 0) dump.Notes.Add($"{unreachable:N0} bytes ({unreachableShare:F2}%) are not reachable from the strong roots in this trace and are reported separately instead of being attributed to an owner.");
        // A trace can describe the same heap more than once, because the runtime answers every attach
        // with a whole walk. Those copies were collapsed above, so what is left describes one
        // snapshot, and the repetition is reported as why the trace and the capture are as long as
        // they are rather than as more heap.
        bool repeated = count < n;
        if (repeated)
            dump.Notes.Add($"{n:N0} walked objects carried only {count:N0} distinct addresses: the runtime answered about {(double)n / count:F1} attaches with a whole walk of the same heap each, and those copies were collapsed onto the {count:N0} objects counted here instead of being counted as more heap. An address identifies one live object per walk, so the copy kept is the one that arrived first and the graph below is a snapshot from that walk.");
        // A coherent snapshot leaves only a trace of objects no strong root reaches. A large share
        // means the node set and the root set do not describe the same moment. One measured cause is
        // named directly: a full EventPipe buffer overwrites the walk's oldest events, and a walk
        // emits the roots first, so that is what a full buffer costs. Repeated walks used to land
        // here as well; they are collapsed above instead of being blamed on the heap.
        if (unreachableShare > CoherentUnreachablePercent)
        {
            dump.Complete = false;
            string cause = saturated
                ? $"the trace filled its {job.BufferMegabytes} MiB EventPipe buffer, which discards the oldest events of the walk first"
                : "the objects and the roots in this trace do not describe the same moment";
            dump.Notes.Add($"{unreachableShare:F1}% of the heap has no matching strong root. The cause is {cause}, so the totals count parts of the heap more than once and the per-owner attribution is unreliable: treat it as a partial picture and repeat the dump without another session attaching.");
        }
        if (rootEntries > rootCount) dump.Notes.Add($"{rootEntries:N0} GC root entries were reported and {rootCount:N0} strong ones were used: weak handles and finalizer-queue entries do not retain an object by themselves. Root counts by kind describe every reported entry.");
        if (dump.UnattributedBytes != 0) dump.Notes.Add($"{dump.UnattributedBytes:N0} bytes could not be attributed to any holder root: the partition does not balance.");
        if (!job.SampledAllocations) dump.Notes.Add("The allocation sample table was disabled for this dump.");
        else if (sampled.Count == 0) dump.Notes.Add("No allocation samples arrived while the dump was captured. The runtime raises one when about 100 KB has been allocated, so a target that allocated nothing during the capture leaves none, and the table is absent rather than zero.");
        else dump.Notes.Add($"Allocation figures come from GC allocation ticks: the runtime raises one when roughly 100 KB has been allocated and names the type and size of the object that crossed that boundary, so {dump.SampledAllocationEvents:N0} samples carrying {dump.SampledAllocationBytes:N0} bytes of sampled objects describe what was being allocated while the dump was captured and drained. They are a sample of allocation volume - the allocated volume behind them is roughly the sample count times that tick budget - not the retained set, and they carry no call stacks.");
        // Capture time is what a player pays in stack coverage, and it is not the span: the worker is
        // reading the walk out of the runtime and writing it to disk from the moment the session
        // starts until the stream goes quiet, and both ends of that transfer can be the slow part.
        // The explanation is deliberately free of this dump's measurements: it is the same caveat for
        // every dump in a recording, and the session-level list adds a note only once, so a version
        // carrying "62.9 s against 1578 ms" would be re-added verbatim-minus-numbers on every request.
        // The measurements stay in the per-dump line below, where they belong.
        dump.Notes.Add("The heap walk is emitted by the runtime once per session that attaches while the dump is capturing, and it is broadcast to every session that asked for one, so attaching sessions add whole walks instead of splitting one. Measured on one unchanged heap: 90 forced blocking gen2 collections inside the capture window changed nothing (19 ms span, 0.06% unattributable), while 15 two-second segments rotating through it produced 15.0 copies of that heap, which are collapsed above. A long span therefore means the walk was repeated, and it is not a measured suspension: compare the recorded frame intervals over the same window.");
        dump.Notes.Add($"This dump spent {dump.CaptureMilliseconds / 1000:F1} s capturing the trace out of the runtime and back to disk against a {dump.SpanMilliseconds:F0} ms walk span; capture can run far longer than the span without the game being stopped.");
        dump.Notes.Add("The raw trace is deleted as soon as this aggregate exists: no object addresses, field values or strings from the heap are kept in the report.");
        dump.ParseMilliseconds = watch.Elapsed.TotalMilliseconds;
    }

    private static string TypeName(List<string> names, int index) => index < 0 || index >= names.Count ? "[unresolved type]" : names[index];

    private static string[] Mods(Dictionary<string, string[]> map, string module) =>
        module.Length == 0 || module == Unreachable ? [] : map.GetValueOrDefault(Options.ModuleKey(module)) ?? [];

    private static string[] OwnerNames(List<ulong> typeModules, Dictionary<ulong, string> moduleNames)
    {
        var owners = new string[typeModules.Count];
        for (int i = 0; i < typeModules.Count; i++)
        {
            string path = moduleNames.GetValueOrDefault(typeModules[i], "");
            // A type whose module never declared itself is shown as unresolved rather than being
            // silently merged into another owner.
            owners[i] = path.Length == 0 ? "unresolved module" : Path.GetFileName(path);
        }
        return owners;
    }

    /// <summary>Iterative dominator tree (Cooper-Harvey-Kennedy) with a virtual root, then subtree sums.</summary>
    private static (long[] Retained, int[] Id) Dominators(int n, int[] childStart, int[] children, ulong[] sizes, CancellationToken cancel)
    {
        int total = n + 1, root = n;
        var predCount = new int[total];
        foreach (int child in children) predCount[child]++;
        var predStart = new int[total + 1];
        for (int i = 0; i < total; i++) predStart[i + 1] = predStart[i] + predCount[i];
        var predList = new int[children.Length];
        var cursor = new int[total];
        for (int u = 0; u < total; u++)
        {
            int end = u < n ? childStart[u + 1] : children.Length;
            for (int e = childStart[u]; e < end; e++)
            {
                int v = children[e];
                predList[predStart[v] + cursor[v]++] = u;
            }
        }

        var reversePost = new List<int>(total);
        var visited = new bool[total];
        var stack = new Stack<(int Node, int Child)>();
        visited[root] = true;
        stack.Push((root, childStart[root]));
        while (stack.Count > 0)
        {
            var (node, child) = stack.Pop();
            int end = node < n ? childStart[node + 1] : children.Length;
            while (child < end && visited[children[child]]) child++;
            if (child >= end) { reversePost.Add(node); continue; }
            stack.Push((node, child + 1));
            int next = children[child];
            visited[next] = true;
            stack.Push((next, childStart[next]));
        }
        reversePost.Reverse();

        var postOrder = new int[total];
        for (int i = 0; i < reversePost.Count; i++) postOrder[reversePost[i]] = i;
        int Intersect(int a, int b, int[] idomLocal)
        {
            while (a != b)
            {
                while (postOrder[a] > postOrder[b]) a = idomLocal[a];
                while (postOrder[b] > postOrder[a]) b = idomLocal[b];
            }
            return a;
        }

        var idom = new int[total];
        Array.Fill(idom, -1);
        idom[root] = root;
        bool changed = true;
        while (changed)
        {
            // The reduction is the second phase that can outrun a deadline, and it is the one the
            // deadline cannot reach through the trace reader. The check is per sweep rather than per
            // node: a sweep is one pass over every node, so one test per sweep cannot be the cost.
            cancel.ThrowIfCancellationRequested();
            changed = false;
            foreach (int node in reversePost)
            {
                if (node == root) continue;
                int newIdom = -1;
                for (int e = predStart[node]; e < predStart[node + 1]; e++)
                {
                    int pred = predList[e];
                    if (idom[pred] == -1) continue;
                    newIdom = newIdom == -1 ? pred : Intersect(pred, newIdom, idom);
                }
                if (newIdom != -1 && idom[node] != newIdom) { idom[node] = newIdom; changed = true; }
            }
        }

        var retained = new long[total];
        for (int i = 0; i < n; i++) retained[i] = (long)sizes[i];
        // Reverse post-order visits a dominator before everything it dominates, so one pass sums
        // every subtree; retained[root] has to equal the shallow total.
        for (int i = reversePost.Count - 1; i >= 0; i--)
        {
            int node = reversePost[i];
            if (node == root || idom[node] == -1) continue;
            retained[idom[node]] += retained[node];
        }
        return (retained, idom);
    }

    private sealed class Growth<T>
    {
        public T[] Items = new T[1024];
        public int Count;
        public T this[int index] => Items[index];
        public void Add(T value)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
            Items[Count++] = value;
        }
        public void Release() => Items = [];
    }
}
