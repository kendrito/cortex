using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Cli;

internal sealed class BenchmarkEntry
{
    public string Schema { get; set; } = "testy.benchmark.v1";
    public string Product { get; set; } = "Testy";
    public string ProductVersion { get; set; } = typeof(BenchmarkEntry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public string Measurement { get; set; } = "observedOwnedWorkerRun";
    public string RunId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Workflow { get; set; } = "";
    public string TestId { get; set; } = "";
    public string TestSha256 { get; set; } = "";
    public string FrozenTestSha256 { get; set; } = "";
    public string TargetSha256 { get; set; } = "";
    public string CliSha256 { get; set; } = "";
    public string TargetBuildSha256 { get; set; } = "";
    public string RunnerBuildSha256 { get; set; } = "";
    public string ProjectConfigurationSha256 { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Outcome { get; set; } = "";
    public bool Passed { get; set; }
    public bool CanonicalCoverageVerified { get; set; }
    public bool CleanupComplete { get; set; }
    public bool InputMayHaveOccurred { get; set; }
    public int SavedSteps { get; set; }
    public int EvidenceImages { get; set; }
    public int RecoveryAttempts { get; set; }
    public int? ModelTurns { get; set; }
    public int ProjectToolCalls { get; set; }
    public int ProjectCommandRuns { get; set; }
    public double StartupMs { get; set; }
    public double ExecutionMs { get; set; }
    public double CleanupMs { get; set; }
    public double WallMs { get; set; }
    public string ArtifactDirectory { get; set; } = "";
    public string WorkerResultPath { get; set; } = "";
    public static BenchmarkEntry From(WorkerResult run) => new()
    {
        ProductVersion = run.ProductVersion, RunId = run.Id, StartedAt = run.StartedAt, FinishedAt = run.FinishedAt,
        Workflow = run.TestName, TestId = run.TestId, TestSha256 = run.TestSha256, FrozenTestSha256 = run.FrozenTestSha256, TargetSha256 = run.TargetSha256, CliSha256 = run.CliSha256,
        TargetBuildSha256 = run.TargetBuildSha256, RunnerBuildSha256 = run.RunnerBuildSha256,
        ProjectConfigurationSha256 = run.ProjectConfigurationSha256, ProjectToolCalls = run.ProjectToolCalls, ProjectCommandRuns = run.ProjectCommandRuns,
        Mode = run.Mode, Driver = run.Driver, Provider = run.Provider, Model = run.Model, Outcome = run.Status, Passed = run.Passed,
        CanonicalCoverageVerified = run.CanonicalCoverageVerified, CleanupComplete = run.CleanupComplete, InputMayHaveOccurred = run.InputMayHaveOccurred,
        SavedSteps = run.SavedSteps, EvidenceImages = run.EvidenceImages, RecoveryAttempts = run.RecoveryAttempts, ModelTurns = run.ModelTurns,
        StartupMs = run.StartupMs, ExecutionMs = run.ExecutionMs, CleanupMs = run.CleanupMs, WallMs = run.WallMs,
        ArtifactDirectory = run.ArtifactDirectory, WorkerResultPath = Path.Combine(run.ArtifactDirectory, "worker-result.json")
    };
}

internal static class BenchmarkCommand
{
    public static async Task<object> ExportAsync(string source, string output, string format, CancellationToken ct)
    {
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Benchmark source directory does not exist.");
        if (format is not ("json" or "csv")) throw new ArgumentException("Benchmark format must be json or csv.");
        var entries = new List<BenchmarkEntry>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(source, "benchmark-entry.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (entries.Count >= 10_000) throw new InvalidDataException("Export is limited to 10,000 runs.");
            var entry = await WorkerCommand.ReadJsonAsync<BenchmarkEntry>(path, ct);
            if (!(entry.Schema == "testy.benchmark.v1" && entry.Product == "Testy" || entry.Schema == "axiom.benchmark.v1" && entry.Product == "Axiom") /* recorded before the rename */ || entry.Measurement != "observedOwnedWorkerRun") throw new InvalidDataException("Unrecognized benchmark entry schema or measurement provenance.");
            if (!ids.Add(entry.RunId)) throw new InvalidDataException("Duplicate run identity in benchmark source.");
            var workerPath = Path.Combine(Path.GetDirectoryName(path)!, "worker-result.json");
            var worker = await WorkerCommand.ReadJsonAsync<WorkerResult>(workerPath, ct);
            ValidateEntry(entry, worker);
            entries.Add(entry);
        }
        if (entries.Count == 0) throw new InvalidDataException("No observed worker benchmark entries were found.");
        var groups = entries.GroupBy(GroupKey, StringComparer.Ordinal).Select(g => new
        {
            identity = g.Key, workflow = g.First().Workflow,
            comparable = g.All(e => e.TestSha256.Length == 64 && e.TargetSha256.Length == 64 && e.CliSha256.Length == 64 && e.TargetBuildSha256.Length == 64 && e.RunnerBuildSha256.Length == 64),
            runs = g.Count(), passed = g.Count(e => e.Passed), failedOrUnavailable = g.Count(e => !e.Passed),
            passedExecutionMs = g.Where(e => e.Passed).Select(e => e.ExecutionMs).ToArray(),
            note = "Descriptive observations only. Same workload, top-level executable-directory binary fingerprints, individual executable hashes, project command configuration, driver, mode, provider and model are grouped. Nested plugins/external runtime dependencies and controlled hardware/load are not established; no general speed claim."
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (format == "json") WorkerCommand.DurableWrite(output, new
        {
            schema = "testy.benchmark.export.v1", generatedAt = DateTimeOffset.UtcNow,
            competitor = new { product = "TestComplete", availability = "notMeasured", executionMs = (double?)null, reason = "No comparative TestComplete workload was executed. No score or timing is inferred." },
            entries, groups
        });
        else
        {
            string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var file = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough), new UTF8Encoding(false)))
            {
                await file.WriteLineAsync(CsvRow(["product", "version", "measurement", "runId", "workflow", "testSha256", "frozenTestSha256", "targetSha256", "cliSha256", "targetBuildSha256", "runnerBuildSha256", "driver", "mode", "provider", "model", "outcome", "canonicalCoverageVerified", "startupMs", "executionMs", "cleanupMs", "wallMs", "savedSteps", "evidenceImages", "recoveryAttempts", "modelTurns", "projectConfigurationSha256", "projectToolCalls", "projectCommandRuns", "artifactDirectory"]));
                foreach (var e in entries) await file.WriteLineAsync(CsvRow([e.Product, e.ProductVersion, e.Measurement, e.RunId, e.Workflow, e.TestSha256, e.FrozenTestSha256, e.TargetSha256, e.CliSha256, e.TargetBuildSha256, e.RunnerBuildSha256, e.Driver, e.Mode, e.Provider, e.Model, e.Outcome, e.CanonicalCoverageVerified.ToString(), Number(e.StartupMs), Number(e.ExecutionMs), Number(e.CleanupMs), Number(e.WallMs), Number(e.SavedSteps), Number(e.EvidenceImages), Number(e.RecoveryAttempts), e.ModelTurns is int turns ? Number(turns) : "", e.ProjectConfigurationSha256, Number(e.ProjectToolCalls), Number(e.ProjectCommandRuns), e.ArtifactDirectory]));
                await file.FlushAsync(ct); ((FileStream)file.BaseStream).Flush(flushToDisk: true);
            }
            File.Move(temporary, output, true);
            WorkerCommand.DurableWrite(output + ".metadata.json", new { schema = "testy.benchmark.csv.v1", competitor = new { product = "TestComplete", availability = "notMeasured", executionMs = (double?)null }, groups, csvFormulaProtection = "Cells starting =,+,-,@ or tab/CR/LF are prefixed with an apostrophe for spreadsheet safety. JSON retains exact text." });
        }
        return new { output, format, entries = entries.Count, groups = groups.Length, competitor = "TestComplete not measured" };
    }
    internal static void ValidateEntry(BenchmarkEntry entry, WorkerResult worker)
    {
        WorkerCommand.Require(worker.FinishedAt >= worker.StartedAt && worker.Status is "passed" or "failed" or "cancelled" or "timedOut" or "invalidConfiguration" or "unavailable", "Benchmark source is not a finalized worker run.");
        var expected = BenchmarkEntry.From(worker);
        WorkerCommand.Require(JsonSerializer.Serialize(expected, TestyJson.Options) == JsonSerializer.Serialize(entry, TestyJson.Options), "Benchmark metrics do not match the adjacent worker terminal result.");
        WorkerCommand.Require(new[] { entry.StartupMs, entry.ExecutionMs, entry.CleanupMs, entry.WallMs }.All(n => double.IsFinite(n) && n >= 0), "Benchmark contains invalid timings.");
    }
    internal static string GroupKey(BenchmarkEntry e) => string.Join("|", e.TestSha256, e.TargetSha256, e.CliSha256, e.TargetBuildSha256, e.RunnerBuildSha256, e.ProjectConfigurationSha256, e.Driver, e.Mode, e.Provider, e.Model);
    internal static string CsvRow(IEnumerable<string> cells) => string.Join(',', cells.Select(cell =>
    {
        if (cell.Length > 0 && "=+-@\t\r\n".Contains(cell[0])) cell = "'" + cell;
        return "\"" + cell.Replace("\"", "\"\"") + "\"";
    }));
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
