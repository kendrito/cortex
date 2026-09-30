using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Testy.Core;

/// <summary>A Hyper-V guest sign-in kept in this Windows user's credential vault (never in files, arguments, reports or logs).
/// The agent uses it for PowerShell Direct; the guest desktop must be signed in with the same account.</summary>
public static class VmCredentialStore
{
    public const int MaximumPasswordChars = 1024;
    public static string TargetName(Guid vmId) => vmId == Guid.Empty ? throw new InvalidDataException("A VM ID is required.") : "Testy/VM/" + vmId.ToString("D");
    /// <summary>Where sign-ins were saved before the product was renamed from Axiom; still read, and removed by Delete.</summary>
    internal static string LegacyTargetName(Guid vmId) => "Axiom/VM/" + TargetName(vmId)["Testy/VM/".Length..];

    public static void Save(Guid vmId, string userName, string password) => Save(TargetName(vmId), userName, password);
    internal static void Save(string target, string userName, string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The credential vault requires Windows.");
        userName = (userName ?? "").Trim();
        if (userName.Length is 0 or > 256 || userName.Any(char.IsControl) || userName.Contains('/')) throw new InvalidDataException("Enter the guest account, for example Administrator or MACHINE\\User.");
        if (string.IsNullOrEmpty(password) || password.Length > MaximumPasswordChars || password.Any(c => c == '\0' || c == '\r' || c == '\n'))
            throw new InvalidDataException("Enter the guest password (no line breaks, at most 1024 characters).");
        var bytes = Encoding.Unicode.GetBytes(password);
        var buffer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var credential = new Credential
            {
                Type = 1, TargetName = target, UserName = userName, Comment = "Testy Hyper-V guest sign-in; PowerShell Direct only",
                CredentialBlobSize = (uint)bytes.Length, CredentialBlob = buffer, Persist = 2
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not save the VM credential.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>Returns only the account name; the password is not read.</summary>
    public static string? StoredUser(Guid vmId) => Read(vmId, includeSecret: false)?.UserName;

    /// <summary>Returns the account and password for an immediate PowerShell Direct hand-off. Callers must not persist or log the password.</summary>
    public static (string UserName, string Password)? Read(Guid vmId)
    {
        var value = Read(vmId, includeSecret: true);
        return value is null ? null : (value.Value.UserName, value.Value.Password!);
    }

    public static void Delete(Guid vmId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The credential vault requires Windows.");
        foreach (var target in new[] { TargetName(vmId), LegacyTargetName(vmId) })
            if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not remove the VM credential.");
    }

    private static (string UserName, string? Password)? Read(Guid vmId, bool includeSecret) =>
        Read(TargetName(vmId), includeSecret) ?? Read(LegacyTargetName(vmId), includeSecret);
    private static (string UserName, string? Password)? Read(string target, bool includeSecret)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 1168 or 1312) return null;
            throw new Win32Exception(error, "Windows could not read the VM credential.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize > MaximumPasswordChars * 2 || credential.CredentialBlobSize % 2 != 0) throw new InvalidDataException("Stored VM credential has an invalid size.");
            return (credential.UserName ?? "", includeSecret ? Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2) : null);
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
