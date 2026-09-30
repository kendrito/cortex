using System.IO;
using System.Text.Json;
using Testy.Core;

namespace Testy.Cli;

internal static class MaintenanceVerifier
{
    public static object Verify(string artifacts)
    {
        var checks = new List<object>();
        int failed = 0;
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; checks.Add(new { name, passed = false, error = ex.Message }); }
        }
        void ExpectRejected(Action action)
        {
            try { action(); } catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException) { return; }
            throw new InvalidOperationException("Invalid input was accepted.");
        }
        TestTemplate Template() => new()
        {
            Name = "Customer matrix",
            Test = new TestCase { Name = "Customer", Steps = [new TestStep { Id = "enter", Action = StepAction.TypeText, Selector = "id:Name", Value = "Ada" }, new TestStep { Id = "check", Action = StepAction.AssertText, Selector = "id:Result", Value = "Ada" }] },
            Bindings = [new() { Parameter = "name", StepId = "enter" }, new() { Parameter = "name", StepId = "check" }]
        };
        TestDataSet Data() => new() { Rows = [new() { Id = "unicode", Name = "Unicode", Values = new() { ["name"] = "Zoë \"QA\" ${value}\n東京" } }, new() { Id = "empty", Name = "Empty input", Values = new() { ["name"] = "" } }] };
        TestCase Acceptance() => new() { Name = "Explicit outcome", Steps = [new() { Action = StepAction.AssertText, Selector = "id:Status", Value = "Saved", TimeoutMs = 1000 }] };
        Demonstration Demo()
        {
            var start = DateTimeOffset.UtcNow.AddSeconds(-2);
            return new() { Target = new() { ProcessId = 123 }, StartedAt = start, FinishedAt = start.AddSeconds(1), Completed = true,
                InitialSnapshot = new() { Target = new() { ProcessId = 123 } }, FinalSnapshot = new() { Target = new() { ProcessId = 123 } },
                Actions = [new() { Sequence = 0, ObservedAt = start.AddMilliseconds(100), ResolvedAt = start.AddMilliseconds(150), RuntimeId = "1.2.3", Step = new() { Action = StepAction.Click, Selector = "id:Save" } }] };
        }
        Check("Parameter values round-trip literally and source test is immutable", () =>
        {
            var template = Template(); var data = Data(); string before = JsonSerializer.Serialize(template, TestyJson.Options);
            var suite = MaintenanceCommand.Materialize(template, data);
            if (suite.Tests.Count != 2 || suite.Tests[0].Steps.Any(s => s.Value != data.Rows[0].Values["name"]) || suite.Tests[1].Steps.Any(s => s.Value != "") || before != JsonSerializer.Serialize(template, TestyJson.Options)) throw new Exception("Literal values or source immutability changed.");
            var copy = TestyJson.Clone(suite);
            if (copy.Tests[0].Steps[0].Value != data.Rows[0].Values["name"]) throw new Exception("JSON round trip changed literal text.");
        });
        Check("Missing parameter is rejected", () => { var d = Data(); d.Rows[0].Values.Clear(); ExpectRejected(() => MaintenanceCommand.Materialize(Template(), d)); });
        Check("Extra parameter is rejected", () => { var d = Data(); d.Rows[0].Values["unknown"] = "x"; ExpectRejected(() => MaintenanceCommand.Materialize(Template(), d)); });
        Check("Duplicate test identities are rejected", () => { var d = Data(); d.Rows[1].Id = d.Rows[0].Id; ExpectRejected(() => MaintenanceCommand.Materialize(Template(), d)); });
        Check("Binding cannot change a selector", () => { var t = Template(); t.Bindings[0].Field = "selector"; ExpectRejected(() => MaintenanceCommand.Materialize(t, Data())); });
        Check("Binding cannot target unknown step", () => { var t = Template(); t.Bindings[0].StepId = "missing"; ExpectRejected(() => MaintenanceCommand.Materialize(t, Data())); });
        Check("Duplicate bindings are rejected", () => { var t = Template(); t.Bindings.Add(TestyJson.Clone(t.Bindings[0])); ExpectRejected(() => MaintenanceCommand.Materialize(t, Data())); });
        Check("Exact assertion cannot become contains through data", () => { var d = Data(); d.Rows[0].Values["name"] = "contains:Saved"; ExpectRejected(() => MaintenanceCommand.Materialize(Template(), d)); });
        Check("Template without acceptance is rejected", () => { var t = Template(); t.Test.Steps.RemoveAt(1); ExpectRejected(() => MaintenanceCommand.Materialize(t, Data())); });
        Check("Unknown input fields are rejected", () => ExpectRejected(() => MaintenanceCommand.ParseStrict<TestDataSet>("{\"rows\":[],\"execute\":true}")));
        Check("Duplicate case-varied input fields are rejected", () => ExpectRejected(() => MaintenanceCommand.ParseStrict<TestDataSet>("{\"version\":1,\"Version\":2,\"rows\":[]}")));
        Check("Complete recording seeds unexecuted actions and explicit acceptance", () =>
        { var demo = Demo(); var seed = MaintenanceCommand.DemonstrationSeed(demo, Acceptance()); if (seed.Steps.Count != 2 || seed.Steps[0].Id == demo.Actions[0].Step.Id) throw new Exception("Draft lacks distinct saved identities."); MaintenanceCommand.VerifyAcceptance(seed, Acceptance()); });
        Check("Lost recording events prevent drafting", () => { var d = Demo(); d.DroppedEvents = 1; ExpectRejected(() => MaintenanceCommand.DemonstrationSeed(d, Acceptance())); });
        Check("Cancelled recording prevents drafting", () => { var d = Demo(); d.Cancelled = true; ExpectRejected(() => MaintenanceCommand.DemonstrationSeed(d, Acceptance())); });
        Check("Unfinished recording prevents drafting", () => { var d = Demo(); d.FinishedAt = null; ExpectRejected(() => MaintenanceCommand.DemonstrationSeed(d, Acceptance())); });
        Check("Reordered recording prevents drafting", () => { var d = Demo(); d.Actions[0].Sequence = 2; ExpectRejected(() => MaintenanceCommand.DemonstrationSeed(d, Acceptance())); });
        Check("Foreign process evidence prevents drafting", () => { var d = Demo(); d.FinalSnapshot.Target.ProcessId = 999; ExpectRejected(() => MaintenanceCommand.DemonstrationSeed(d, Acceptance())); });
        Check("Truncated recording evidence prevents drafting", () => { var d = Demo(); d.FinalSnapshot.IsTruncated = true; ExpectRejected(() => MaintenanceCommand.DemonstrationSeed(d, Acceptance())); });
        Check("Acceptance file cannot contain input actions", () => { var a = Acceptance(); a.Steps[0].Action = StepAction.TypeText; ExpectRejected(() => MaintenanceCommand.DemonstrationSeed(Demo(), a)); });
        foreach (string change in new[] { "selector", "value", "timeout", "remove", "duplicate" })
            Check("AI draft rejects changed acceptance: " + change, () =>
            {
                var acceptance = Acceptance(); var plan = MaintenanceCommand.DemonstrationSeed(Demo(), acceptance);
                if (change == "selector") plan.Steps[^1].Selector = "id:Other";
                if (change == "value") plan.Steps[^1].Value = "contains:Saved";
                if (change == "timeout") plan.Steps[^1].TimeoutMs++;
                if (change == "remove") plan.Steps.RemoveAt(plan.Steps.Count - 1);
                if (change == "duplicate") { var extra = TestyJson.Clone(plan.Steps[^1]); extra.Id = Guid.NewGuid().ToString("N"); plan.Steps.Add(extra); }
                ExpectRejected(() => MaintenanceCommand.VerifyAcceptance(plan, acceptance));
            });
        Check("New-output writer preserves an existing user file", () =>
        {
            Directory.CreateDirectory(artifacts); string path = Path.Combine(artifacts, "preserve-" + Guid.NewGuid().ToString("N") + ".json");
            MaintenanceCommand.WriteNew(path, new { original = true }); string before = File.ReadAllText(path);
            ExpectRejected(() => MaintenanceCommand.WriteNew(path, new { original = false }));
            if (File.ReadAllText(path) != before) throw new Exception("Existing file was overwritten.");
        });
        Check("Draft provenance keeps distinct schema and provider kinds through actual JSON serialization", () =>
        {
            var value = MaintenanceCommand.DraftProvenance("demo source", "acceptance source", new() { Kind = ProviderKind.Compatible, Model = "fixture-model" }, Demo(), Acceptance());
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, TestyJson.Options));
            if (json.RootElement.GetProperty("kind").GetString() != "testy.demonstration-draft.v1" || json.RootElement.GetProperty("providerKind").GetString() != "compatible"
                || json.RootElement.GetProperty("state").GetString() != "UnexecutedDraft" || json.RootElement.GetProperty("preservedAssertions").GetInt32() != 1)
                throw new Exception("Draft provenance schema or provider identity changed.");
        });
        string SuiteFile(object value)
        {
            Directory.CreateDirectory(artifacts);
            string path = Path.Combine(Path.GetFullPath(artifacts), "suite-" + Guid.NewGuid().ToString("N") + ".json");
            MaintenanceCommand.WriteNew(path, value); return path;
        }
        Check("Materialized inline suite round-trips through the real CLI loader", () =>
        {
            var generated = MaintenanceCommand.Materialize(Template(), Data());
            var loaded = SuiteCommand.LoadSuiteAsync(SuiteFile(generated), CancellationToken.None).GetAwaiter().GetResult();
            if (loaded.Tests.Count != generated.Tests.Count || JsonSerializer.Serialize(loaded.Tests, TestyJson.Options) != JsonSerializer.Serialize(generated.Tests, TestyJson.Options))
                throw new Exception("CLI suite loading changed the generated test contracts.");
        });
        Check("Existing file-manifest suite remains supported", () =>
        {
            string test = SuiteFile(Template().Test);
            var loaded = SuiteCommand.LoadSuiteAsync(SuiteFile(new { name = "File manifest", repetitions = 2, testFiles = new[] { Path.GetFileName(test) } }), CancellationToken.None).GetAwaiter().GetResult();
            if (loaded.Tests.Count != 1 || loaded.Repetitions != 2 || loaded.Tests[0].Steps.Count != 2) throw new Exception("File manifest compatibility changed.");
        });
        Check("Mixed inline and file suite forms are rejected", () =>
            ExpectRejected(() => SuiteCommand.LoadSuiteAsync(SuiteFile(new { tests = new[] { Template().Test }, testFiles = new[] { "unused.json" } }), CancellationToken.None).GetAwaiter().GetResult()));
        Check("Invalid later inline test prevents suite loading", () =>
        {
            var generated = MaintenanceCommand.Materialize(Template(), Data()); generated.Tests[1].Steps[0].TimeoutMs = -1;
            ExpectRejected(() => SuiteCommand.LoadSuiteAsync(SuiteFile(generated), CancellationToken.None).GetAwaiter().GetResult());
        });
        Check("Recording shutdown waits for callbacks already in progress", () =>
        {
            var gate = new RecordingCaptureGate<string>(4);
            if (!gate.TryBegin(out _)) throw new Exception("Capture did not start.");
            gate.StopAccepting();
            var finish = gate.FinishAsync(TimeSpan.FromSeconds(1));
            if (finish.IsCompleted || gate.TryBegin(out _)) throw new Exception("Shutdown admitted callbacks or skipped an active callback.");
            gate.Complete("last event"); finish.GetAwaiter().GetResult();
            if (gate.Dropped != 0 || !gate.Drain().SequenceEqual(new[] { "last event" })) throw new Exception("The last callback was lost.");
        });
        Check("Timed-out callbacks cannot publish into finalized capture", () =>
        {
            var gate = new RecordingCaptureGate<string>(4); gate.TryBegin(out _);
            gate.FinishAsync(TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult();
            gate.Complete("late event");
            if (gate.Dropped != 1 || gate.Drain().Length != 0) throw new Exception("Late callback modified finalized evidence.");
        });
        Check("In-flight capture cannot drain out of callback order", () =>
        {
            var gate = new RecordingCaptureGate<string>(4); gate.TryBegin(out long first); gate.TryBegin(out long second);
            gate.Complete("second");
            if (second <= first || gate.Drain().Length != 0) throw new Exception("Incomplete callback batch was drained.");
            gate.Complete("first");
            if (gate.Drain().Length != 2) throw new Exception("Completed callbacks were lost.");
        });
        Check("Bounded recording queue reports overflow and capture failure", () =>
        {
            var gate = new RecordingCaptureGate<string>(1);
            gate.TryBegin(out _); gate.Complete("first"); gate.TryBegin(out _); gate.Complete("overflow");
            gate.TryBegin(out _); gate.Complete(null, true);
            if (gate.Dropped != 2 || !gate.Drain().SequenceEqual(new[] { "first" })) throw new Exception("Capture loss was not recorded.");
        });
        Check("Recycled recording node identity is rejected", () =>
        {
            var node = new UiElementInfo { RuntimeId = "1", AutomationId = "Save", Name = "Save invoice A", ControlType = "Button" };
            var identity = new RecordingCommand.Identity("1", "Save", "Save invoice A", "Button");
            if (!RecordingCommand.SameIdentity(identity, node)) throw new Exception("Unchanged identity was rejected.");
            node.Name = "Save invoice B";
            if (RecordingCommand.SameIdentity(identity, node)) throw new Exception("Recycled runtime identity was accepted.");
            node.Name = identity.Name; node.IsPassword = true;
            if (RecordingCommand.SameIdentity(identity, node)) throw new Exception("Password node was accepted.");
        });
        Check("Recording ancestor identity retains business row but ignores unrelated siblings", () =>
        {
            var root = new UiElementInfo { RuntimeId = "root", ControlType = "Window", Depth = 0 };
            var row = new UiElementInfo { RuntimeId = "row", ControlType = "DataItem", Name = "Invoice A", Depth = 1 };
            var node = new UiElementInfo { RuntimeId = "edit", ControlType = "Edit", Depth = 2 };
            var snapshot = new UiSnapshot { Elements = [root, new() { RuntimeId = "sibling", Depth = 1 }, row, node] };
            var original = RecordingCommand.SnapshotAncestors(snapshot, node);
            if (original.Length != 2 || original[0].Name != "Invoice A" || original[1].RuntimeId != "root") throw new Exception("Incorrect snapshot ancestry.");
            row.Name = "Invoice B";
            if (original.SequenceEqual(RecordingCommand.SnapshotAncestors(snapshot, node))) throw new Exception("A recycled business row was accepted.");
        });
        var report = new { finishedAt = DateTimeOffset.UtcNow, passed = failed == 0, total = checks.Count, failed, mode = "Offline authoring and data-integrity checks; no model or desktop calls", checks };
        WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "maintenance-report.json"), report);
        return report;
    }
}
