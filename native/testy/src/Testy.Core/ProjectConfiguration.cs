using System.Text.Json;
using System.Text.Json.Serialization;

namespace Testy.Core;

public static class ProjectConfiguration
{
    private const int MaximumBytes = 256 * 1024;
    public static ProjectToolSettings Parse(string json) => ParseCore(json, validateEnvironment: true);
    /// <summary>Decode a saved selection without requiring its repository or SDK to be available at application startup.</summary>
    internal static ProjectToolSettings ReadStored(string json) => ParseCore(json, validateEnvironment: false);
    private static ProjectToolSettings ParseCore(string json, bool validateEnvironment)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Project configuration exceeds 256 KiB.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Project configuration must be a JSON object.");
        RejectDuplicateFields(document.RootElement);
        var options = new JsonSerializerOptions(TestyJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var settings = JsonSerializer.Deserialize<ProjectToolSettings>(json, options) ?? throw new InvalidDataException("Project configuration cannot be null.");
        if (validateEnvironment) ProjectToolSession.ValidateConfiguration(settings);
        return settings;
    }
    public static async Task<ProjectToolSettings> LoadAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.Length > MaximumBytes) throw new InvalidDataException("Project configuration is missing or exceeds 256 KiB.");
        return Parse(await File.ReadAllTextAsync(info.FullName, ct));
    }
    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) throw new InvalidDataException("Project configuration properties cannot be null.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Project configuration contains a duplicate property.");
                RejectDuplicateFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }
}
