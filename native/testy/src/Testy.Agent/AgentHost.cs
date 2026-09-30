using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Testy.Core;

namespace Testy.Agent;

/// <summary>The always-on supervisor. It never claims jobs itself: it launches short-lived CLI pumps, one per ready target,
/// so every claim is owned by a process whose exit the existing queue reconciliation already understands.</summary>
internal sealed class AgentHost
{
    private sealed class Pump
    {
        public required string Target { get; init; }
        public required Process Process { get; init; }
        public required string CancelFile { get; init; }
        public required string OutputFile { get; init; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public Task? Drain { get; set; }
    }
    private readonly string workspace, stateDirectory, cli;
    private readonly OperationsStore store;
    private readonly AgentLog log;
    private readonly Dictionary<string, Pump> pumps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> backoff = new(StringComparer.Ordinal);
    private readonly List<AgentJobNotice> recent = [];
    private readonly Dictionary<string, OperationsJobStatus> seen = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private readonly object gate = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private MachineInventory? machines;
    private DateTimeOffset machinesAt = DateTimeOffset.MinValue, readinessAt = DateTimeOffset.MinValue;
    private Task? machineRefresh;
    private volatile bool deepRequested = true;
    private DateTimeOffset processNowUntil = DateTimeOffset.MinValue;
    private bool firstSnapshot = true;

