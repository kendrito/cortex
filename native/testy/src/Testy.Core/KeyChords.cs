namespace Testy.Core;

/// <summary>
/// The keys and chords a keyPress step may name, and the values a toggle step accepts. Pure data and checks, shared by the validator
/// (so an impossible step is refused when a test is saved) and the Windows input layer (which sends exactly these virtual-key codes).
/// </summary>
public static class KeyChords
{
    public const int MaximumKeys = 4;
    public const string Allowed = "Keys: ENTER, TAB, ESC, SPACE, BACKSPACE, DELETE, INSERT, HOME, END, LEFT, RIGHT, UP, DOWN, PAGEUP, PAGEDOWN, F1–F12, A–Z and 0–9, combined with CTRL+, SHIFT+ or ALT+ (at most 4 keys, for example CTRL+SHIFT+S). " +
        "Windows-key chords, ALT+TAB, ALT+F4, ALT+ESC and CTRL+ESC are refused.";
    public const string ToggleValues = "empty (flip), on/off, true/false, checked/unchecked or 1/0";
    private const ushort Control = 0x11, Alt = 0x12, Tab = 0x09, Escape = 0x1B, F4 = 0x73, Windows = 0x5B;

    /// <summary>The virtual-key code of one key name, or null when the name is not supported. WIN is known only so its chords can be refused by name.</summary>
    public static ushort? Code(string key) => key.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => Control, "SHIFT" => 0x10, "ALT" => Alt,
        "WIN" or "WINDOWS" or "META" or "CMD" => Windows,
        "ENTER" or "RETURN" => 0x0D, "TAB" => Tab, "ESC" or "ESCAPE" => Escape,
        "SPACE" => 0x20, "BACKSPACE" => 0x08, "DELETE" => 0x2E, "HOME" => 0x24, "END" => 0x23,
        "LEFT" or "ARROWLEFT" => 0x25, "UP" or "ARROWUP" => 0x26, "RIGHT" or "ARROWRIGHT" => 0x27, "DOWN" or "ARROWDOWN" => 0x28,
        "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "INSERT" => 0x2D,
        _ when key.Length is 2 or 3 && key.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[1..], out var f) && f is >= 1 and <= 12 => (ushort)(0x6F + f),
        _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToUpperInvariant(key[0]),
        _ => null
    };

    /// <summary>Splits a chord such as CTRL+SHIFT+S into virtual-key codes. False with a message that names the problem and lists what is allowed.</summary>
    public static bool TryParse(string? chord, out ushort[] codes, out string problem)
    {
        codes = []; problem = "";
        var keys = (chord ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (keys.Length is 0 or > MaximumKeys) { problem = $"keyPress value '{chord}' is not supported: name 1 to {MaximumKeys} keys. {Allowed}"; return false; }
        var parsed = new ushort[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            if (Code(keys[index]) is not { } code) { problem = $"keyPress value '{chord}' is not supported: unsupported key '{keys[index]}'. {Allowed}"; return false; }
            parsed[index] = code;
        }
        if (parsed.Contains(Windows) || (parsed.Contains(Alt) && (parsed.Contains(Tab) || parsed.Contains(F4) || parsed.Contains(Escape))) || (parsed.Contains(Control) && parsed.Contains(Escape)))
        { problem = $"keyPress value '{chord}' is not supported: system navigation and close-window chords are refused. {Allowed}"; return false; }
        codes = parsed;
        return true;
    }

    /// <summary>True, false, or null for an empty value (flip). False when the text is none of the accepted spellings.</summary>
    public static bool TryParseToggle(string? value, out bool? desired)
    {
        desired = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "on" or "checked" or "1": desired = true; return true;
            case "false" or "off" or "unchecked" or "0": desired = false; return true;
            default: return false;
        }
    }
}
