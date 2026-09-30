using System.Text.Json;

namespace Testy.Core;

/// <summary>Credential-free request mailbox inside one privately staged guest run. The authenticated
/// PowerShell Direct owner forwards each request to its pinned host model context; no guest listener exists.</summary>
public static class CortexGuestRelay
{
    public const string DirectoryVariable = "TESTY_CORTEX_RELAY_DIRECTORY";
    public const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static bool Enabled => Environment.GetEnvironmentVariable(DirectoryVariable) is not null;
    public static async Task<JsonDocument> SendAsync(string body, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable(DirectoryVariable) ?? throw new InvalidOperationException("No Cortex guest relay."));
        if (!Directory.Exists(root)) throw new InvalidOperationException("The owned Cortex relay directory is unavailable.");
        OperationsStorage.NoReparse(root);
        if (System.Text.Encoding.UTF8.GetByteCount(body) > MaximumBytes) throw new InvalidDataException("Guest model request exceeds 16 MiB.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await Gate.WaitAsync(deadline.Token);
        string request = Path.Combine(root, "request.json"), response = Path.Combine(root, "response.json");
        var id = Guid.NewGuid().ToString("N");
        try
        {
            if (File.Exists(request) || File.Exists(response)) throw new InvalidOperationException("Guest relay contains an unfinished exchange; no model request was retried.");
            WorkspaceStore.WriteAtomic(request, new { id, body });
            while (!File.Exists(response)) await Task.Delay(100, deadline.Token);
            OperationsStorage.NoReparse(response);
            if (new FileInfo(response).Length > MaximumBytes * 2L) throw new InvalidDataException("Guest model response exceeds its bound.");
            using var envelope = JsonDocument.Parse(await File.ReadAllTextAsync(response, deadline.Token));
            var item = envelope.RootElement;
            if (item.GetProperty("id").GetString() != id) throw new InvalidDataException("Guest model response belongs to another request.");
            int status = item.GetProperty("status").GetInt32();
            if (status is < 200 or >= 300) throw new HttpRequestException("Cortex guest model relay returned HTTP " + status + ". No provider fallback is available.");
            return JsonDocument.Parse(item.GetProperty("body").GetString() ?? throw new InvalidDataException("Guest model response is empty."));
        }
        finally
        {
            // Mailbox data contains only model request/response material, never credentials. The run owns these exact paths.
            try { if (File.Exists(request)) File.Delete(request); if (File.Exists(response)) File.Delete(response); }
            finally { Gate.Release(); }
        }
    }
}
