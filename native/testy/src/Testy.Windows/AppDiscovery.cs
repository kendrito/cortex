using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Testy.Core;

namespace Testy.Windows;

/// <summary>What <see cref="AppDiscovery.Discover"/> looks at.</summary>
public sealed class AppDiscoveryRequest
{
    public bool IncludeRunning { get; init; } = true;
    /// <summary>Start menu shortcuts (per user and all users), App Paths registrations (HKCU and HKLM) and packaged apps.</summary>
    public bool IncludeInstalled { get; init; } = true;
    /// <summary>Apps earlier tests used: their exe path, stored name and AppUserModelID.</summary>
    public IReadOnlyList<(string ExePath, string Name, string AppId)> Recent { get; init; } = [];
    /// <summary>Testy's sample apps found next to the running Testy program (<see cref="AppDiscovery.SamplePaths"/>).</summary>
    public IReadOnlyList<string> SamplePaths { get; init; } = [];
    /// <summary>Processes that are never candidates (Testy's own processes).</summary>
    public IReadOnlyCollection<int> ExcludeProcessIds { get; init; } = [];
}

/// <summary>
/// Finds the applications a name can refer to: running top-level windows (visible, titled, not cloaked, not owned tool windows), installed
/// desktop programs (Start menu shortcuts resolved through the Windows Script Host shell object, App Paths registrations), packaged
/// Store/MSIX apps listed in the shell's Applications folder, the programs earlier tests used, and Testy's sample apps. Names come from the
/// window titles and the programs' version resources. Installed apps are cached for three minutes; COM work runs on a short-lived STA thread.
/// </summary>
public static partial class AppDiscovery
{
    private static readonly TimeSpan InstalledLifetime = TimeSpan.FromMinutes(3);
    private static readonly object installedGate = new();
    private static List<AppCandidate>? installed;
    private static DateTime installedAt;
    private static readonly ConcurrentDictionary<string, FileVersionInfo?> versions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"];
    public static readonly string[] SampleFileNames = ["Testy.TestLab.exe", "Testy.OrderLab.exe", "Testy.WpfLab.exe"];

