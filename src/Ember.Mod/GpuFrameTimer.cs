using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace Ember.Mod;

/// <summary>
/// Optional, render-thread-only GPU timestamp sampling. Construct with the game's GL context
/// current; call BeginFrame at RenderBefore and EndFrame at RenderDone. BeginFrame also polls.
/// Samples measure elapsed GPU-clock time between those submitted markers, including pipeline
/// bubbles/CPU starvation. They exclude earlier game ticks and the subsequent present/swap;
/// this is neither GPU busy time/utilization nor total frame latency. Results arrive later and
/// must not be attributed to the CPU frame in which the callback happens.
/// </summary>
/// <remarks>
/// Uses independent timestamp queries, never BeginQuery, fences, Finish, Flush or GetError.
/// Availability is checked before every result read; no waiting or spinning is performed.
/// Driver calls themselves have no hard execution-time guarantee. Unsupported contexts,
/// context changes and managed GL/callback errors permanently disable this instance.
/// A driver that never makes results available simply fills the ring and causes skips.
/// </remarks>
internal sealed class GpuFrameTimer : IDisposable
{
    private const int Capacity = 16;
    private const int MaxSamplesPerFrame = 2;
    private readonly Action<double> recordMilliseconds;
    private readonly Action<string> onUnavailable;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly uint[] queries = new uint[Capacity * 2];
    private readonly SlotState[] states = new SlotState[Capacity];
    private Func<nint>? currentContext;
    private nint context;
    private ulong timestampMask = ulong.MaxValue;
    private int head, tail, count, active = -1, pollBudget = MaxSamplesPerFrame;
    private bool disposed, notified;

    public bool IsAvailable { get; private set; }
    public string Status { get; private set; } = "GPU timestamps not initialized";
    /// <summary>Successfully submitted complete Before/Done timestamp pairs.</summary>
    public long Submitted { get; private set; }
    /// <summary>Complete pairs read back and passed successfully to the sample callback.</summary>
    public long Completed { get; private set; }
    /// <summary>Frames skipped by ring pressure, missing Done, or discarded after disable/disposal.</summary>
    public long Skipped { get; private set; }

    public GpuFrameTimer(Action<double> recordMilliseconds, Action<string> onUnavailable)
    {
        this.recordMilliseconds = recordMilliseconds;
        this.onUnavailable = onUnavailable;
        try
        {
            currentContext = FindContextReader();
            context = currentContext?.Invoke() ?? 0;
            if (context == 0)
            {
                Disable("GPU timestamps unavailable: no supported current desktop GL context");
                return;
            }

            // GetString is valid on older desktop contexts, unlike asking them for MajorVersion.
            // Deliberately do not consume the game's/other mods' GL error flags.
            string? version = GL.GetString(StringName.Version);
            string[] numbers = (version ?? "").Split(' ')[0].Split('.');
            if (numbers.Length < 2 || !int.TryParse(numbers[0], out int major)
                || !int.TryParse(numbers[1], out int minor) || major < 3 || (major == 3 && minor < 3))
            {
                Disable("GPU timestamps unavailable: desktop OpenGL 3.3 or newer required");
                return;
            }

            GL.GetQuery(QueryTarget.Timestamp, GetQueryParam.QueryCounterBits, out int bits);
            if (bits is < 1 or > 64)
            {
                Disable("GPU timestamps unavailable: driver has no usable timestamp counter");
                return;
            }
            timestampMask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
            GL.GenQueries(queries.Length, queries);
            if (Array.Exists(queries, id => id == 0))
            {
                Disable("GPU timestamps unavailable: query allocation failed");
                return;
            }
            IsAvailable = true;
            Status = $"Available: GPU Before/Done elapsed timestamps ({bits}-bit; asynchronous; includes pipeline bubbles; excludes earlier tick/present; not GPU utilization)";
        }
        catch (Exception e) { Disable("GPU timestamps unavailable: " + e.GetType().Name); }
    }

    public void BeginFrame()
    {
        if (!CanUseContext()) return;
        try
        {
            // A missing Done must never pair the old start with a later frame's end, or reuse
            // an in-flight name. Retire its start query via the same bounded FIFO instead.
            if (active >= 0)
            {
                states[active] = SlotState.Abandoned;
                active = -1;
                Skipped++;
            }
            pollBudget = MaxSamplesPerFrame;
            Poll();
            if (!IsAvailable) return;
            if (count == Capacity) { Skipped++; return; }

            GL.QueryCounter(queries[tail * 2], QueryCounterTarget.Timestamp);
            active = tail;
            states[active] = SlotState.Active;
            tail = (tail + 1) % Capacity;
            count++;
        }
        catch (Exception e) { Disable("GPU timestamp begin failed: " + e.GetType().Name); }
    }

