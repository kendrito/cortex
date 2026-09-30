using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace Testy.Core;

public sealed class WorkspaceStore
{
    private readonly object gate = new();
    public string RootDirectory { get; }
    public WorkspaceStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("A workspace directory is required.", nameof(rootDirectory));
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(Path.Combine(RootDirectory, "tests"));
        Directory.CreateDirectory(Path.Combine(RootDirectory, "runs"));
    }
    public List<TestCase> LoadTests() { lock (gate) return ReadDirectory<TestCase>("tests").OrderByDescending(t => t.UpdatedAt).ToList(); }
    public List<RunResult> LoadRuns() { lock (gate) return ReadDirectory<RunResult>("runs").OrderByDescending(r => r.StartedAt).ToList(); }
    public static string Revision(TestCase test) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(test, TestyJson.Options))));
    private void CheckRevision(string id, string? expectedRevision)
    {
        if (expectedRevision is null) return;
        var path = Path.Combine(RootDirectory, "tests", id + ".json");
        if (!File.Exists(path) || Revision(Read<TestCase>(path)) != expectedRevision)
            throw new InvalidDataException("This test changed outside this editor. Reload it before saving or deleting.");
    }
    public void SaveTest(TestCase test) => SaveTest(test, null);
    public void SaveTest(TestCase test, string? expectedRevision)
    {
        TestValidator.Validate(test);
        lock (gate)
        {
            using var fileLock = OperationsStorage.Lock(Path.Combine(RootDirectory, "tests-write.lock"));
            CheckRevision(test.Id, expectedRevision);
            test.UpdatedAt = DateTimeOffset.UtcNow;
            WriteAtomic(Path.Combine(RootDirectory, "tests", test.Id + ".json"), test);
        }
    }
    public void SaveDraft(TestCase test)
    {
        ArgumentNullException.ThrowIfNull(test);
        TestValidator.ValidateId(test.Id);
        if (string.IsNullOrWhiteSpace(test.Name) || test.Name.Length > 1000) throw new InvalidDataException("Draft name must contain 1–1000 characters.");
        if (test.Steps is null || test.Steps.Count > TestValidator.MaximumSteps) throw new InvalidDataException("Drafts may contain at most 200 steps.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in test.Steps)
        {
            if (step is null) throw new InvalidDataException("Draft steps cannot be null.");
            TestValidator.ValidateId(step.Id);
            if (!ids.Add(step.Id)) throw new InvalidDataException("Draft step IDs must be unique.");
            if (!Enum.IsDefined(step.Action)) throw new InvalidDataException("Unknown draft step action.");
        }
        lock (gate)
        {
            using var fileLock = OperationsStorage.Lock(Path.Combine(RootDirectory, "tests-write.lock"));
            test.UpdatedAt = DateTimeOffset.UtcNow;
            WriteAtomic(Path.Combine(RootDirectory, "tests", test.Id + ".json"), test);
        }
    }
    public void DeleteTest(string id) => DeleteTest(id, null);
    public void DeleteTest(string id, string? expectedRevision)
    {
        TestValidator.ValidateId(id);
        lock (gate)
        {
            using var fileLock = OperationsStorage.Lock(Path.Combine(RootDirectory, "tests-write.lock"));
            CheckRevision(id, expectedRevision);
            File.Delete(Path.Combine(RootDirectory, "tests", id + ".json"));
        }
    }
    public void SaveRun(RunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        TestValidator.ValidateId(run.Id);
        lock (gate) WriteAtomic(Path.Combine(RootDirectory, "runs", run.Id + ".json"), run);
    }
    public ProviderSettings LoadSettings()
    {
        lock (gate)
        {
            var path = Path.Combine(RootDirectory, "settings.json");
            var settings = File.Exists(path) ? ReadProviderSettings(path) : new ProviderSettings();
            ApplySelectedProject(settings, validateEnvironment: false);
            return settings;
        }
    }
    public void SaveSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Kind)) throw new InvalidDataException("Unknown provider kind.");
        lock (gate)
        {
            ApplySelectedProject(settings, validateEnvironment: true);
            WriteAtomic(Path.Combine(RootDirectory, "settings.json"), settings);
        }
    }
    /// <summary>Configure the explicit project independently of Studio's visual connection editor.</summary>
    public void ConfigureProjectTools(ProjectToolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ProjectToolSession.ValidateConfiguration(settings);
        lock (gate) WriteAtomic(Path.Combine(RootDirectory, "project-tools.json"), TestyJson.Clone(settings));
    }
    private void ApplySelectedProject(ProviderSettings settings, bool validateEnvironment)
    {
        var path = Path.Combine(RootDirectory, "project-tools.json");
        if (File.Exists(path)) settings.ProjectTools = validateEnvironment
            ? ProjectConfiguration.Parse(File.ReadAllText(path)) : ProjectConfiguration.ReadStored(File.ReadAllText(path));
        else if (settings.ProjectTools is null && File.Exists(Path.Combine(RootDirectory, "settings.json")))
            settings.ProjectTools = ReadProviderSettings(Path.Combine(RootDirectory, "settings.json")).ProjectTools;
        if (validateEnvironment && settings.ProjectTools is not null) ProjectToolSession.ValidateConfiguration(settings.ProjectTools);
    }
    private static ProviderSettings ReadProviderSettings(string path)
    {
        var settings = Read<ProviderSettings>(path);
        // Validate the original embedded JSON before deserialization could discard unknown fields.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var projects = document.RootElement.EnumerateObject().Where(p => p.Name.Equals("projectTools", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projects.Length > 1) throw new InvalidDataException("Stored provider settings contain duplicate project selections.");
        if (projects.Length == 1 && projects[0].Value.ValueKind != JsonValueKind.Null)
            settings.ProjectTools = ProjectConfiguration.ReadStored(projects[0].Value.GetRawText());
        return settings;
    }
    private List<T> ReadDirectory<T>(string directory) => Directory.EnumerateFiles(Path.Combine(RootDirectory, directory), "*.json").Select(Read<T>).ToList();
    private static T Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), TestyJson.Options) ?? throw new JsonException("Empty JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"Invalid JSON in '{path}': {ex.Message}", ex); }
    }
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, TestyJson.Options), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
