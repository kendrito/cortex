using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Testy.Core;

namespace Testy.Studio;

/// <summary>
/// Settings › Agent access (MCP): the command any MCP-capable agent on this PC runs to start Testy's server (stdio), the common client JSON, an
/// optional HTTP listener Studio keeps running as a child process (remembered per user and restarted at launch, on a remembered port and with a
/// bearer token of its own), a connection test and the guide. Whatever the agents create lands in the workspace and shows up through the
/// workspace watcher. Nothing here names a particular agent product.
/// </summary>
public partial class MainWindow
{
    private const string AgentSource = "Agent access";
    private static readonly TimeSpan McpStartupTimeout = TimeSpan.FromSeconds(20);
    /// <summary>What the server's own orderly stop may take: 5 s for HTTP requests, 15 s for background runs and 3 s per launched app; the switch waits for it.</summary>
    private static readonly TimeSpan McpStopGrace = TimeSpan.FromSeconds(25), McpCloseGrace = TimeSpan.FromSeconds(5);
    private McpServerCommand? _mcpCommand;
    private McpListener? _mcpListener;
    private bool _mcpSwitching;
    private int _mcpGeneration;
    private Task? _mcpProbe;
    private DateTimeOffset _mcpLogLast = DateTimeOffset.MinValue;

    private void InitializeAgentAccess()
    {
        if (CortexModelBridge.Enabled)
        {
            SetMcpSwitch(false); McpListenToggle.IsEnabled = false; McpPortBox.IsEnabled = false;
            McpCommandBox.Text = "Managed by the Cortex Testy plugin.";
            UpdateMcpStatus("Cortex owns Testy's MCP connection. Use Testy tools in a Cortex chat; separate Studio listeners are unavailable in integrated mode.");
            return;
        }
        _mcpCommand = McpServerCommand.Resolve(_store.RootDirectory);
        McpCommandBox.Text = _mcpCommand?.CommandLine ?? "";
        if (_mcpCommand?.RequiredRuntimeRoot is { } root) McpCommandHelp.Text += $" This development build needs DOTNET_ROOT={root} in the agent's environment; Copy JSON includes it.";
        if (string.IsNullOrEmpty(StudioPreferences.Current.McpToken)) StudioPreferences.Current.McpToken = McpServerCommand.NewToken();
        McpPortBox.Text = StudioPreferences.Current.McpPort.ToString(CultureInfo.InvariantCulture);
        ShowMcpToken();
        SetMcpSwitch(StudioPreferences.Current.McpListen);
        UpdateMcpStatus();
        if (StudioPreferences.Current.McpListen) _ = StartMcpListenerAsync();
    }
    private void SetMcpSwitch(bool on) { _mcpSwitching = true; try { McpListenToggle.IsChecked = on; } finally { _mcpSwitching = false; } }
    /// <summary>The token is a secret: the card shows only its last characters; Copy token copies it whole.</summary>
    private void ShowMcpToken()
    {
        var token = StudioPreferences.Current.McpToken;
        McpTokenBox.Text = token.Length < 4 ? "" : "Bearer token required · ends in " + token[^4..];
    }

