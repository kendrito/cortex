using System.Diagnostics;
using System.Text.Json;

namespace Testy.Core;

/// <summary>Heartbeat written by the background agent every few seconds. Studio treats a stale heartbeat as "not running".</summary>
public sealed class AgentStatus
{
    public string Schema { get; set; } = "testy.agent-status.v1";
    public string Version { get; set; } = "";
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset HeartbeatAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Elevated { get; set; }
    public string InstallDirectory { get; set; } = "";
    public string Workspace { get; set; } = "";
    /// <summary>starting, idle, running, waiting, paused, stopping or error.</summary>
    public string State { get; set; } = "starting";
    public bool Paused { get; set; }
    public string Message { get; set; } = "";
    public bool DesktopAvailable { get; set; }
    public string DesktopReason { get; set; } = "";
    public List<AgentPumpStatus> Pumps { get; set; } = [];
    public List<AgentWaitStatus> Waiting { get; set; } = [];
    public List<AgentJobNotice> Recent { get; set; } = [];
    public DateTimeOffset? MachinesUpdatedAt { get; set; }
    public const int HeartbeatSeconds = 5, StaleSeconds = 20;
    public bool IsFresh(DateTimeOffset now) => now - HeartbeatAt < TimeSpan.FromSeconds(StaleSeconds) && HeartbeatAt <= now.AddMinutes(1);
}
public sealed class AgentPumpStatus
{
    public string Target { get; set; } = OperationsTargets.LocalName;
    public int ProcessId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}
