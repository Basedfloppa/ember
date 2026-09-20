using System.Runtime.CompilerServices;

// This is a separate process: the worker must attach to this PID, not to itself.
Console.WriteLine(Environment.ProcessId);
Console.Out.Flush();

var gate = new object();
for (int i = 0; i < 3; i++)
{
    new Thread(() =>
    {
        while (true)
        {
            lock (gate) Thread.Sleep(2);
            Thread.Yield();
        }
    }) { IsBackground = true }.Start();
}

while (true)
{
    Work.Recurse(8);
    try { throw new InvalidOperationException("PRIVATE_EXCEPTION_MESSAGE_MUST_NOT_APPEAR"); }
    catch (InvalidOperationException) { }
    GC.KeepAlive(new byte[120_000]);
}

static class Work
{
    // Retain the recursive stack even in the Release integration build.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static long Recurse(int depth)
    {
        if (depth > 0) return Recurse(depth - 1) + depth;
        long result = 0;
        for (int i = 0; i < 100_000; i++) result += i;
        return result;
    }
}
