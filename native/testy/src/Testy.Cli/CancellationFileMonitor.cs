namespace Testy.Cli;

internal sealed class CancellationFileMonitor : IDisposable
{
    private readonly Timer timer;
    internal CancellationFileMonitor(string path, CancellationTokenSource cancellation)
    {
        void Check(object? state)
        {
            try { if (File.Exists(path)) cancellation.Cancel(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
        }
        Check(null);
        timer = new Timer(Check, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(150));
    }
    public void Dispose() => timer.Dispose();
}
