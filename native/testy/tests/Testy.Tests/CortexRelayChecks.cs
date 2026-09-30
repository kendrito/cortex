using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class CortexRelayChecks
{
    public static (string Name, Func<Task> Execute)[] All() =>
    [
        ("Cortex guest relay forwards requests without a provider credential", Exchange),
        ("Cortex guest relay rejects foreign responses and cleans cancellation", Refusal),
        ("Cortex guest relay refuses oversized requests before writing", Bounds)
    ];
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private sealed class Scope : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "testy-cortex-relay-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> previous = new();
        public Scope()
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var item in new Dictionary<string, string?> { ["TESTY_CORTEX_INTEGRATED"] = "1", [CortexGuestRelay.DirectoryVariable] = Directory, [CortexModelBridge.UrlVariable] = null, [CortexModelBridge.TokenVariable] = null, [CortexModelBridge.ContextVariable] = null })
            { previous[item.Key] = Environment.GetEnvironmentVariable(item.Key); Environment.SetEnvironmentVariable(item.Key, item.Value); }
        }
        public void Dispose() { foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value); System.IO.Directory.Delete(Directory, true); }
    }
    private static async Task<JsonElement> Request(string root)
    {
        var path = Path.Combine(root, "request.json"); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path)) await Task.Delay(10, stop.Token);
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(path, stop.Token)); return request.RootElement.Clone();
    }
    private static async Task Exchange()
    {
        using var scope = new Scope();
        Check(ProviderCredentialStore.Resolve(new()) is null, "Guest resolved a provider key.");
        OperationsTestValidation.Validate(new OperationsTestRequest { Executable = @"C:\fixture.exe", Provider = new(), Test = new() { Steps = [new() { Action = StepAction.AssertExists, Selector = "id:expected" }] } }, OperationsTarget.ForVirtualMachine(Guid.NewGuid()));
        var child = new System.Diagnostics.ProcessStartInfo("testy.exe"); CortexModelBridge.BindChild(child);
        Check(child.Environment[CortexGuestRelay.DirectoryVariable] == scope.Directory && !child.Environment.ContainsKey(CortexModelBridge.TokenVariable), "Owned guest worker lost relay or inherited key.");
        var planner = new CompatiblePlanner(new ProviderSettings()); var pending = planner.ExplainAsync(new RunResult { Summary = "A recorded failure" });
        var request = await Request(scope.Directory); using var body = JsonDocument.Parse(request.GetProperty("body").GetString()!);
        Check(body.RootElement.GetProperty("model").GetString() == "cortex", "Guest changed model.");
        Check(request.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "body", "id" }), "Guest envelope contains extra authority.");
        File.Delete(Path.Combine(scope.Directory, "request.json"));
        WorkspaceStore.WriteAtomic(Path.Combine(scope.Directory, "response.json"), new { id = request.GetProperty("id").GetString(), status = 200, body = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Observed failure.\"}}]}" });
        Check(await pending == "Observed failure." && !System.IO.Directory.EnumerateFiles(scope.Directory).Any(), "Guest exchange failed or left mailbox data.");
    }
    private static async Task Refusal()
    {
        using var scope = new Scope(); var pending = CortexGuestRelay.SendAsync("{}", default); await Request(scope.Directory);
        WorkspaceStore.WriteAtomic(Path.Combine(scope.Directory, "response.json"), new { id = Guid.NewGuid().ToString("N"), status = 200, body = "{}" });
        try { using var response = await pending; throw new Exception("Foreign response accepted."); } catch (InvalidDataException) { }
        using var stop = new CancellationTokenSource(); var cancelled = CortexGuestRelay.SendAsync("{}", stop.Token); await Request(scope.Directory); stop.Cancel();
        try { using var response = await cancelled; throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        Check(!System.IO.Directory.EnumerateFiles(scope.Directory).Any(), "Cancelled exchange left model material.");
    }
    private static async Task Bounds()
    {
        using var scope = new Scope();
        try { using var response = await CortexGuestRelay.SendAsync(new string('x', CortexGuestRelay.MaximumBytes + 1), default); throw new Exception("Oversized request accepted."); } catch (InvalidDataException) { }
        Check(!System.IO.Directory.EnumerateFiles(scope.Directory).Any(), "Oversized request was written.");
    }
}
