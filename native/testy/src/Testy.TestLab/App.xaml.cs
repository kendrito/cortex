using System.Windows;
using Testy.WpfProbe;
namespace Testy.TestLab;
public partial class App : Application
{
    private ProbeServer? probe;
    protected override void OnStartup(StartupEventArgs e) { base.OnStartup(e); probe = ProbeServer.Start(); }
    protected override void OnExit(ExitEventArgs e) { probe?.Dispose(); base.OnExit(e); }
}
