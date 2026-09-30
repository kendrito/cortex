using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Testy.Core;

/// <summary>Runs a host-configured command under the current user, not a security sandbox.
/// The caller must resolve only an approved catalog ID; this API never accepts a model-authored command line.</summary>
public static class ProjectCommandExecutor
{
    internal const int OutputLimit = 65536;

    public static async Task<ProjectCommandExecution> ExecuteAsync(ProjectCommandDefinition command, string projectRoot, CancellationToken cancellationToken = default)
    {
        var result = new ProjectCommandExecution { Status = ProjectCommandStatus.InvalidConfiguration, CleanupComplete = true };
        OwnedCommand? process = null; Task<Captured>? stdout = null, stderr = null;
        using var drainCancellation = new CancellationTokenSource();
        var secrets = new List<string>();
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Configured commands require Windows.");
            ArgumentNullException.ThrowIfNull(command);
            // Freeze mutable configuration before validation and before the first await.
            string executable = command.Executable, directory = command.WorkingDirectory;
            string[] arguments = command.Arguments?.ToArray() ?? throw new ArgumentException("Command arguments are required.");
            int timeout = command.TimeoutSeconds;
            if (string.IsNullOrWhiteSpace(command.Id) || command.Id.Length > 128 || timeout is < 1 or > 600)
                throw new ArgumentException("Command ID is required and timeout must be from 1 to 600 seconds.");
            if (arguments.Length > 256 || arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')))
                throw new ArgumentException("Command arguments exceed the bounded literal argument contract.");
            secrets.AddRange(ProjectToolSession.SensitiveArgumentValues(arguments));
            if (!LocalPath(projectRoot) || !Directory.Exists(projectRoot)) throw new ArgumentException("Project root must be an existing absolute local directory; network and device paths are unsupported.");
            string root = ResolveDirectory(projectRoot);
            if (string.IsNullOrWhiteSpace(directory) || Path.IsPathRooted(directory)) throw new ArgumentException("Command working directory must be relative to the project root.");
            // Validate the caller's spelling before Windows normalization can erase trailing spaces/dots.
            if (directory != ".") foreach (string component in directory.Split(['\\', '/'])) ValidateSegment(component);
            string requested = Path.GetFullPath(Path.Combine(root, directory));
            if (!Within(root, requested)) throw new ArgumentException("Command working directory leaves the project root.");
            directory = ResolveDirectory(requested);
            if (!Within(root, directory)) throw new ArgumentException("Command working directory resolves outside the project root.");
            if (!LocalPath(executable) || !File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Command executable must be a configured absolute existing .exe path; shell association and PATH lookup are not used.");
            var file = new FileInfo(Path.Combine(ResolveDirectory(Path.GetDirectoryName(executable)!), Path.GetFileName(executable)));
            ValidateSegment(file.Name);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Executable reparse points are excluded.");
            executable = file.FullName;
            string commandLine = string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));
            if (commandLine.Length >= 32767) throw new ArgumentException("The configured Windows command line is too long.");
            string environment = EnvironmentBlock(secrets);
            if (cancellationToken.IsCancellationRequested) { result.Status = ProjectCommandStatus.Cancelled; result.Message = "Cancelled before command launch."; return result; }
            // Retain checked path identities until the OS has opened both executable and working directory.
            using (ProjectToolSession.AcquireCommandPathLease(root, directory, executable))
            {
                if (cancellationToken.IsCancellationRequested) { result.Status = ProjectCommandStatus.Cancelled; result.Message = "Cancelled before command launch."; return result; }
                result.Status = ProjectCommandStatus.Failed;
                process = new OwnedCommand(executable, commandLine, directory, environment);
            }
            result.ProcessId = process.ProcessId; result.CleanupComplete = false;
            stdout = Drain(process.Stdout, drainCancellation.Token); stderr = Drain(process.Stderr, drainCancellation.Token);
            var timer = Stopwatch.StartNew();
            while (!process.Exited)
            {
                if (cancellationToken.IsCancellationRequested) { result.Status = ProjectCommandStatus.Cancelled; result.Message = "Command cancelled; owned processes were stopped."; break; }
                if (timer.Elapsed >= TimeSpan.FromSeconds(timeout)) { result.Status = ProjectCommandStatus.TimedOut; result.Message = "Command exceeded its configured deadline; owned processes were stopped."; break; }
                await Task.Delay(20).ConfigureAwait(false);
            }
            if (process.Exited)
            {
                result.ExitCode = process.ExitCode;
                if (result.Status is not (ProjectCommandStatus.TimedOut or ProjectCommandStatus.Cancelled))
                {
                    result.Status = cancellationToken.IsCancellationRequested ? ProjectCommandStatus.Cancelled : result.ExitCode == 0 ? ProjectCommandStatus.Completed : ProjectCommandStatus.Failed;
                    result.Message = result.Status == ProjectCommandStatus.Completed ? "Configured command exited successfully; this is not a UI acceptance verdict."
                        : result.Status == ProjectCommandStatus.Cancelled ? "Cancellation was requested before command completion was accepted." : "Configured command returned a nonzero exit code.";
                }
            }
        }
        catch (Exception ex)
        {
            result.Message = "Configured command could not complete: " + ex.Message;
            if (result.ProcessId is not null) result.Status = ProjectCommandStatus.Failed;
            if (ex is LaunchFailure failure)
            {
                result.ProcessId = failure.ProcessId; result.CleanupComplete = failure.CleanupComplete;
                if (!failure.CleanupComplete) result.Status = ProjectCommandStatus.CleanupFailed;
            }
        }
        finally
        {
            if (process is not null)
            {
                try { result.CleanupComplete = await process.StopAndVerifyAsync().ConfigureAwait(false); }
                catch (Exception ex) { result.CleanupComplete = false; result.Message += " Owned-job cleanup failed: " + ex.Message; }
                if (!result.CleanupComplete) { result.Status = ProjectCommandStatus.CleanupFailed; result.Message += " Owned descendant cleanup could not be verified."; }
                try
                {
                    // Stop descendants first: a grandchild can otherwise keep a redirected pipe open after its parent exits.
                    var captured = await Task.WhenAll(stdout!, stderr!).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    result.Truncated = captured.Any(c => c.Truncated);
                    result.Stdout = SafeOutput(captured[0], secrets); result.Stderr = SafeOutput(captured[1], secrets);
                }
                catch (Exception)
                {
                    drainCancellation.Cancel();
                    try { if (stdout is not null && stderr is not null) await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
                    result.Truncated = true;
                    if (result.Status == ProjectCommandStatus.Completed) result.Status = ProjectCommandStatus.Failed;
                    result.Message += " Command output could not be completely drained within the bounded cleanup interval.";
                }
                process.Dispose();
            }
            result.Message = ProjectToolSession.Redact(result.Message, secrets);
            result.FinishedAt = DateTimeOffset.UtcNow;
        }
        return result;
    }

