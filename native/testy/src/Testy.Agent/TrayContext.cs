using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Testy.Core;
using Microsoft.Win32;

namespace Testy.Agent;

/// <summary>Tray icon and menu for the background agent. All state changes marshal onto the UI thread.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AgentHost host;
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem header = new("Testy Agent — starting") { Enabled = false };
    private readonly ToolStripMenuItem pause = new("Pause jobs");
    private readonly ToolStripMenuItem machinesMenu = new("Machines");
    private readonly SynchronizationContext ui;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private string lastState = "";

    public TrayContext(AgentHost host)
    {
        this.host = host;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Testy Studio", null, (_, _) => OpenStudio()));
        menu.Items.Add(new ToolStripMenuItem("Run queued jobs now", null, (_, _) => host.ProcessNow()));
        pause.Click += (_, _) => host.SetPaused(!AgentFiles.LoadSettings(host.Workspace).Paused);
        menu.Items.Add(pause);
        machinesMenu.DropDownItems.Add(new ToolStripMenuItem("Refresh", null, (_, _) => host.RequestMachineRefresh()));
        menu.Items.Add(machinesMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open agent logs", null, (_, _) => Shell(host.Log.Folder)));
        menu.Items.Add(new ToolStripMenuItem("Exit agent", null, (_, _) => ExitThread()));
        menu.Opening += (_, _) => RefreshMachinesMenu();
        icon = new NotifyIcon { Icon = Icons.For("starting"), Text = "Testy Agent — starting", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => OpenStudio();
        host.StatusChanged += status => ui.Post(_ => Update(status), null);
        host.JobFinished += notice => ui.Post(_ => Notify(notice), null);
        host.ShutdownRequested += () => ui.Post(_ => ExitThread(), null);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;
        loop = Task.Run(() => host.RunAsync(stop.Token));
    }

    private void Update(AgentStatus status)
    {
        header.Text = "Testy Agent — " + Describe(status.State);
        pause.Text = status.Paused ? "Resume jobs" : "Pause jobs";
        var tip = "Testy Agent: " + Describe(status.State);
        icon.Text = tip.Length > 120 ? tip[..120] : tip;
        if (status.State != lastState) { icon.Icon = Icons.For(status.State); lastState = status.State; }
    }
    private static string Describe(string state) => state switch
    {
        "idle" => "idle", "running" => "running a test", "waiting" => "waiting", "paused" => "paused", "error" => "needs attention", "stopping" => "stopping", _ => "starting"
    };

    private void Notify(AgentJobNotice notice)
    {
        var title = notice.Status == OperationsJobStatus.Passed ? "Testy test passed" : "Testy test " + notice.Status.ToString().ToLowerInvariant();
        var text = notice.Name + " · " + OperationsTargets.Label(notice.Target);
        if (notice.Status != OperationsJobStatus.Passed && notice.Message.Length > 0) text += Environment.NewLine + (notice.Message.Length > 160 ? notice.Message[..160] + "…" : notice.Message);
        icon.ShowBalloonTip(6000, title, text, notice.Status == OperationsJobStatus.Passed ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void RefreshMachinesMenu()
    {
        while (machinesMenu.DropDownItems.Count > 1) machinesMenu.DropDownItems.RemoveAt(1);
        var machines = host.Machines;
        if (machines.Count == 0) { machinesMenu.DropDownItems.Add(new ToolStripMenuItem(host.Elevated ? "No Hyper-V VMs found yet" : "Hyper-V needs the elevated agent") { Enabled = false }); return; }
        foreach (var machine in machines)
        {
            var readiness = machine.HostReadiness();
            var detail = !machine.Running ? machine.State : !machine.CredentialStored ? "not set up" : machine.Readiness is { Checked: true } r ? (r.Ready ? "ready" : "not ready") : "set up";
            machinesMenu.DropDownItems.Add(new ToolStripMenuItem($"{machine.Name} — {detail}") { Enabled = false, ToolTipText = readiness.Reason });
        }
    }

    private void OpenStudio()
    {
        // The agent is elevated; Studio must start with the signed-in user's normal rights. Explorer launches it un-elevated.
        var studio = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Testy", "Testy.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Testy.lnk"), Path.Combine(AppContext.BaseDirectory, "Testy.Studio.exe") }
            .FirstOrDefault(File.Exists);
        if (studio is null) { icon.ShowBalloonTip(4000, "Testy Agent", "Testy Studio was not found.", ToolTipIcon.Warning); return; }
        Shell(studio);
    }
    private static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { ArgumentList = { path }, UseShellExecute = false }); }
        catch (Exception) { }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    { if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect or SessionSwitchReason.SessionLogon) host.Wake(); }
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) { host.CancelRunningPumps("Windows is signing out or shutting down."); ExitThread(); }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.SessionEnding -= OnSessionEnding;
        stop.Cancel();
        try { loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        icon.Visible = false; icon.Dispose(); menu.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>
/// Tray icons: the Testy app icon (Studio's Assets/Testy.ico, embedded as "Testy.ico") at the small-icon size, with a status dot while the
/// agent is running a test (blue), waiting (amber), paused or stopping (gray) or needs attention (red). If the app icon can't be loaded,
/// a drawn "T" in the state's color is used instead.
/// </summary>
internal static class Icons
{
    private static readonly Dictionary<string, Icon> Cache = [];
    public static Icon For(string state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;
        var icon = FromAppIcon(state) ?? Drawn(state);
        Cache[state] = icon;
        return icon;
    }

    private static Icon? FromAppIcon(string state)
    {
        try
        {
            using var stream = typeof(Icons).Assembly.GetManifestResourceStream("Testy.ico");
            if (stream is null) return null;
            var app = new Icon(stream, SystemInformation.SmallIconSize);
            Color? dot = state switch
            {
                "running" => Color.FromArgb(0x3B, 0x8E, 0xEA), "waiting" => Color.FromArgb(0xE8, 0xA3, 0x17), "paused" or "stopping" => Color.FromArgb(0x9A, 0xA0, 0xA6),
                "error" => Color.FromArgb(0xE0, 0x44, 0x3E), _ => null
            };
            if (dot is not { } color) return app;
            using (app)
            {
                using var bitmap = app.ToBitmap();
                var size = bitmap.Width;
                var diameter = (int)Math.Round(size * 0.375); // 6 px at 16, 9 at 24: clear of the check
                var ring = size >= 32 ? 2 : 1;
                var outer = diameter + 2 * ring;
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var edge = new SolidBrush(Color.FromArgb(0x23, 0x26, 0x2A)); // the tile's graphite, so the dot stands apart from the mark
                    using var fill = new SolidBrush(color);
                    graphics.FillEllipse(edge, size - outer, size - outer, outer, outer);
                    graphics.FillEllipse(fill, size - outer + ring, size - outer + ring, diameter, diameter);
                }
                return FromBitmap(bitmap);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException or InvalidOperationException) { return null; }
    }

    private static Icon Drawn(string state)
    {
        var color = state switch
        {
            "idle" => Color.FromArgb(0x1F, 0x7A, 0x5C), "running" => Color.FromArgb(0x1F, 0x5F, 0xA8), "waiting" => Color.FromArgb(0xB7, 0x79, 0x1F),
            "paused" => Color.FromArgb(0x6B, 0x72, 0x80), "error" => Color.FromArgb(0xB4, 0x2B, 0x2B), _ => Color.FromArgb(0x4B, 0x55, 0x63)
        };
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            using var fill = new SolidBrush(color); graphics.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("T", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        }
        return FromBitmap(bitmap);
    }

    private static Icon FromBitmap(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
