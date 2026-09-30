using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;

namespace Testy.Cli;

// These are offline integrity fixtures, not evidence of model calls, UI input or application compatibility.
internal static class ProjectWorkerChecks
{
    internal static async Task<ProjectWorkerReport> VerifyAsync(string directory, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var report = new ProjectWorkerReport { ArtifactDirectory = directory };
        var variants = new (string Name, string? ExpectedRejection)[]
        {
            ("valid-project-sequence", null),
            ("missing-flat-evidence", "Required project commands"),
            ("missing-agent-evidence", "Flat project evidence differs"),
            ("forged-flat-evidence", "persisted record"),
            ("forged-agent-evidence", "Flat project evidence differs"),
            ("ui-before-required-command", "UI execution preceded"),
            ("project-claims-ui-action", "coverage is incomplete"),
            ("replay-with-project-evidence", "Project execution was not enabled"),
            ("required-command-omitted", "Required project commands"),
            ("project-tool-without-record", "omitted its project evidence")
        };
        try
        {
            foreach (var (name, rejection) in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var check = new ProjectWorkerCheck { Name = name }; report.Checks.Add(check);
                try
                {
                    var fixture = await CreateAsync(Path.Combine(directory, name), cancellationToken);
                    switch (name)
                    {
                        case "missing-flat-evidence": fixture.Run.ProjectEvidence.Clear(); break;
                        case "missing-agent-evidence": fixture.Agent.Observations.RemoveAll(o => o.Project is not null); break;
                        case "forged-flat-evidence": fixture.Run.ProjectEvidence[0].Message = "Forged flat evidence"; break;
                        case "forged-agent-evidence": fixture.Agent.Observations.First(o => o.Project is not null).Project!.Message = "Forged model observation"; break;
                        case "ui-before-required-command":
                            var action = fixture.Agent.Observations.Last(); fixture.Agent.Observations.RemoveAt(fixture.Agent.Observations.Count - 1); fixture.Agent.Observations.Insert(0, action); break;
                        case "project-claims-ui-action":
                            fixture.Agent.Observations.First(o => o.Project is not null).Execution = TestyJson.Clone(fixture.Agent.Observations.Last().Execution!); break;
                        case "required-command-omitted":
                            fixture.Run.ProjectEvidence.RemoveAll(e => e.Command is not null); fixture.Agent.Observations.RemoveAll(o => o.Project?.Command is not null); break;
                        case "project-tool-without-record":
                            fixture.Agent.Observations.Insert(0, new() { ToolName = "project_read_file" }); break;
                    }
                    fixture.Save();
                    string? rejected = null;
                    try { WorkerCommand.ValidateTerminalRun(fixture.Run, fixture.Test, TargetPid, fixture.Directory, name == "replay-with-project-evidence", 0); }
                    catch (InvalidDataException ex) { rejected = ex.Message; }
                    check.Passed = rejection is null ? rejected is null : rejected?.Contains(rejection, StringComparison.OrdinalIgnoreCase) == true;
                    check.Message = rejected is null ? "Synthetic terminal sequence accepted." : "Synthetic terminal sequence rejected: " + rejected;
                    if (!check.Passed) check.Message += " Expected: " + (rejection ?? "acceptance");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { check.Message = ex.GetType().Name + ": " + ex.Message; }
                WorkspaceStore.WriteAtomic(Path.Combine(directory, "project-worker-report.json"), report);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var a = new BenchmarkEntry { ProjectConfigurationSha256 = new string('A', 64) };
            var b = TestyJson.Clone(a); b.ProjectConfigurationSha256 = new string('B', 64);
            report.Checks.Add(new() { Name = "benchmark-project-config-separation", Passed = BenchmarkCommand.GroupKey(a) != BenchmarkCommand.GroupKey(b), Message = "Otherwise identical synthetic benchmark entries with different project configurations must remain separate." });
            cancellationToken.ThrowIfCancellationRequested(); report.CompletedAllChecks = true;
        }
        catch (OperationCanceledException) { report.Cancelled = true; report.Message = "Offline integrity qualification cancelled."; }
        finally { report.FinishedAt = DateTimeOffset.UtcNow; WorkspaceStore.WriteAtomic(Path.Combine(directory, "project-worker-report.json"), report); }
        return report;
    }

    private const int TargetPid = 54321;
    private sealed record Fixture(string Directory, RunResult Run, TestCase Test, ComputerAgentResult Agent)
    {
        internal void Save()
        {
            WorkspaceStore.WriteAtomic(Path.Combine(Run.ArtifactDirectory, "run.json"), Run);
            WorkspaceStore.WriteAtomic(Path.Combine(Run.ArtifactDirectory, "requested-test.json"), Test);
            WorkspaceStore.WriteAtomic(Path.Combine(Run.ArtifactDirectory, "computer-agent.json"), Agent);
        }
    }
    private static async Task<Fixture> CreateAsync(string directory, CancellationToken ct)
    {
        string workerDirectory = directory;
        directory = Path.Combine(workerDirectory, "run"); System.IO.Directory.CreateDirectory(directory);
        string projectRoot = Path.Combine(workerDirectory, "selected-project"); System.IO.Directory.CreateDirectory(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "Feature.cs"), "// Owned synthetic source for project evidence integrity checks.\n", ct);
        var settings = new ProjectToolSettings
        {
            Enabled = true, RootDirectory = projectRoot, RequiredBeforeUiCommands = ["build"],
            Commands = [new() { Id = "build", Description = "Synthetic completed command; no process is launched by this integrity fixture.", Executable = Environment.ProcessPath!, Arguments = [] }]
        };
        var session = new ProjectToolSession(settings, Path.Combine(workerDirectory, "project-evidence"), commandExecutor: (_, _, _) =>
        {
            var start = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProjectCommandExecution
            {
                Status = ProjectCommandStatus.Completed, ExitCode = 0, CleanupComplete = true, ProcessId = 4242,
                StartedAt = start, FinishedAt = DateTimeOffset.UtcNow,
                Message = "Synthetic command result and synthetic PID4242; no process launched."
            });
        });
        var read = await session.DispatchAsync("project_read_file", "{\"path\":\"Feature.cs\",\"startLine\":1,\"maxLines\":10}", ct);
        var command = await session.DispatchAsync("project_run_command", "{\"commandId\":\"build\"}", ct);
        WorkerCommand.Require(read.Status == ProjectToolStatus.Succeeded && command.Status == ProjectToolStatus.Succeeded, "Synthetic fixture's real project session could not persist its read and configured command records.");
        WorkspaceStore.WriteAtomic(Path.Combine(workerDirectory, "provider-settings.json"), new ProviderSettings { ProjectTools = settings });
        var target = new TargetInfo { ProcessId = TargetPid };
        var snapshot = new UiSnapshot { Target = target, Source = "Synthetic offline integrity fixture, not a live application", Elements =
            [new() { Depth = 0, Selector = "id:Status", AutomationId = "Status", Name = "Saved", Value = "Saved", ControlType = "Text", RuntimeId = "synthetic:status", IsEnabled = true }] };
        var test = new TestCase { Id = "project-integrity", Name = "Synthetic project-worker integrity; no actual UI or model", Steps =
            [new() { Id = "assert", Title = "Synthetic saved assertion", Action = StepAction.AssertText, Selector = "id:Status", Value = "Saved" }] };
        string png = Path.Combine(directory, "synthetic.png");
        var encoder = new PngBitmapEncoder(); byte[] pixels = Enumerable.Range(0, 32 * 32 * 4).Select(i => (byte)(i % 251)).ToArray();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4)));
        using (var stream = File.Create(png)) encoder.Save(stream);
        var step = new StepResult { Index = 0, Step = TestyJson.Clone(test.Steps[0]), Status = RunStatus.Passed, Snapshot = snapshot, ScreenshotPath = png, Message = "Synthetic assertion evidence." };
        var agent = new ComputerAgentResult { Completed = true, Status = RunStatus.Passed, ModelTurns = 5, Message = "Synthetic model transcript; no model requested.", Observations =
        [new() { ToolName = read.ToolName, Project = TestyJson.Clone(read) }, new() { ToolName = command.ToolName, Project = TestyJson.Clone(command) },
         new() { ToolName = "observe_application", Snapshot = snapshot, ScreenshotPath = png, Execution = new() { Target = target, TestName = "Agent observation", Status = RunStatus.Passed } },
         new() { ToolName = "perform_saved_step", SavedStepId = test.Steps[0].Id, Snapshot = snapshot, ScreenshotPath = png, Execution = new() { Target = target, Status = RunStatus.Passed, Steps = [TestyJson.Clone(step)] } }] };
        var run = new RunResult { TestId = test.Id, TestName = test.Name, Target = target, Status = RunStatus.Passed, ArtifactDirectory = directory,
            StartedAt = read.StartedAt, FinishedAt = DateTimeOffset.UtcNow, Steps = [step], ProjectEvidence = [TestyJson.Clone(read), TestyJson.Clone(command)], Summary = "Synthetic offline integrity fixture." };
        WorkspaceStore.WriteAtomic(Path.Combine(directory, "step-001.json"), snapshot);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.html"), "Synthetic offline integrity fixture, not a desktop result.", ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "junit.xml"), "<testsuite name=\"synthetic-project-integrity\" />", ct);
        return new(workerDirectory, run, test, agent);
    }
}

internal sealed class ProjectWorkerReport
{
    public string Schema { get; set; } = "testy.project-worker-integrity.v1";
    public string Scope { get; set; } = "Offline integrity checks using real persisted project records and explicitly synthetic command, UI and model evidence. No desktop/model/command execution.";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string ArtifactDirectory { get; set; } = "";
    public List<ProjectWorkerCheck> Checks { get; set; } = [];
    public bool CompletedAllChecks { get; set; }
    public bool Cancelled { get; set; }
    public string Message { get; set; } = "";
    public bool Passed => FinishedAt >= StartedAt && CompletedAllChecks && !Cancelled && Checks.Count == 11 && Checks.All(c => c.Passed);
}
internal sealed class ProjectWorkerCheck
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
}
