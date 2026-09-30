using System.Windows;
using Testy.WpfProbe;
namespace Testy.WpfLab;
public partial class App : Application
{
    private ProbeServer? probe;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); probe = ProbeServer.Start();
        MainWindow = e.Args.Contains("--grid-editors", StringComparer.Ordinal) ? new GridEditorsWindow()
            : e.Args.Contains("--capture-lab", StringComparer.Ordinal) ? new CaptureLabWindow()
            : e.Args.Contains("--grid-workflows", StringComparer.Ordinal)
            ? new GridWorkflowWindow()
            : new LabWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { probe?.Dispose(); base.OnExit(e); }
}
