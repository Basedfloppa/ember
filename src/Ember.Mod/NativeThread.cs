using System.Runtime.InteropServices;

namespace Ember.Mod;

internal static class NativeThread
{
    public static long? CurrentId()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return GetCurrentThreadId();
            if (OperatingSystem.IsLinux()) return GetTid();
            if (OperatingSystem.IsMacOS() && PthreadThreadId(IntPtr.Zero, out ulong id) == 0) return unchecked((long)id);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("libc", EntryPoint = "gettid")] private static extern int GetTid();
    [DllImport("libSystem.B.dylib", EntryPoint = "pthread_threadid_np")] private static extern int PthreadThreadId(IntPtr thread, out ulong id);
}
