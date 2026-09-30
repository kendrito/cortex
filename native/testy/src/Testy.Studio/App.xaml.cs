using System.Windows;
namespace Testy.Studio;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Hidden options for screenshots, reviews and the verifiers: --theme system|light|dark (App.xaml defaults to System),
        // --technical-details on|off and --mcp-listen on|off, which apply to this session only.
        if (Option(e.Args, "--theme") is { } theme)
        {
#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 9; it selects the Fluent light, dark or system theme.
            ThemeMode = theme.ToLowerInvariant() switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
        }
        if (Option(e.Args, "--technical-details") is { } details) StudioPreferences.Current.OverrideForSession(details.Equals("on", StringComparison.OrdinalIgnoreCase));
        if (Option(e.Args, "--mcp-listen") is { } listen) StudioPreferences.Current.OverrideMcpListenForSession(listen.Equals("on", StringComparison.OrdinalIgnoreCase));
        // --mcp-port N and --mcp-token T (verifiers): the listener's endpoint for this session, so a check can reach it without reading the clipboard.
        var port = Option(e.Args, "--mcp-port") is { } portText && int.TryParse(portText, out var parsed) && parsed is >= 0 and <= 65535 ? parsed : (int?)null;
        var token = Option(e.Args, "--mcp-token");
        if (port is not null || token is not null) StudioPreferences.Current.OverrideMcpEndpointForSession(port, token);
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Testy · Action could not finish", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
    }

    internal static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
