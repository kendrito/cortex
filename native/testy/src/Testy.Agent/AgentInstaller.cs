using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Agent;

internal sealed class AgentInstallRecord
{
    public string Schema { get; set; } = "testy.agent-install.v1";
    public string Version { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string Directory { get; set; } = "";
    public string UserSid { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string TaskPath { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
internal sealed class AgentInstallResult
{
    public string Schema { get; set; } = "testy.agent-install-result.v1";
    public string Action { get; set; } = "";
    public bool Succeeded { get; set; }
    public string Message { get; set; } = "";
    public AgentInstallRecord? Install { get; set; }
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Installs the sealed bundle under Program Files and registers the per-user sign-in task (highest privileges, interactive token).
/// Requires elevation; one UAC prompt per install, update or removal.</summary>
internal static class AgentInstaller
{
    public static string ProgramRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Testy", "Agent");
    public static string ResultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "Agent", "install-result.json");
    private static string RecordPath(string sid) => Path.Combine(ProgramRoot, "agent-install-" + sid + ".json");
    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static int Install(string workspace, bool start)
    {
        var result = new AgentInstallResult { Action = "install" };
        try
        {
            if (!Elevated) throw new UnauthorizedAccessException("Installing the agent needs administrator approval (UAC).");
            RequireLocalWorkspace(workspace);
            var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!.Value;
            var source = AppContext.BaseDirectory;
            var manifestPath = Path.Combine(source, "release-manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Install the agent from a sealed Testy bundle (release-manifest.json is missing next to Testy.Agent.exe).");
            var manifestBytes = File.ReadAllBytes(manifestPath); var manifestSha = Convert.ToHexString(SHA256.HashData(manifestBytes));
            using var manifest = JsonDocument.Parse(manifestBytes.AsMemory(manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF ? 3 : 0));
            var version = manifest.RootElement.GetProperty("version").GetString() ?? "0.0.0";
            var files = manifest.RootElement.GetProperty("files").EnumerateArray().Select(f => (Path: f.GetProperty("path").GetString()!, Bytes: f.GetProperty("bytes").GetInt64(), Sha: f.GetProperty("sha256").GetString()!)).ToList();
            var destination = Path.Combine(ProgramRoot, version + "-" + manifestSha[..12].ToLowerInvariant());
            StopAgent(workspace, sid);
            if (!Verified(destination, files, manifestSha))
            {
                var staging = destination + ".staging-" + Guid.NewGuid().ToString("N")[..6];
                Directory.CreateDirectory(staging);
                foreach (var file in files)
                {
                    if (file.Path.Contains("..") || Path.IsPathRooted(file.Path)) throw new InvalidDataException("Invalid manifest path.");
                    var from = Path.Combine(source, file.Path.Replace('/', '\\')); var to = Path.Combine(staging, file.Path.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to);
                }
                File.Copy(manifestPath, Path.Combine(staging, "release-manifest.json"));
                if (!Verified(staging, files, manifestSha)) { Directory.Delete(staging, true); throw new InvalidDataException("The copied agent bundle does not match its sealed manifest."); }
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                Directory.Move(staging, destination);
            }
            var executable = Path.Combine(destination, "Testy.Agent.exe");
            var arguments = "run --workspace " + AgentTaskXml.Quote(workspace);
            var xml = AgentTaskXml.Build(sid, executable, arguments, destination);
            var xmlPath = Path.Combine(Path.GetTempPath(), "testy-agent-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                SchTasks("/Create", "/TN", AgentTaskXml.TaskPath(sid), "/XML", xmlPath, "/F");
            }
            finally { File.Delete(xmlPath); }
            var record = new AgentInstallRecord { Version = version, ManifestSha256 = manifestSha, Directory = destination, UserSid = sid, UserName = identity.Name, Workspace = workspace, TaskPath = AgentTaskXml.TaskPath(sid) };
            Directory.CreateDirectory(ProgramRoot);
            File.WriteAllText(RecordPath(sid), JsonSerializer.Serialize(record, TestyJson.Options));
            Prune();
            if (start) SchTasks("/Run", "/TN", AgentTaskXml.TaskPath(sid));
            result.Succeeded = true; result.Install = record;
            result.Message = $"Testy Agent {version} installed in {destination} and set to start at sign-in with your highest privileges." + (start ? " It is starting now." : "");
            return 0;
        }
        catch (Exception ex) { result.Message = ex.Message; return 1; }
        finally { WriteResult(result); }
    }

    public static int Uninstall(string workspace)
    {
        var result = new AgentInstallResult { Action = "uninstall" };
        try
        {
            if (!Elevated) throw new UnauthorizedAccessException("Removing the agent needs administrator approval (UAC).");
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            StopAgent(workspace, sid);
            try { SchTasks("/Delete", "/TN", AgentTaskXml.TaskPath(sid), "/F"); } catch (InvalidOperationException) { }
            if (File.Exists(RecordPath(sid))) File.Delete(RecordPath(sid));
            Prune();
            result.Succeeded = true; result.Message = "The Testy Agent no longer starts at sign-in and its program files were removed where unused. Workspaces, results and stored sign-ins are kept.";
            return 0;
        }
        catch (Exception ex) { result.Message = ex.Message; return 1; }
        finally { WriteResult(result); }
    }

    /// <summary>Relaunches this executable elevated (one UAC prompt) and waits for it.</summary>
    public static int RunElevated(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        start.Arguments = string.Join(' ', arguments.Select(AgentTaskXml.Quote));
        try { using var process = Process.Start(start)!; process.WaitForExit(); return process.ExitCode; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { WriteResult(new AgentInstallResult { Action = "elevation", Message = "Administrator approval was declined; nothing changed." }); return 1223; }
    }

    private static void StopAgent(string workspace, string sid)
    {
        var status = AgentFiles.TryRead<AgentStatus>(AgentFiles.StatusPath(workspace));
        if (AgentFiles.IsRunning(status))
        {
            AgentFiles.Submit(workspace, "shutdown");
            try
            {
                using var process = Process.GetProcessById(status!.ProcessId);
                if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: false); process.WaitForExit(5000); }
            }
            catch (ArgumentException) { }
        }
        try { SchTasks("/End", "/TN", AgentTaskXml.TaskPath(sid)); } catch (InvalidOperationException) { }
    }

    private static bool Verified(string directory, List<(string Path, long Bytes, string Sha)> files, string manifestSha)
    {
        if (!Directory.Exists(directory)) return false;
        var manifest = Path.Combine(directory, "release-manifest.json");
        if (!File.Exists(manifest) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))) != manifestSha) return false;
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file.Path.Replace('/', '\\'));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes) return false;
            using var stream = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(stream)) != file.Sha) return false;
        }
        return true;
    }

    /// <summary>Keeps every version referenced by an install record plus the newest other one (for rollback); never deletes a folder in use.</summary>
    private static void Prune()
    {
        if (!Directory.Exists(ProgramRoot)) return;
        var referenced = Directory.EnumerateFiles(ProgramRoot, "agent-install-*.json")
            .Select(f => { try { return JsonSerializer.Deserialize<AgentInstallRecord>(File.ReadAllText(f), TestyJson.Options)?.Directory; } catch (JsonException) { return null; } })
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = new DirectoryInfo(ProgramRoot).GetDirectories().Where(d => !referenced.Contains(d.FullName)).OrderByDescending(d => d.CreationTimeUtc).ToList();
        foreach (var directory in others.Skip(referenced.Count == 0 ? 0 : 1))
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) continue;
            try { directory.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void RequireLocalWorkspace(string workspace)
    {
        var full = Path.GetFullPath(workspace);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("The agent needs a workspace on a local disk, not a network path.");
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) throw new InvalidDataException("The agent needs a workspace on a local disk; mapped network drives are invisible to elevated programs.");
    }

    private static void SchTasks(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"schtasks {arguments[0]} failed ({process.ExitCode}): {(error.Length > 0 ? error : output).Trim()}");
    }

    private static void WriteResult(AgentInstallResult result)
    {
        result.FinishedAt = DateTimeOffset.UtcNow;
        try { AgentFiles.Write(ResultPath, result); } catch (Exception) { }
        Program.Print(JsonSerializer.Serialize(result, TestyJson.Options));
    }
}
