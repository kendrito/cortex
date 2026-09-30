using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using Testy.Core;

namespace Testy.Studio;

/// <summary>
/// Per-user Studio preferences, stored in %LOCALAPPDATA%\Testy\studio-user.json (not in a workspace).
/// <see cref="ShowTechnicalDetails"/> is the single app-wide flag for technical UI: XAML binds to it with
/// <c>{Binding ShowTechnicalDetails, Source={x:Static local:StudioPreferences.Current}}</c>; code subscribes to PropertyChanged.
/// </summary>
public sealed class StudioPreferences : INotifyPropertyChanged
{
    public static StudioPreferences Current { get; } = Load();
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "studio-user.json");
    private bool showTechnicalDetails, gettingStartedDismissed, mcpListen;
    private int mcpPort;
    private string mcpToken = "";
    private bool persist = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Shows selectors, IDs, process IDs, the JSON editor and raw command results. Off by default.</summary>
    public bool ShowTechnicalDetails
    {
        get => showTechnicalDetails;
        set
        {
            if (showTechnicalDetails == value) return;
            showTechnicalDetails = value; Save();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowTechnicalDetails)));
        }
    }

    /// <summary>The user closed the getting-started InfoBar on the Tests page. Remembered per Windows user.</summary>
    public bool GettingStartedDismissed
    {
        get => gettingStartedDismissed;
        set
        {
            if (gettingStartedDismissed == value) return;
            gettingStartedDismissed = value; Save();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GettingStartedDismissed)));
        }
    }

    /// <summary>Settings › Agent access: Studio starts the MCP server's HTTP listener at launch and keeps it running. Off by default; remembered per Windows user.</summary>
    public bool McpListen
    {
        get => mcpListen;
        set
        {
            if (mcpListen == value) return;
            mcpListen = value; Save();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(McpListen)));
        }
    }

    /// <summary>
    /// The loopback port the HTTP listener uses, so an agent configured with the address finds it again after a restart. 0 until the first start
    /// picked a free port, which is then kept; the card lets it be changed. Remembered per Windows user.
    /// </summary>
    public int McpPort
    {
        get => mcpPort;
        set
        {
            if (mcpPort == value || value is < 0 or > 65535) return;
            mcpPort = value; Save();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(McpPort)));
        }
    }

    /// <summary>The bearer token the HTTP listener requires, generated once per Windows user and handed to the server through its environment.</summary>
    public string McpToken
    {
        get => mcpToken;
        set
        {
            if (mcpToken == value) return;
            mcpToken = value; Save();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(McpToken)));
        }
    }

    /// <summary>The hidden --technical-details on|off option applies to this session only; nothing is saved in that session.</summary>
    internal void OverrideForSession(bool show) { persist = false; ShowTechnicalDetails = show; }
    /// <summary>The hidden --mcp-listen on|off option: the listener switch for this session only; nothing is saved in that session (the verifiers use off).</summary>
    internal void OverrideMcpListenForSession(bool listen) { persist = false; McpListen = listen; }
    /// <summary>The hidden --mcp-port N and --mcp-token T options (verifiers): the listener's endpoint for this session only; nothing is saved.</summary>
    internal void OverrideMcpEndpointForSession(int? port, string? token) { persist = false; if (port is { } p) McpPort = p; if (token is not null) McpToken = token; }
    /// <summary>Screenshot mode changes switches for its captures; none of them is saved as a preference.</summary>
    internal void DisablePersistenceForSession() => persist = false;

    private static StudioPreferences Load()
    {
        var preferences = new StudioPreferences();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<StoredPreferences>(File.ReadAllText(FilePath), TestyJson.Options) is { } stored)
            {
                preferences.showTechnicalDetails = stored.ShowTechnicalDetails;
                preferences.gettingStartedDismissed = stored.GettingStartedDismissed;
                preferences.mcpListen = stored.McpListen;
                preferences.mcpPort = stored.McpPort is >= 0 and <= 65535 ? stored.McpPort : 0;
                preferences.mcpToken = stored.McpToken ?? "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { } // Preferences are a convenience; defaults apply.
        return preferences;
    }

    private void Save()
    {
        if (!persist) return;
        try { WorkspaceStore.WriteAtomic(FilePath, new StoredPreferences { ShowTechnicalDetails = showTechnicalDetails, GettingStartedDismissed = gettingStartedDismissed, McpListen = mcpListen, McpPort = mcpPort, McpToken = mcpToken }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class StoredPreferences
    {
        public bool ShowTechnicalDetails { get; set; }
        public bool GettingStartedDismissed { get; set; }
        public bool McpListen { get; set; }
        public int McpPort { get; set; }
        public string? McpToken { get; set; }
    }
}

/// <summary>Local, friendly times ("Today 14:05", "Yesterday 09:12", "Mon 14:05", "28 Sep 14:05"). ConverterParameter "full" gives the full local date and time for tooltips. No UTC in the UI.</summary>
public sealed class FriendlyTimeConverter : IValueConverter
{
    public static string Format(DateTimeOffset time, bool full = false)
    {
        var culture = CultureInfo.CurrentCulture; var local = time.ToLocalTime(); var today = DateTime.Today;
        if (full) return local.ToString("dddd, d MMMM yyyy, ", culture) + local.ToString("T", culture);
        var clock = local.ToString("t", culture);
        var days = (local.Date - today).Days;
        return days switch
        {
            0 => "Today " + clock,
            -1 => "Yesterday " + clock,
            1 => "Tomorrow " + clock,
            > -7 and < 0 => local.ToString("ddd ", culture) + clock,
            _ when local.Year == today.Year => local.ToString("d MMM ", culture) + clock,
            _ => local.ToString("d MMM yyyy", culture)
        };
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset time => Format(time, parameter as string == "full"),
        DateTime time => Format(new DateTimeOffset(time), parameter as string == "full"),
        _ => ""
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
