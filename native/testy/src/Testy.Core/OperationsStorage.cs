using System.Security.Cryptography;
using System.Text.Json;

namespace Testy.Core;

internal static class OperationsStorage
{
    internal const long MaximumStateBytes = 64 * 1024 * 1024;
    internal static string Root(string path, bool create)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("A local directory is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A volume root or network/device path is not a workspace.");
        NoReparse(full);
        if (create) Directory.CreateDirectory(full);
        NoReparse(full);
        return full;
    }
    internal static void NoReparse(string path)
    {
        var cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse-point workspace paths are unsupported.");
            cursor = Path.GetDirectoryName(cursor);
        }
    }
    internal static string Relative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') || relative.Contains('\0'))
            throw new InvalidDataException("Invalid relative archive/store path.");
        foreach (var part in relative.Split('/'))
        {
            var stem = part.Split('.')[0];
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || "<>\"|?*".Contains(c))
                || new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException("Ambiguous relative archive/store path.");
        }
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes its workspace.");
        NoReparse(path); return path;
    }
    internal static FileStream Lock(string path, TimeSpan? timeout = null)
    {
        var until = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            NoReparse(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTimeOffset.UtcNow < until) { Thread.Sleep(25); }
        }
    }
    internal static T Read<T>(string path)
    {
        NoReparse(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumStateBytes) throw new InvalidDataException("Persisted state is absent or exceeds its size limit.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<T>(file, TestyJson.Options) ?? throw new InvalidDataException("Persisted state is null.");
    }
    internal static void Write<T>(string path, T data)
    {
        NoReparse(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, TestyJson.Options);
        if (bytes.Length > MaximumStateBytes) throw new InvalidDataException("Persisted state exceeds 64 MiB; export/remove terminal records first.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static string Hash<T>(T data) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data, TestyJson.Options)));
}
