namespace Testy.Core;

public static class CodexExecutableResolver
{
    public static string Resolve(string? configuredExecutable, string processPath, string localApplicationData)
    {
        var executable = string.IsNullOrWhiteSpace(configuredExecutable) ? "codex.exe" : configuredExecutable;
        if (!string.Equals(executable, "codex.exe", StringComparison.OrdinalIgnoreCase)) return executable;

        // Explorer may not inherit the Codex desktop app's process-only PATH entry.
        foreach (var entry in processPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0) continue;
            try
            {
                var candidate = Path.Combine(directory, "codex.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }

        string? latest = null;
        var latestWrite = DateTime.MinValue;
        void Consider(string candidate)
        {
            try
            {
                if (!File.Exists(candidate)) return;
                var written = File.GetLastWriteTimeUtc(candidate);
                if (latest == null || written > latestWrite)
                { latest = Path.GetFullPath(candidate); latestWrite = written; }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }

        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            try
            {
                var bin = Path.Combine(localApplicationData, "OpenAI", "Codex", "bin");
                Consider(Path.Combine(bin, "codex.exe"));
                if (Directory.Exists(bin))
                    foreach (var versionDirectory in Directory.EnumerateDirectories(bin, "*", SearchOption.TopDirectoryOnly))
                        Consider(Path.Combine(versionDirectory, "codex.exe"));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }
        return latest ?? "codex.exe";
    }
}
