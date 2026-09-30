using System.Diagnostics;

namespace Testy.Cli;

/// <summary>
/// How every verifier starts Testy Studio: on its own workspace and with the session-only options that keep the person's saved preferences
/// out of the check and untouched by it. Without them a saved "Listen for agents" switch would start an HTTP server on the scratch workspace,
/// and a failed start there could change what the person saved.
/// </summary>
internal static class StudioLaunch
{
    public static ProcessStartInfo StartInfo(string executable, string workspace, bool hidden = true, params string[] extra)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in new[] { "--workspace", workspace, "--technical-details", "off", "--mcp-listen", "off" }.Concat(extra)) start.ArgumentList.Add(argument);
        return start;
    }
}
