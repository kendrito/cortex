using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;

namespace Testy.Windows;

internal static partial class Native
{
    internal sealed record CaptureSurfaceInfo(nint Window, RECT Bounds);
    internal sealed record CaptureLayout(nint Active, RECT Bounds, CaptureSurfaceInfo[] Surfaces);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);

    // Surfaces with no drawable client area, minimized windows and DWM-cloaked helper
    // windows have no user-visible client evidence. Titles are deliberately irrelevant.
    internal static CaptureLayout ObserveLayout(TargetInfo target)
    {
        Validate(target); using var dpi = new DpiScope();
        var surfaces = new List<CaptureSurfaceInfo>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid != target.ProcessId || !IsWindowVisible(window) || IsIconic(window)) return true;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetClientRect(window, out var client) || client.Right - client.Left < 2 || client.Bottom - client.Top < 2) return true;
            if (!GetWindowRect(window, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            surfaces.Add(new(window, rect)); return surfaces.Count <= 16;
        }, 0);
        if (surfaces.Count is 0 or > 16) throw new InvalidOperationException("Capture requires between 1 and 16 visible same-process client surfaces.");
        surfaces = surfaces.OrderBy(s => ZOrder(s.Window)).ToList();
        var bounds = UnionBounds(surfaces.Select(s => s.Bounds));
        ValidateCaptureSize(bounds);
        return new(ActiveWindow(target), bounds, surfaces.ToArray());
    }
    private static int ZOrder(nint window)
    {
        var visited = new HashSet<nint>();
        for (nint previous = GetWindow(window, 3); previous != 0; previous = GetWindow(previous, 3)) // GW_HWNDPREV
            if (!visited.Add(previous) || visited.Count > 4096) throw new InvalidOperationException("Window stacking order changed or exceeded the bounded observation limit.");
        return visited.Count;
    }

    internal static RECT UnionBounds(IEnumerable<RECT> bounds)
    {
        var items = bounds.ToArray();
        if (items.Length == 0) throw new ArgumentException("No capture bounds were supplied.");
        return new() { Left = items.Min(r => r.Left), Top = items.Min(r => r.Top), Right = items.Max(r => r.Right), Bottom = items.Max(r => r.Bottom) };
    }
    private static void ValidateCaptureSize(RECT bounds)
    {
        long width = (long)bounds.Right - bounds.Left, height = (long)bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || width * height > 24_000_000)
            throw new InvalidOperationException("Combined target surfaces exceed the 8192-pixel side or 24-megapixel capture limit.");
    }
    private static bool SameRect(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    internal static bool SameLayout(CaptureLayout a, CaptureLayout b) => a.Active == b.Active && SameRect(a.Bounds, b.Bounds) &&
        a.Surfaces.Length == b.Surfaces.Length && a.Surfaces.Zip(b.Surfaces).All(pair => pair.First.Window == pair.Second.Window && SameRect(pair.First.Bounds, pair.Second.Bounds));
    internal static ElementBounds ScreenshotBounds(TargetInfo target)
    {
        var rect = ObserveLayout(target).Bounds;
        return new() { X = rect.Left, Y = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top };
    }
    internal static POINT LayoutPoint(CaptureLayout layout, int x, int y)
    {
        if (x < 0 || y < 0 || x >= layout.Bounds.Right - layout.Bounds.Left || y >= layout.Bounds.Bottom - layout.Bounds.Top)
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must lie inside the target screenshot in physical pixels.");
        var point = new POINT { X = checked(layout.Bounds.Left + x), Y = checked(layout.Bounds.Top + y) };
        if (!layout.Surfaces.Any(s => Contains(s.Bounds, point)))
            throw new InvalidOperationException("The screenshot point lies in the neutral gap between captured target windows. No input was sent.");
        return point;
    }
    private static bool Contains(RECT bounds, POINT point) => point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
    private static CaptureLayout? CurrentCaptureLayout(TargetInfo target, bool required)
    {
        var identity = identities.GetValue(target, _ => throw new InvalidOperationException());
        lock (identity)
        {
            if (required && identity.CapturedLayout is null)
                throw new InvalidOperationException("The captured target surfaces are unavailable or a new capture is in progress. Request fresh evidence before input.");
            return identity.CapturedLayout;
        }
    }
    internal static POINT InputPoint(TargetInfo target, nint window, int x, int y, bool requireCapturedLayout = true)
    {
        Validate(target);
        var captured = CurrentCaptureLayout(target, requireCapturedLayout);
        var current = ObserveLayout(target);
        if (captured is not null && !SameLayout(captured, current))
            throw new InvalidOperationException("The captured target surfaces changed. Request a new screenshot before using coordinates.");
        if (current.Active != window) throw new InvalidOperationException("The target active input window changed. No input was sent.");
        return LayoutPoint(captured ?? current, x, y);
    }
    private static void VerifyCapturedHit(TargetInfo target, nint hit, POINT point, bool requireCapturedLayout = true)
    {
        var current = ObserveLayout(target);
        var captured = CurrentCaptureLayout(target, requireCapturedLayout);
        if (captured is not null && !SameLayout(captured, current))
            throw new InvalidOperationException("The target surfaces changed after coordinate resolution. No input was sent.");
        nint root = RootWindow(hit);
        // The visible point must land on the highest captured target surface at that point.
        // Another same-process window opening over an old image is not an acceptable hit.
        var expected = current.Surfaces.FirstOrDefault(s => Contains(s.Bounds, point));
        if (expected is null || root != expected.Window)
            throw new InvalidOperationException("The point does not hit the captured topmost target surface. No input was sent.");
    }
    internal static byte[] Composite(CaptureLayout layout, Func<CaptureSurfaceInfo, byte[]> capture)
    {
        ValidateCaptureSize(layout.Bounds);
        int width = layout.Bounds.Right - layout.Bounds.Left, height = layout.Bounds.Bottom - layout.Bounds.Top;
        var pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 32; pixels[i + 1] = 32; pixels[i + 2] = 32; }
        foreach (var surface in layout.Surfaces.Reverse())
        {
            int sw = surface.Bounds.Right - surface.Bounds.Left, sh = surface.Bounds.Bottom - surface.Bounds.Top;
            byte[] content = capture(surface);
            if (content.Length != checked(sw * sh * 4)) throw new InvalidOperationException("Captured surface size changed.");
            for (int row = 0; row < sh; row++)
                Buffer.BlockCopy(content, row * sw * 4, pixels, ((row + surface.Bounds.Top - layout.Bounds.Top) * width + surface.Bounds.Left - layout.Bounds.Left) * 4, sw * 4);
        }
        return pixels;
    }
    internal static string Capture(TargetInfo target, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long generation = 0;
        if (identities.TryGetValue(target, out var prior))
        { lock (prior) { generation = ++prior.CaptureGeneration; prior.CapturedWindow = 0; prior.CapturedWidth = 0; prior.CapturedHeight = 0; prior.CapturedLayout = null; prior.LastScreenshot = null; } }
        Validate(target); using var dpi = new DpiScope();
        CaptureLayout layout; byte[] pixels; var readiness = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                layout = ObserveLayout(target);
                pixels = Composite(layout, surface => { cancellationToken.ThrowIfCancellationRequested(); return CaptureSurface(surface.Window, surface.Bounds); });
                cancellationToken.ThrowIfCancellationRequested();
                if (!SameLayout(layout, ObserveLayout(target))) throw new InvalidOperationException("Target surfaces changed while capturing. Request a new screenshot.");
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception && readiness.ElapsedMilliseconds < 1500)
            {
                // Native window-show animation helpers can exist briefly but cannot render via
                // PrintWindow. Retry observation/rendering only; never hide a titled/titleless
                // surface heuristically and never repeat input. Persistent unsupported surfaces fail.
                if (cancellationToken.WaitHandle.WaitOne(75)) cancellationToken.ThrowIfCancellationRequested();
            }
        }
        int width = layout.Bounds.Right - layout.Bounds.Left, height = layout.Bounds.Bottom - layout.Bounds.Top;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".capture-" + Guid.NewGuid().ToString("N") + ".tmp";
        var identity = identities.GetValue(target, _ => throw new InvalidOperationException());
        try
        {
            using (var output = File.Create(temporary)) encoder.Save(output);
            CommitCaptureFile(temporary, fullPath, identity, () => identity.CaptureGeneration == generation, () =>
            {
                identity.CapturedWindow = layout.Active; identity.CapturedWidth = width; identity.CapturedHeight = height; identity.CapturedLayout = layout;
                identity.LastScreenshot = new() { Path = fullPath, ProcessId = target.ProcessId, CapturedAt = DateTimeOffset.UtcNow, Bounds = new() { X = layout.Bounds.Left, Y = layout.Bounds.Top, Width = width, Height = height } };
            }, cancellationToken);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return fullPath;
    }
    internal static void CommitCaptureFile(string temporary, string destination, object synchronization, Func<bool> isCurrent, Action commitMetadata, CancellationToken cancellationToken)
    {
        lock (synchronization)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isCurrent()) throw new InvalidOperationException("A newer target capture superseded this image. Request fresh evidence before input.");
            // The generation check and final-path replacement share the metadata lock. A late
            // old renderer cannot overwrite the image advertised by a newer successful capture.
            File.Move(temporary, destination, overwrite: true);
            commitMetadata();
        }
    }
    internal static ScreenshotEvidence? LastScreenshot(TargetInfo target)
    {
        if (!identities.TryGetValue(target, out var identity)) return null;
        lock (identity) return identity.LastScreenshot is null ? null : TestyJson.Clone(identity.LastScreenshot);
    }
}
