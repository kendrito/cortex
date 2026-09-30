using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Testy.Core;

/// <summary>Current-user Windows credentials, bound to one configured endpoint and key slot.</summary>
public static class ProviderCredentialStore
{
    public static string? Resolve(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (CortexGuestRelay.Enabled) return null;
        if (CortexModelBridge.Enabled) return CortexModelBridge.Token;
        if (settings.Kind is ProviderKind.Codex or ProviderKind.Offline) return null;
        if (OperatingSystem.IsWindows() && TryTarget(settings, out var target))
        {
            var stored = Read(target) ?? Read(Legacy(target));
            if (!string.IsNullOrWhiteSpace(stored)) return stored;
        }
        if (string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariable)) return null;
        var value = Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
            value = Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable, EnvironmentVariableTarget.User);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool HasStored(ProviderSettings settings) => !CortexModelBridge.Enabled && OperatingSystem.IsWindows() && TryTarget(settings, out var target) && (Read(target) ?? Read(Legacy(target))) is not null;
    /// <summary>Where keys were stored before the product was renamed from Axiom; still read, and removed by Delete.</summary>
    internal static string Legacy(string target) => "Axiom" + target["Testy".Length..];

    public static void Save(ProviderSettings settings, string secret)
    {
        if (CortexModelBridge.Enabled) throw new InvalidOperationException("Model credentials are managed in Cortex.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The credential vault requires Windows.");
        if (!TryTarget(settings, out var target)) throw new InvalidDataException("Select an HTTP model provider and a valid HTTPS or loopback endpoint first.");
        if (string.IsNullOrWhiteSpace(secret) || secret.Any(char.IsControl)) throw new InvalidDataException("Enter a nonempty credential without control characters.");
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 2560) throw new InvalidDataException("Credential exceeds the Windows vault size limit.");
        var buffer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var credential = new Credential
            {
                Type = 1, TargetName = target, UserName = "Testy provider", Comment = "Testy model credential; endpoint scoped",
                CredentialBlobSize = (uint)bytes.Length, CredentialBlob = buffer, Persist = 2
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not save the provider credential.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    public static void Delete(ProviderSettings settings)
    {
        if (CortexModelBridge.Enabled) throw new InvalidOperationException("Model credentials are managed in Cortex.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The credential vault requires Windows.");
        if (!TryTarget(settings, out var target)) throw new InvalidDataException("Select a valid provider endpoint first.");
        foreach (var name in new[] { target, Legacy(target) })
            if (!CredDelete(name, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not remove the provider credential.");
    }

    internal static bool TryTarget(ProviderSettings settings, out string target)
    {
        target = "";
        if (settings.Kind is not (ProviderKind.OpenAI or ProviderKind.Compatible) ||
            !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) ||
            !(endpoint.Scheme == "https" || endpoint.Scheme == "http" && endpoint.IsLoopback) ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0) return false;
        // Include path/query and key slot: changing the destination never silently forwards a stored credential.
        target = "Testy/Provider/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri + "\n" + settings.ApiKeyEnvironmentVariable)));
        return true;
    }

    private static string? Read(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 1168 or 1312) return null; // Missing credential or noninteractive logon: environment fallback remains available.
            throw new Win32Exception(error, "Windows could not read the provider credential.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize > 2560 || credential.CredentialBlobSize % 2 != 0) throw new InvalidDataException("Stored provider credential has an invalid size.");
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint buffer);
}