    /// <summary>Every candidate, merged per program (see <see cref="AppResolver.Merge"/>).</summary>
    public static List<AppCandidate> Discover(AppDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        var all = new List<AppCandidate>();
        if (request.IncludeRunning) all.AddRange(Running(request.ExcludeProcessIds));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (path, name, appId) in request.Recent)
        {
            if (!string.IsNullOrWhiteSpace(appId))
            {
                all.Add(new AppCandidate { Kind = AppCandidateKind.Recent, Name = name, AppId = appId.Trim(), Packaged = true, ExePath = ExecutableRules.IsLocalDrivePath(path) ? path!.Trim() : "", Sources = [AppCandidateKind.Recent] });
                continue;
            }
            // A stored path comes from a test file (restored, imported, synced or edited by hand): a network path is never opened, not even to check it.
            if (!ExecutableRules.IsLocalDrivePath(path) || !File.Exists(path)) continue;
            var recent = FromProgram(path, AppCandidateKind.Recent);
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, recent.Name, StringComparison.OrdinalIgnoreCase)) { recent.OtherNames.Add(recent.Name); recent.Name = name.Trim(); }
            all.Add(recent);
        }
        foreach (var sample in request.SamplePaths.Where(File.Exists)) all.Add(FromProgram(sample, AppCandidateKind.Sample));
        if (request.IncludeInstalled) all.AddRange(Installed(cancellationToken).Select(c => c.Clone()));
        return AppResolver.Merge(all);
    }

    /// <summary>Testy's sample apps next to <paramref name="baseDirectory"/> (the portable bundle) or in the development build tree above it.</summary>
    public static List<string> SamplePaths(string baseDirectory)
    {
        var found = new List<string>();
        // In a development build (src\<Project>\bin\<Configuration>\<framework>) the samples of the same build come first, not an older bundle.
        var build = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)));
        var sibling = build.Parent?.Parent is { Name: "bin" } bin && bin.Parent?.Parent is { Name: "src" } src ? (Source: src.FullName, Configuration: build.Parent!.Name, Framework: build.Name) : default;
        foreach (var file in SampleFileNames)
        {
            var project = Path.GetFileNameWithoutExtension(file);
            if (sibling.Source is not null && Path.Combine(sibling.Source, project, "bin", sibling.Configuration, sibling.Framework, file) is var same && File.Exists(same)) { found.Add(same); continue; }
            var directory = new DirectoryInfo(baseDirectory);
            for (var level = 0; directory is not null && level < 7; level++, directory = directory.Parent)
            {
                string[] candidates =
                [
                    Path.Combine(directory.FullName, file), Path.Combine(directory.FullName, "TestLab", file), Path.Combine(directory.FullName, "dist", "Testy", file),
                    Path.Combine(directory.FullName, "src", project, "bin", "Release", "net9.0-windows", file), Path.Combine(directory.FullName, "src", project, "bin", "Debug", "net9.0-windows", file)
                ];
                var hit = candidates.FirstOrDefault(File.Exists);
                if (hit is null) continue;
                found.Add(Path.GetFullPath(hit));
                break;
            }
        }
        return found;
    }

    // ═══════════════ Running windows ═══════════════
    /// <summary>One candidate per visible, titled, uncloaked, unowned top-level window (merged per process later).</summary>
    public static List<AppCandidate> Running(IReadOnlyCollection<int>? exclude = null)
    {
        var windows = new List<(nint Handle, int Pid, string Title)>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != 0) return true; // GW_OWNER: dialogs and popups belong to their owner
                var style = GetWindowLongPtr(hwnd, -20).ToInt64(); // GWL_EXSTYLE
                if ((style & 0x80) != 0 && (style & 0x40000) == 0) return true; // tool windows that do not ask for a taskbar button
                if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var length = GetWindowTextLength(hwnd);
                if (length <= 0) return true;
                var title = new StringBuilder(length + 2); GetWindowText(hwnd, title, title.Capacity);
                var className = new StringBuilder(256); GetClassName(hwnd, className, className.Capacity);
                if (ShellClasses.Contains(className.ToString(), StringComparer.Ordinal)) return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid == Environment.ProcessId || exclude?.Contains((int)pid) == true) return true;
                windows.Add((hwnd, (int)pid, title.ToString()));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            return true;
        }, 0);
        var result = new List<AppCandidate>();
        foreach (var group in windows.GroupBy(w => w.Pid))
        {
            string processName;
            nint main;
            try { using var process = Process.GetProcessById(group.Key); processName = process.ProcessName; main = process.MainWindowHandle; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            var primary = group.FirstOrDefault(w => w.Handle == main);
            if (primary.Handle == 0) primary = group.First();
            // A classic UWP app's frame belongs to ApplicationFrameHost; its name and package come from the app process inside the frame.
            var infoPid = string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ? HostedProcess(primary.Handle, group.Key) ?? group.Key : group.Key;
            var exe = ImagePath(infoPid) ?? "";
            var appId = AppUserModelId(infoPid) ?? "";
            var version = exe.Length > 0 ? Version(exe) : null;
            var candidate = new AppCandidate
            {
                Kind = AppCandidateKind.Running, ProcessId = group.Key, WindowHandle = primary.Handle, WindowTitle = primary.Title, ProcessName = processName,
                OtherTitles = group.Where(w => w.Handle != primary.Handle).Select(w => w.Title).Distinct().ToList(),
                ExePath = exe, AppId = appId, Packaged = appId.Length > 0, ProductName = Clean(version?.ProductName), FileDescription = Clean(version?.FileDescription),
                Sources = [AppCandidateKind.Running]
            };
            candidate.Name = FriendlyName(candidate);
            result.Add(candidate);
        }
        return result;
    }
    private static int? HostedProcess(nint frame, int host)
    {
        int? found = null;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var pid);
            if (pid != 0 && pid != host) { found = (int)pid; return false; }
            return true;
        }, 0);
        return found;
    }

    /// <summary>The name shown for a program: its file description when that says more than the file name, else the app part of its window title, else its product name, else the file name.</summary>
    public static string FriendlyName(AppCandidate candidate)
    {
        var stem = candidate.ExePath.Length > 0 ? Path.GetFileNameWithoutExtension(candidate.ExePath) : candidate.ProcessName;
        if (candidate.FileDescription.Length > 0 && !Same(candidate.FileDescription, stem) && !Same(candidate.FileDescription, candidate.ProcessName)) return candidate.FileDescription;
        if (candidate.WindowTitle.Length > 0)
        {
            var parts = TitleParts().Split(candidate.WindowTitle).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            return parts.Length > 1 ? parts[^1] : candidate.WindowTitle.Trim();
        }
        if (candidate.ProductName.Length > 0) return candidate.ProductName;
        return stem;
        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(a.Trim(), b.Trim() + ".exe", StringComparison.OrdinalIgnoreCase);
    }
    [GeneratedRegex(@"\s[—–\-|]\s")]
    private static partial Regex TitleParts();

    /// <summary>A candidate for a program file, named from its version resource.</summary>
    public static AppCandidate FromProgram(string exe, AppCandidateKind kind)
    {
        var full = Path.GetFullPath(exe);
        var version = Version(full);
        var candidate = new AppCandidate { Kind = kind, ExePath = full, ProductName = Clean(version?.ProductName), FileDescription = Clean(version?.FileDescription), Sources = [kind] };
        candidate.Name = FriendlyName(candidate);
        return candidate;
    }
    private static FileVersionInfo? Version(string exe) => versions.GetOrAdd(exe, path =>
    {
        try { return FileVersionInfo.GetVersionInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    });
    private static string Clean(string? text) => (text ?? "").Replace("\0", "").Trim();

    // ═══════════════ Installed programs ═══════════════
    /// <summary>Installed desktop and packaged apps, cached for three minutes.</summary>
    public static List<AppCandidate> Installed(CancellationToken cancellationToken = default)
    {
        lock (installedGate)
        {
            if (installed is not null && DateTime.UtcNow - installedAt < InstalledLifetime) return installed;
        }
        var found = new List<AppCandidate>();
        found.AddRange(AppPaths());
        cancellationToken.ThrowIfCancellationRequested();
        // Each source on its own: a shell extension that fails one listing must not cost the other.
        found.AddRange(RunOnSta(() => Shortcuts().ToList(), TimeSpan.FromSeconds(15)) ?? []);
        cancellationToken.ThrowIfCancellationRequested();
        found.AddRange(RunOnSta(() => PackagedApps.Listed().ToList(), TimeSpan.FromSeconds(15)) ?? []);
        lock (installedGate) { installed = found; installedAt = DateTime.UtcNow; }
        return found;
    }
    /// <summary>Forgets the cached installed apps (for example after an app was installed).</summary>
    public static void Refresh() { lock (installedGate) installed = null; }

    private static IEnumerable<AppCandidate> Shortcuts()
    {
        var folders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }.Where(Directory.Exists);
        var links = new List<string>();
        foreach (var folder in folders)
        {
            try { links.AddRange(Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 }).Take(3000)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) yield break;
        object? shell = null;
        try { shell = Activator.CreateInstance(type); } catch (Exception ex) when (ex is COMException or TargetInvocationException or MemberAccessException) { }
        if (shell is null) yield break;
        try
        {
            foreach (var link in links)
            {
                var name = Path.GetFileNameWithoutExtension(link);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("uninst", StringComparison.OrdinalIgnoreCase)) continue;
                string? target = null;
                try
                {
                    var shortcut = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, link);
                    if (shortcut is not null)
                    {
                        try
                        {
                            target = Invoke(shortcut, "TargetPath", BindingFlags.GetProperty) as string;
                            // A shortcut with arguments opens something with a program ("Lua documentation" → notepad.exe <file>); it does not name the program.
                            if (!string.IsNullOrWhiteSpace(Invoke(shortcut, "Arguments", BindingFlags.GetProperty) as string)) target = null;
                        }
                        finally { Marshal.FinalReleaseComObject(shortcut); }
                    }
                }
                catch (Exception ex) when (ex is COMException or TargetInvocationException or ArgumentException or InvalidCastException) { continue; }
                if (string.IsNullOrWhiteSpace(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.Contains("unins", StringComparison.OrdinalIgnoreCase)) continue;
                string full;
                try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target)); if (!File.Exists(full)) continue; }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { continue; }
                var candidate = FromProgram(full, AppCandidateKind.Installed);
                candidate.ShortcutNames.Add(name);
                candidate.Name = name;
                yield return candidate;
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static IEnumerable<AppCandidate> AppPaths()
    {
        const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var results = new List<AppCandidate>();
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var paths = root.OpenSubKey(Key);
                if (paths is null) continue;
                foreach (var name in paths.GetSubKeyNames())
                {
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    using var entry = paths.OpenSubKey(name);
                    var value = (entry?.GetValue(null) as string)?.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    string full;
                    try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value)); if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue; }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { continue; }
                    var candidate = FromProgram(full, AppCandidateKind.Installed);
                    candidate.ShortcutNames.Add(Path.GetFileNameWithoutExtension(name));
                    results.Add(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return results;
    }

    internal static object? Invoke(object target, string member, BindingFlags flags, params object?[] arguments) =>
        target.GetType().InvokeMember(member, flags, null, target, arguments);

    /// <summary>Runs COM work on its own STA thread; null when it failed or did not finish in time.</summary>
    internal static T? RunOnSta<T>(Func<T?> work, TimeSpan timeout) where T : class
    {
        T? result = null;
        var thread = new Thread(() =>
        {
            // Whatever a shell extension or COM server throws must not end the process from this thread: the caller gets null.
            try { result = work(); }
            catch (Exception) { result = null; }
        }) { IsBackground = true, Name = "Testy app discovery" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(timeout) ? result : null;
    }

    // ═══════════════ Process facts ═══════════════
    /// <summary>The full path of a process's program, readable for most processes of this user (PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    public static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }
    /// <summary>The AppUserModelID of a packaged process, or null for a desktop program.</summary>
    public static string? AppUserModelId(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return null;
        try
        {
            var length = 0;
            if (GetApplicationUserModelId(handle, ref length, null) != 122 || length <= 0) return null; // ERROR_INSUFFICIENT_BUFFER; 15703: not packaged
            var buffer = new StringBuilder(length);
            return GetApplicationUserModelId(handle, ref length, buffer) == 0 ? buffer.ToString() : null;
        }
        catch (EntryPointNotFoundException) { return null; }
        finally { CloseHandle(handle); }
    }
    /// <summary>The running processes whose program is <paramref name="exe"/> (or whose package app is <paramref name="appId"/>) that show a candidate window.</summary>
    public static List<AppCandidate> RunningInstances(string? exe, string? appId, IReadOnlyCollection<int>? exclude = null)
    {
        var program = string.IsNullOrWhiteSpace(exe) ? "" : AppCandidate.NormalizePath(exe);
        return AppResolver.Merge(Running(exclude)).Where(c =>
            !string.IsNullOrWhiteSpace(appId) && string.Equals(c.AppId, appId.Trim(), StringComparison.OrdinalIgnoreCase)
            || program.Length > 0 && c.ExePath.Length > 0 && AppCandidate.NormalizePath(c.ExePath) == program).ToList();
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref int length, StringBuilder? id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

/// <summary>
/// Packaged (Store/MSIX) apps: listed from the shell's Applications folder (shell:AppsFolder) with their AppUserModelID and install folder,
/// their program read from the package manifest, and started through the documented activation manager, which returns the new process id.
/// Starting one is best effort: the package decides what runs (a single-instance app may bring its existing window forward and report that
/// process), and the process is started by Windows, so it can be added to a cleanup job only afterwards and only when Windows allows it.
/// </summary>
public static partial class PackagedApps
{
    internal static IEnumerable<AppCandidate> Listed()
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) yield break;
        object? shell = null, folder = null, items = null;
        try
        {
            try { shell = Activator.CreateInstance(type); } catch (Exception ex) when (ex is COMException or TargetInvocationException or MemberAccessException) { yield break; }
            if (shell is null) yield break;
            folder = AppDiscovery.Invoke(shell, "NameSpace", BindingFlags.InvokeMethod, "shell:AppsFolder");
            if (folder is null) yield break;
            items = AppDiscovery.Invoke(folder, "Items", BindingFlags.InvokeMethod);
            if (items is null) yield break;
            var count = Convert.ToInt32(AppDiscovery.Invoke(items, "Count", BindingFlags.GetProperty) ?? 0);
            for (var index = 0; index < Math.Min(count, 2000); index++)
            {
                AppCandidate? candidate = null;
                object? item = null;
                try
                {
                    item = AppDiscovery.Invoke(items, "Item", BindingFlags.InvokeMethod, index);
                    if (item is null) continue;
                    var id = AppDiscovery.Invoke(item, "Path", BindingFlags.GetProperty) as string ?? "";
                    if (!IsAppUserModelId(id)) continue;
                    var name = (AppDiscovery.Invoke(item, "Name", BindingFlags.GetProperty) as string ?? "").Trim();
                    var install = (AppDiscovery.Invoke(item, "ExtendedProperty", BindingFlags.InvokeMethod, "System.AppUserModel.PackageInstallPath") as string ?? "").Trim();
                    if (name.Length == 0 || install.Length == 0) continue;
                    candidate = new AppCandidate { Kind = AppCandidateKind.Installed, Name = name, ShortcutNames = [name], AppId = id, Packaged = true, PackageInstallPath = install, Sources = [AppCandidateKind.Installed] };
                }
                catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException or ArgumentException) { continue; }
                finally { if (item is not null) Marshal.FinalReleaseComObject(item); }
                if (candidate is not null) yield return candidate;
            }
        }
        finally
        {
            if (items is not null) Marshal.FinalReleaseComObject(items);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>"PackageName_publisherhash!AppId": a package family name (13-character publisher id) and the application id.</summary>
    public static bool IsAppUserModelId(string? value) => value is { Length: > 3 and <= 400 } && AumidPattern().IsMatch(value);
    [GeneratedRegex(@"\A[A-Za-z0-9.\-]+_[a-z0-9]{13}![A-Za-z0-9.\-_]+\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AumidPattern();

    /// <summary>The install folder of the package of <paramref name="appId"/>, from the installed-app list.</summary>
    public static string? InstallPath(string appId) =>
        AppDiscovery.Installed().FirstOrDefault(c => string.Equals(c.AppId, appId, StringComparison.OrdinalIgnoreCase))?.PackageInstallPath is { Length: > 0 } path ? path : null;

    /// <summary>The program the package manifest names for the application of <paramref name="appId"/>, or null when it cannot be read.</summary>
    public static string? ProgramFor(string appId, string? installPath = null)
    {
        if (!IsAppUserModelId(appId)) return null;
        installPath = string.IsNullOrWhiteSpace(installPath) ? InstallPath(appId) : installPath;
        if (installPath is null) return null;
        var manifest = Path.Combine(installPath, "AppxManifest.xml");
        try
        {
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var application = appId[(appId.IndexOf('!') + 1)..];
            var entry = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application" && string.Equals((string?)e.Attribute("Id"), application, StringComparison.OrdinalIgnoreCase));
            var executable = (string?)entry?.Attribute("Executable");
            if (string.IsNullOrWhiteSpace(executable) || executable.Contains("..", StringComparison.Ordinal)) return null;
            var full = Path.GetFullPath(Path.Combine(installPath, executable));
            return full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Starts the packaged app through IApplicationActivationManager and returns its process id.</summary>
    public static int Activate(string appId)
    {
        if (!IsAppUserModelId(appId)) throw new ArgumentException("Not an AppUserModelID: " + appId, nameof(appId));
        Exception? failure = null;
        var pid = AppDiscovery.RunOnSta<object>(() =>
        {
            try
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                try
                {
                    var hr = manager.ActivateApplication(appId, null, 0, out var id);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    return (object)(int)id;
                }
                finally { Marshal.FinalReleaseComObject(manager); }
            }
            catch (Exception ex) { failure = ex; return null; }
        }, TimeSpan.FromSeconds(30));
        if (pid is int value && value > 0) return value;
        throw new InvalidOperationException($"Windows did not start the packaged app {appId}" + (failure is null ? " in time." : ": " + failure.Message));
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;
}
