using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Cli.HyperV;

internal sealed class VmRunStage
{
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One saved test executed inside a Hyper-V guest. Host timestamps only; guest clocks are never trusted for acceptance.</summary>
internal sealed class VmRunResult
{
    public string Schema { get; set; } = "testy.vm-run.v1";
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public Guid VmId { get; set; }
    public string VmName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>passed, failed, cancelled, timedOut, unavailable or invalidConfiguration.</summary>
    public string Status { get; set; } = "failed";
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Passed => Status == "passed" && WorkerPassed && EvidenceVerified && SecretScanPassed && GuestCleanupComplete && IdentityVerified && FinishedAt >= StartedAt;
    public int ExitCode => Passed ? 0 : Status is "unavailable" or "invalidConfiguration" ? 2 : 1;
    public string ArtifactDirectory { get; set; } = "";
    public string EvidenceDirectory { get; set; } = "";
    public string GuestRunDirectory { get; set; } = "";
    public string GuestExecutable { get; set; } = "";
    public bool Staged { get; set; }
    public string WorkerStatus { get; set; } = "";
    public bool WorkerPassed { get; set; }
    public string WorkerMessage { get; set; } = "";
    public string WorkerArtifactDirectory { get; set; } = "";
    public int? WorkerExitCode { get; set; }
    public bool WorkerCleanupComplete { get; set; }
    public bool ActionOutcomeUnknown { get; set; }
    public bool IdentityVerified { get; set; }
    public List<string> IdentityFailures { get; set; } = [];
    public bool EvidenceVerified { get; set; }
    public int EvidenceFiles { get; set; }
    public bool SecretScanPassed { get; set; }
    public bool GuestCleanupComplete { get; set; }
    public bool KeyDeliveredAndCleared { get; set; }
    public bool WorkerInstalled { get; set; }
    public string Mode { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Model { get; set; } = "";
    public int? ModelTurns { get; set; }
    public int SavedSteps { get; set; }
    public int EvidenceImages { get; set; }
    public MachineReadiness? Readiness { get; set; }
    public List<VmRunStage> Stages { get; set; } = [];
    public string Policy => "Guest acceptance comes from the guest worker's own terminal validation. The host verifies transfer integrity, workload/build/model identity, secret absence and guest cleanup. No input is retried.";
}

internal sealed record TestyBundle(string Root, string ManifestPath, string ManifestSha256, string Version, JsonElement Files)
{
    public string WorkerKey => ManifestSha256[..16].ToLowerInvariant();
}

internal static class VmRunner
{
    private static readonly string[] TextExtensions = [".json", ".txt", ".html", ".htm", ".xml", ".log", ".csv", ".md"];
    internal static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "HyperV");
    /// <summary>Scratch folder for inventory, readiness and setup calls; entries older than a day are pruned (the agent checks VMs every few minutes).</summary>
    internal static string SharedWork()
    {
        var directory = Path.Combine(WorkRoot, "work"); Directory.CreateDirectory(directory);
        try { foreach (var old in new DirectoryInfo(directory).GetFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))) old.Delete(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return directory;
    }