    public event Action<AgentStatus>? StatusChanged;
    public event Action<AgentJobNotice>? JobFinished;
    public event Action? ShutdownRequested;
    public AgentStatus Status { get; private set; } = new();
    public string Workspace => workspace;
    public string StateDirectory => stateDirectory;
    public AgentLog Log => log;
    public bool Elevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static string Version => typeof(AgentHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public AgentHost(string workspace, AgentLog log)
    {
        this.workspace = workspace; this.log = log; stateDirectory = AgentFiles.Directory(workspace);
        cli = Path.Combine(AppContext.BaseDirectory, "Testy.Cli.exe");
        store = new OperationsStore(Path.Combine(workspace, "operations"));
        Directory.CreateDirectory(stateDirectory);
    }

    public void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    public void ProcessNow() { processNowUntil = DateTimeOffset.UtcNow.AddMinutes(2); backoff.Remove(OperationsTargets.LocalName); Wake(); }
    public void RequestMachineRefresh() { deepRequested = true; Wake(); }
    public IReadOnlyList<MachineInfo> Machines { get { lock (gate) return machines?.Machines.ToList() ?? []; } }
    public void SetPaused(bool paused) { var settings = AgentFiles.LoadSettings(workspace); settings.Paused = paused; AgentFiles.SaveSettings(workspace, settings); log.Info(paused ? "Paused by the user." : "Resumed by the user."); Wake(); }

    public async Task RunAsync(CancellationToken ct)
    {
        log.Info($"Agent {Version} started for workspace {workspace}; elevated={Elevated}; cli={cli}.");
        if (!File.Exists(cli)) log.Error("Testy.Cli.exe is missing next to the agent; queued work cannot run.");
        try { store.Reconcile(); } catch (Exception ex) { log.Error("Startup reconcile failed.", ex); }
        using var watcher = Watch();
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { log.Error("Agent loop iteration failed.", ex); Publish("error", "The agent hit an error and will retry: " + ex.Message, null, null); }
            try { await wake.WaitAsync(TimeSpan.FromSeconds(AgentStatus.HeartbeatSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
        log.Info($"Agent stopping; {pumps.Count} pump(s) continue independently until their jobs finish.");
        Publish("stopping", "The agent is stopping. Running jobs finish on their own; queued work waits for the agent.", null, null);
    }

    /// <summary>Cooperatively cancels running pumps (sign-out or shutdown). Their jobs end with verified cleanup or an uncertain outcome; nothing is retried.</summary>
    public void CancelRunningPumps(string reason)
    {
        foreach (var pump in pumps.Values) { try { File.WriteAllText(pump.CancelFile, "{\"cancel\":true}"); } catch (IOException) { } }
        if (pumps.Count > 0) log.Warn($"Cancelled {pumps.Count} running pump(s): {reason}");
    }

    private FileSystemWatcher? Watch()
    {
        try
        {
            var directory = Path.Combine(workspace, "operations"); Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory, "queue.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
            watcher.Changed += (_, _) => Wake(); watcher.Created += (_, _) => Wake(); watcher.Renamed += (_, _) => Wake();
            return watcher;
        }
        catch (Exception ex) { log.Warn("Queue change notifications unavailable; polling only. " + ex.Message); return null; }
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        ProcessControl();
        var settings = AgentFiles.LoadSettings(workspace);
        Reap(now);
        var snapshot = store.Snapshot(); var jobs = snapshot.Jobs; var schedules = snapshot.Schedules;
        Notice(jobs);
        var desktop = InteractiveDesktop.Observe();
        var local = LocalReadiness(desktop, settings, now);
        MaybeRefreshMachines(now);
        Dictionary<string, OperationsTargetReadiness> vmReady;
        lock (gate) vmReady = (machines?.Machines ?? []).Where(m => m.Target.Length > 0).ToDictionary(m => m.Target, m => m.HostReadiness(), StringComparer.Ordinal);
        var plan = AgentPlanner.Decide(jobs, schedules, now, settings.Paused, local, vmReady, pumps.Keys.ToHashSet(StringComparer.Ordinal), backoff,
            OperationsStore.IsClaimOwnerAlive, settings.MaximumVmRuns);
        if (plan.ReconcileNeeded) { try { store.Reconcile(); } catch (Exception ex) { log.Error("Reconcile failed.", ex); } }
        foreach (var launch in plan.Launch) StartPump(launch.Target, launch.QueuedJobs);
        var state = settings.Paused ? "paused" : pumps.Count > 0 ? "running" : plan.Waiting.Count > 0 ? "waiting" : "idle";
        var message = state switch
        {
            "paused" => "Paused. Queued and scheduled work waits until you resume.",
            "running" => $"Running {pumps.Count} job(s): " + string.Join(", ", pumps.Keys.Select(OperationsTargets.Label)),
            "waiting" => plan.Waiting[0].Reason,
            _ => "Idle. Watching the queue and schedules."
        };
        Publish(state, message, desktop, plan.Waiting);
    }

    private OperationsTargetReadiness LocalReadiness(InteractiveDesktopState desktop, AgentSettings settings, DateTimeOffset now)
    {
        if (!desktop.Available) return new(false, desktop.Reason);
        if (settings.LocalRuns == AgentSettings.Never) return new(false, "Runs on this PC are turned off; the agent runs VM jobs only.");
        if (settings.LocalRuns == AgentSettings.WhenIdle && now > processNowUntil && IdleTime() < TimeSpan.FromMinutes(settings.IdleMinutes))
            return new(false, $"Waiting until this PC has been idle for {settings.IdleMinutes} min, because local runs take over the mouse and keyboard. Use \"Run queued jobs now\" to start immediately.");
        return new(true, "Desktop available.");
    }

    private void StartPump(string target, int queued)
    {
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var directory = Path.Combine(stateDirectory, "pumps"); Directory.CreateDirectory(directory);
        var cancel = Path.Combine(directory, id + ".cancel"); var output = Path.Combine(directory, id + ".json");
        try
        {
            var process = Process.Start(CliStart("operations", "--operation", "pump", "--once", "--target", target, "--workspace", workspace, "--cancel-file", cancel))
                ?? throw new InvalidOperationException("The pump did not start.");
            var pump = new Pump { Target = target, Process = process, CancelFile = cancel, OutputFile = output };
            pump.Drain = DrainAsync(process, output);
            pumps[target] = pump;
            log.Info($"Started pump {id} (pid {process.Id}) for {target}; {queued} queued.");
        }
        catch (Exception ex) { backoff[target] = DateTimeOffset.UtcNow.AddMinutes(1); log.Error($"Could not start a pump for {target}.", ex); }
    }

    private void Reap(DateTimeOffset now)
    {
        foreach (var pump in pumps.Values.Where(p => p.Process.HasExited).ToList())
        {
            pumps.Remove(pump.Target);
            try { pump.Drain?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            string state = "unreadable", message = "";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pump.OutputFile));
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                { state = results[0].GetProperty("state").GetString() ?? state; message = results[0].GetProperty("message").GetString() ?? ""; }
                else if (root.TryGetProperty("error", out var error)) { state = "error"; message = error.GetString() ?? ""; }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
            if (state is "completed" or "idle") backoff.Remove(pump.Target);
            else backoff[pump.Target] = now + (pump.Target == OperationsTargets.LocalName ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(90));
            log.Info($"Pump for {pump.Target} ended (exit {pump.Process.ExitCode}): {state}. {message}");
            pump.Process.Dispose();
            try { File.Delete(pump.CancelFile); } catch (IOException) { }
        }
        try
        {
            var outputs = new DirectoryInfo(Path.Combine(stateDirectory, "pumps"));
            if (outputs.Exists) foreach (var old in outputs.GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(200)) old.Delete();
        }
        catch (IOException) { }
    }

    private void Notice(IReadOnlyList<OperationsJob> jobs)
    {
        foreach (var job in jobs)
        {
            bool terminal = job.Status is not (OperationsJobStatus.Queued or OperationsJobStatus.Running);
            if (!firstSnapshot && terminal && seen.TryGetValue(job.Id, out var previous) && previous is OperationsJobStatus.Queued or OperationsJobStatus.Running)
            {
                var notice = new AgentJobNotice { JobId = job.Id, Name = job.Request.Name, Target = OperationsTargets.Parse(job.Request.Target).Canonical, Status = job.Status, FinishedAt = job.FinishedAt, Message = job.Message };
                recent.Insert(0, notice); if (recent.Count > 20) recent.RemoveAt(recent.Count - 1);
                log.Info($"Job {job.Id} ({job.Request.Name}) on {notice.Target} finished: {job.Status}.");
                JobFinished?.Invoke(notice);
            }
            seen[job.Id] = job.Status;
        }
        firstSnapshot = false;
    }

    private void MaybeRefreshMachines(DateTimeOffset now)
    {
        if (machineRefresh is { IsCompleted: false }) return;
        bool hyperV; lock (gate) hyperV = machines?.HyperVAvailable ?? true;
        bool shallowDue = machines is null || now - machinesAt > TimeSpan.FromSeconds(hyperV ? 60 : 600);
        bool deepDue = hyperV && (deepRequested || now - readinessAt > TimeSpan.FromMinutes(5));
        if (!shallowDue && !deepDue) return;
        if (!Elevated)
        {
            lock (gate) { machines = new MachineInventory { Message = "The agent is not elevated, so Hyper-V VMs cannot be listed. Enable the agent at sign-in (it runs with your highest privileges)." }; machinesAt = now; }
            AgentFiles.Write(AgentFiles.MachinesPath(workspace), machines);
            return;
        }
        bool deep = deepDue; deepRequested = false;
        machineRefresh = Task.Run(async () =>
        {
            var inventory = await RunCliJsonAsync<MachineInventory>(TimeSpan.FromMinutes(3), "vms", "--operation", "list")
                ?? new MachineInventory { Message = "The Hyper-V inventory command failed; see the agent log." };
            Dictionary<Guid, MachineReadiness?> previous;
            lock (gate) previous = machines?.Machines.ToDictionary(m => m.VmId, m => m.Readiness) ?? [];
            foreach (var machine in inventory.Machines)
            {
                if (deep && machine.CredentialStored && machine.Running)
                    machine.Readiness = await RunCliJsonAsync<MachineReadiness>(TimeSpan.FromMinutes(5), "vms", "--operation", "check", "--vm", machine.VmId.ToString("D"))
                        ?? new MachineReadiness { Checked = true, Ready = false, Reason = "The readiness check did not return a result; see the agent log." };
                else if (machine.CredentialStored && previous.TryGetValue(machine.VmId, out var last)) machine.Readiness = last;
            }
            lock (gate) { machines = inventory; machinesAt = DateTimeOffset.UtcNow; if (deep) readinessAt = machinesAt; }
            AgentFiles.Write(AgentFiles.MachinesPath(workspace), inventory);
            if (deep) log.Info($"Machine readiness refreshed: {inventory.Machines.Count} VM(s). " + inventory.Message);
            Wake();
        });
    }

    private void ProcessControl()
    {
        var directory = AgentFiles.ControlDirectory(workspace);
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.request.json").Take(20).ToList())
        {
            var request = AgentFiles.TryRead<AgentControlRequest>(file);
            try { File.Delete(file); } catch (IOException) { continue; }
            if (request is null || !AgentControlRequest.Actions.Contains(request.Action, StringComparer.Ordinal) || request.Id.Length is 0 or > 64 || request.Id.Any(c => !char.IsAsciiLetterOrDigit(c))) continue;
            var result = new AgentControlResult { Id = request.Id, Action = request.Action };
            switch (request.Action)
            {
                case "process-now": ProcessNow(); result.Succeeded = true; result.Message = "Queued work on this PC runs now without waiting for idle time."; break;
                case "refresh-machines": RequestMachineRefresh(); result.Succeeded = true; result.Message = "Refreshing the VM list and readiness."; break;
                case "shutdown": result.Succeeded = true; result.Message = "The agent is stopping."; AgentFiles.Write(Path.Combine(directory, request.Id + ".result.json"), result); ShutdownRequested?.Invoke(); continue;
                case "setup-vm": SetupVm(request, result, directory); continue;
            }
            result.FinishedAt = DateTimeOffset.UtcNow;
            AgentFiles.Write(Path.Combine(directory, request.Id + ".result.json"), result);
        }
        try { foreach (var old in new DirectoryInfo(directory).GetFiles("*.result.json").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))) old.Delete(); }
        catch (IOException) { }
    }

    private void SetupVm(AgentControlRequest request, AgentControlResult result, string directory)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var target = OperationsTargets.Parse(request.Target);
                if (target.IsLocal) throw new InvalidDataException("Choose a VM to set up.");
                log.Info($"Setting up {target} for Testy.");
                using var output = await RunCliAsync(TimeSpan.FromMinutes(40), "vms", "--operation", "setup", "--vm", target.VirtualMachineId.ToString("D"));
                var root = output?.RootElement;
                result.Succeeded = root is { } r && r.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True;
                result.Message = root is { } m && m.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "Setup returned no result; see the agent log.";
            }
            catch (Exception ex) { result.Succeeded = false; result.Message = ex.Message; }
            result.FinishedAt = DateTimeOffset.UtcNow;
            AgentFiles.Write(Path.Combine(directory, request.Id + ".result.json"), result);
            log.Info($"VM setup finished: {(result.Succeeded ? "ok" : "failed")}. {result.Message}");
            RequestMachineRefresh();
        });
    }

    private void Publish(string state, string message, InteractiveDesktopState? desktop, IReadOnlyList<AgentWaitStatus>? waiting)
    {
        using var process = Process.GetCurrentProcess();
        var status = new AgentStatus
        {
            Version = Version, ProcessId = process.Id, ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks, StartedAt = started, HeartbeatAt = DateTimeOffset.UtcNow,
            Elevated = Elevated, InstallDirectory = AppContext.BaseDirectory, Workspace = workspace, State = state, Message = message,
            Paused = state == "paused", DesktopAvailable = desktop?.Available ?? false, DesktopReason = desktop?.Reason ?? "",
            Pumps = pumps.Values.Select(p => new AgentPumpStatus { Target = p.Target, ProcessId = p.Process.Id, StartedAt = p.StartedAt }).ToList(),
            Waiting = waiting?.ToList() ?? [], Recent = recent.ToList(), MachinesUpdatedAt = machinesAt == DateTimeOffset.MinValue ? null : machinesAt
        };
        Status = status;
        try { AgentFiles.Write(AgentFiles.StatusPath(workspace), status); } catch (Exception ex) { log.Warn("Could not write agent status: " + ex.Message); }
        StatusChanged?.Invoke(status);
    }

    private ProcessStartInfo CliStart(params string[] arguments)
    {
        var start = new ProcessStartInfo(cli)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)).ToList()) start.Environment.Remove(name);
        CortexModelBridge.BindChild(start);
        return start;
    }
    private static async Task DrainAsync(Process process, string output)
    {
        var stderr = process.StandardError.ReadToEndAsync();
        await using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var buffer = new byte[16384]; long total = 0; int count;
            var stream = process.StandardOutput.BaseStream;
            while ((count = await stream.ReadAsync(buffer)) > 0) { if (total < 16 * 1024 * 1024) { await file.WriteAsync(buffer.AsMemory(0, count)); total += count; } }
        }
        var error = await stderr;
        if (error.Length > 0) await File.WriteAllTextAsync(output + ".stderr.txt", error.Length > 65536 ? error[..65536] : error);
    }
    private async Task<JsonDocument?> RunCliAsync(TimeSpan timeout, params string[] arguments)
    {
        try
        {
            using var process = Process.Start(CliStart(arguments)) ?? throw new InvalidOperationException("The CLI did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(timeout)) != exited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } log.Warn("CLI command timed out: " + arguments[0]); return null; }
            var text = await stdout; await stderr;
            return text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.Error("CLI command failed: " + string.Join(' ', arguments.Take(3)), ex); return null; }
    }
    private async Task<T?> RunCliJsonAsync<T>(TimeSpan timeout, params string[] arguments) where T : class
    {
        using var document = await RunCliAsync(timeout, arguments);
        try { return document?.RootElement.Deserialize<T>(TestyJson.Options); }
        catch (JsonException ex) { log.Error("Unexpected CLI output for " + arguments[0], ex); return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    internal static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }
}
