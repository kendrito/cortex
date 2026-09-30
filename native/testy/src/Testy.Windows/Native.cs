using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;

namespace Testy.Windows;

internal static partial class Native
{
    private sealed class ProcessIdentity(long startTimeTicks)
    {
        public long StartTimeTicks { get; } = startTimeTicks;
        public nint CapturedWindow { get; set; }
        public int CapturedWidth { get; set; }
        public int CapturedHeight { get; set; }
        public CaptureLayout? CapturedLayout { get; set; }
        public long CaptureGeneration;
        public ScreenshotEvidence? LastScreenshot { get; set; }
    }
    private static readonly ConditionalWeakTable<TargetInfo, ProcessIdentity> identities = new();
    internal delegate bool EnumWindowProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out RECT rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(nint dc, int x, int y, int width, int height, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] data, ref BITMAPINFO info, uint usage);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public nuint ExtraInfo; }

    internal static void Validate(TargetInfo target)
    {
        nint hwnd = (nint)target.WindowHandle;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!IsWindow(hwnd) || pid != target.ProcessId) throw new InvalidOperationException("The attached window has closed or changed process. Attach again.");
        if (!identities.TryGetValue(target, out var identity)) throw new InvalidOperationException("The target identity has not been verified by this driver.");
        using var process = Process.GetProcessById(target.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != identity.StartTimeTicks) throw new InvalidOperationException("The attached process has restarted. Attach again before continuing.");
    }

    internal static void RememberIdentity(TargetInfo target)
    {
        using var process = Process.GetProcessById(target.ProcessId);
        identities.Add(target, new ProcessIdentity(process.StartTime.ToUniversalTime().Ticks));
    }

    internal static List<nint> ProcessWindows(TargetInfo target)
    {
        Validate(target);
        var result = new List<nint> { (nint)target.WindowHandle };
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == target.ProcessId && hwnd != (nint)target.WindowHandle && IsWindowVisible(hwnd))
            {
                // WPF ComboBox/context-menu popups commonly have no window title. They still
                // belong to the attached process and must be included in the complete UIA tree.
                result.Add(hwnd);
            }
            return true;
        }, 0);
        return result;
    }

    internal static nint ActiveWindow(TargetInfo target)
    {
        Validate(target);
        nint OwnedRoot(nint candidate)
        {
            var root = RootWindow(candidate); GetWindowThreadProcessId(root, out var pid);
            return pid == target.ProcessId && IsWindowVisible(root) ? root : 0;
        }
        var foreground = OwnedRoot(GetForegroundWindow());
        if (foreground != 0 && IsWindowEnabled(foreground)) return foreground;
        var popup = OwnedRoot(GetLastActivePopup((nint)target.WindowHandle));
        if (popup != 0 && IsWindowEnabled(popup)) return popup;
        return foreground != 0 ? foreground : popup != 0 ? popup : (nint)target.WindowHandle;
    }

    // GA_ROOT follows parent HWNDs but deliberately preserves separately owned modal windows.
    internal static nint RootWindow(nint window) => window == 0 ? 0 : GetAncestor(window, 2);
    private static nint ForegroundRoot() => RootWindow(GetForegroundWindow());

    internal static nint Foreground(TargetInfo target, nint requestedWindow = default, CancellationToken cancellationToken = default)
    {
        Validate(target);
        nint window = requestedWindow == 0 ? ActiveWindow(target) : RootWindow(requestedWindow);
        GetWindowThreadProcessId(window, out var pid);
        if (pid != target.ProcessId) throw new InvalidOperationException("The requested input window does not belong to the attached process.");
        if (!IsWindowEnabled(window)) throw new InvalidOperationException("The target window is disabled or blocked by a modal dialog. No input was sent.");
        if (IsIconic(window)) throw new InvalidOperationException("Restore the target window before sending input.");
        SetForegroundWindow(window);
        // Activation can be delivered asynchronously to a just-started target's UI thread.
        // Only wait for the exact requested window; never weaken the focus guard.
        var activation = Stopwatch.StartNew();
        while (ForegroundRoot() != window && activation.ElapsedMilliseconds < 300)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(window)) break;
            Thread.Sleep(15);
        }
        if (ForegroundRoot() != window && IsWindow(window))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Ask only the selected application's accessibility provider to focus its window.
            // No global key or foreign-window focus workaround is used.
            var focus = Task.Run(() => System.Windows.Automation.AutomationElement.FromHandle(window).SetFocus(), cancellationToken);
            try { focus.Wait(TimeSpan.FromMilliseconds(500), cancellationToken); }
            catch (AggregateException) { /* Exact foreground verification remains authoritative. */ }
            activation.Restart();
            while (ForegroundRoot() != window && activation.ElapsedMilliseconds < 200)
            { cancellationToken.ThrowIfCancellationRequested(); Thread.Sleep(15); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (ForegroundRoot() != window) throw new InvalidOperationException("Windows did not grant foreground focus to the attached target. " + DesktopAvailability() + " Return to the process's interactive desktop and bring the target forward before retrying.");
        if (!IsWindowEnabled(window)) throw new InvalidOperationException("The target window became disabled or blocked by a modal dialog. No input was sent.");
        return window;
    }

    internal static void Click(TargetInfo target, int x, int y, CancellationToken cancellationToken = default)
    {
        nint window = Foreground(target, cancellationToken: cancellationToken);
        Move(target, window, x, y, requireCapturedLayout: false);
        MouseButton(target, window, "left", false, requireCapturedLayout: false);
    }

    internal static void Move(TargetInfo target, nint window, int x, int y, bool requireCapturedLayout = true)
    {
        RequireFocus(target, window, requireCapturedLayout);
        using var dpi = new DpiScope();
        var point = InputPoint(target, window, x, y, requireCapturedLayout);
        int sx = point.X, sy = point.Y;
        var atPoint = WindowFromPoint(new POINT { X = sx, Y = sy });
        GetWindowThreadProcessId(atPoint, out uint pid);
        if (pid != target.ProcessId) throw new InvalidOperationException($"The requested point ({x},{y}; screen {sx},{sy}) is covered by a different process (PID {pid}, target {target.ProcessId}). No click was sent.");
        VerifyCapturedHit(target, atPoint, point, requireCapturedLayout);
        if (!SetCursorPos(sx, sy)) throw new Win32Exception();
        // Recheck immediately before sending input; no unscoped desktop fallback.
        RequireFocus(target, window, requireCapturedLayout);
    }

    internal static nint CapturedWindow(TargetInfo target)
    {
        Validate(target);
        var identity = identities.GetValue(target, _ => throw new InvalidOperationException());
        if (identity.CapturedWindow == 0) throw new InvalidOperationException("Capture the target before executing a native computer action.");
        using var dpi = new DpiScope();
        if (identity.CapturedLayout is null || !SameLayout(identity.CapturedLayout, ObserveLayout(target)))
            throw new InvalidOperationException("The captured window surfaces moved, resized, opened, closed or changed stacking order. Request a new screenshot before using coordinates.");
        if (ActiveWindow(target) != identity.CapturedWindow) throw new InvalidOperationException("The target's active window changed since its last screenshot. Request a new screenshot before acting.");
        return identity.CapturedWindow;
    }

    internal static void VerifyPointer(TargetInfo target, nint window, int x, int y)
    {
        RequireFocus(target, window, true);
        using var dpi = new DpiScope();
        var expected = InputPoint(target, window, x, y);
        if (!GetCursorPos(out var pointer)) throw new Win32Exception();
        if (pointer.X != expected.X || pointer.Y != expected.Y) throw new InvalidOperationException($"The pointer moved after the target hit check (expected screen {expected.X},{expected.Y}; observed {pointer.X},{pointer.Y}). No click was sent.");
        GetWindowThreadProcessId(WindowFromPoint(pointer), out var pid);
        if (pid != target.ProcessId) throw new InvalidOperationException("The click point became covered by another process. No click was sent.");
        VerifyCapturedHit(target, WindowFromPoint(pointer), pointer);
    }

    internal static void MouseButton(TargetInfo target, nint window, string button, bool doubleClick, bool requireCapturedLayout = true)
    {
        RequireFocus(target, window, requireCapturedLayout);
        (uint down, uint up) = button.ToLowerInvariant() switch
        {
            "left" => (0x0002u, 0x0004u), "right" => (0x0008u, 0x0010u), "middle" or "wheel" => (0x0020u, 0x0040u),
            _ => throw new ArgumentException($"Unsupported mouse button '{button}'.")
        };
        var click = new[] { MouseInput(down), MouseInput(up) };
        Send(doubleClick ? click.Concat(click).ToArray() : click);
    }
    internal static INPUT MouseInput(uint flags, int data = 0) => new() { Type = 0, Data = new() { Mouse = new() { Flags = flags, MouseData = unchecked((uint)data) } } };
    internal static void RequireFocus(TargetInfo target, nint window, bool requireCapturedLayout = false)
    {
        Validate(target);
        if (!IsWindowEnabled(window)) throw new InvalidOperationException("The target window is disabled or blocked by a modal dialog. Input stopped.");
        if (ForegroundRoot() != window) throw new InvalidOperationException("Target lost foreground focus. Input stopped.");
        if (requireCapturedLayout && !SameLayout(CurrentCaptureLayout(target, true)!, ObserveLayout(target)))
            throw new InvalidOperationException("Captured target surfaces changed before input. Request a new screenshot.");
    }

    internal static void Press(TargetInfo target, string chord, nint requestedWindow = default, CancellationToken cancellationToken = default, bool requireCapturedLayout = false)
    {
        nint window = Foreground(target, requestedWindow, cancellationToken);
        // The key table and the refused chords live in Testy.Core so the validator rejects the same values when a test is saved.
        if (!KeyChords.TryParse(chord, out var codes, out var problem)) throw new ArgumentException(problem);
        var input = codes.Select(c => new INPUT { Type = 1, Data = new() { Keyboard = new() { Key = c } } })
            .Concat(codes.Reverse().Select(c => new INPUT { Type = 1, Data = new() { Keyboard = new() { Key = c, Flags = 2 } } })).ToArray();
        RequireFocus(target, window, requireCapturedLayout);
        Send(input);
    }

    internal static void Send(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked input. The target may have a higher integrity level.");
    }

    private static byte[] CaptureSurface(nint hwnd, RECT r)
    {
        if (IsIconic(hwnd) || !IsWindowVisible(hwnd)) throw new InvalidOperationException("Cannot capture a minimized or hidden target window.");
        if (!GetClientRect(hwnd, out var client)) throw new Win32Exception();
        var clientTopLeft = new POINT { X = client.Left, Y = client.Top };
        var clientBottomRight = new POINT { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(hwnd, ref clientTopLeft) || !ClientToScreen(hwnd, ref clientBottomRight)) throw new Win32Exception();
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 24_000_000) throw new InvalidOperationException("Target window capture size is invalid or exceeds the 24 megapixel limit.");
        nint source = GetWindowDC(hwnd), dc = 0, bitmap = 0, previous = 0;
        try
        {
            if (source == 0) throw new Win32Exception();
            dc = CreateCompatibleDC(source); bitmap = CreateCompatibleBitmap(source, width, height);
            if (dc == 0 || bitmap == 0) throw new Win32Exception();
            previous = SelectObject(dc, bitmap);
            if (!PatBlt(dc, 0, 0, width, height, 0x00000042)) throw new Win32Exception(); // BLACKNESS: never expose unpainted allocation contents.
            if (!PrintWindow(hwnd, dc, 2)) throw new InvalidOperationException("This application does not support target-window capture (PrintWindow failed).");
            SelectObject(dc, previous); previous = 0;
            byte[] pixels = new byte[checked(width * height * 4)];
            var info = new BITMAPINFO { Size = (uint)Marshal.SizeOf<BITMAPINFO>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) != height) throw new Win32Exception();
            try
            {
                WindowCaptureValidator.ValidateClientPixels(pixels, width, height,
                    clientTopLeft.X - r.Left, clientTopLeft.Y - r.Top, clientBottomRight.X - clientTopLeft.X, clientBottomRight.Y - clientTopLeft.Y);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(ex.Message + " PrintWindow did not provide usable client evidence; an inactive desktop or unsupported renderer may be responsible. " + DesktopAvailability(), ex);
            }
            return pixels;
        }
        finally
        {
            if (previous != 0 && dc != 0) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap); if (dc != 0) DeleteDC(dc); if (source != 0) ReleaseDC(hwnd, source);
        }
    }
    private static string DesktopAvailability()
    {
        nint desktop = OpenInputDesktop(0, false, 1); int error = Marshal.GetLastWin32Error();
        if (desktop != 0) CloseDesktop(desktop);
        using var process = Process.GetCurrentProcess();
        return $"Desktop diagnostic: foreground HWND={GetForegroundWindow()}, input desktop={(desktop != 0 ? "accessible" : "unavailable (Win32 " + error + ")")}, process session={process.SessionId}, active console session={WTSGetActiveConsoleSessionId()}.";
    }
    internal sealed class DpiScope : IDisposable
    {
        private readonly nint previous = SetThreadDpiAwarenessContext((nint)(-4));
        public void Dispose() { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
}
