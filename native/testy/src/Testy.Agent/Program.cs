using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Agent;

/// <summary>Testy background agent.
///   run [--workspace DIR]                  tray agent (the sign-in task uses this)
///   install [--workspace DIR] [--no-start] copy this sealed bundle to Program Files and start it at sign-in (asks for UAC)
///   uninstall [--workspace DIR]            stop starting at sign-in (asks for UAC)
///   status [--workspace DIR]               print the current heartbeat</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "run";
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = command == "run" && (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) ? 0 : 1; i < args.Length; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : throw new ArgumentException("Unexpected argument: " + args[i]);
            if (name is "no-start") { options[name] = null; continue; }
            if (name is not "workspace" || ++i >= args.Length) return Fail("Usage: Testy.Agent.exe [run|install|uninstall|status] [--workspace DIR] [--no-start]");
            options[name] = args[i];
        }
        var workspace = Path.GetFullPath(options.GetValueOrDefault("workspace") ?? AgentFiles.DefaultWorkspace);
        switch (command)
        {
            case "run": return Run(workspace);
            case "install":
                return AgentInstaller.Elevated ? AgentInstaller.Install(workspace, !options.ContainsKey("no-start"))
                    : AgentInstaller.RunElevated(["install", "--workspace", workspace, .. options.ContainsKey("no-start") ? new[] { "--no-start" } : []]);
            case "uninstall":
                return AgentInstaller.Elevated ? AgentInstaller.Uninstall(workspace) : AgentInstaller.RunElevated(["uninstall", "--workspace", workspace]);
            case "status":
            {
                var status = AgentFiles.TryRead<AgentStatus>(AgentFiles.StatusPath(workspace));
                Print(JsonSerializer.Serialize(new { running = AgentFiles.IsRunning(status), status }, TestyJson.Options));
                return AgentFiles.IsRunning(status) ? 0 : 1;
            }
            default: return Fail("Unknown command. Use run, install, uninstall or status.");
        }
    }

    private static int Run(string workspace)
    {
        var name = @"Global\Testy.Agent." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(workspace).ToUpperInvariant())))[..16];
        Mutex? mutex;
        try { mutex = new Mutex(true, name, out bool created); if (!created) { mutex.Dispose(); return 3; } }
        catch (UnauthorizedAccessException) { return 3; } // An elevated instance owns this workspace.
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var log = new AgentLog(AgentFiles.LogsDirectory(workspace));
            AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled agent exception.", e.ExceptionObject as Exception);
            Application.Run(new TrayContext(new AgentHost(workspace, log)));
            return 0;
        }
        finally { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    internal static void Print(string text)
    {
        // A GUI-subsystem program has no console; attach to the caller's when started from a terminal.
        if (AttachConsole(-1)) { try { using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }; output.WriteLine(); output.WriteLine(text); } catch (IOException) { } }
    }
    private static int Fail(string message) { Print(message); return 2; }
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
}