    private static string SafeOutput(Captured value, List<string> secrets) => value.Truncated
        ? "[Output exceeded the 65,536-character capture limit and was withheld to avoid exposing partial credentials. Configure a narrower summary command.]"
        : ProjectToolSession.Redact(value.Text, secrets);
    private sealed record Captured(string Text, bool Truncated);
    private sealed class LaunchFailure(Exception inner, bool cleanupComplete, int? processId) : Exception(inner.Message, inner)
    {
        internal bool CleanupComplete { get; } = cleanupComplete;
        internal int? ProcessId { get; } = processId;
    }
    private static async Task<Captured> Drain(StreamReader reader, CancellationToken cancellationToken)
    {
        var content = new StringBuilder(); var buffer = new char[4096]; bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            int retained = Math.Min(read, OutputLimit - content.Length);
            content.Append(buffer, 0, retained); truncated |= retained < read;
        }
        return new(content.ToString(), truncated);
    }
    private static bool Within(string root, string path) => string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool LocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':'
            || value[2] is not ('\\' or '/') || value[2..].Contains(':')) return false;
        foreach (string component in value[3..].TrimEnd('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)) ValidateSegment(component);
        return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(value))!).DriveType != DriveType.Network;
    }
    private static void ValidateSegment(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        bool device = new[] { "CON", "CONIN$", "CONOUT$", "CLOCK$", "PRN", "AUX", "NUL" }.Contains(stem)
            || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "0123456789¹²³".Contains(stem[3]);
        if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || device)
            throw new ArgumentException("Configured command path contains an ambiguous or prohibited Windows component.");
    }
    private static string ResolveDirectory(string path)
    {
        path = Path.GetFullPath(path);
        string drive = Path.GetPathRoot(path)!;
        var current = new DirectoryInfo(drive);
        foreach (string part in path[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateSegment(part);
            current = new DirectoryInfo(Path.Combine(current.FullName, part));
            if (!current.Exists) throw new ArgumentException("A configured directory does not exist.");
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Directory reparse points are excluded from configured command paths.");
        }
        return Path.TrimEndingDirectorySeparator(current.FullName);
    }
    private static string EnvironmentBlock(List<string> secrets)
    {
        // An allowlist avoids forwarding credentials stored under an otherwise innocuous custom
        // variable name. Applications still run as the current user and can read that user's files.
        var permitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SystemRoot", "windir", "SystemDrive", "ComSpec", "TEMP", "TMP", "PATH", "PATHEXT", "ProgramData",
            "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
            "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "HOME", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "PUBLIC", "OS",
            "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_ARCHITEW6432", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
            "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_ROOT_ARM64", "DOTNET_ROOT(x86)", "DOTNET_CLI_HOME", "DOTNET_MULTILEVEL_LOOKUP",
            "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_ROLL_FORWARD", "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
            "NUGET_PACKAGES", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "VSINSTALLDIR", "VCINSTALLDIR", "VCToolsInstallDir", "WindowsSdkDir",
            "INCLUDE", "LIB", "LIBPATH", "VSCMD_ARG_TGT_ARCH", "VSCMD_ARG_HOST_ARCH", "VSCMD_VER"
        };
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = (string)entry.Key, value = entry.Value?.ToString() ?? "";
            string upper = name.ToUpperInvariant();
            bool secret = upper.Contains("TOKEN") || upper.Contains("SECRET") || upper.Contains("PASSWORD") || upper.Contains("CREDENTIAL") || upper.Contains("AUTHORIZATION")
                || upper.Contains("API_KEY") || upper.EndsWith("_KEY") || upper.Contains("CONNECTIONSTRING") || upper.Contains("CONNECTION_STRING")
                || new[] { "OPENAI", "ANTHROPIC", "CODEX", "CHATGPT", "AWS_", "AZURE_", "GOOGLE_", "GCP_", "GEMINI_", "OPENROUTER_", "CLOUDFLARE_", "DIGITALOCEAN_" }.Any(upper.StartsWith);
            if (secret || !permitted.Contains(name)) { if (value.Length >= 8) secrets.Add(value); continue; }
            if (!name.Contains('=') && !name.Contains('\0') && !value.Contains('\0')) values[name] = value;
        }
        return string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
    }
    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            quoted.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes).Append(ch); slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class OwnedCommand : IDisposable
    {
        private nint job, process;
        internal int ProcessId { get; }
        internal StreamReader Stdout { get; private set; } = null!;
        internal StreamReader Stderr { get; private set; } = null!;
        internal bool Exited
        {
            get { uint state = WaitForSingleObject(process, 0); if (state == uint.MaxValue) throw new Win32Exception(); return state == 0; }
        }
        internal int ExitCode { get { if (!GetExitCodeProcess(process, out uint code)) throw new Win32Exception(); return unchecked((int)code); } }
        internal OwnedCommand(string executable, string command, string directory, string environment)
        {
            SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, inputRead = null, inputWrite = null;
            nint attributes = 0, jobs = 0, handles = 0, env = 0; bool initialized = false;
            try
            {
                job = CreateJobObjectW(0, null); if (job == 0) throw new Win32Exception();
                var limits = new ExtendedLimit { Basic = new BasicLimit { Flags = 0x2000 } };
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new Win32Exception();
                var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
                if (!CreatePipe(out outRead, out outWrite, ref security, 0) || !CreatePipe(out errRead, out errWrite, ref security, 0) || !CreatePipe(out inputRead, out inputWrite, ref security, 0)) throw new Win32Exception();
                if (!SetHandleInformation(outRead.DangerousGetHandle(), 1, 0) || !SetHandleInformation(errRead.DangerousGetHandle(), 1, 0)) throw new Win32Exception();
                inputWrite.Dispose(); inputWrite = null; // Immediate EOF on stdin: commands cannot prompt the host interactively.
                nuint size = 0; InitializeProcThreadAttributeList(0, 2, 0, ref size);
                attributes = Marshal.AllocHGlobal(checked((int)size));
                if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception();
                initialized = true; jobs = Marshal.AllocHGlobal(nint.Size); Marshal.WriteIntPtr(jobs, job);
                handles = Marshal.AllocHGlobal(nint.Size * 3);
                Marshal.WriteIntPtr(handles, inputRead.DangerousGetHandle()); Marshal.WriteIntPtr(handles, nint.Size, outWrite.DangerousGetHandle()); Marshal.WriteIntPtr(handles, nint.Size * 2, errWrite.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, (nint)0x2000D, jobs, (nuint)nint.Size, 0, 0)
                    || !UpdateProcThreadAttribute(attributes, 0, (nint)0x20002, handles, (nuint)(nint.Size * 3), 0, 0)) throw new Win32Exception();
                var startup = new StartupInfoEx { Startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x101, ShowWindow = 0,
                    StandardInput = inputRead.DangerousGetHandle(), StandardOutput = outWrite.DangerousGetHandle(), StandardError = errWrite.DangerousGetHandle() }, Attributes = attributes };
                env = Marshal.StringToHGlobalUni(environment);
                // Atomic job association and a strict inherited-handle list precede all target code.
                if (!CreateProcessW(executable, new StringBuilder(command), 0, 0, true, 0x00080000 | 0x08000000 | 0x400, env, directory, ref startup, out var created)) throw new Win32Exception();
                process = created.Process; ProcessId = (int)created.ProcessId; CloseHandle(created.Thread);
                Stdout = new StreamReader(new FileStream(outRead, FileAccess.Read), new UTF8Encoding(false, false), true); outRead = null;
                Stderr = new StreamReader(new FileStream(errRead, FileAccess.Read), new UTF8Encoding(false, false), true); errRead = null;
            }
            catch (Exception error)
            {
                bool clean = process == 0;
                if (process != 0) { try { clean = StopAndVerifyAsync().GetAwaiter().GetResult(); } catch { clean = false; } }
                Dispose(); throw new LaunchFailure(error, clean, ProcessId > 0 ? ProcessId : null);
            }
            finally
            {
                outRead?.Dispose(); outWrite?.Dispose(); errRead?.Dispose(); errWrite?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose();
                if (initialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != 0) Marshal.FreeHGlobal(attributes); if (jobs != 0) Marshal.FreeHGlobal(jobs); if (handles != 0) Marshal.FreeHGlobal(handles); if (env != 0) Marshal.FreeHGlobal(env);
            }
        }
        internal async Task<bool> StopAndVerifyAsync()
        {
            if (!TerminateJobObject(job, 1)) throw new Win32Exception();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), 0)) throw new Win32Exception();
                if (accounting.ActiveProcesses == 0) return true;
                await Task.Delay(20).ConfigureAwait(false);
            }
            return false;
        }
        public void Dispose()
        {
            if (job != 0) { CloseHandle(job); job = 0; }
            Stdout?.Dispose(); Stderr?.Dispose();
            if (process != 0) { CloseHandle(process); process = 0; }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, ReservedSize; public nint ReservedBytes, StandardInput, StandardOutput, StandardError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting { public long UserTime, KernelTime, ThisPeriodUserTime, ThisPeriodKernelTime; public uint PageFaultCount, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessUserTime, PerJobUserTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperation, WriteOperation, OtherOperation, ReadTransfer, WriteTransfer, OtherTransfer; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int information, ref ExtendedLimit value, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int information, out BasicAccounting value, uint length, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