    public void EndFrame()
    {
        if (!CanUseContext() || active < 0) return;
        try
        {
            GL.QueryCounter(queries[active * 2 + 1], QueryCounterTarget.Timestamp);
            states[active] = SlotState.Pending;
            active = -1;
            Submitted++;
        }
        catch (Exception e) { Disable("GPU timestamp end failed: " + e.GetType().Name); }
    }

    /// <summary>
    /// Drains at most two slots (four 64-bit result reads) per BeginFrame interval. Repeated
    /// Poll calls cannot increase that budget; an unavailable head costs one polling attempt.
    /// </summary>
    public void Poll()
    {
        if (!CanUseContext()) return;
        try
        {
            while (count > 0 && pollBudget > 0 && states[head] != SlotState.Active)
            {
                pollBudget--;
                uint startQuery = queries[head * 2], endQuery = queries[head * 2 + 1];
                // Check both markers explicitly; QueryResult is never used speculatively.
                if (!Ready(startQuery)) break;
                if (states[head] == SlotState.Abandoned) { ReleaseHead(); continue; }
                if (!Ready(endQuery)) break;
                GL.GetQueryObject(startQuery, GetQueryObjectParam.QueryResult, out ulong start);
                GL.GetQueryObject(endQuery, GetQueryObjectParam.QueryResult, out ulong end);
                // Modulo subtraction handles a single counter wrap. As with timestamp queries
                // generally, device-clock discontinuities/multiple wraps cannot be diagnosed
                // reliably here. There is no portable desktop-GL disjoint flag.
                double elapsed = (unchecked(end - start) & timestampMask) / 1_000_000.0;
                ReleaseHead();
                recordMilliseconds(elapsed);
                Completed++;
                if (!IsAvailable || disposed) return;
            }
        }
        catch (Exception e) { Disable("GPU timestamp readback failed: " + e.GetType().Name); }
    }

    private static bool Ready(uint query)
    {
        GL.GetQueryObject(query, GetQueryObjectParam.QueryResultAvailable, out int available);
        return available != 0;
    }

    private void ReleaseHead()
    {
        states[head] = SlotState.Free;
        head = (head + 1) % Capacity;
        count--;
    }

    private bool CanUseContext()
    {
        if (disposed || !IsAvailable) return false;
        try
        {
            if (Environment.CurrentManagedThreadId == ownerThread && currentContext?.Invoke() == context)
                return true;
            Disable("GPU timestamps unavailable: original render context is no longer current");
        }
        catch (Exception e) { Disable("GPU timestamp context check failed: " + e.GetType().Name); }
        return false;
    }

    private void Disable(string reason)
    {
        IsAvailable = false;
        Status = reason;
        DiscardOutstanding();
        DeleteQueriesIfCurrent();
        if (notified) return;
        notified = true;
        try { onUnavailable(reason); }
        catch { /* Optional diagnostics callbacks must not interrupt rendering. */ }
    }

    private void DiscardOutstanding()
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (states[i] is SlotState.Active or SlotState.Pending) Skipped++;
            states[i] = SlotState.Free;
        }
        count = 0;
        active = -1;
    }

    private void DeleteQueriesIfCurrent()
    {
        try
        {
            // Never delete names in a different context (they may belong to another mod), or
            // make a context current during teardown. If unavailable, retain at most 32 names
            // for a later Dispose attempt; context destruction ultimately reclaims them.
            if (context == 0 || Environment.CurrentManagedThreadId != ownerThread
                || currentContext?.Invoke() != context) return;
            if (!Array.Exists(queries, id => id != 0)) return;
            GL.DeleteQueries(queries.Length, queries);
            Array.Clear(queries);
        }
        catch { /* Best effort only, including a lost context/unloaded GL binding. */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        IsAvailable = false;
        Status = "GPU timestamps disposed";
        DiscardOutstanding();
        DeleteQueriesIfCurrent();
    }

    private enum SlotState : byte { Free, Active, Pending, Abandoned }

    // OpenTK.Graphics exposes GL bindings, not a portable current-context object. Native
    // identity checks avoid loading another windowing library or changing the game's context.
    private static Func<nint>? FindContextReader()
    {
        if (OperatingSystem.IsWindows()) return WglGetCurrentContext;
        if (OperatingSystem.IsMacOS()) return CglGetCurrentContext;
        if (OperatingSystem.IsLinux())
        {
            try { if (GlxGetCurrentContext() != 0) return GlxGetCurrentContext; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return EglGetCurrentContext;
        }
        return null;
    }

    [DllImport("opengl32.dll", EntryPoint = "wglGetCurrentContext")]
    private static extern nint WglGetCurrentContext();
    [DllImport("libGL.so.1", EntryPoint = "glXGetCurrentContext")]
    private static extern nint GlxGetCurrentContext();
    [DllImport("libEGL.so.1", EntryPoint = "eglGetCurrentContext")]
    private static extern nint EglGetCurrentContext();
    [DllImport("/System/Library/Frameworks/OpenGL.framework/OpenGL", EntryPoint = "CGLGetCurrentContext")]
    private static extern nint CglGetCurrentContext();
}
