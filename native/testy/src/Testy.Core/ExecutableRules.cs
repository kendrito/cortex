using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Testy.Core;

/// <summary>What a program's header says about it: the PE subsystem, whether it is a library, and why it is not a desktop program (null when it is).</summary>
public sealed record ExecutableImage(int Subsystem, bool IsLibrary, string? Problem);

/// <summary>
/// The rules every Testy surface applies before it starts a program on someone's behalf (the MCP server's launch_app and run_test, apps that
/// are resolved from a name, and Studio when it starts the app a test names): a local .exe whose PE header declares the Windows GUI subsystem.
/// Console programs and the shells, terminals, script hosts and program launchers Windows ships are refused, by file name and by the names
/// recorded in the file's version resource, so a renamed copy is caught too. The MCP server also refuses the programs that are part of Windows
/// itself (<see cref="WindowsComponentOf"/>) unless it was told to allow one, and arguments that name a refused program. This is a guard against
/// accidents and steered agents, not a sandbox.
/// </summary>
public static partial class ExecutableRules
{
    /// <summary>
    /// Shells, terminals, script hosts and program launchers that are never started, whatever their subsystem: the launchers start whatever
    /// program their arguments name (explorer, pcalua, runas, forfiles, …), the others run scripts or code they are given (powershell_ise, hh, mmc, …).
    /// </summary>
    public static readonly string[] RefusedPrograms =
    [
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec", "conhost", "wsl", "bash", "windowsterminal", "wt", "openconsole",
        "explorer", "pcalua", "powershell_ise", "hh", "mmc", "msdt", "runas", "forfiles", "cmstp", "presentationhost", "scriptrunner", "syncappvpublishingserver",
        "appvlp", "infdefaultinstall", "mavinject", "installutil", "msbuild", "regasm", "regsvcs", "wmic", "schtasks", "bitsadmin"
    ];
    /// <summary>The product name Windows records in the version resource of its own programs (packaged apps such as Notepad or Paint use their own).</summary>
    public const string WindowsProductName = "Microsoft® Windows® Operating System";
    /// <summary>File types that start something when a launcher is given them; an argument ending in one is read as a program name.</summary>
    private static readonly string[] LaunchExtensions = [".exe", ".com", ".bat", ".cmd", ".lnk", ".msc", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".scr", ".cpl", ".pif"];

