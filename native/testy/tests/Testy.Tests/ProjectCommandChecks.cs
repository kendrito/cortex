using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class ProjectCommandChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("project commands preserve literal Windows arguments and closed stdin", LiteralArguments);
        yield return ("project commands drain both large pipes with bounded retained output", LargeOutput);
        yield return ("project commands preserve nonzero exit failure and stderr", FailedExit);
        yield return ("project command timeout terminates owned descendants", TimeoutTree);
        yield return ("project command cancellation terminates owned descendants", CancelTree);
        yield return ("project command normal exit also cleans up sleeping descendants", CompletedTree);
        yield return ("project commands scrub inherited credentials and redact known output secrets", CredentialBoundary);
        yield return ("project commands redact bare echoed configured secret arguments", ArgumentSecret);
        yield return ("project command path preflight and prior cancellation launch nothing", Preflight);
    }
    private static (string Executable, List<string> Arguments) Self()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test host executable.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) arguments.Add(typeof(ProjectCommandChecks).Assembly.Location);
        arguments.Add("--project-command-helper");
        return (executable, arguments);
    }
    private static ProjectCommandDefinition Command(string mode, params string[] values)
    {
        var self = Self(); self.Arguments.Add(mode); self.Arguments.AddRange(values);
        return new() { Id = "owned-helper", Executable = self.Executable, Arguments = self.Arguments, TimeoutSeconds = 5 };
    }
    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Testy-command-checks-" + Guid.NewGuid().ToString("N"));
        internal Folder() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Finalized(ProjectCommandExecution result) => Check(result.FinishedAt >= result.StartedAt && result.CleanupComplete, "Command was not finalized with verified cleanup: " + result.Message);
    private static async Task LiteralArguments()
    {
        using var folder = new Folder();
        string[] expected = ["a b", "quote \"x\"", "C:\\space folder\\", "\\\"", "", "東京 Zoë 😀", "& echo changed > should-not-exist.txt"];
        var command = Command("arguments", expected);
        var pending = ProjectCommandExecutor.ExecuteAsync(command, folder.Root);
        command.Arguments.Clear(); // Mutation of the host's configuration object must not alter a launched command.
        var result = await pending; Finalized(result);
        Check(result.Status == ProjectCommandStatus.Completed && result.ExitCode == 0, result.Message);
        using var json = JsonDocument.Parse(result.Stdout);
        Check(json.RootElement.GetProperty("arguments").EnumerateArray().Select(e => e.GetString()).SequenceEqual(expected), "Literal arguments were reinterpreted or changed.");
        Check(json.RootElement.GetProperty("stdinClosed").GetBoolean() && !File.Exists(Path.Combine(folder.Root, "should-not-exist.txt")), "Command unexpectedly received interactive stdin or shell parsing.");
    }
    private static async Task LargeOutput()
    {
        using var folder = new Folder();
        var result = await ProjectCommandExecutor.ExecuteAsync(Command("large"), folder.Root); Finalized(result);
        Check(result.Status == ProjectCommandStatus.Completed && result.ExitCode == 0 && result.Truncated, "Large concurrent stdout/stderr deadlocked or lost truncation state: " + result.Message);
        Check(result.Stdout.Length <= 65536 && result.Stderr.Length <= 65536 && result.Stdout.Contains("withheld") && result.Stderr.Contains("withheld"), "Oversized output was not bounded and conservatively redacted.");
    }
    private static async Task FailedExit()
    {
        using var folder = new Folder(); var result = await ProjectCommandExecutor.ExecuteAsync(Command("fail"), folder.Root); Finalized(result);
        Check(result.Status == ProjectCommandStatus.Failed && result.ExitCode == 23 && result.Stderr.Contains("owned failure detail"), "Nonzero exit code or useful stderr was lost.");
    }
    private static async Task TimeoutTree() => await Tree(ProjectCommandStatus.TimedOut, false);
    private static async Task CancelTree() => await Tree(ProjectCommandStatus.Cancelled, false);
    private static async Task CompletedTree() => await Tree(ProjectCommandStatus.Completed, true);
    private static async Task Tree(ProjectCommandStatus expected, bool parentCompletes)
    {
        using var folder = new Folder();
        string record = Path.Combine(folder.Root, "child.json");
        var command = Command(parentCompletes ? "tree-exit" : "tree-wait", record);
        command.TimeoutSeconds = expected == ProjectCommandStatus.TimedOut ? 1 : 5;
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var pending = ProjectCommandExecutor.ExecuteAsync(command, folder.Root, cancel.Token);
        if (expected == ProjectCommandStatus.Cancelled)
        {
            while (!File.Exists(record) && !pending.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            cancel.Cancel();
        }
        var result = await pending; Finalized(result);
        Check(result.Status == expected && watch.Elapsed < TimeSpan.FromSeconds(6), "Unexpected outcome or unbounded process-tree stop: " + result.Status + " " + result.Message);
        Check(File.Exists(record), "Sleeping descendant never started; cleanup scenario was not exercised.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(record));
        int childPid = json.RootElement.GetProperty("pid").GetInt32();
        DateTime start = json.RootElement.GetProperty("startTimeUtc").GetDateTime();
        Check(OwnedExited(childPid, start), "Owned descendant survived job cleanup.");
        Check(result.ProcessId is not null, "No owned parent identity was recorded.");
    }
    private static bool OwnedExited(int pid, DateTime start)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime() != start || process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    private static async Task CredentialBoundary()
    {
        using var folder = new Folder();
        string[] names = ["TESTY_TEST_API_KEY", "AWS_SECRET_ACCESS_KEY", "OPENAI_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS", "TESTY_CUSTOM_VALUE"];
        var old = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        string secret = "testy-owned-credential-" + Guid.NewGuid().ToString("N");
        try
        {
            foreach (string name in names) Environment.SetEnvironmentVariable(name, secret);
            var result = await ProjectCommandExecutor.ExecuteAsync(Command("environment", names), folder.Root); Finalized(result);
            using var json = JsonDocument.Parse(result.Stdout);
            Check(result.Status == ProjectCommandStatus.Completed && json.RootElement.GetProperty("allAbsent").GetBoolean() && json.RootElement.GetProperty("pathAvailable").GetBoolean(), "Credential environment leaked or essential PATH disappeared.");
            var echo = await ProjectCommandExecutor.ExecuteAsync(Command("echo", secret), folder.Root); Finalized(echo);
            Check(echo.Status == ProjectCommandStatus.Completed && !echo.Stdout.Contains(secret, StringComparison.Ordinal), "Known inherited secret was not redacted from output.");
        }
        finally { foreach (string name in names) Environment.SetEnvironmentVariable(name, old[name]); }
    }
    private static async Task ArgumentSecret()
    {
        using var folder = new Folder();
        string secret = "owned-argument-only-" + Guid.NewGuid().ToString("N");
        // This value never enters the environment or caller's explicit secret collection.
        var result = await ProjectCommandExecutor.ExecuteAsync(Command("echo-token", "--token", secret), folder.Root); Finalized(result);
        Check(result.Status == ProjectCommandStatus.Completed && result.ExitCode == 0, result.Message);
        Check(!result.Stdout.Contains(secret, StringComparison.Ordinal) && !result.Stderr.Contains(secret, StringComparison.Ordinal)
            && result.Stdout.Contains("before [REDACTED] after") && result.Stderr.Contains("error context [REDACTED] preserved"),
            "Configured secret argument leaked when echoed without a credential label, or ordinary context was removed.");
    }
    private static async Task Preflight()
    {
        using var folder = new Folder();
        var outside = Command("echo", "must not launch"); outside.WorkingDirectory = "..";
        var relative = Command("echo", "must not launch"); relative.Executable = "dotnet.exe";
        var timeout = Command("echo", "must not launch"); timeout.TimeoutSeconds = 0;
        var ambiguous = Command("echo", "must not launch"); ambiguous.WorkingDirectory = ".. ";
        foreach (var command in new[] { outside, relative, timeout, ambiguous })
        {
            var result = await ProjectCommandExecutor.ExecuteAsync(command, folder.Root); Finalized(result);
            Check(result.Status == ProjectCommandStatus.InvalidConfiguration && result.ProcessId is null,
                $"Invalid command preflight failed: cwd='{command.WorkingDirectory}', status={result.Status}, pid={result.ProcessId}, message={result.Message}");
        }
        var ambiguousRoot = await ProjectCommandExecutor.ExecuteAsync(Command("echo", "must not launch"), folder.Root + " "); Finalized(ambiguousRoot);
        Check(ambiguousRoot.Status == ProjectCommandStatus.InvalidConfiguration && ambiguousRoot.ProcessId is null, "Ambiguous absolute project spelling launched a process.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = await ProjectCommandExecutor.ExecuteAsync(Command("echo", "must not launch"), folder.Root, cancel.Token); Finalized(cancelled);
        Check(cancelled.Status == ProjectCommandStatus.Cancelled && cancelled.ProcessId is null, "Prior cancellation launched a process.");
    }

    // Called only through an explicit test-harness command-line branch; helpers own no user applications.
    public static async Task<int> RunHelperAsync(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false); Console.InputEncoding = new UTF8Encoding(false);
        string mode = args[1]; string[] values = args.Skip(2).ToArray();
        switch (mode)
        {
            case "arguments": Console.Write(JsonSerializer.Serialize(new { arguments = values, stdinClosed = Console.ReadLine() is null })); return 0;
            case "echo": Console.Write(values[0]); return 0;
            case "echo-token": Console.Write("before " + values[1] + " after"); Console.Error.Write("error context " + values[1] + " preserved"); return 0;
            case "fail": Console.Error.Write("owned failure detail"); return 23;
            case "environment": Console.Write(JsonSerializer.Serialize(new { allAbsent = values.All(n => Environment.GetEnvironmentVariable(n) is null), pathAvailable = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PATH")) })); return 0;
            case "large":
                await Task.WhenAll(Task.Run(() => Console.Out.Write(new string('O', 300000))), Task.Run(() => Console.Error.Write(new string('E', 300000)))); return 0;
            case "sleep-child":
                using (var self = Process.GetCurrentProcess())
                {
                    string temp = values[0] + ".tmp";
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { pid = self.Id, startTimeUtc = self.StartTime.ToUniversalTime() })); File.Move(temp, values[0]);
                }
                await Task.Delay(8000); return 0;
            case "tree-exit":
            case "tree-wait":
                var launch = Self(); launch.Arguments.Add("sleep-child"); launch.Arguments.Add(values[0]);
                var info = new ProcessStartInfo(launch.Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (string argument in launch.Arguments) info.ArgumentList.Add(argument);
                using (var child = Process.Start(info)!)
                {
                    var timer = Stopwatch.StartNew();
                    while (!File.Exists(values[0]) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
                    if (!File.Exists(values[0])) return 24;
                }
                if (mode == "tree-wait") await Task.Delay(8000);
                return 0;
            default: return 25;
        }
    }
}
