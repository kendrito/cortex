using System.Diagnostics;
using System.Text.Json.Nodes;
using Testy.Core;

namespace Testy.Cli.Mcp;

/// <summary>
/// What launch_app and run_test (exe) may start, and which applications the UI tools may connect to. The default lets an agent start the
/// application under test, but only a real desktop program: a local .exe whose PE header declares the Windows GUI subsystem. Console programs
/// and the script hosts, shells and program launchers Windows ships are refused, by file name and by the name recorded in the file's version
/// resource, so a renamed copy is caught too; so is every other program that is part of Windows itself (inside the Windows folder, an app
/// execution alias, or a copy whose version resource names the Windows product) unless --allow-exe names it, and an argument that names a
/// refused program. Every check that needs no file access runs before the first file-system call, so a network or device path is never touched.
/// This is a guard against accidents and against an agent steered by hostile text, not a sandbox: a third-party GUI interpreter or an already
/// open terminal remains reachable through the application the caller names.
/// </summary>
internal sealed class McpLaunchPolicy
{
    /// <summary>Shells, terminals, script hosts and system launchers that are never started, whatever their subsystem (shared with every Testy surface).</summary>
    public static readonly string[] RefusedPrograms = ExecutableRules.RefusedPrograms;
    private const string Alternative = "Start it yourself and connect to its window with list_apps.";
    private readonly string[] allowedExecutables;
    private readonly string[] allowedTargets;

    public McpLaunchPolicy(bool allowLaunch = true, IEnumerable<string>? allowedExecutables = null, IEnumerable<string>? allowedTargets = null)
    {
        AllowLaunch = allowLaunch;
        this.allowedExecutables = (allowedExecutables ?? []).Select(path => Path.TrimEndingDirectorySeparator(LocalPath(path, "--allow-exe"))).ToArray();
        this.allowedTargets = (allowedTargets ?? []).Select(target => target.Trim()).Where(target => target.Length > 0).ToArray();
        foreach (var target in this.allowedTargets.Where(LooksLikePath)) LocalPath(target, "--allow-target");
    }
    public static McpLaunchPolicy Default { get; } = new();
    /// <summary>False when the server was started with --no-launch.</summary>
    public bool AllowLaunch { get; }
    /// <summary>Files or folders named with --allow-exe; empty means any local GUI program.</summary>
    public IReadOnlyList<string> AllowedExecutables => allowedExecutables;
    /// <summary>Process names or exe paths named with --allow-target; empty means any application the caller names.</summary>
    public IReadOnlyList<string> AllowedTargets => allowedTargets;
    /// <summary>File-system probes, replaceable so a check can prove that a refused path was never touched.</summary>
    internal Func<string, bool> FileExists { get; init; } = File.Exists;
    internal Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;

    public JsonObject Describe() => new()
    {
        ["launch"] = !AllowLaunch ? "disabled (--no-launch)" : allowedExecutables.Length == 0 ? "any local Windows GUI program (.exe)" : "only the files and folders in allowedExecutables",
        ["allowedExecutables"] = JsonRpc.Strings(allowedExecutables),
        ["targets"] = allowedTargets.Length == 0 ? "any application the caller names by pid" : "only the processes in allowedTargets",
        ["allowedTargets"] = JsonRpc.Strings(allowedTargets),
        ["refusedPrograms"] = JsonRpc.Strings(RefusedPrograms),
        ["rules"] = "exe must be a full local drive path to an existing .exe with the Windows GUI subsystem; console programs, script hosts, shells, terminals, program launchers, network (UNC) and device paths are refused, and so is an argument that names a refused program. Programs that are part of Windows itself (inside the Windows folder, app execution aliases, copies of Windows programs) are refused unless --allow-exe names them. An app named with app is started under the same rules (a packaged app only when the program its manifest names passes them). This is not a sandbox."
    };

