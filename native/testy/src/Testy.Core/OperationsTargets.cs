namespace Testy.Core;

public enum OperationsTargetKind { Local, VirtualMachine }

/// <summary>Where a queued job executes. Only the canonical spelling is accepted in a frozen request, so one target never has two queue identities.</summary>
public readonly record struct OperationsTarget(OperationsTargetKind Kind, Guid VirtualMachineId)
{
    public static OperationsTarget Local { get; } = new(OperationsTargetKind.Local, Guid.Empty);
    public static OperationsTarget ForVirtualMachine(Guid id) => id == Guid.Empty ? throw new InvalidDataException("A VM target requires a nonempty Hyper-V VM ID.") : new(OperationsTargetKind.VirtualMachine, id);
    public bool IsLocal => Kind == OperationsTargetKind.Local;
    public string Canonical => IsLocal ? OperationsTargets.LocalName : OperationsTargets.VirtualMachinePrefix + VirtualMachineId.ToString("D");
    public override string ToString() => Canonical;
}

public static class OperationsTargets
{
    public const string LocalName = "local", VirtualMachinePrefix = "vm:";
    /// <summary>Null, empty or "local" is the local desktop; "vm:&lt;guid&gt;" is a VM. Other spellings are rejected.</summary>
    public static OperationsTarget Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == LocalName) return OperationsTarget.Local;
        if (value.StartsWith(VirtualMachinePrefix, StringComparison.Ordinal) && Guid.TryParseExact(value[VirtualMachinePrefix.Length..], "D", out var id))
            return OperationsTarget.ForVirtualMachine(id);
        throw new InvalidDataException("Target must be \"local\" or \"vm:<Hyper-V VM GUID>\".");
    }
    /// <summary>Accepts user input such as "VM:{GUID}" and returns the only spelling a frozen request may store.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals(LocalName, StringComparison.OrdinalIgnoreCase)) return LocalName;
        var text = value.Trim();
        if (text.StartsWith(VirtualMachinePrefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(text[VirtualMachinePrefix.Length..], out var id))
            return OperationsTarget.ForVirtualMachine(id).Canonical;
        throw new InvalidDataException("Target must be \"local\" or \"vm:<Hyper-V VM GUID>\".");
    }
    public static bool IsCanonical(string? value) => value is null || value == LocalName || (value.StartsWith(VirtualMachinePrefix, StringComparison.Ordinal) && Parse(value).Canonical == value);
    public static string Label(string? value)
    {
        try { var target = Parse(value); return target.IsLocal ? "This PC" : "VM " + target.VirtualMachineId.ToString("D")[..8]; }
        catch (InvalidDataException) { return "Invalid target"; }
    }
}
