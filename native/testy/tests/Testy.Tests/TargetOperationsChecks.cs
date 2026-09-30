using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Testy.Core;

namespace Testy.Tests;

/// <summary>Execution targets, saved-test jobs, VM leases, the background agent's pure decisions and its sign-in task definition.</summary>
internal static class TargetOperationsChecks
{
    internal static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("targets accept only canonical local and VM spellings", TargetParsing);
        yield return ("legacy lifecycle requests serialize byte-identically and keep store format 1", LegacyHashes);
        yield return ("saved-test jobs validate target, executable and provider rules", TestJobValidation);
        yield return ("saved-test jobs pass only with a no-preparation result and mark store format 2", TestJobCompletion);
        yield return ("VM jobs are claimed only through their VM lease after readiness", VmClaims);
        yield return ("VM lease excludes another process", VmLeaseAcrossProcesses);
        yield return ("an unavailable desktop leaves local jobs queued without dispatch", DesktopUnavailable);
        yield return ("agent planner waits for locked desktops, pauses, busy targets and unready VMs", PlannerWaits);
        yield return ("agent planner launches ready targets and requests reconcile for due schedules or dead claims", PlannerLaunches);
        yield return ("agent sign-in task runs in the interactive session with highest privileges", TaskXml);
        yield return ("VM sign-in round-trips through the current user's credential vault", VmCredentials);
        yield return ("agent state lives outside the workspace and reads tolerate replacement", AgentFileLayout);
        yield return ("data saved before the rename from Axiom still loads: lifecycle profiles, VM sign-ins and API keys", LegacyNameCompatibility);
    }

    private static Task LegacyNameCompatibility()
    {
        using var f = new Fixture();
        var legacy = OperationsChecks.RequestForRoot(f.Root); legacy.Lifecycle!.Profile.Schema = LifecycleProfile.LegacySchema;
        OperationsStore.ValidateRequest(legacy);
        Check(LifecycleProfile.LegacySchema == "axiom.lifecycle.v1" && new LifecycleProfile().Schema == "testy.lifecycle.v1", "Lifecycle schema names changed unexpectedly.");
        var unknown = OperationsChecks.RequestForRoot(f.Root); unknown.Lifecycle!.Profile.Schema = "other.lifecycle.v1";
        Reject(() => OperationsStore.ValidateRequest(unknown), "unknown lifecycle schema");
        var vm = Guid.NewGuid();
        try
        {
            VmCredentialStore.Save(VmCredentialStore.LegacyTargetName(vm), "Administrator", "legacy secret");
            Check(VmCredentialStore.LegacyTargetName(vm) == "Axiom/VM/" + vm.ToString("D") && VmCredentialStore.StoredUser(vm) == "Administrator" && VmCredentialStore.Read(vm)?.Password == "legacy secret", "A VM sign-in saved under the old name was not found.");
            VmCredentialStore.Save(vm, "Administrator", "current secret");
            Check(VmCredentialStore.Read(vm)?.Password == "current secret", "The current VM sign-in must win over the old one.");
            VmCredentialStore.Delete(vm); Check(VmCredentialStore.Read(vm) is null, "Delete left an old-name VM sign-in behind.");
        }
        finally { VmCredentialStore.Delete(vm); }
        var provider = new ProviderSettings { Kind = ProviderKind.Compatible, Model = "m", Endpoint = $"https://legacy-{Guid.NewGuid():N}.example.test/v1/chat/completions", ApiKeyEnvironmentVariable = "TESTY_LEGACY_UNSET_" + Guid.NewGuid().ToString("N") };
        Check(ProviderCredentialStore.TryTarget(provider, out var target) && target.StartsWith("Testy/Provider/", StringComparison.Ordinal), "Provider target name changed unexpectedly.");
        try
        {
            VmCredentialStore.Save(ProviderCredentialStore.Legacy(target), "Axiom provider", "legacy-key-value");
            Check(ProviderCredentialStore.Resolve(provider) == "legacy-key-value" && ProviderCredentialStore.HasStored(provider), "An API key stored under the old name was not found.");
            ProviderCredentialStore.Delete(provider);
            Check(ProviderCredentialStore.Resolve(provider) is null && !ProviderCredentialStore.HasStored(provider), "Delete left an old-name API key behind.");
        }
        finally { ProviderCredentialStore.Delete(provider); }
        return Task.CompletedTask;
    }

    private static readonly Guid Vm = Guid.Parse("27b5d3be-49d0-42a0-8227-5485028fb9b5");
    private static string VmTarget => "vm:" + Vm.ToString("D");
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Testy-target-checks-" + Guid.NewGuid().ToString("N"));
        internal Fixture() { Directory.CreateDirectory(Root); }
        internal OperationsStore Store() => new(Path.Combine(Root, "operations"));
        internal OperationsDesktopLease? DesktopLease() => OperationsDesktopLease.Acquire(Path.Combine(Root, "leases"), "owned-test-desktop");
        internal OperationsTargetLease? VmLease() => OperationsTargetLease.Acquire(Path.Combine(Root, "leases"), OperationsTarget.ForVirtualMachine(Vm));
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Testy-target-checks-", StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private static TestCase SavedTest() => new() { Name = "Owned saved test", Steps = [new() { Action = StepAction.Click, Selector = "id:Go" }, new() { Action = StepAction.AssertText, Selector = "id:Status", Value = "Done" }] };
    private static OperationsJobRequest TestJob(string? target, string exe, ProviderSettings? provider = null, string? stage = null) => new()
    {
        Name = "Saved test job", Target = target,
        Test = new() { Test = SavedTest(), Executable = exe, Provider = provider, StageDirectory = stage, TimeoutSeconds = 120 }
    };
    private static ProviderSettings Compatible() => new() { Kind = ProviderKind.Compatible, Model = "openai/gpt-5.6-terra", Endpoint = "https://openrouter.ai/api/v1/chat/completions", ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY", NativeComputerUse = false, MaximumAgentTurns = 12 };
    private static LifecycleResult TestResult(bool passed = true)
    {
        var now = DateTimeOffset.UtcNow;
        return new() { Status = passed ? LifecycleStatus.Passed : LifecycleStatus.Failed, StartedAt = now, FinishedAt = now, WorkerPassed = passed, CleanupComplete = true, PreparationNotApplicable = true, Target = "local" };
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message) { try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException) { return; } throw new InvalidOperationException("Expected rejection: " + message); }

    private static Task TargetParsing()
    {
        Check(OperationsTargets.Parse(null).IsLocal && OperationsTargets.Parse("").IsLocal && OperationsTargets.Parse("local").IsLocal, "Local spellings.");
        var vm = OperationsTargets.Parse(VmTarget); Check(!vm.IsLocal && vm.VirtualMachineId == Vm && vm.Canonical == VmTarget, "VM parse.");
        Check(OperationsTargets.Normalize("VM:{" + Vm.ToString("D").ToUpperInvariant() + "}") == VmTarget && OperationsTargets.Normalize(" Local ") == "local", "Normalization.");
        Check(!OperationsTargets.IsCanonical("vm:" + Vm.ToString("D").ToUpperInvariant()) && OperationsTargets.IsCanonical(VmTarget) && OperationsTargets.IsCanonical(null), "Canonical spelling.");
        foreach (var bad in new[] { "vm:", "vm:not-a-guid", "vm:" + Guid.Empty.ToString("D"), "hyperv:" + Vm, "LOCAL" }) Reject(() => OperationsTargets.Parse(bad), bad);
        Check(OperationsTargets.Label(VmTarget).StartsWith("VM 27b5d3be", StringComparison.Ordinal) && OperationsTargets.Label(null) == "This PC", "Labels.");
        return Task.CompletedTask;
    }

    private static Task LegacyHashes()
    {
        using var f = new Fixture();
        var legacy = OperationsChecks.RequestForRoot(f.Root);
        var json = JsonSerializer.Serialize(legacy, TestyJson.Options);
        Check(json.StartsWith("{\r\n  \"name\": ", StringComparison.Ordinal) || json.StartsWith("{\n  \"name\": ", StringComparison.Ordinal), "Legacy property order changed.");
        using (var parsed = JsonDocument.Parse(json))
            Check(parsed.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(["name", "lifecycle"]), "New optional members leaked into a legacy request.");
        var roundTrip = JsonSerializer.Deserialize<OperationsJobRequest>(json, TestyJson.Options)!;
        Check(JsonSerializer.Serialize(roundTrip, TestyJson.Options) == json && roundTrip.Lifecycle is not null && roundTrip.Test is null, "Legacy request bytes changed after a round trip.");
        var store = f.Store(); store.Enqueue(legacy);
        Check(File.ReadAllText(Path.Combine(f.Root, "operations", "queue.json")).Contains("\"formatVersion\": 1", StringComparison.Ordinal), "A lifecycle-only store must stay format 1 for older builds.");
        var result = new LifecycleResult(); var resultJson = JsonSerializer.Serialize(result, TestyJson.Options);
        Check(!resultJson.Contains("preparationNotApplicable", StringComparison.Ordinal) && !resultJson.Contains("\"target\"", StringComparison.Ordinal), "Historical lifecycle results gained new members.");
        return Task.CompletedTask;
    }

    private static Task TestJobValidation()
    {
        var exe = Environment.ProcessPath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath! : @"C:\Apps\Owned.exe";
        OperationsStore.ValidateRequest(TestJob(null, exe));
        OperationsStore.ValidateRequest(TestJob(null, exe, Compatible()));
        OperationsStore.ValidateRequest(TestJob(VmTarget, @"{worker}\Testy.TestLab.exe"));
        OperationsStore.ValidateRequest(TestJob(VmTarget, @"C:\Apps\Owned.exe", Compatible()));
        OperationsStore.ValidateRequest(TestJob(VmTarget, @"bin\Owned.exe", null, @"C:\Builds\Owned"));
        Reject(() => OperationsStore.ValidateRequest(TestJob(null, @"relative\Owned.exe")), "relative local exe");
        Reject(() => OperationsStore.ValidateRequest(TestJob(null, exe, null, @"C:\Builds\Owned")), "local staging");
        Reject(() => OperationsStore.ValidateRequest(TestJob(VmTarget, @"bin\Owned.exe")), "relative VM exe without staging");
        Reject(() => OperationsStore.ValidateRequest(TestJob(VmTarget, @"C:\Apps\Owned.exe", null, @"C:\Builds\Owned")), "absolute VM exe with staging");
        Reject(() => OperationsStore.ValidateRequest(TestJob(VmTarget, @"{worker}\..\Other.exe")), "worker path escape");
        Reject(() => OperationsStore.ValidateRequest(TestJob(VmTarget, @"C:\Apps\..\Owned.exe")), "guest path traversal");
        Reject(() => OperationsStore.ValidateRequest(TestJob(VmTarget, @"\\server\share\Owned.exe")), "UNC guest path");
        Reject(() => OperationsStore.ValidateRequest(TestJob(VmTarget, @"{worker}\Testy.TestLab.exe", new ProviderSettings { Kind = ProviderKind.Codex, MaximumAgentTurns = 12 })), "Codex in a VM");
        Reject(() => OperationsStore.ValidateRequest(TestJob(null, exe, new ProviderSettings { Kind = ProviderKind.Offline })), "offline provider as AI");
        var loopback = Compatible(); loopback.Endpoint = "http://localhost:1234/v1/chat/completions";
        OperationsStore.ValidateRequest(TestJob(null, exe, loopback));
        Reject(() => OperationsStore.ValidateRequest(TestJob(VmTarget, @"{worker}\Testy.TestLab.exe", loopback)), "loopback endpoint for a VM run");
        var withProject = Compatible(); withProject.ProjectTools = new() { Enabled = true, RootDirectory = Path.GetTempPath() };
        Reject(() => OperationsStore.ValidateRequest(TestJob(null, exe, withProject)), "project tools in a saved-test job");
        var noAssertion = TestJob(null, exe, Compatible()); noAssertion.Test!.Test.Steps.RemoveAt(1);
        Reject(() => OperationsStore.ValidateRequest(noAssertion), "AI test without assertions");
        Reject(() => OperationsStore.ValidateRequest(TestJob("vm:" + Vm.ToString("D").ToUpperInvariant(), @"{worker}\Testy.TestLab.exe")), "non-canonical target");
        using var f = new Fixture();
        var lifecycleOnVm = OperationsChecks.RequestForRoot(f.Root); lifecycleOnVm.Target = VmTarget;
        Reject(() => OperationsStore.ValidateRequest(lifecycleOnVm), "lifecycle on a VM");
        var both = OperationsChecks.RequestForRoot(f.Root); both.Test = TestJob(null, exe).Test;
        Reject(() => OperationsStore.ValidateRequest(both), "two workloads");
        Reject(() => OperationsStore.ValidateRequest(new OperationsJobRequest { Name = "empty" }), "no workload");
        return Task.CompletedTask;
    }

    private static async Task TestJobCompletion()
    {
        using var f = new Fixture(); var store = f.Store();
        var empty = store.Snapshot(); Check(empty.Jobs is { Count: 0 } && empty.Schedules is { Count: 0 }, "An empty store snapshot lost its lists.");
        var job = store.Enqueue(TestJob(null, Environment.ProcessPath!));
        store.AddSchedule(new() { Name = "s", Request = TestJob(VmTarget, @"{worker}\Testy.TestLab.exe"), FirstRunAt = DateTimeOffset.UtcNow.AddHours(1) });
        var snapshot = store.Snapshot();
        Check(snapshot.Jobs.Single().Id == job.Id && snapshot.Schedules.Single().TargetLabel.StartsWith("VM ", StringComparison.Ordinal), "One-lock snapshot lost jobs or schedules.");
        Check(File.ReadAllText(Path.Combine(f.Root, "operations", "queue.json")).Contains("\"formatVersion\": 2", StringComparison.Ordinal), "A saved-test job must mark store format 2.");
        OperationsJobRequest? received = null;
        var dispatcher = OperationsDispatcher.ForAllJobs(store, (request, _, _, lease) => { received = request; Check(lease is not null, "Local job without desktop lease."); return Task.FromResult(TestResult()); });
        var result = await dispatcher.RunNextAsync(f.DesktopLease, CancellationToken.None);
        Check(result.Job?.Passed == true && received?.Test?.Executable == Environment.ProcessPath && result.Target == "local", "Saved-test job did not pass with a no-preparation result.");
        store.Enqueue(TestJob(null, Environment.ProcessPath!));
        var wrongKind = OperationsDispatcher.ForAllJobs(store, (_, _, _, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new LifecycleResult { Status = LifecycleStatus.Passed, StartedAt = now, FinishedAt = now, WorkerPassed = true, CleanupComplete = true, Preparation = new() { Completed = true, Status = RunStatus.Passed, StartedAt = now, FinishedAt = now } });
        });
        var mismatch = await wrongKind.RunNextAsync(f.DesktopLease, CancellationToken.None);
        Check(mismatch.Job?.Status == OperationsJobStatus.Interrupted && mismatch.Job.ActionOutcomeUnknown, "A lifecycle-kind result was accepted for a saved-test job.");
        var legacy = new OperationsDispatcher(store, (_, _, _, _) => throw new InvalidOperationException("Legacy executor must not receive saved-test jobs."));
        store.Enqueue(TestJob(null, Environment.ProcessPath!));
        Check((await legacy.RunNextAsync(f.DesktopLease, CancellationToken.None)).State == "idle", "The lifecycle-only dispatcher claimed a saved-test job.");
    }

    private static async Task VmClaims()
    {
        using var f = new Fixture(); var store = f.Store();
        store.Enqueue(TestJob(VmTarget, @"{worker}\Testy.TestLab.exe"));
        var lifecycle = store.Enqueue(OperationsChecks.RequestForRoot(f.Root));
        using (var desktop = f.DesktopLease())
        {
            var claimed = store.Claim(desktop!);
            Check(claimed?.Id == lifecycle.Id, "The desktop lease claimed a VM job or skipped the local job.");
        }
        int calls = 0;
        var dispatcher = OperationsDispatcher.ForAllJobs(store, (request, _, _, lease) => { calls++; Check(lease is null && request.Target == VmTarget, "VM job received a desktop lease."); return Task.FromResult(TestResult()); });
        var target = OperationsTarget.ForVirtualMachine(Vm);
        var busy = await dispatcher.RunNextAsync(target, () => null, _ => Task.FromResult(new OperationsTargetReadiness(true, "ok")), CancellationToken.None);
        Check(busy.State == "targetBusy" && calls == 0, "A held VM lease did not block the dispatch.");
        var locked = await dispatcher.RunNextAsync(target, f.VmLease, _ => Task.FromResult(new OperationsTargetReadiness(false, "VM desktop is locked.")), CancellationToken.None);
        Check(locked.State == "targetUnavailable" && calls == 0 && store.ListJobs().Single(j => j.Request.Target == VmTarget).Status == OperationsJobStatus.Queued, "An unready VM claimed or dispatched its job.");
        var thrown = await dispatcher.RunNextAsync(target, f.VmLease, _ => throw new IOException("PowerShell Direct failed"), CancellationToken.None);
        Check(thrown.State == "targetUnavailable" && calls == 0, "A failing readiness check dispatched work.");
        var ran = await dispatcher.RunNextAsync(target, f.VmLease, _ => Task.FromResult(new OperationsTargetReadiness(true, "ok")), CancellationToken.None);
        Check(ran.State == "completed" && ran.Job?.Passed == true && calls == 1 && ran.Target == VmTarget, "Ready VM job did not complete.");
        bool checkedReadiness = false;
        var idle = await dispatcher.RunNextAsync(target, f.VmLease, _ => { checkedReadiness = true; return Task.FromResult(new OperationsTargetReadiness(true, "ok")); }, CancellationToken.None);
        Check(idle.State == "idle" && !checkedReadiness, "An idle VM target still ran its slow readiness check.");
    }

    private static async Task VmLeaseAcrossProcesses()
    {
        using var f = new Fixture(); string ready = Path.Combine(f.Root, "ready"), release = Path.Combine(f.Root, "release");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--operations-helper", "vmhold", f.Root, ready, release }) info.ArgumentList.Add(arg);
        using var helper = Process.Start(info)!;
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(ready)) { if (helper.HasExited || timer.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("VM lease helper did not become ready."); await Task.Delay(25); }
            using var denied = f.VmLease(); Check(denied is null, "Two processes held the same VM lease.");
        }
        finally { File.WriteAllText(release, "release"); if (!helper.WaitForExit(12000)) { helper.Kill(true); helper.WaitForExit(); } }
        Check(helper.ExitCode == 0, "VM lease helper failed.");
        using var lease = f.VmLease(); Check(lease is not null, "VM lease was not released on process exit.");
    }
    internal static async Task<int> HoldVmLeaseAsync(string root, string ready, string release)
    {
        using var lease = OperationsTargetLease.Acquire(Path.Combine(root, "leases"), OperationsTarget.ForVirtualMachine(Vm));
        if (lease is null) return 3;
        File.WriteAllText(ready, "held"); var timer = Stopwatch.StartNew();
        while (!File.Exists(release) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
        return File.Exists(release) ? 0 : 4;
    }

    private static async Task DesktopUnavailable()
    {
        using var f = new Fixture(); var store = f.Store(); store.Enqueue(OperationsChecks.RequestForRoot(f.Root)); int calls = 0;
        var dispatcher = new OperationsDispatcher(store, (_, _, _, _) => { calls++; return Task.FromResult(new LifecycleResult()); });
        var result = await dispatcher.RunNextAsync(f.DesktopLease, () => new OperationsTargetReadiness(false, "The session is locked."), CancellationToken.None);
        Check(result.State == "desktopUnavailable" && calls == 0 && store.ListJobs().Single().Status == OperationsJobStatus.Queued && result.Message.Contains("locked", StringComparison.Ordinal), "A locked desktop consumed a queued job.");
        using var lease = f.DesktopLease(); Check(lease is not null, "The desktop lease leaked after an unavailable desktop.");
    }

    private static OperationsJob Job(OperationsJobStatus status, string? target, int owner = 0) => new()
    { Status = status, Request = new() { Name = "j", Target = target }, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1), OwnerProcessId = owner };
    private static readonly HashSet<string> NoPumps = [];
    private static readonly Dictionary<string, DateTimeOffset> NoBackoff = [];
    private static Task PlannerWaits()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new[] { Job(OperationsJobStatus.Queued, null), Job(OperationsJobStatus.Queued, VmTarget) };
        var ready = new Dictionary<string, OperationsTargetReadiness> { [VmTarget] = new(true, "ok") };
        var locked = AgentPlanner.Decide(jobs, [], now, false, new(false, "Session locked."), ready, NoPumps, NoBackoff, _ => true);
        Check(locked.Launch.Select(l => l.Target).SequenceEqual([VmTarget]) && locked.Waiting.Single().Target == "local" && locked.Waiting[0].Reason == "Session locked.", "A locked desktop should block only local work.");
        var paused = AgentPlanner.Decide(jobs, [], now, true, new(true, "ok"), ready, NoPumps, NoBackoff, _ => true);
        Check(paused.Launch.Count == 0 && paused.Waiting.Count == 2, "Pause must stop every launch.");
        var unready = AgentPlanner.Decide(jobs, [], now, false, new(true, "ok"), new Dictionary<string, OperationsTargetReadiness> { [VmTarget] = new(false, "VM desktop locked.") }, NoPumps, NoBackoff, _ => true);
        Check(unready.Launch.Single().Target == "local" && unready.Waiting.Single().Reason == "VM desktop locked.", "An unready VM should wait.");
        var missing = AgentPlanner.Decide(jobs, [], now, false, new(true, "ok"), new Dictionary<string, OperationsTargetReadiness>(), NoPumps, NoBackoff, _ => true);
        Check(missing.Waiting.Single().Target == VmTarget, "A VM absent from inventory should wait.");
        var busy = AgentPlanner.Decide([.. jobs, Job(OperationsJobStatus.Running, null, 42)], [], now, false, new(true, "ok"), ready, NoPumps, NoBackoff, _ => true);
        Check(!busy.Launch.Any(l => l.Target == "local") && !busy.ReconcileNeeded, "A target with another worker's live claim must wait.");
        var backedOff = AgentPlanner.Decide(jobs, [], now, false, new(true, "ok"), ready, NoPumps, new Dictionary<string, DateTimeOffset> { ["local"] = now.AddSeconds(30) }, _ => true);
        Check(!backedOff.Launch.Any(l => l.Target == "local"), "Backoff was ignored.");
        var running = AgentPlanner.Decide(jobs, [], now, false, new(true, "ok"), ready, new HashSet<string> { "local" }, NoBackoff, _ => true);
        Check(running.Launch.Single().Target == VmTarget, "A target with an active pump got a second pump.");
        var capped = AgentPlanner.Decide(jobs, [], now, false, new(true, "ok"), ready, new HashSet<string> { "vm:" + Guid.NewGuid().ToString("D") }, NoBackoff, _ => true, maximumVmPumps: 1);
        Check(capped.Launch.Single().Target == "local" && capped.Waiting.Single().Target == VmTarget, "The VM run limit was ignored.");
        return Task.CompletedTask;
    }
    private static Task PlannerLaunches()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new OperationsSchedule { Definition = new() { Request = new() { Name = "s", Target = VmTarget }, Enabled = true }, NextRunAt = now.AddSeconds(-5) };
        var ready = new Dictionary<string, OperationsTargetReadiness> { [VmTarget] = new(true, "ok") };
        var due = AgentPlanner.Decide([], [schedule], now, false, new(true, "ok"), ready, NoPumps, NoBackoff, _ => true);
        Check(due.ReconcileNeeded && due.Launch.Single().Target == VmTarget, "A due VM schedule should reconcile and launch its target.");
        var future = new OperationsSchedule { Definition = new() { Request = new() { Name = "s" }, Enabled = true }, NextRunAt = now.AddMinutes(5) };
        var quiet = AgentPlanner.Decide([], [future], now, false, new(true, "ok"), ready, NoPumps, NoBackoff, _ => true);
        Check(!quiet.ReconcileNeeded && quiet.Launch.Count == 0 && quiet.Waiting.Count == 0, "A future schedule caused work.");
        var dead = AgentPlanner.Decide([Job(OperationsJobStatus.Running, null, 999999)], [], now, false, new(true, "ok"), ready, NoPumps, NoBackoff, _ => false);
        Check(dead.ReconcileNeeded, "A dead claim owner must trigger reconcile.");
        return Task.CompletedTask;
    }

    private static Task TaskXml()
    {
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        var exe = @"C:\Program Files\Testy\Agent\0.6.0-abc\Testy.Agent.exe";
        var arguments = "run --workspace " + AgentTaskXml.Quote(@"C:\Users\Test User\AppData\Local\Testy\Workspace");
        var document = XDocument.Parse(AgentTaskXml.Build(sid, exe, arguments, Path.GetDirectoryName(exe)!));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(params string[] path) { var element = document.Root!; foreach (var name in path) element = element.Element(ns + name) ?? throw new InvalidOperationException("Missing " + name); return element.Value; }
        Check(Value("Triggers", "LogonTrigger", "UserId") == sid && Value("Principals", "Principal", "UserId") == sid, "Task must start for exactly this user at sign-in.");
        Check(Value("Principals", "Principal", "LogonType") == "InteractiveToken" && Value("Principals", "Principal", "RunLevel") == "HighestAvailable", "Task must use the interactive session with highest privileges.");
        Check(Value("Settings", "MultipleInstancesPolicy") == "IgnoreNew" && Value("Settings", "ExecutionTimeLimit") == "PT0S" && Value("Settings", "Priority") == "5"
            && Value("Settings", "DisallowStartIfOnBatteries") == "false" && Value("Settings", "StopIfGoingOnBatteries") == "false", "Task settings would stop or throttle the always-on agent.");
        Check(Value("Actions", "Exec", "Command") == exe && Value("Actions", "Exec", "Arguments") == arguments && arguments.Contains("\"C:\\Users\\Test User\\AppData\\Local\\Testy\\Workspace\"", StringComparison.Ordinal), "Task action changed.");
        Check(AgentTaskXml.TaskPath(sid) == @"\Testy\Testy Agent " + sid && AgentTaskXml.Quote(@"a\") == "\"a\\\\\"" && AgentTaskXml.Quote("x\"y") == "\"x\\\"y\"", "Task path or quoting changed.");
        Reject(() => AgentTaskXml.Build("not-a-sid", exe, "", @"C:\"), "invalid SID");
        Reject(() => AgentTaskXml.Build(sid, "Testy.Agent.exe", "", @"C:\"), "relative executable");
        return Task.CompletedTask;
    }

    private static Task VmCredentials()
    {
        var vm = Guid.NewGuid();
        try
        {
            Check(VmCredentialStore.StoredUser(vm) is null && VmCredentialStore.Read(vm) is null, "Fresh VM has a stored sign-in.");
            VmCredentialStore.Save(vm, @"GUEST\Tester", "Pa55 wörd;\"quoted\"");
            Check(VmCredentialStore.StoredUser(vm) == @"GUEST\Tester" && VmCredentialStore.Read(vm) is { } read && read.UserName == @"GUEST\Tester" && read.Password == "Pa55 wörd;\"quoted\"", "VM sign-in did not round-trip.");
            Reject(() => VmCredentialStore.Save(vm, "", "x"), "empty user");
            Reject(() => VmCredentialStore.Save(vm, "user", "line\nbreak"), "multi-line password");
            VmCredentialStore.Delete(vm); VmCredentialStore.Delete(vm);
            Check(VmCredentialStore.Read(vm) is null, "Deleted VM sign-in is still readable.");
        }
        finally { VmCredentialStore.Delete(vm); }
        return Task.CompletedTask;
    }

    private static Task AgentFileLayout()
    {
        using var f = new Fixture();
        var workspace = Path.Combine(f.Root, "workspace");
        var directory = AgentFiles.Directory(workspace);
        Check(!directory.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) && AgentFiles.Directory(workspace.ToUpperInvariant() + "\\") == directory, "Agent state must live outside the workspace, keyed case-insensitively.");
        var path = Path.Combine(f.Root, "status.json");
        AgentFiles.Write(path, new AgentStatus { State = "idle", ProcessId = Environment.ProcessId });
        // A poller briefly holds the file open while the agent replaces it: the writer retries until the reader closes.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var closer = Task.Run(async () => { await Task.Delay(120); reader.Dispose(); });
        AgentFiles.Write(path, new AgentStatus { State = "running", ProcessId = Environment.ProcessId });
        closer.Wait();
        Check(AgentFiles.TryRead<AgentStatus>(path)?.State == "running" && AgentFiles.TryRead<AgentStatus>(Path.Combine(f.Root, "missing.json")) is null, "Status replacement or read failed.");
        File.WriteAllText(path, "{ not json"); Check(AgentFiles.TryRead<AgentStatus>(path) is null, "Corrupt status must read as absent.");
        var settings = new AgentSettings { LocalRuns = "sometimes", IdleMinutes = 0, MaximumVmRuns = 99 }; settings.Normalize();
        Check(settings.LocalRuns == AgentSettings.WhenIdle && settings.IdleMinutes == 1 && settings.MaximumVmRuns == 16, "Settings were not normalized.");
        var stale = new AgentStatus { HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-5), ProcessId = Environment.ProcessId };
        Check(!AgentFiles.IsRunning(stale) && !AgentFiles.IsRunning(null), "A stale heartbeat counted as running.");
        using var self = Process.GetCurrentProcess();
        Check(AgentFiles.IsRunning(new AgentStatus { HeartbeatAt = DateTimeOffset.UtcNow, ProcessId = self.Id, ProcessStartUtcTicks = self.StartTime.ToUniversalTime().Ticks }), "A fresh heartbeat from a live process did not count as running.");
        return Task.CompletedTask;
    }
}
