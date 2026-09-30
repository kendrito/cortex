using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Testy.Core;

/// <summary>Same-user cross-process exclusion for one Windows session/window-station/desktop. Not a security sandbox.</summary>
public sealed class OperationsDesktopLease : IDisposable
{
    private FileStream? handle;
    private readonly int owner = Environment.ProcessId;
    private int executing;
    private readonly LeaseRecord record;
    public string DesktopIdentity { get; }
    private OperationsDesktopLease(FileStream handle, string identity, LeaseRecord record) { this.handle = handle; DesktopIdentity = identity; this.record = record; }
    private sealed class ProcessIdentity
    {
        public int Id { get; set; }
        public long StartUtcTicks { get; set; }
    }
    private sealed class LeaseRecord
    {
        public int Version { get; set; } = 1;
        public ProcessIdentity Owner { get; set; } = new();
        public bool Released { get; set; }
        public List<ProcessIdentity> Children { get; set; } = [];
    }
    public static OperationsDesktopLease? TryAcquire()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Interactive operations require Windows.");
        string identity = Process.GetCurrentProcess().SessionId + "/" + ObjectName(GetProcessWindowStation()) + "/" + ObjectName(GetThreadDesktop(GetCurrentThreadId()));
        var root = OperationsStorage.Root(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "OperationsLeases"), true);
        return Acquire(root, identity);
    }
    internal static OperationsDesktopLease? Acquire(string root, string identity)
    {
        root = OperationsStorage.Root(root, true);
        var path = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".lock");
        OperationsStorage.NoReparse(path);
        FileStream handle;
        try { handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        try
        {
            if (handle.Length > 0)
            {
                if (handle.Length > 65536) throw new InvalidDataException("Desktop lease metadata is invalid.");
                var previous = JsonSerializer.Deserialize<LeaseRecord>(handle, TestyJson.Options) ?? throw new InvalidDataException("Desktop lease metadata is absent.");
                if (previous.Version != 1 || previous.Children is null || previous.Children.Count > 32 || previous.Owner.Id < 1 || previous.Owner.StartUtcTicks <= 0)
                    throw new InvalidDataException("Desktop lease metadata is invalid.");
                if ((!previous.Released && Alive(previous.Owner)) || previous.Children.Any(Alive)) { handle.Dispose(); return null; }
            }
            using var process = Process.GetCurrentProcess();
            var record = new LeaseRecord { Owner = new() { Id = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks } };
            var lease = new OperationsDesktopLease(handle, identity, record); lease.Save(); return lease;
        }
        catch { handle.Dispose(); throw; }
    }
    /// <summary>Must be persisted before the child is authorized to send input. Residual exact process identities block the next lease even after host death.</summary>
    public void RegisterOwnedProcess(Process process)
    {
        AssertHeld();
        var identity = new ProcessIdentity { Id = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks };
        if (record.Children.Any(p => p.Id == identity.Id && p.StartUtcTicks == identity.StartUtcTicks)) return;
        if (record.Children.Count >= 32) throw new InvalidOperationException("Desktop lease process registration limit reached.");
        record.Children.Add(identity); Save();
    }
    private static bool Alive(ProcessIdentity identity)
    {
        if (identity.Id < 1 || identity.StartUtcTicks <= 0) return true;
        try { using var process = Process.GetProcessById(identity.Id); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartUtcTicks; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, TestyJson.Options);
        handle!.Position = 0; handle.Write(bytes); handle.SetLength(bytes.Length); handle.Flush(true);
    }
    public IDisposable EnterExecution()
    {
        AssertHeld();
        if (Interlocked.CompareExchange(ref executing, 1, 0) != 0) throw new InvalidOperationException("This desktop lease already has an active execution.");
        return new Execution(this);
    }
    internal void AssertHeld() { if (owner != Environment.ProcessId || handle is null) throw new InvalidOperationException("Desktop lease is unavailable or owned by a different process."); }
    private sealed class Execution(OperationsDesktopLease lease) : IDisposable
    {
        private OperationsDesktopLease? owner = lease;
        public void Dispose() { var value = Interlocked.Exchange(ref owner, null); if (value is not null) Volatile.Write(ref value.executing, 0); }
    }
    public void Dispose()
    {
        if (Volatile.Read(ref executing) != 0) throw new InvalidOperationException("Cannot release the desktop while an execution is still active.");
        if (handle is null) return;
        try { record.Released = true; Save(); }
        finally { Interlocked.Exchange(ref handle, null)?.Dispose(); }
    }
    private static string ObjectName(nint value)
    {
        if (value == 0) throw new InvalidOperationException("Windows desktop identity is unavailable.");
        var buffer = new StringBuilder(512);
        if (!GetUserObjectInformation(value, 2, buffer, buffer.Capacity * 2, out _)) throw new InvalidOperationException("Windows desktop identity could not be read.");
        return buffer.ToString();
    }
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int length, out int needed);
}