    /// <summary>
    /// A full path on a local drive (C:\…). Relative paths, PATH lookup, UNC paths (\\host\share) and device paths (\\?\, \\.\) are refused
    /// without touching the file system: opening a network path would already authenticate this user to that host.
    /// </summary>
    public static string LocalPath(string path, string argument)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new McpToolException($"{argument} cannot be blank.");
        var text = path.Trim();
        if (text.Length >= 2 && IsSeparator(text[0]) && IsSeparator(text[1]))
            throw new McpToolException($"{argument} must be on a local drive: network (UNC) and device paths are not accepted. Only local drive paths such as C:\\Apps\\App.exe are.");
        if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || !IsSeparator(text[2]))
            throw new McpToolException($"{argument} must be a full path such as C:\\Apps\\App.exe (relative paths and PATH lookup are not supported).");
        if (text.IndexOf(':', 2) >= 0) throw new McpToolException($"{argument} is not a valid path: a colon is allowed only after the drive letter.");
        string full;
        try { full = Path.GetFullPath(text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new McpToolException($"{argument} is not a valid path: {ex.Message}"); }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new McpToolException($"{argument} must be on a local drive: network (UNC) and device paths are not accepted.");
        return full;
    }
    private static bool IsSeparator(char value) => value is '\\' or '/';
    private static bool LooksLikePath(string value) => value.Contains('\\') || value.Contains('/') || value.Contains(':');

    /// <summary>An existing local folder for workingDirectory.</summary>
    public string RequireDirectory(string path, string argument)
    {
        var full = LocalPath(path, argument);
        if (!DirectoryExists(full)) throw new McpToolException($"{argument} does not exist: {full}");
        return full;
    }

    /// <summary>The path of a test's target program: a local .exe path. The file is not opened; a test may be written before the app is installed.</summary>
    public static string TargetPath(string path, string argument)
    {
        var full = LocalPath(path, argument);
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new McpToolException($"{argument} must be the path of an .exe.");
        return full;
    }

    /// <summary>The full path of a program this server may start, or a tool error that names the rule and the alternative. Nothing is started here.</summary>
    public string RequireLaunchable(string path, string argument = "exe")
    {
        if (!AllowLaunch) throw new McpToolException("This server was started with --no-launch, so it starts no programs. " + Alternative);
        var full = LocalPath(path, argument);
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new McpToolException($"{argument} must be an .exe file: {full}. {Alternative}");
        var name = Path.GetFileNameWithoutExtension(full);
        if (IsRefused(name)) throw Refused(name);
        var allowed = ExplicitlyAllowed(full);
        if (allowedExecutables.Length > 0 && !allowed)
            throw new McpToolException($"{argument} is outside the locations this server may start programs from ({string.Join("; ", allowedExecutables)}). {Alternative}");
        if (!FileExists(full)) throw new McpToolException($"{argument} does not exist: {full}");
        var image = ReadImage(full);
        if (image.Problem is { } problem) throw new McpToolException($"{Path.GetFileName(full)} cannot be started: {problem} Only Windows desktop (GUI) programs are started. {Alternative}");
        foreach (var recorded in RecordedNames(full)) if (IsRefused(recorded)) throw Refused(recorded);
        // The application under test is almost never part of Windows, and several Windows programs start whatever they are given.
        if (!allowed && WindowsComponent(full) is { } component)
            throw new McpToolException($"{Path.GetFileName(full)} is {component}, and this server starts programs of Windows itself only when it was started with --allow-exe naming them. {Alternative}");
        return full;
    }
    /// <summary>Where a program belongs to Windows itself, or null (replaceable so a check can use a folder of its own).</summary>
    internal Func<string, string?> WindowsComponent { get; init; } = ExecutableRules.WindowsComponentOf;
    /// <summary>True when --allow-exe names this file or a folder that contains it.</summary>
    private bool ExplicitlyAllowed(string full) =>
        allowedExecutables.Any(allowed => full.Equals(allowed, StringComparison.OrdinalIgnoreCase) || full.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The arguments of a program this server starts: none may name a refused program (a launcher that is allowed would start it), whatever
    /// the program is. Nothing is read or started here.
    /// </summary>
    public static void RequireArguments(IReadOnlyList<string> arguments, string argument = "args")
    {
        if (ExecutableRules.RefusedArgument(arguments) is { } named)
            throw new McpToolException($"{argument} names {named.ToLowerInvariant()}, a shell, script host or program launcher this server never starts, so nothing was started (refused: {string.Join(", ", RefusedPrograms)}). {Alternative}");
    }
    private static bool IsRefused(string name) => ExecutableRules.IsRefusedName(name);
    private static McpToolException Refused(string name) =>
        new($"{name.ToLowerInvariant()} is a shell, script host or system launcher, and this server starts only desktop applications (refused: {string.Join(", ", RefusedPrograms)}). {Alternative}");
    private static IEnumerable<string> RecordedNames(string path) => ExecutableRules.RecordedNames(path);

    /// <summary>
    /// A packaged (Store/MSIX) app may be started through its AppUserModelID only when the program its package manifest names for that
    /// application passes the same checks as exe (GUI subsystem, not a shell, inside --allow-exe when given). Nothing is started here.
    /// </summary>
    public void RequirePackagedLaunchable(string appId, string? packageExecutable, string name)
    {
        if (!AllowLaunch) throw new McpToolException("This server was started with --no-launch, so it starts no programs. " + Alternative);
        if (string.IsNullOrWhiteSpace(packageExecutable))
            throw new McpToolException($"{name} is a packaged app ({appId}) whose program could not be read from its package manifest, so this server cannot check it and does not start it. {Alternative}");
        RequireLaunchable(packageExecutable, "the packaged app's program");
    }

    /// <summary>True when the process may be inspected and driven: no --allow-target was given, or its name or exe path is listed.</summary>
    public void RequireTarget(int pid)
    {
        if (allowedTargets.Length == 0) return;
        string name; string? path = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
            try { path = process.MainModule?.FileName; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new McpToolException($"No process with pid {pid} is running. Use list_apps to find the pid of a visible window."); }
        foreach (var allowed in allowedTargets)
        {
            if (LooksLikePath(allowed)) { if (path is not null && string.Equals(Path.GetFullPath(allowed), path, StringComparison.OrdinalIgnoreCase)) return; }
            else if (name.Equals(allowed, StringComparison.OrdinalIgnoreCase) || name.Equals(Path.GetFileNameWithoutExtension(allowed), StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new McpToolException($"pid {pid} ({name}) is not one of the applications this server may connect to ({string.Join("; ", allowedTargets)}).");
    }

    /// <summary>The non-throwing form of <see cref="RequireTarget"/>: false for a process this server may not connect to (or that has ended).</summary>
    public bool AllowsTarget(int pid)
    {
        if (allowedTargets.Length == 0) return true;
        try { RequireTarget(pid); return true; }
        catch (McpToolException) { return false; }
    }

    internal sealed record ImageInfo(int Subsystem, bool IsLibrary, string? Problem);

    /// <summary>Reads the PE header (the shared <see cref="ExecutableRules"/> implementation).</summary>
    internal static ImageInfo ReadImage(string path) => From(ExecutableRules.ReadImage(path));
    internal static ImageInfo ReadImage(ReadOnlySpan<byte> header) => From(ExecutableRules.ReadImage(header));
    private static ImageInfo From(ExecutableImage image) => new(image.Subsystem, image.IsLibrary, image.Problem);
}
