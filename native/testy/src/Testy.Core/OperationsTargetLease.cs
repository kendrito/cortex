using System.Diagnostics;
using System.Text.Json;

namespace Testy.Core;

/// <summary>Same-user exclusion for one VM target: at most one Testy run per guest desktop from this Windows user.
/// The guest-side run-task check covers other hosts and users. Not a security sandbox.</summary>
public sealed class OperationsTargetLease : IDisposable
{
    private FileStream? handle;
    private readonly int owner = Environment.ProcessId;
    public OperationsTarget Target { get; }
    private OperationsTargetLease(FileStream handle, OperationsTarget target) { this.handle = handle; Target = target; }

    public static OperationsTargetLease? TryAcquire(OperationsTarget target) =>
        Acquire(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "OperationsLeases"), target);

    internal static OperationsTargetLease? Acquire(string root, OperationsTarget target)
    {
        if (target.IsLocal) throw new InvalidOperationException("The local desktop uses OperationsDesktopLease.");
        root = OperationsStorage.Root(root, true);
        var path = Path.Combine(root, "vm-" + target.VirtualMachineId.ToString("D") + ".lock");
        OperationsStorage.NoReparse(path);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        try
        {
            using var process = Process.GetCurrentProcess();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, target = target.Canonical, processId = process.Id, startUtcTicks = process.StartTime.ToUniversalTime().Ticks, acquiredAt = DateTimeOffset.UtcNow }, TestyJson.Options);
            stream.Position = 0; stream.Write(bytes); stream.SetLength(bytes.Length); stream.Flush(true);
            return new OperationsTargetLease(stream, target);
        }
        catch { stream.Dispose(); throw; }
    }
    internal void AssertHeld() { if (owner != Environment.ProcessId || handle is null) throw new InvalidOperationException("Target lease is unavailable or owned by a different process."); }
    public void Dispose() => Interlocked.Exchange(ref handle, null)?.Dispose();
}
