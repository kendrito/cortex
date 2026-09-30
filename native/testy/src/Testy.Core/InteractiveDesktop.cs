using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Testy.Core;

/// <summary>What the calling process can observe about its own Windows desktop right now.</summary>
public sealed class InteractiveDesktopState
{
    public bool Available { get; set; }
    public string Reason { get; set; } = "";
    public int ProcessSession { get; set; }
    public uint ActiveConsoleSession { get; set; }
    public string WindowStation { get; set; } = "";
    public string ThreadDesktop { get; set; } = "";
    public string InputDesktop { get; set; } = "";
    public int SessionState { get; set; } = -1;
    public int InputDesktopError { get; set; }
    /// <summary>WTSINFOEX session flags on Windows 10+: 0 locked, 1 unlocked, -1 unknown. Informational; availability uses the input desktop.</summary>
    public int SessionLockFlags { get; set; } = -1;
    public bool Locked => SessionLockFlags == 0;
}

/// <summary>Shared by the worker preflight, the pump's pre-claim check and the background agent, so all three agree.</summary>
public static class InteractiveDesktop
{
    public static InteractiveDesktopState Observe()
    {
        if (!OperatingSystem.IsWindows()) return new() { Reason = "Interactive desktop execution requires Windows." };
        using var process = Process.GetCurrentProcess();
        var result = new InteractiveDesktopState { ProcessSession = process.SessionId, ActiveConsoleSession = WTSGetActiveConsoleSessionId() };
        result.WindowStation = Name(GetProcessWindowStation()); result.ThreadDesktop = Name(GetThreadDesktop(GetCurrentThreadId()));
        nint desktop = OpenInputDesktop(0, false, 1); result.InputDesktopError = desktop == 0 ? Marshal.GetLastWin32Error() : 0;
        if (desktop != 0) { try { result.InputDesktop = Name(desktop); } finally { CloseDesktop(desktop); } }
        if (WTSQuerySessionInformationW(0, result.ProcessSession, 8, out nint buffer, out int bytes))
        { try { if (bytes >= 4) result.SessionState = Marshal.ReadInt32(buffer); } finally { WTSFreeMemory(buffer); } }
        // WTSSessionInfoEx: DWORD Level; 8-byte-aligned WTSINFOEX_LEVEL1_W { SessionId @8; SessionState @12; SessionFlags @16; ... }.
        if (WTSQuerySessionInformationW(0, result.ProcessSession, 25, out nint ex, out int exBytes))
        {
            try { if (exBytes >= 20 && Marshal.ReadInt32(ex, 0) == 1 && Marshal.ReadInt32(ex, 8) == result.ProcessSession) result.SessionLockFlags = Marshal.ReadInt32(ex, 16); }
            finally { WTSFreeMemory(ex); }
        }
        result.Available = Environment.UserInteractive && result.ProcessSession > 0 && result.SessionState == 0 && result.WindowStation.Equals("WinSta0", StringComparison.OrdinalIgnoreCase)
            && result.ThreadDesktop.Equals("Default", StringComparison.OrdinalIgnoreCase) && result.ThreadDesktop.Equals(result.InputDesktop, StringComparison.OrdinalIgnoreCase);
        result.Reason = result.Available ? "Calling session has an accessible active default input desktop. Console-session equality is not required; active RDP sessions are supported."
            : result.ProcessSession == 0 ? "Interactive desktop unavailable: this process runs in Session 0. Do not run a UI worker as a Windows service."
            : result.SessionState != 0 ? "Interactive desktop unavailable: this Windows session is disconnected or not active. Reconnect it; queued local jobs stay queued."
            : result.Locked || result.InputDesktop.Length == 0 ? "Interactive desktop unavailable: this Windows session is locked or showing a secure desktop. Unlock it; queued local jobs stay queued."
            : "Interactive desktop unavailable. Unlock/connect the calling Windows session on its default input desktop; do not run this UI worker as a Session 0 service.";
        return result;
    }
    private static string Name(nint handle)
    {
        var value = new StringBuilder(256);
        return handle != 0 && GetUserObjectInformationW(handle, 2, value, value.Capacity * 2, out _) ? value.ToString() : "";
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder value, int size, out int needed);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformationW(nint server, int session, int information, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint memory);
}