    /// <summary>The status line: where the server is and whether Studio is listening, unless a more specific message was just produced.</summary>
    private void UpdateMcpStatus(string? message = null, string severity = "Informational")
    {
        if (message is null)
        {
            if (_mcpCommand is null)
            {
                message = "Testy.Cli.exe was not found next to Testy Studio, so agents have nothing to start. Keep the application files together.";
                severity = "Error";
            }
            else
            {
                var server = Path.GetFileName(_mcpCommand.ServerPath) + (_mcpCommand.IsBundled ? " next to Testy Studio" : " in the development build");
                if (_mcpListener is { } listener) { message = $"Listening for agents at {listener.Url} (server process {listener.ProcessId}, token required). Server: {server}."; severity = "Success"; }
                else message = $"Server found: {server}. Not listening; an agent starts the server itself with the command below.";
            }
        }
        McpStatusText.Text = message; McpStatusIcon.Tag = severity;
        AnnounceLiveRegion(McpStatusText);
    }
    /// <summary>Tells assistive technology that a live region changed (the text changes without user action).</summary>
    private static void AnnounceLiveRegion(UIElement element)
    {
        if (UIElementAutomationPeer.FromElement(element) is { } peer && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void CopyMcpCommand_Click(object sender, RoutedEventArgs e) => CopyAgentText(McpCommandBox.Text, "Command copied. Paste it where your agent lists its MCP servers.");
    private void CopyMcpJson_Click(object sender, RoutedEventArgs e) => CopyAgentText(_mcpCommand?.ClientConfigurationJson ?? "", "Configuration copied: an mcpServers entry named testy with the command and its arguments.");
    private void CopyMcpUrl_Click(object sender, RoutedEventArgs e) => CopyAgentText(McpUrlBox.Text, "Address copied. Agents that connect over HTTP send their requests there, with the token as Authorization: Bearer.");
    private void CopyMcpToken_Click(object sender, RoutedEventArgs e) => CopyAgentText(StudioPreferences.Current.McpToken, "Token copied. Agents send it as Authorization: Bearer with every HTTP request; keep it to yourself.");
    private void CopyMcpHttpJson_Click(object sender, RoutedEventArgs e) =>
        CopyAgentText(McpUrlBox.Text.Length == 0 ? "" : McpServerCommand.HttpConfigurationJson(McpUrlBox.Text, StudioPreferences.Current.McpToken), "Configuration copied: an mcpServers entry named testy with the address and the token as its Authorization header.");
    private void CopyAgentText(string text, string message)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetStatus(_mcpCommand is null ? "There is nothing to copy: the server was not found." : "There is no address yet. Turn on Listen for agents (HTTP) first.", ActivityLevel.Warning, AgentSource);
            return;
        }
        try { Clipboard.SetText(text); SetStatus(message, ActivityLevel.Info, AgentSource); }
        catch (Exception ex) when (ex is COMException or ExternalException) { SetStatus("Could not copy: " + ex.Message, ActivityLevel.Error, AgentSource); }
    }
    /// <summary>The port takes effect the next time the listener starts; 0 lets Windows pick a free one, which is then remembered.</summary>
    private void McpPort_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(McpPortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 0 and <= 65535)
        {
            if (port == StudioPreferences.Current.McpPort) return;
            StudioPreferences.Current.McpPort = port;
            SetStatus(port == 0 ? "The listener picks a free port the next time it starts and remembers it." : $"The listener uses port {port} the next time it starts.", ActivityLevel.Info, AgentSource);
        }
        else
        {
            McpPortBox.Text = StudioPreferences.Current.McpPort.ToString(CultureInfo.InvariantCulture);
            SetStatus("The port must be a number from 0 to 65535 (0 picks a free one).", ActivityLevel.Warning, AgentSource);
        }
    }
    /// <summary>Opens the guide with its associated program; a PC without one for .md files gets the file shown in its folder instead.</summary>
    private void OpenMcpGuide_Click(object sender, RoutedEventArgs e) => TryShell(() =>
    {
        var guide = FindMcpGuide();
        try { OpenPath(guide); SetStatus("Opened " + Path.GetFileName(guide) + ".", ActivityLevel.Info, AgentSource); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + guide + "\"") { UseShellExecute = false });
            SetStatus($"No program opens .md files on this PC, so {Path.GetFileName(guide)} is shown in its folder instead; open it with any text editor.", ActivityLevel.Warning, AgentSource);
        }
    });
    /// <summary>docs\MCP.md (repository or package docs folder), else AGENT-ACCESS.md at the bundle root.</summary>
    internal static string FindMcpGuide()
    {
        try { var guide = Path.Combine(FindDocumentation(), "MCP.md"); if (File.Exists(guide)) return guide; }
        catch (DirectoryNotFoundException) { }
        var bundled = Path.Combine(AppContext.BaseDirectory, "AGENT-ACCESS.md");
        if (File.Exists(bundled)) return bundled;
        throw new FileNotFoundException("The agent guide (docs\\MCP.md, or AGENT-ACCESS.md beside the app) was not found.");
    }

    // ── Listen for agents (HTTP) ──
    private async void McpListen_Changed(object sender, RoutedEventArgs e)
    {
        if (_mcpSwitching) return;
        var on = McpListenToggle.IsChecked == true;
        StudioPreferences.Current.McpListen = on;
        if (on) await StartMcpListenerAsync(); else await StopMcpListenerAsync();
    }
    private async Task StartMcpListenerAsync()
    {
        if (CortexModelBridge.Enabled) { SetMcpSwitch(false); return; }
        if (_mcpListener is not null) return;
        if (_mcpCommand is null) { SetMcpSwitch(false); StudioPreferences.Current.McpListen = false; UpdateMcpStatus(); return; }
        var generation = ++_mcpGeneration;
        UpdateMcpStatus("Starting the server…");
        var token = StudioPreferences.Current.McpToken;
        var port = StudioPreferences.Current.McpPort;
        McpListener listener;
        try
        {
            try { listener = await McpListener.StartAsync(_mcpCommand, line => Dispatcher.BeginInvoke(() => LogServerLine(line)), McpStartupTimeout, port, token); }
            catch (Exception ex) when (port != 0 && ex is IOException or InvalidOperationException or TimeoutException or Win32Exception or JsonException)
            {
                // The remembered port is taken by another program: fall back to a free one and say so, rather than stay silent.
                if (generation != _mcpGeneration) return;
                Log(ActivityLevel.Warning, AgentSource, $"Port {port} could not be used ({ex.Message}); trying a free port instead.");
                listener = await McpListener.StartAsync(_mcpCommand, line => Dispatcher.BeginInvoke(() => LogServerLine(line)), McpStartupTimeout, 0, token);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or Win32Exception or JsonException)
        {
            if (generation != _mcpGeneration) return;
            // The switch stays remembered: a slow or failed start at sign-in must not turn the preference off; the switch is off for this session only.
            SetMcpSwitch(false);
            UpdateMcpStatus("The server could not start: " + ex.Message + " The switch stays on for the next start; turn it on again to retry now.", "Error");
            Log(ActivityLevel.Error, AgentSource, "The MCP server could not start: " + ex.Message);
            return;
        }
        if (generation != _mcpGeneration) { await listener.StopAsync(McpStopGrace); return; } // switched off (or Studio closed) while it started
        _mcpListener = listener;
        listener.Exited += code => Dispatcher.BeginInvoke(() => OnMcpListenerExited(listener, code));
        if (listener.HasExited) { OnMcpListenerExited(listener, -1); return; }
        McpUrlBox.Text = listener.Url;
        if (listener.Startup.Port != StudioPreferences.Current.McpPort)
        {
            StudioPreferences.Current.McpPort = listener.Startup.Port; // picked free: kept for the next start, so an agent's saved address stays valid
            McpPortBox.Text = listener.Startup.Port.ToString(CultureInfo.InvariantCulture);
        }
        UpdateMcpStatus();
        Log(ActivityLevel.Success, AgentSource, $"Listening for agents at {listener.Url} (server process {listener.ProcessId}, token required).");
    }
    private async Task StopMcpListenerAsync()
    {
        ++_mcpGeneration;
        var listener = _mcpListener;
        if (listener is null) { UpdateMcpStatus(); return; }
        _mcpListener = null; McpUrlBox.Text = "";
        UpdateMcpStatus("Stopping the server… (a run in progress gets a moment to finish)");
        Log(ActivityLevel.Info, AgentSource, $"Stopping the listener (server process {listener.ProcessId}).");
        await listener.StopAsync(McpStopGrace);
        if (ReferenceEquals(_mcpListener, null)) UpdateMcpStatus();
    }
    private void OnMcpListenerExited(McpListener listener, int exitCode)
    {
        if (!ReferenceEquals(listener, _mcpListener)) return;
        _mcpListener = null; McpUrlBox.Text = "";
        // For this session only: the remembered switch stays on, so the listener starts again with Studio.
        SetMcpSwitch(false);
        UpdateMcpStatus($"The server stopped on its own (exit code {exitCode}). Turn Listen for agents (HTTP) on to start it again.", "Error");
        Log(ActivityLevel.Error, AgentSource, $"The MCP server stopped on its own (exit code {exitCode}).");
    }
    /// <summary>Closing Studio ends the listener: stdin closed, a bounded wait for its orderly stop, then a kill of the server process only.</summary>
    private void StopAgentAccessBlocking()
    {
        ++_mcpGeneration; // a start still under way stops itself when it completes
        var listener = _mcpListener; _mcpListener = null;
        listener?.StopBlocking(McpCloseGrace);
    }
    /// <summary>The server's stderr: warnings and errors always; ordinary lines at most one every 5 s, so a busy agent doesn't flood the activity log.</summary>
    private void LogServerLine(string line)
    {
        var text = McpStdioSession.TrimLogPrefix(line);
        var split = text.IndexOf(' ');
        var levelWord = split > 0 ? text[..split] : "";
        var level = levelWord switch { "error" => ActivityLevel.Error, "warn" => ActivityLevel.Warning, _ => ActivityLevel.Info };
        if (levelWord is "error" or "warn" or "info") text = text[(split + 1)..];
        var now = DateTimeOffset.Now;
        if (level == ActivityLevel.Info && now - _mcpLogLast < TimeSpan.FromSeconds(5)) return;
        _mcpLogLast = now;
        Log(level, AgentSource, text, detail: line);
    }

    // ── Test the connection ──
    private async void TestMcpConnection_Click(object sender, RoutedEventArgs e)
    {
        if (CortexModelBridge.Enabled) { UpdateMcpStatus("Cortex owns the MCP connection. Check plugin status in Cortex."); return; }
        if (_mcpProbe is { IsCompleted: false }) return;
        if (_mcpCommand is null) { UpdateMcpStatus(); SetStatus(McpStatusText.Text, ActivityLevel.Error, AgentSource); return; }
        UpdateMcpStatus("Starting a server over stdio and asking for its tools…");
        var probe = ProbeMcpServerAsync(_mcpCommand);
        _mcpProbe = probe;
        var (message, ok) = await probe;
        // The result goes to the status line and the activity log; the card returns to what it shows (listening or not).
        SetStatus(message, ok ? ActivityLevel.Success : ActivityLevel.Error, AgentSource);
        if (_mcpListener is not null) UpdateMcpStatus(); else UpdateMcpStatus(message, ok ? "Success" : "Error");
    }
    /// <summary>initialize + tools/list against a fresh stdio server, then its stdin is closed. Off the UI thread; nothing is disabled meanwhile.</summary>
    private static async Task<(string Message, bool Ok)> ProbeMcpServerAsync(McpServerCommand command)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await using var session = McpStdioSession.Start(command);
            var initialized = await session.InitializeAsync(TimeSpan.FromSeconds(20));
            var tools = await session.RequestAsync("tools/list", null, TimeSpan.FromSeconds(20));
            var count = tools.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
            if (count == 0) return ("The server answered, but offered no tools. Reinstall or rebuild Testy; agents would find nothing to call.", false);
            var protocol = initialized.TryGetProperty("protocolVersion", out var version) ? version.GetString() : "?";
            var server = initialized.TryGetProperty("serverInfo", out var info) && info.TryGetProperty("name", out var name)
                ? name.GetString() + (info.TryGetProperty("version", out var serverVersion) ? " " + serverVersion.GetString() : "") : "the server";
            return ($"Testy answered with {count} tools in {watch.Elapsed.TotalSeconds:0.0} s ({server}, protocol {protocol}). Agents can connect.", true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or Win32Exception or JsonException or KeyNotFoundException)
        { return ("No answer from the server: " + ex.Message, false); }
    }
}
