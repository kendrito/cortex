using System.IO;
using Testy.Core;
using Testy.Windows;

internal static class CompositeCaptureChecks
{
    public static void Run(string output)
    {
        var checks = new List<object>(); var startedAt = DateTimeOffset.UtcNow; string error = "";
        try
        {
            var rear = new Native.CaptureSurfaceInfo(1, new() { Left = -100, Top = 50, Right = -90, Bottom = 60 });
            var popup = new Native.CaptureSurfaceInfo(2, new() { Left = -95, Top = 55, Right = -85, Bottom = 65 });
            var bounds = Native.UnionBounds([rear.Bounds, popup.Bounds]);
            var layout = new Native.CaptureLayout(1, bounds, [popup, rear]);
            byte[] image = Native.Composite(layout, s => Enumerable.Repeat((byte)(s.Window == 1 ? 51 : 204), 10 * 10 * 4).ToArray());
            Require(bounds.Left == -100 && bounds.Top == 50 && bounds.Right == -85 && bounds.Bottom == 65, "Union lost negative monitor coordinates.");
            Require(image[(7 * 15 + 7) * 4] == 204 && image[(2 * 15 + 2) * 4] == 51 && image[(2 * 15 + 12) * 4] == 32, "Composite did not preserve topmost popup, rear content and neutral gaps.");
            checks.Add(new { name = "Composite preserves Z-order, negative origins and neutral gaps", passed = true });
            bool rejected = false; try { Native.LayoutPoint(layout, 12, 2); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Gap accepted input coordinates.");
            var point = Native.LayoutPoint(layout, 7, 7); Require(point.X == -93 && point.Y == 57, "Screenshot point mapped incorrectly.");
            checks.Add(new { name = "Coordinate map rejects gaps and maps physical pixels", passed = true });
            Require(!Native.SameLayout(layout, new(1, bounds, [rear, popup])), "Changed Z-order accepted stale frame.");
            Require(!Native.SameLayout(layout, new(2, bounds, [popup, rear])), "Changed active window accepted stale frame.");
            Require(!Native.SameLayout(layout, new(1, bounds, [new(3, popup.Bounds), rear])), "Replaced HWND accepted stale frame.");
            checks.Add(new { name = "Stale surface ordering, activation and identity fail closed", passed = true });
            string files = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "capture-commit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(files);
            string destination = Path.Combine(files, "frame.bin"), old = Path.Combine(files, "old.tmp"), current = Path.Combine(files, "new.tmp"), cancelled = Path.Combine(files, "cancelled.tmp");
            File.WriteAllText(old, "old-render"); File.WriteAllText(current, "new-render"); File.WriteAllText(cancelled, "cancelled-render");
            var sync = new object(); int generation = 1; string metadata = "";
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var older = Task.Run(() =>
            {
                started.Set(); Require(release.Wait(TimeSpan.FromSeconds(3)), "Concurrent file regression timed out.");
                try { Native.CommitCaptureFile(old, destination, sync, () => generation == 1, () => metadata = "old", CancellationToken.None); return false; }
                catch (InvalidOperationException) { return true; }
            });
            Require(started.Wait(TimeSpan.FromSeconds(3)), "Old renderer did not start.");
            lock (sync) generation = 2;
            Native.CommitCaptureFile(current, destination, sync, () => generation == 2, () => metadata = "new", CancellationToken.None);
            release.Set(); Require(older.GetAwaiter().GetResult(), "Superseded renderer replaced the newer file.");
            Require(File.ReadAllText(destination) == "new-render" && metadata == "new", "Image bytes no longer match newer metadata.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelledRejected = false;
            try { Native.CommitCaptureFile(cancelled, destination, sync, () => generation == 2, () => metadata = "cancelled", cancellation.Token); }
            catch (OperationCanceledException) { cancelledRejected = true; }
            Require(cancelledRejected && File.ReadAllText(destination) == "new-render" && metadata == "new", "Cancelled renderer replaced valid evidence.");
            checks.Add(new { name = "Superseded concurrent and cancelled render cannot replace committed bytes or metadata", passed = true });
        }
        catch (Exception ex) { error = ex.ToString(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var report = new { passed = error.Length == 0 && checks.Count == 4, startedAt, finishedAt = DateTimeOffset.UtcNow, checks, error };
        WorkspaceStore.WriteAtomic(output, report); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, TestyJson.Options));
        if (!report.passed) throw new InvalidOperationException("Composite capture checks failed.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
