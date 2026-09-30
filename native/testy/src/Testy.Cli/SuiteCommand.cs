using System.Text.Json;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal static class SuiteCommand
{
    internal sealed class Manifest
    {
        public string Name { get; set; } = "Functional suite";
        public int Repetitions { get; set; } = 1;
        public List<string> TestFiles { get; set; } = [];
        public List<TestCase> Tests { get; set; } = [];
    }

    internal static async Task<SuiteResult> RunAsync(string manifestPath, string? settingsPath, bool replay,
        ITargetDriver driver, string artifacts, CancellationToken ct, string? projectFile = null)
    {
        if (replay && projectFile is not null) throw new ArgumentException("Project tools require AI-directed execution, not replay.");
        if (replay && settingsPath is not null)
            throw new ArgumentException("Choose --settings for AI execution or --replay, not both.");
        if (!replay && string.IsNullOrWhiteSpace(settingsPath))
            throw new ArgumentException("AI suite execution requires --settings FILE. Use --replay explicitly for execution without a model.");
        var suite = await LoadSuiteAsync(manifestPath, ct);
        ProviderSettings settings;
        if (replay) settings = new() { Kind = ProviderKind.Offline, AiDirectedExecution = false };
        else
        {
            settings = await ProjectCliCommand.SettingsAsync(settingsPath!, projectFile, ct);
            if (!settings.AiDirectedExecution || settings.Kind == ProviderKind.Offline)
                throw new InvalidDataException("Suite --settings must enable AI-directed execution with a model provider. Use --replay explicitly for replay.");
            if (!Enum.IsDefined(settings.Kind) || settings.MaximumAgentTurns < 1 || settings.MaximumAgentTurns > AgentLimits.Maximum(settings))
                throw new InvalidDataException("The provider kind or AI turn budget is invalid.");
            foreach (var test in suite.Tests)
            {
                AiTestRunner.ValidateExecution(test, settings);
            }
        }
        var mode = replay ? "Deterministic replay (explicit)" : $"AI-directed · {settings.Kind} · {settings.Model}";
        var targetId = driver.Target?.ProcessId ?? throw new InvalidOperationException("Attach a target before running the suite.");
        var runner = new SuiteRunner(async (test, directory, token) =>
        {
            if (driver.Target?.ProcessId != targetId) throw new InvalidOperationException("The attached target changed during the suite.");
            return await new TestExecutionService(driver, settings, directory, new NativeComputerActionExecutor(driver)).RunAsync(test, null, token);
        }, artifacts, mode);
        return await runner.RunAsync(suite, ct);
    }

    // The materializer writes inline tests; hand-authored manifests may reference files.
    // Presence, rather than a nonempty-list fallback, makes mixed/null definitions fail closed.
    internal static async Task<TestSuite> LoadSuiteAsync(string manifestPath, CancellationToken ct)
    {
        var absolute = Path.GetFullPath(manifestPath);
        var json = await File.ReadAllTextAsync(absolute, ct);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The suite manifest must be an object.");
        var forms = document.RootElement.EnumerateObject()
            .Where(p => p.Name.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                        p.Name.Equals("testFiles", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (forms.Length != 1 || forms[0].Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("A suite requires exactly one tests array or testFiles array, never both.");
        var manifest = JsonSerializer.Deserialize<Manifest>(json, TestyJson.Options)
            ?? throw new InvalidDataException("The suite manifest is empty or null.");
        var suite = new TestSuite { Name = manifest.Name, Repetitions = manifest.Repetitions };
        if (forms[0].Name.Equals("tests", StringComparison.OrdinalIgnoreCase))
            suite.Tests = manifest.Tests;
        else
        {
            if (manifest.TestFiles is null || manifest.TestFiles.Count is < 1 or > 200 || manifest.TestFiles.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("A suite manifest requires 1–200 nonempty testFiles paths.");
            foreach (var file in manifest.TestFiles)
            {
                var path = Path.GetFullPath(file, Path.GetDirectoryName(absolute)!);
                suite.Tests.Add(JsonSerializer.Deserialize<TestCase>(await File.ReadAllTextAsync(path, ct), TestyJson.Options)
                    ?? throw new InvalidDataException("A suite test file is empty or null: " + path));
            }
        }
        if (string.IsNullOrWhiteSpace(suite.Name) || suite.Name.Length > 1000)
            throw new InvalidDataException("Suite name must contain 1–1000 characters.");
        if (suite.Repetitions is < 1 or > 100 || suite.Tests is null || suite.Tests.Count is < 1 or > 200 || suite.Tests.Count * suite.Repetitions > 1000)
            throw new InvalidDataException("Suites require 1–200 tests, 1–100 repetitions, and at most 1000 planned runs.");
        foreach (var test in suite.Tests) TestValidator.Validate(test);
        if (suite.Tests.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != suite.Tests.Count)
            throw new InvalidDataException("Suite test IDs must be unique; use repetitions to run tests again.");
        return suite;
    }
}