    internal static TestyBundle? TryLoadBundle()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "release-manifest.json");
        if (!File.Exists(manifest)) return null;
        var bytes = File.ReadAllBytes(manifest);
        using var doc = JsonDocument.Parse(bytes.AsMemory(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0));
        var root = doc.RootElement;
        if (root.GetProperty("schema").GetString() != "testy.release.v1") throw new InvalidDataException("Unsupported release manifest.");
        return new TestyBundle(AppContext.BaseDirectory, manifest, Convert.ToHexString(SHA256.HashData(bytes)), root.GetProperty("version").GetString() ?? "", root.GetProperty("files").Clone());
    }
    internal static TestyBundle LoadBundle() => TryLoadBundle()
        ?? throw new InvalidDataException("VM runs copy a sealed Testy bundle into the guest. Run Testy from a published bundle (scripts/publish.ps1) or the installed agent; this build folder has no release-manifest.json.");

    /// <summary>Zip of exactly the sealed files (plus the manifest), verified against the manifest while it is built. Cached per manifest hash.</summary>
    internal static async Task<(string Path, string Sha256)> WorkerZipAsync(TestyBundle bundle, CancellationToken ct)
    {
        var cache = Path.Combine(WorkRoot, "cache"); Directory.CreateDirectory(cache);
        var zip = Path.Combine(cache, "worker-" + bundle.WorkerKey + ".zip"); var shaFile = zip + ".sha256";
        PruneWorkerZips(cache, zip);
        if (File.Exists(zip) && File.Exists(shaFile))
        {
            var recorded = (await File.ReadAllTextAsync(shaFile, ct)).Trim();
            if (recorded.Length == 64 && WorkerCommand.HashFile(zip) == recorded) return (zip, recorded);
        }
        var temp = zip + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in bundle.Files.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = file.GetProperty("path").GetString() ?? throw new InvalidDataException("Manifest entry without a path.");
                    if (relative.Contains("..") || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Invalid manifest path.");
                    var source = Path.Combine(bundle.Root, relative.Replace('/', Path.DirectorySeparatorChar));
                    var info = new FileInfo(source);
                    if (!info.Exists || info.Length != file.GetProperty("bytes").GetInt64() || WorkerCommand.HashFile(source) != file.GetProperty("sha256").GetString())
                        throw new InvalidDataException("This Testy bundle no longer matches its sealed manifest: " + relative);
                    archive.CreateEntryFromFile(source, relative, CompressionLevel.Fastest);
                }
                archive.CreateEntryFromFile(bundle.ManifestPath, "release-manifest.json", CompressionLevel.Fastest);
            }
            var sha = WorkerCommand.HashFile(temp);
            File.Move(temp, zip, true); await File.WriteAllTextAsync(shaFile, sha, ct);
            return (zip, sha);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    /// <summary>Keeps the current bundle's zip and the newest other one; older bundles are no longer copied to guests. Best effort.</summary>
    private static void PruneWorkerZips(string cache, string current)
    {
        try
        {
            foreach (var old in new DirectoryInfo(cache).GetFiles("worker-*.zip").Where(f => !f.FullName.Equals(current, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTimeUtc).Skip(1))
            { old.Delete(); File.Delete(old.FullName + ".sha256"); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static object Identity(Guid vmId, string userName, TestyBundle? bundle) => new
    {
        vmId = vmId.ToString("D"), userName, workerKey = bundle?.WorkerKey ?? "", manifestSha256 = bundle?.ManifestSha256 ?? ""
    };

    /// <summary>Full guest readiness over PowerShell Direct. Read-only in the guest.</summary>
    internal static async Task<MachineReadiness> CheckAsync(Guid vmId, CancellationToken ct)
    {
        var credential = VmCredentialStore.Read(vmId);
        if (credential is null) return new MachineReadiness { Checked = false, Ready = false, Reason = "Set up this VM for Testy first (guest sign-in not stored)." };
        var bundle = TryLoadBundle();
        var result = await HyperVBridge.InvokeAsync("Check", Identity(vmId, credential.Value.UserName, bundle), [credential.Value.Password], SharedWork(), TimeSpan.FromMinutes(4), ct);
        return ReadinessFrom(result);
    }

    internal static async Task<JsonElement> SetupAsync(Guid vmId, CancellationToken ct)
    {
        var credential = VmCredentialStore.Read(vmId) ?? throw new InvalidDataException("Store the guest sign-in for this VM first.");
        var bundle = LoadBundle(); var (zip, sha) = await WorkerZipAsync(bundle, ct);
        var request = new
        {
            vmId = vmId.ToString("D"), userName = credential.UserName, workerKey = bundle.WorkerKey, manifestSha256 = bundle.ManifestSha256, version = bundle.Version,
            workerZip = zip, workerZipSha256 = sha, manifest = bundle.Files
        };
        return await HyperVBridge.InvokeAsync("Setup", request, [credential.Password], SharedWork(), TimeSpan.FromMinutes(40), ct);
    }

    internal static MachineReadiness ReadinessFrom(JsonElement bridge)
    {
        if (bridge.TryGetProperty("readiness", out var readiness) && readiness.ValueKind == JsonValueKind.Object)
            return readiness.Deserialize<MachineReadiness>(TestyJson.Options) ?? new MachineReadiness { Checked = false, Reason = "Readiness was not reported." };
        return new MachineReadiness { Checked = false, Ready = false, Reason = HyperVBridge.String(bridge, "message") is { Length: > 0 } message ? message : "The VM could not be checked." };
    }

    internal static async Task<MachineInventory> InventoryAsync(bool deep, CancellationToken ct)
    {
        var inventory = new MachineInventory();
        JsonElement result;
        try { result = await HyperVBridge.InvokeAsync("Inventory", new { }, [], SharedWork(), TimeSpan.FromMinutes(2), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { inventory.Message = "Hyper-V inventory failed: " + ex.Message; return inventory; }
        if (HyperVBridge.String(result, "status") != "ok")
        {
            inventory.Message = "Hyper-V is unavailable or this process is not elevated: " + (HyperVBridge.String(result, "message") ?? "no detail");
            return inventory;
        }
        inventory.HyperVAvailable = true;
        foreach (var vm in result.GetProperty("machines").EnumerateArray())
        {
            var info = new MachineInfo
            {
                VmId = Guid.Parse(vm.GetProperty("vmId").GetString()!), Name = vm.GetProperty("name").GetString() ?? "", State = vm.GetProperty("state").GetString() ?? "",
                Heartbeat = vm.GetProperty("heartbeat").GetString() ?? "", UptimeSeconds = vm.GetProperty("uptimeSeconds").GetInt64(), Generation = vm.GetProperty("generation").GetInt32(),
                IpAddresses = vm.GetProperty("ipAddresses").ValueKind == JsonValueKind.Array ? vm.GetProperty("ipAddresses").EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToList() : [],
                GuestOs = vm.GetProperty("guestOs").GetString() ?? ""
            };
            var user = VmCredentialStore.StoredUser(info.VmId);
            info.CredentialStored = user is not null; info.CredentialUser = user ?? "";
            if (deep && info.CredentialStored && info.Running)
            {
                try { info.Readiness = await CheckAsync(info.VmId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { info.Readiness = new MachineReadiness { Checked = true, Ready = false, Reason = "Check failed: " + ex.Message }; }
            }
            inventory.Machines.Add(info);
        }
        inventory.Message = inventory.Machines.Count == 0 ? "Hyper-V is available but has no VMs." : $"{inventory.Machines.Count} VM(s) found.";
        return inventory;
    }

    internal static async Task<VmRunResult> RunAsync(Guid vmId, OperationsTestRequest request, string artifactsRoot, CancellationToken ct, Action<string, string>? progress = null)
    {
        var run = new VmRunResult { VmId = vmId };
        run.ArtifactDirectory = Path.Combine(Path.GetFullPath(artifactsRoot), $"vm-{run.StartedAt:yyyyMMdd-HHmmss}-{run.Id[..8]}");
        Directory.CreateDirectory(run.ArtifactDirectory);
        var resultPath = Path.Combine(run.ArtifactDirectory, "vm-run-result.json");
        var gate = new object();
        void Save() { lock (gate) WorkerCommand.DurableWrite(resultPath, run); }
        void Stage(string stage, string message) { lock (gate) { run.Stage = stage; run.Stages.Add(new VmRunStage { Stage = stage, Message = message }); } Save(); progress?.Invoke(stage, message); }
        string? password = null, key = null; bool bridgeStarted = false;
        try
        {
            request = TestyJson.Clone(request);
            run.Driver = request.Probe ? "wpfProbe" : "uia"; run.Model = request.Provider?.Model ?? "";
            run.Mode = request.Provider is null ? "replay" : request.Provider.Kind == ProviderKind.OpenAI && request.Provider.NativeComputerUse ? "aiNativeHybrid" : "aiLocalTools";
            OperationsTestValidation.Validate(request, OperationsTarget.ForVirtualMachine(vmId));
            Stage("preflight", "Validating the saved test, the VM sign-in and the Testy bundle.");
            var credential = VmCredentialStore.Read(vmId);
            if (credential is null) { run.Status = "unavailable"; run.Message = "Set up this VM for Testy first (guest sign-in not stored on this PC)."; return run; }
            password = credential.Value.Password;
            ProviderSettings? guestSettings = null; string? slot = null;
            if (request.Provider is { } provider)
            {
                WorkerCommand.ValidateProvider(provider);
                if (!CortexModelBridge.Enabled)
                {
                    key = ProviderCredentialStore.Resolve(provider);
                    if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("The model API credential is not stored on this PC.");
                    slot = "TESTY_RUN_KEY_" + run.Id.ToUpperInvariant();
                }
                else if (CortexModelBridge.Context is null) throw new InvalidDataException("Start guest AI from Cortex so its model context can be pinned.");
                guestSettings = TestyJson.Clone(provider); if (slot is not null) guestSettings.ApiKeyEnvironmentVariable = slot;
            }
            var bundle = LoadBundle();
            var (zip, zipSha) = await WorkerZipAsync(bundle, ct);
            string? stageZip = null, stageSha = null;
            if (request.StageDirectory is { } stageDirectory)
            {
                Stage("package", "Packing the app folder to copy into the VM.");
                (stageZip, stageSha) = PackStageDirectory(stageDirectory, Path.Combine(run.ArtifactDirectory, "app.zip"), ct);
            }
            var bridgeRequest = new
            {
                vmId = vmId.ToString("D"), userName = credential.Value.UserName, workerKey = bundle.WorkerKey, manifestSha256 = bundle.ManifestSha256, version = bundle.Version,
                workerZip = zip, workerZipSha256 = zipSha, manifest = bundle.Files, runId = run.Id,
                testJson = JsonSerializer.Serialize(request.Test, TestyJson.Options), settingsJson = guestSettings is null ? null : JsonSerializer.Serialize(guestSettings, TestyJson.Options),
                argumentsJson = JsonSerializer.Serialize(request.TargetArguments, TestyJson.Options), executable = request.Executable, probe = request.Probe,
                timeoutSeconds = request.TimeoutSeconds, startupTimeoutSeconds = request.StartupTimeoutSeconds, shutdownGraceSeconds = request.ShutdownGraceSeconds,
                keySlot = slot, cortexRelay = CortexModelBridge.Enabled && request.Provider is not null, stageZip, stageZipSha256 = stageSha, hostDirectory = run.ArtifactDirectory
            };
            Stage("bridge", "Connecting to the VM over PowerShell Direct.");
            var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds + request.StartupTimeoutSeconds + 3600);
            bridgeStarted = true;
            var bridge = await HyperVBridge.InvokeAsync("Run", bridgeRequest, [password, key], run.ArtifactDirectory, timeout, ct, (stage, message) => Stage(stage, message));
            password = null; key = null;
            WorkerCommand.DurableWrite(Path.Combine(run.ArtifactDirectory, "bridge-result.json"), bridge);
            // Evidence verification and the secret scan always complete, including after a cancellation: they protect what was returned.
            Accept(run, bridge, request, bundle, credential.Value.Password, slot is null ? null : ProviderCredentialStore.Resolve(request.Provider!), CancellationToken.None);
            if (ct.IsCancellationRequested && run.Status == "passed") run.Status = "cancelled";
        }
        catch (OperationCanceledException) when (!bridgeStarted) { run.Status = "cancelled"; run.Message = "Cancelled before the VM run started; nothing was sent to the VM."; }
        catch (InvalidDataException ex) when (!bridgeStarted) { run.Status = "invalidConfiguration"; run.Message = ex.Message; }
        catch (Exception ex) when (!bridgeStarted) { run.Status = "unavailable"; run.Message = ex.GetType().Name + ": " + ex.Message; }
        catch (Exception ex)
        {
            // The bridge may have started guest work before failing; never claim a known outcome.
            run.Status = ex is OperationCanceledException ? "cancelled" : "failed"; run.ActionOutcomeUnknown = true;
            run.Message = ex.GetType().Name + ": " + ex.Message + " The VM may have run part of the test; inspect the retained evidence and the VM before a fresh run.";
        }
        finally
        {
            password = null; key = null;
            run.FinishedAt = DateTimeOffset.UtcNow; Save();
        }
        return run;
    }

    /// <summary>Host-side acceptance after the bridge returns: transfer integrity, identity, secrets and cleanup. Guest UI acceptance is the guest worker's own.</summary>
    private static void Accept(VmRunResult run, JsonElement bridge, OperationsTestRequest request, TestyBundle bundle, string password, string? key, CancellationToken ct)
    {
        run.VmName = HyperVBridge.String(bridge, "vmName") ?? "";
        if (bridge.TryGetProperty("readiness", out _)) run.Readiness = ReadinessFrom(bridge);
        run.WorkerInstalled = bridge.TryGetProperty("worker", out var worker) && HyperVBridge.Bool(worker, "installed");
        run.Staged = HyperVBridge.Bool(bridge, "staged");
        if (bridge.TryGetProperty("run", out var staged) && staged.ValueKind == JsonValueKind.Object)
        { run.GuestRunDirectory = HyperVBridge.String(staged, "runDirectory") ?? ""; run.GuestExecutable = HyperVBridge.String(staged, "executable") ?? ""; }
        var bridgeStatus = HyperVBridge.String(bridge, "status") ?? "failed";
        var bridgeMessage = HyperVBridge.String(bridge, "message") ?? "";
        if (bridge.TryGetProperty("cleanup", out var cleanup) && cleanup.ValueKind == JsonValueKind.Object)
            run.GuestCleanupComplete = HyperVBridge.Bool(cleanup, "taskRemoved") && HyperVBridge.Bool(cleanup, "keyAbsent") && cleanup.GetProperty("runProcesses").GetInt32() == 0;
        else run.GuestCleanupComplete = !run.Staged;
        if (!run.Staged)
        {
            run.Status = bridgeStatus is "unavailable" or "cancelled" ? bridgeStatus : "unavailable";
            run.Message = bridgeMessage.Length > 0 ? bridgeMessage : run.Readiness?.Reason ?? "The VM run could not start.";
            return;
        }
        run.ActionOutcomeUnknown = true; // Until the guest worker's terminal result says otherwise.
        // Evidence: verify the transferred zip, extract it, and match every file to the guest inventory.
        if (bridge.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Object)
        {
            var zip = HyperVBridge.String(evidence, "zip")!;
            WorkerCommand.Require(File.Exists(zip) && WorkerCommand.HashFile(zip) == HyperVBridge.String(evidence, "zipSha256"), "Evidence archive hash differs after transfer.");
            run.EvidenceDirectory = Path.Combine(run.ArtifactDirectory, "guest");
            WorkerCommand.Require(!Directory.Exists(run.EvidenceDirectory), "Evidence directory is not fresh.");
            ZipFile.ExtractToDirectory(zip, run.EvidenceDirectory);
            var expected = evidence.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar), f => (f.GetProperty("bytes").GetInt64(), f.GetProperty("sha256").GetString()!), StringComparer.OrdinalIgnoreCase);
            var actual = Directory.EnumerateFiles(run.EvidenceDirectory, "*", SearchOption.AllDirectories).ToList();
            WorkerCommand.Require(actual.Count == expected.Count, "Evidence file count differs from the guest inventory.");
            foreach (var path in actual)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(run.EvidenceDirectory, path);
                WorkerCommand.Require(expected.TryGetValue(relative, out var item) && new FileInfo(path).Length == item.Item1 && WorkerCommand.HashFile(path) == item.Item2, "Evidence file differs from the guest inventory: " + relative);
            }
            run.EvidenceFiles = actual.Count; run.EvidenceVerified = true;
            File.Delete(zip);
            run.SecretScanPassed = SecretFree(run.EvidenceDirectory, password, key);
        }
        JsonElement? exit = bridge.TryGetProperty("exit", out var e) && e.ValueKind == JsonValueKind.Object ? e : null;
        if (exit is { } exitValue)
        {
            run.WorkerExitCode = exitValue.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : null;
            run.KeyDeliveredAndCleared = request.Provider is null || CortexModelBridge.Enabled && HyperVBridge.Bool(exitValue, "cortexRelay") && !HyperVBridge.Bool(exitValue, "keyImported")
                || !CortexModelBridge.Enabled && HyperVBridge.Bool(exitValue, "keyImported") && HyperVBridge.Bool(exitValue, "keyFileDeleted") && HyperVBridge.Bool(exitValue, "keyCleared");
            if (HyperVBridge.String(exitValue, "error") is { Length: > 0 } guestError) run.IdentityFailures.Add("Guest runner error: " + guestError);
        }
        var stdoutPath = Path.Combine(run.EvidenceDirectory, "worker-stdout.json");
        if (run.EvidenceVerified && File.Exists(stdoutPath))
        {
            var workerResult = JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(stdoutPath), TestyJson.Options) ?? throw new InvalidDataException("Guest worker result is empty.");
            run.WorkerStatus = workerResult.Status; run.WorkerPassed = workerResult.Passed; run.WorkerMessage = workerResult.Message; run.WorkerArtifactDirectory = workerResult.ArtifactDirectory;
            run.WorkerCleanupComplete = workerResult.CleanupComplete; run.ActionOutcomeUnknown = workerResult.ActionOutcomeUnknown;
            run.ModelTurns = workerResult.ModelTurns; run.SavedSteps = workerResult.SavedSteps; run.EvidenceImages = workerResult.EvidenceImages;
            var failures = run.IdentityFailures;
            if (workerResult.TestSha256 != WorkerCommand.WorkloadHash(request.Test)) failures.Add("Guest ran a different test definition.");
            if (workerResult.Mode != run.Mode) failures.Add($"Guest mode {workerResult.Mode} differs from the requested {run.Mode}.");
            if (workerResult.Driver != run.Driver) failures.Add($"Guest driver {workerResult.Driver} differs from the requested {run.Driver}.");
            if ((workerResult.Model ?? "") != run.Model) failures.Add("Guest used a different model.");
            if (!string.Equals(workerResult.TargetExecutable, run.GuestExecutable, StringComparison.OrdinalIgnoreCase)) failures.Add("Guest launched a different executable.");
            if (workerResult.RunnerBuildSha256 != BuildFingerprint.Capture(Path.Combine(bundle.Root, "Testy.Cli.dll")).Sha256) failures.Add("Guest worker build differs from this Testy bundle.");
            if (run.WorkerExitCode != workerResult.ExitCode) failures.Add("Guest worker exit code contradicts its result.");
            var persisted = Path.Combine(run.EvidenceDirectory, "artifacts", Path.GetFileName(workerResult.ArtifactDirectory.TrimEnd('\\')), "worker-result.json");
            if (!File.Exists(persisted) || JsonSerializer.Serialize(JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(persisted), TestyJson.Options), TestyJson.Options) != JsonSerializer.Serialize(workerResult, TestyJson.Options))
                failures.Add("Guest worker stdout disagrees with its persisted worker-result.json.");
            if (!run.KeyDeliveredAndCleared) failures.Add("Provider key hand-off or clearing was not confirmed in the guest.");
            run.IdentityVerified = failures.Count == 0;
            run.Status = workerResult.Status is "passed" or "failed" or "cancelled" or "timedOut" or "unavailable" or "invalidConfiguration" ? workerResult.Status : "failed";
            if (run.Status == "passed" && !(run.IdentityVerified && run.SecretScanPassed && run.GuestCleanupComplete)) run.Status = "failed";
            run.Message = run.Status == "passed" ? $"Passed in VM {run.VmName}: {workerResult.Message}"
                : string.Join(" ", new[] { workerResult.Message }.Concat(failures).Concat(run.SecretScanPassed ? [] : ["Secret material was found in the returned evidence."]).Concat(run.GuestCleanupComplete ? [] : ["Guest cleanup was not confirmed."]).Where(m => m.Length > 0));
        }
        else
        {
            run.Status = bridgeStatus switch { "timedOut" => "timedOut", "cancelled" => "cancelled", _ => "failed" };
            run.Message = (bridgeMessage.Length > 0 ? bridgeMessage + " " : "") + "The guest worker did not return a terminal result; input outcome is unknown. Inspect the retained evidence.";
        }
    }

    internal static bool SecretFree(string directory, string password, string? key)
    {
        var needles = new List<byte[]>();
        foreach (var secret in new[] { password, key }.Where(s => !string.IsNullOrEmpty(s) && s!.Length >= 4))
        { needles.Add(Encoding.UTF8.GetBytes(secret!)); needles.Add(Encoding.Unicode.GetBytes(secret!)); }
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || new FileInfo(file).Length > 64 * 1024 * 1024) continue;
            var bytes = File.ReadAllBytes(file);
            if (needles.Any(n => bytes.AsSpan().IndexOf(n) >= 0)) return false;
        }
        return true;
    }

    private static (string Path, string Sha256) PackStageDirectory(string directory, string zipPath, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new InvalidDataException("The folder to copy into the VM does not exist: " + root);
        long total = 0; int count = 0;
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The folder to copy contains a reparse point: " + file);
                total += info.Length; count++;
                if (count > 20000 || total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The folder to copy exceeds 20,000 files or 2 GiB.");
                archive.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Fastest);
            }
        }
        return (zipPath, WorkerCommand.HashFile(zipPath));
    }

    internal static LifecycleResult ToLifecycle(VmRunResult run, DateTimeOffset startedAt) => new()
    {
        StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow, PreparationNotApplicable = true, Target = OperationsTarget.ForVirtualMachine(run.VmId).Canonical,
        ArtifactDirectory = run.ArtifactDirectory, WorkerArtifactDirectory = run.EvidenceDirectory, WorkerStatus = run.Status, WorkerPassed = run.Passed,
        CleanupComplete = run.Staged ? run.WorkerCleanupComplete && run.GuestCleanupComplete : !run.ActionOutcomeUnknown, ActionOutcomeUnknown = run.ActionOutcomeUnknown,
        Status = run.Passed ? LifecycleStatus.Passed : run.Status switch { "cancelled" => LifecycleStatus.Cancelled, "timedOut" => LifecycleStatus.TimedOut, _ => LifecycleStatus.Failed },
        Message = run.Message
    };
}