    public static bool IsRefusedName(string name) => RefusedPrograms.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for a full path on a local drive (C:\…), decided from the text alone: UNC (\\host\share), device (\\?\, \\.\) and relative paths are
    /// false. Call it before any File.Exists or version lookup of a path that came from a file (a stored test's program): opening a network path
    /// would already authenticate this user to that host.
    /// </summary>
    public static bool IsLocalDrivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var text = path.Trim();
        static bool Separator(char value) => value is '\\' or '/';
        if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || !Separator(text[2]) || text.IndexOf(':', 2) >= 0) return false;
        try { return !Path.GetFullPath(text).StartsWith(@"\\", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// The refused program an argument names, or null: an argument that is itself a program name ("cmd", "powershell.exe"), or a path or a file
    /// name with a program extension anywhere inside an argument ("-a C:\Windows\System32\cmd.exe", "/run=wscript.exe"). A word inside a longer
    /// argument without a path or an extension ("open the control panel") is text, not a program. No file is touched.
    /// </summary>
    public static string? RefusedArgument(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            var whole = argument.Trim().Trim('"', '\'').Trim();
            if (whole.Length > 0 && !whole.Any(char.IsWhiteSpace) && ProgramName(whole) is { } alone && IsRefusedName(alone)) return alone;
            foreach (var token in ArgumentTokens().Split(argument))
            {
                if (token.Length == 0 || !(token.Contains('\\') || token.Contains('/') || LaunchExtensions.Any(e => token.EndsWith(e, StringComparison.OrdinalIgnoreCase)))) continue;
                if (ProgramName(token) is { } named && IsRefusedName(named)) return named;
            }
        }
        return null;
    }
    /// <summary>The file name of a path or name without a program extension ("C:\x\cmd.exe" → "cmd"); null when nothing is left.</summary>
    private static string? ProgramName(string text)
    {
        var name = text.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        foreach (var extension in LaunchExtensions)
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) { name = name[..^extension.Length]; break; }
        name = name.Trim();
        return name.Length == 0 ? null : name;
    }
    [System.Text.RegularExpressions.GeneratedRegex(@"[\s""',;=|&()<>]+")]
    private static partial System.Text.RegularExpressions.Regex ArgumentTokens();

    /// <summary>
    /// Where a program is part of Windows itself, or null: its real location (links and junctions resolved) is inside the Windows folder
    /// (%SystemRoot%, System32 and SysWOW64 included) or the per-user folder of app execution aliases (%LOCALAPPDATA%\Microsoft\WindowsApps), or
    /// its version resource names the Windows product (a copy of a Windows program elsewhere). The application under test is almost never one of
    /// these, and several of them start other programs. Packaged apps (Notepad, Paint, Calculator) live elsewhere and name their own product.
    /// </summary>
    public static string? WindowsComponentOf(string fullPath)
    {
        var real = FinalPath(fullPath) ?? fullPath;
        foreach (var (folder, label) in WindowsFolders())
            if (Inside(folder, real) || Inside(folder, fullPath)) return label;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(fullPath);
            if (string.Equals(info.ProductName?.Trim(), WindowsProductName, StringComparison.Ordinal)) return "a program of Windows itself (its version resource names " + WindowsProductName + ")";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
    private static IEnumerable<(string Folder, string Label)> WindowsFolders()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) windows = Environment.GetEnvironmentVariable("SystemRoot") ?? "";
        if (windows.Length > 0) yield return (windows, "inside the Windows folder (" + windows + ")");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) yield return (Path.Combine(local, "Microsoft", "WindowsApps"), "an app execution alias (" + Path.Combine(local, "Microsoft", "WindowsApps") + "), not the program itself");
    }
    private static bool Inside(string folder, string path)
    {
        try { return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    /// <summary>The path Windows opens for <paramref name="path"/> after symbolic links and junctions, or null when it cannot be read.</summary>
    public static string? FinalPath(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return null;
            var text = buffer.ToString();
            if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + text[8..];
            return text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    /// <summary>OriginalFilename and InternalName from the version resource, without extension: they survive a rename of the file.</summary>
    public static IEnumerable<string> RecordedNames(string path)
    {
        FileVersionInfo info;
        try { info = FileVersionInfo.GetVersionInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { yield break; }
        foreach (var recorded in new[] { info.OriginalFilename, info.InternalName })
        {
            if (string.IsNullOrWhiteSpace(recorded)) continue;
            var name = recorded.Trim();
            // "Cmd.Exe", "POWERSHELL.EXE", "wscript.exe.mui": keep what precedes the first extension.
            var dot = name.IndexOf('.');
            yield return dot > 0 ? name[..dot] : name;
        }
    }

    /// <summary>The first refused name among the file name and its recorded names, or null.</summary>
    public static string? RefusedNameOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (IsRefusedName(name)) return name;
        return RecordedNames(path).FirstOrDefault(IsRefusedName);
    }

    /// <summary>Reads the PE header of a file.</summary>
    public static ExecutableImage ReadImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[4096];
            var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return ReadImage(header.AsSpan(0, length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new ExecutableImage(0, false, "the file could not be read (" + ex.Message + ")."); }
    }

    /// <summary>Reads the PE header: MZ, e_lfanew, PE\0\0, the COFF characteristics and the optional header's subsystem.</summary>
    public static ExecutableImage ReadImage(ReadOnlySpan<byte> header)
    {
        const int GuiSubsystem = 2, ConsoleSubsystem = 3;
        if (header.Length < 0x40 || header[0] != (byte)'M' || header[1] != (byte)'Z') return new ExecutableImage(0, false, "it is not a Windows program (no MZ header).");
        var offset = BitConverter.ToInt32(header.Slice(0x3C, 4));
        // Signature (4) + COFF header (20) + the optional header up to and including Subsystem (offset 68, 2 bytes).
        if (offset < 0x40 || offset > header.Length - (4 + 20 + 70)) return new ExecutableImage(0, false, "its program header is truncated or not where Windows expects it.");
        if (header[offset] != (byte)'P' || header[offset + 1] != (byte)'E' || header[offset + 2] != 0 || header[offset + 3] != 0) return new ExecutableImage(0, false, "it is not a Windows program (no PE signature).");
        var characteristics = BitConverter.ToUInt16(header.Slice(offset + 4 + 18, 2));
        var optional = offset + 4 + 20;
        var magic = BitConverter.ToUInt16(header.Slice(optional, 2));
        if (magic is not (0x10B or 0x20B)) return new ExecutableImage(0, false, "its program header is not a 32-bit or 64-bit Windows image.");
        var subsystem = BitConverter.ToUInt16(header.Slice(optional + 68, 2));
        var library = (characteristics & 0x2000) != 0;
        if (library) return new ExecutableImage(subsystem, true, "it is a library (DLL), not a program.");
        return subsystem switch
        {
            GuiSubsystem => new ExecutableImage(subsystem, false, null),
            ConsoleSubsystem => new ExecutableImage(subsystem, false, "it is a console program."),
            _ => new ExecutableImage(subsystem, false, $"its subsystem ({subsystem}) is not the Windows GUI subsystem.")
        };
    }

    /// <summary>
    /// Why an existing local file may not be started as a desktop app, or null when it may: it must be an .exe that is not a refused program
    /// (by name or recorded name) and whose header declares the Windows GUI subsystem. The caller has already checked that the path is local.
    /// </summary>
    public static string? LaunchProblem(string fullPath)
    {
        if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return $"{Path.GetFileName(fullPath)} is not an .exe file.";
        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (IsRefusedName(name)) return RefusedMessage(name);
        if (!File.Exists(fullPath)) return $"{fullPath} does not exist.";
        var image = ReadImage(fullPath);
        if (image.Problem is { } problem) return $"{Path.GetFileName(fullPath)} cannot be started: {problem} Only Windows desktop (GUI) programs are started.";
        foreach (var recorded in RecordedNames(fullPath)) if (IsRefusedName(recorded)) return RefusedMessage(recorded);
        return null;
    }
    public static string RefusedMessage(string name) =>
        $"{name.ToLowerInvariant()} is a shell, script host or system launcher (terminals and program launchers included), and Testy starts only desktop applications (refused: {string.Join(", ", RefusedPrograms)}).";
}
