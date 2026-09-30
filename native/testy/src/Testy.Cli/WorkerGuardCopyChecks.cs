using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;

namespace Testy.Cli;

// Synthetic offline integrity checks. These files are not evidence of model or desktop execution.
internal static class WorkerGuardCopyChecks
{
    internal static string Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string variant in new[] { "valid-copy", "changed-source", "changed-copy" })
        {
            string folder = Path.Combine(directory, variant); Directory.CreateDirectory(folder);
            var snapshot = new UiSnapshot { Target = new() { ProcessId = 12345 }, Elements =
            [new() { Depth = 0, AutomationId = "Window", Selector = "id:Window", ControlType = "Window", RuntimeId = "1", IsEnabled = true },
             new() { Depth = 1, AutomationId = "RecoveryArea", Selector = "id:RecoveryArea", ControlType = "Group", RuntimeId = "2", IsEnabled = true },
             new() { Depth = 2, AutomationId = "UpdatedApply", Selector = "id:UpdatedApply", ControlType = "Button", Name = "Apply", RuntimeId = "3", IsEnabled = true }] };
            const string original = "query:{\"id\":\"OriginalApply\",\"ancestor\":{\"id\":\"RecoveryArea\"}}";
            const string alternate = "query:{\"id\":\"UpdatedApply\",\"ancestor\":{\"id\":\"RecoveryArea\"}}";
            var test = new TestCase { Id = "synthetic-guard-copy", Name = "Synthetic guard copy integrity; no actual model or desktop", Steps =
            [new() { Id = "apply", Action = StepAction.Click, Selector = original, SelectorAlternatives = [new() { Id = "approved", Selector = alternate, ExpectedAutomationId = "UpdatedApply", ExpectedControlType = "Button", ExpectedName = "Apply" }] },
             new() { Id = "assert", Action = StepAction.AssertText, Selector = "id:Status", Value = "Saved" }] };
            var run = new RunResult { TestId = test.Id, TestName = test.Name, Target = snapshot.Target, ArtifactDirectory = folder, Status = RunStatus.Passed, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), FinishedAt = DateTimeOffset.UtcNow };
            var agent = new ComputerAgentResult { Completed = true, Status = RunStatus.Pending, ModelTurns = 3 };
            string sourceGuard = Path.Combine(folder, "source-guard.json"), copiedGuard = Path.Combine(folder, "step-001-recovery.json");
            WorkspaceStore.WriteAtomic(sourceGuard, snapshot); File.Copy(sourceGuard, copiedGuard, true);
            string sourceImage = Path.Combine(folder, "synthetic.png");
            byte[] pixels = Enumerable.Range(0, 32 * 32 * 4).Select(i => (byte)(i % 251)).ToArray();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4)));
            using (var stream = File.Create(sourceImage)) encoder.Save(stream);
            for (int i = 0; i < test.Steps.Count; i++)
            {
                var source = new StepResult { Step = TestyJson.Clone(test.Steps[i]), Status = RunStatus.Passed, Snapshot = snapshot, ScreenshotPath = sourceImage };
                if (i == 0) source.SelectorRecovery = new() { AlternativeId = "approved", OriginalSelector = original, ResolvedSelector = alternate, EngineVerified = true, GuardedAt = snapshot.CapturedAt, Guard = SelectorRecovery.Resolve(test.Steps[0], "approved", snapshot), ActionOutcome = ActionOutcome.Completed, GuardSnapshotPath = sourceGuard };
                agent.Observations.Add(new() { ToolName = i == 0 ? "perform_saved_step_alternate" : "perform_saved_step", SavedStepId = source.Step.Id, Snapshot = snapshot, ScreenshotPath = sourceImage, Execution = new() { Target = snapshot.Target, Status = RunStatus.Passed, Steps = [source] } });
                var flat = TestyJson.Clone(source); flat.Index = i;
                flat.ScreenshotPath = Path.Combine(folder, $"step-{i + 1:000}.png"); File.Copy(sourceImage, flat.ScreenshotPath, true);
                if (flat.SelectorRecovery is not null) flat.SelectorRecovery.GuardSnapshotPath = copiedGuard;
                run.Steps.Add(flat); WorkspaceStore.WriteAtomic(Path.Combine(folder, $"step-{i + 1:000}.json"), flat.Snapshot);
            }
            if (variant != "valid-copy")
            {
                var changed = TestyJson.Clone(snapshot); changed.Source = "Changed bytes with otherwise valid guarded identity";
                WorkspaceStore.WriteAtomic(variant == "changed-source" ? sourceGuard : copiedGuard, changed);
            }
            WorkspaceStore.WriteAtomic(Path.Combine(folder, "run.json"), run);
            WorkspaceStore.WriteAtomic(Path.Combine(folder, "requested-test.json"), test);
            WorkspaceStore.WriteAtomic(Path.Combine(folder, "computer-agent.json"), agent);
            File.WriteAllText(Path.Combine(folder, "report.html"), "Synthetic offline integrity fixture, not a desktop result.");
            File.WriteAllText(Path.Combine(folder, "junit.xml"), "<testsuite name=\"synthetic-integrity\" />");
            bool accepted = false;
            try { WorkerCommand.ValidateTerminalRun(run, test, 12345, directory, false, 0); accepted = true; }
            catch (InvalidDataException) when (variant != "valid-copy") { }
            WorkerCommand.Require(accepted == (variant == "valid-copy"), "Recovery guard copy integrity case failed: " + variant);
        }
        return "Three synthetic offline cases: exact guard copy at a different path accepted; modified source and modified copy rejected. No model calls or desktop input.";
    }
}
