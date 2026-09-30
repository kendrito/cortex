using Testy.Core;

namespace Testy.Cli.HyperV;

/// <summary>`vms` and `vm-run` verbs. Hyper-V access requires an elevated process (the background agent is one).</summary>
internal static class VmCommand
{
    public static async Task<(object Result, int ExitCode)> ExecuteAsync(string operation, IReadOnlyDictionary<string, string?> options, CancellationToken ct)
    {
        Guid Vm() => Guid.TryParse(options.GetValueOrDefault("vm"), out var id) && id != Guid.Empty ? id : throw new ArgumentException("--vm requires the Hyper-V VM ID (GUID).");
        switch (operation)
        {
            case "list":
            {
                var inventory = await VmRunner.InventoryAsync(options.ContainsKey("deep"), ct);
                return (inventory, inventory.HyperVAvailable ? 0 : 2);
            }
            case "check":
            {
                var readiness = await VmRunner.CheckAsync(Vm(), ct);
                return (readiness, readiness.Ready ? 0 : 1);
            }
            case "setup":
            {
                var result = await VmRunner.SetupAsync(Vm(), ct);
                var readiness = VmRunner.ReadinessFrom(result);
                bool ok = HyperV.HyperVBridge.String(result, "status") == "ok";
                return (new { completed = ok, message = ok ? "VM set up: sign-in verified and the Testy worker is staged." : HyperV.HyperVBridge.String(result, "message") ?? "Setup did not complete.", readiness, bridge = result }, ok ? 0 : 1);
            }
            case "set-credential":
            {
                // Password arrives on standard input (one line); it is never an argument or a log entry.
                var user = options.GetValueOrDefault("username") ?? throw new ArgumentException("--username is required.");
                var password = ReadSecretLine();
                if (string.IsNullOrEmpty(password)) throw new ArgumentException("Supply the guest password as one UTF-8 line on standard input.");
                VmCredentialStore.Save(Vm(), user, password);
                return (new { completed = true, vmId = Vm(), user, message = "Guest sign-in stored in Windows Credential Manager for this Windows user." }, 0);
            }
            case "forget":
            {
                VmCredentialStore.Delete(Vm());
                return (new { completed = true, vmId = Vm(), message = "Stored guest sign-in removed. Files already staged inside the VM are unchanged." }, 0);
            }
            default: throw new ArgumentException("vms operations: list [--deep] | check --vm ID | setup --vm ID | set-credential --vm ID --username USER (password on stdin) | forget --vm ID");
        }
    }

    /// <summary>One UTF-8 line from raw standard input. Writers such as Windows PowerShell may prepend a byte-order mark, which is not part of the secret.</summary>
    internal static string? ReadSecretLine()
    {
        using var input = Console.OpenStandardInput();
        var bytes = new List<byte>(256); int value;
        while ((value = input.ReadByte()) >= 0 && value != '\n') { bytes.Add((byte)value); if (bytes.Count > 16 * 1024) throw new ArgumentException("Secret line is too long."); }
        int start = bytes.Count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        int end = bytes.Count; if (end > start && bytes[end - 1] == '\r') end--;
        if (end <= start) return value < 0 && bytes.Count == 0 ? null : "";
        var text = new System.Text.UTF8Encoding(false, true).GetString(bytes.GetRange(start, end - start).ToArray());
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes));
        return text.TrimStart('﻿');
    }

    public static async Task<VmRunResult> RunAsync(IReadOnlyDictionary<string, string?> options, string artifacts, CancellationToken ct)
    {
        var vm = Guid.TryParse(options.GetValueOrDefault("vm"), out var id) && id != Guid.Empty ? id : throw new ArgumentException("--vm requires the Hyper-V VM ID (GUID).");
        var request = await OperationsCommand.LoadTestRequestAsync(options.ToDictionary(x => x.Key, x => x.Value ?? "true"), OperationsTarget.ForVirtualMachine(vm), ct);
        return await VmRunner.RunAsync(vm, request, artifacts, ct, (stage, message) => Console.Error.WriteLine($"[{stage}] {message}"));
    }
}
