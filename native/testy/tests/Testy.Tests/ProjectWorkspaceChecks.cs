using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class ProjectWorkspaceChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("selected project survives Studio-style provider connection replacement", () => Owned((root, store) =>
        {
            store.ConfigureProjectTools(new() { Enabled = true, RootDirectory = root });
            var edited = new ProviderSettings { Kind = ProviderKind.Compatible, Model = "edited-model" };
            store.SaveSettings(edited);
            Check(edited.ProjectTools?.RootDirectory == root && edited.ProjectTools.Enabled, "In-memory Studio connection lost the selected project.");
            Check(store.LoadSettings().ProjectTools?.RootDirectory == root && store.LoadSettings().Model == "edited-model", "Saved connection or project changed.");
        }));
        yield return ("explicitly disabling project access survives restart and connection save", () => Owned((root, store) =>
        {
            store.ConfigureProjectTools(new() { Enabled = true, RootDirectory = root });
            store.ConfigureProjectTools(new() { Enabled = false });
            store.SaveSettings(new() { Model = "changed" });
            Check(store.LoadSettings().ProjectTools?.Enabled == false, "Disabled project configuration was silently re-enabled.");
        }));
        yield return ("embedded project settings survive older connection editors", () => Owned((root, store) =>
        {
            store.SaveSettings(new() { ProjectTools = new() { Enabled = true, RootDirectory = root } });
            store.SaveSettings(new() { Model = "updated" });
            Check(store.LoadSettings().ProjectTools?.RootDirectory == root, "Legacy editor dropped embedded project settings.");
        }));
        yield return ("strict project config rejects unknown duplicate and null properties", () => Owned((root, store) =>
        {
            foreach (var json in new[] { "{\"enabled\":false,\"arbitraryShell\":true}", "{\"enabled\":false,\"Enabled\":true}", "null", "{\"enabled\":false,\"commands\":null}" })
            {
                bool denied = false;
                try { ProjectConfiguration.Parse(json); } catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException) { denied = true; }
                Check(denied, "Invalid project configuration was accepted.");
            }
        }));
        yield return ("invalid project selection leaves previous configured workspace intact", () => Owned((root, store) =>
        {
            store.ConfigureProjectTools(new() { Enabled = true, RootDirectory = root });
            var before = File.ReadAllBytes(Path.Combine(store.RootDirectory, "project-tools.json"));
            bool denied = false;
            try { store.ConfigureProjectTools(new() { Enabled = true, RootDirectory = Path.Combine(root, "missing") }); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IOException) { denied = true; }
            Check(denied && before.SequenceEqual(File.ReadAllBytes(Path.Combine(store.RootDirectory, "project-tools.json"))), "Invalid selection changed the active project.");
        }));
        yield return ("unavailable saved repositories load for recovery but reject execution and saving", () => Owned((root, store) =>
        {
            var selected = Path.Combine(root, "selected"); Directory.CreateDirectory(selected);
            store.ConfigureProjectTools(new() { Enabled = true, RootDirectory = selected });
            store.SaveSettings(new() { Model = "retained" });
            var before = File.ReadAllBytes(Path.Combine(store.RootDirectory, "settings.json"));
            Directory.Delete(selected);
            var loaded = store.LoadSettings();
            Check(loaded.ProjectTools?.Enabled == true && loaded.ProjectTools.RootDirectory == selected && loaded.Model == "retained", "Startup discarded an unavailable selected repository.");
            Reject(() => AiTestRunner.ValidateExecution(AssertionTest(), loaded));
            var artifacts = Path.Combine(root, "not-created");
            Reject(() => _ = new ProjectToolSession(loaded.ProjectTools!, artifacts));
            Check(!Directory.Exists(artifacts), "Unavailable repository reached tool setup before validation.");
            Reject(() => store.SaveSettings(new() { Model = "must-not-save" }));
            Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(store.RootDirectory, "settings.json"))), "Failed validation overwrote provider configuration.");
            store.ConfigureProjectTools(new() { Enabled = false });
            store.SaveSettings(new() { Model = "repaired" });
            Check(store.LoadSettings().ProjectTools?.Enabled == false && store.LoadSettings().Model == "repaired", "Explicit disabled configuration did not repair unavailable project access.");
        }));
        yield return ("missing configured SDK remains visible in embedded settings but cannot execute", () => Owned((root, store) =>
        {
            var executable = Path.Combine(root, "owned-sdk.exe"); File.Copy(Environment.ProcessPath!, executable);
            store.SaveSettings(new() { ProjectTools = new() { Enabled = true, RootDirectory = root, Commands = [new() { Id = "build", Executable = executable }] } });
            File.Delete(executable);
            var loaded = store.LoadSettings();
            Check(loaded.ProjectTools?.Enabled == true && loaded.ProjectTools.Commands.Single().Executable == executable, "Loading silently removed an unavailable configured command.");
            Reject(() => AiTestRunner.ValidateExecution(AssertionTest(), loaded));
            Reject(() => ProjectConfiguration.Parse(JsonSerializer.Serialize(loaded.ProjectTools, TestyJson.Options)));
            Reject(() => store.SaveSettings(new() { Model = "must-not-save" }));
            store.ConfigureProjectTools(new() { Enabled = false });
            Check(store.LoadSettings().ProjectTools?.Enabled == false, "Missing SDK prevented explicit repair.");
        }));
        yield return ("stored project JSON stays strict while filesystem availability is deferred", () => Owned((root, store) =>
        {
            var selected = Path.Combine(store.RootDirectory, "project-tools.json");
            foreach (var invalid in new[] { "{", "null", "{\"enabled\":false,\"unknownField\":1}", "{\"enabled\":false,\"Enabled\":true}", "{\"commands\":null}", "{\"commands\":[null]}" })
            {
                File.WriteAllText(selected, invalid); Reject(() => store.LoadSettings());
            }
            File.Delete(selected);
            foreach (var embedded in new[] { "{\"projectTools\":{\"enabled\":false,\"unknownField\":1}}", "{\"projectTools\":{\"commands\":null}}", "{\"projectTools\":null,\"ProjectTools\":null}" })
            {
                File.WriteAllText(Path.Combine(store.RootDirectory, "settings.json"), embedded); Reject(() => store.LoadSettings());
            }
        }));
    }
    private static Task Owned(Action<string, WorkspaceStore> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Testy-project-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root, new WorkspaceStore(Path.Combine(root, "workspace"))); }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static TestCase AssertionTest() => new() { Name = "Preflight only", Steps = [new() { Title = "Verify ready", Action = StepAction.AssertExists, Selector = "id:Status" }] };
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or IOException) { return; }
        throw new InvalidOperationException("Unavailable or malformed project configuration was accepted for a live operation.");
    }
}
