using System;
using System.Runtime.InteropServices;

namespace DevBreak;

/// <summary>
/// Calls back when a window of another app comes to the front. A window's Deactivated is not enough to let go of
/// always-on-top: the hourly question can be shown on top without ever getting focus (Windows keeps focus with the app
/// you are typing in), and a window that never had focus never loses it - it stayed on top of everything.
/// </summary>
internal sealed class ForegroundWatch : IDisposable
{
    private delegate void WinEventProc(IntPtr hook, uint winEvent, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    private const uint ForegroundChanged = 3; // EVENT_SYSTEM_FOREGROUND
    private const uint OutOfContext = 0;      // called back on this thread, through its message loop

    // kept: the hook calls it for as long as it is set
    private readonly WinEventProc _proc;
    private readonly IntPtr _hook;

    public ForegroundWatch(Action otherApp)
    {
        uint self = (uint)Environment.ProcessId;
        _proc = (_, _, window, _, _, _, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process != self) otherApp();
        };
        _hook = SetWinEventHook(ForegroundChanged, ForegroundChanged, IntPtr.Zero, _proc, 0, 0, OutOfContext);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }
}