public sealed class AgentWaitStatus
{
    public string Target { get; set; } = OperationsTargets.LocalName;
    public int QueuedJobs { get; set; }
    public string Reason { get; set; } = "";
}
public sealed class AgentJobNotice
{
    public string JobId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = OperationsTargets.LocalName;
    public OperationsJobStatus Status { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Message { get; set; } = "";
}
/// <summary>A request dropped into the agent's control folder by Studio or the CLI. It carries no secrets.</summary>
public sealed class AgentControlRequest
{
    public string Schema { get; set; } = "testy.agent-control.v1";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>process-now (run eligible local work once without waiting for idle time), refresh-machines, setup-vm or shutdown.
    /// Pausing is a persisted setting; cancelling a job goes through the queue.</summary>
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public static readonly string[] Actions = ["process-now", "refresh-machines", "setup-vm", "shutdown"];
}
public sealed class AgentControlResult
{
    public string Id { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Succeeded { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Hyper-V inventory as seen by the elevated agent. Guest readiness requires a stored credential and a PowerShell Direct check.</summary>
public sealed class MachineInventory
{
    public string Schema { get; set; } = "testy.machines.v1";
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool HyperVAvailable { get; set; }
    public string Message { get; set; } = "";
    public List<MachineInfo> Machines { get; set; } = [];
}
public sealed class MachineInfo
{
    public Guid VmId { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string Heartbeat { get; set; } = "";
    public long UptimeSeconds { get; set; }
    public int Generation { get; set; }
    public List<string> IpAddresses { get; set; } = [];
    public string GuestOs { get; set; } = "";
    public bool CredentialStored { get; set; }
    public string CredentialUser { get; set; } = "";
    public MachineReadiness? Readiness { get; set; }
    public string Target => VmId == Guid.Empty ? "" : OperationsTarget.ForVirtualMachine(VmId).Canonical;
    public bool Running => State.Equals("Running", StringComparison.OrdinalIgnoreCase);
    /// <summary>Cheap host-side gate for the agent. The pump still performs the full guest check before claiming a job.</summary>
    public OperationsTargetReadiness HostReadiness() =>
        !Running ? new(false, $"VM is {State}; start it and sign in to run tests.")
        : !CredentialStored ? new(false, "Set up this VM for Testy first (guest sign-in not stored).")
        : Readiness is { Checked: true, Ready: false } r && DateTimeOffset.UtcNow - r.CheckedAt < TimeSpan.FromMinutes(2) ? new(false, r.Reason)
        : new(true, "VM is running and set up.");
}
public sealed class MachineReadiness
{
    public bool Checked { get; set; }
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Ready { get; set; }
    public string Reason { get; set; } = "";
    public bool IdentityMatches { get; set; }
    public string ComputerName { get; set; } = "";
    public string DesktopUser { get; set; } = "";
    public bool DesktopUnlocked { get; set; }
    public int ActiveRunTasks { get; set; }
    public string WorkerManifestSha256 { get; set; } = "";
    public bool WorkerCurrent { get; set; }
    public double FreeDiskGb { get; set; }
}

/// <summary>User-editable agent behavior, read on every loop. Local runs take over this PC's mouse and keyboard, so they wait for idle time by default.</summary>
public sealed class AgentSettings
{
    public const string WhenIdle = "whenIdle", Always = "always", Never = "never";
    /// <summary>whenIdle (default), always, or never (VM targets only).</summary>
    public string LocalRuns { get; set; } = WhenIdle;
    public int IdleMinutes { get; set; } = 2;
    public bool Paused { get; set; }
    public int MaximumVmRuns { get; set; } = AgentPlanner.DefaultMaximumVmPumps;
    public void Normalize()
    {
        if (LocalRuns is not (WhenIdle or Always or Never)) LocalRuns = WhenIdle;
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 240); MaximumVmRuns = Math.Clamp(MaximumVmRuns, 1, 16);
    }
}

/// <summary>Paths and atomic I/O for the agent's per-workspace state. It lives outside the workspace so heartbeats never disturb
/// verified workspace backups. Readers allow replacement so polling never blocks the writer.</summary>
public static class AgentFiles
{
    public static string Directory(string workspace)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "Agent", hash);
    }
    public static string SettingsPath(string workspace) => Path.Combine(Directory(workspace), "settings.json");
    public static AgentSettings LoadSettings(string workspace) { var settings = TryRead<AgentSettings>(SettingsPath(workspace)) ?? new AgentSettings(); settings.Normalize(); return settings; }
    public static void SaveSettings(string workspace, AgentSettings settings) { settings.Normalize(); Write(SettingsPath(workspace), settings); }
    public static string StatusPath(string workspace) => Path.Combine(Directory(workspace), "status.json");
    public static string MachinesPath(string workspace) => Path.Combine(Directory(workspace), "machines.json");
    public static string ControlDirectory(string workspace) => Path.Combine(Directory(workspace), "control");
    public static string LogsDirectory(string workspace) => Path.Combine(Directory(workspace), "logs");
    public static string PumpsDirectory(string workspace) => Path.Combine(Directory(workspace), "pumps");
    public static string DefaultWorkspace => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "Workspace");

    public static void Write<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Replacement fails while a poller has the file open; pollers close within milliseconds, so retry for up to ~1 s.
        for (int attempt = 0; ; attempt++)
        {
            try { OperationsStorage.Write(path, value); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }
    public static T? TryRead<T>(string path, long maximumBytes = 4 * 1024 * 1024) where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maximumBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, TestyJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static AgentControlRequest Submit(string workspace, string action, string? target = null)
    {
        if (!AgentControlRequest.Actions.Contains(action, StringComparer.Ordinal)) throw new InvalidDataException("Unknown agent control action.");
        var request = new AgentControlRequest { Action = action, Target = target is null ? null : OperationsTargets.Normalize(target) };
        Write(Path.Combine(ControlDirectory(workspace), request.Id + ".request.json"), request);
        return request;
    }
    public static AgentControlResult? TryReadResult(string workspace, string id) => TryRead<AgentControlResult>(Path.Combine(ControlDirectory(workspace), id + ".result.json"));
    /// <summary>Fresh heartbeat plus a matching live process. An unreadable elevated process counts as alive while its heartbeat is fresh.</summary>
    public static bool IsRunning(AgentStatus? status)
    {
        if (status is null || !status.IsFresh(DateTimeOffset.UtcNow)) return false;
        try { using var process = Process.GetProcessById(status.ProcessId); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == status.ProcessStartUtcTicks; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}
