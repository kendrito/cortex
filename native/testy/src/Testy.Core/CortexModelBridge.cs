using System.Net.Http.Headers;

namespace Testy.Core;

/// <summary>Process-owned model routing for Cortex's packaged Testy. No provider credential leaves Cortex.</summary>
public static class CortexModelBridge
{
    public const string UrlVariable = "TESTY_CORTEX_BRIDGE_URL", TokenVariable = "TESTY_CORTEX_BRIDGE_TOKEN", ContextVariable = "TESTY_CORTEX_CONTEXT";
    private static readonly AsyncLocal<string?> Current = new();
    public static bool Enabled => Environment.GetEnvironmentVariable("TESTY_CORTEX_INTEGRATED") == "1"
        || Environment.GetEnvironmentVariable(UrlVariable) is not null || Environment.GetEnvironmentVariable(TokenVariable) is not null || CortexGuestRelay.Enabled;
    public static string Endpoint
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(UrlVariable);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.AbsolutePath != "/v1/chat/completions")
                throw new InvalidOperationException("Cortex's authenticated local model bridge is unavailable. Restart Testy from Cortex.");
            return uri.AbsoluteUri;
        }
    }
    public static string Token => Environment.GetEnvironmentVariable(TokenVariable) is { Length: >= 24 } value
        ? value : throw new InvalidOperationException("Cortex's model bridge credential is unavailable. Restart Testy from Cortex.");
    public static string? Context => Current.Value ?? Environment.GetEnvironmentVariable(ContextVariable);
    public static IDisposable Enter(string? context)
    {
        if (context is not null && (context.Length is < 16 or > 256 || context.Any(char.IsWhiteSpace) || context.Any(char.IsControl)))
            throw new InvalidDataException("Invalid Cortex operation context.");
        var previous = Current.Value;
        Current.Value = context;
        return new Scope(() => Current.Value = previous);
    }
    public static void BindRequest(HttpRequestMessage request)
    {
        if (!Enabled) return;
        if (Context is not { Length: >= 16 } context) throw new InvalidOperationException("Start this AI operation from Cortex. No Cortex model context was supplied.");
        if (request.RequestUri?.AbsoluteUri != Endpoint) throw new InvalidOperationException("Integrated Testy can only use Cortex's model bridge.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Add("X-Testy-Cortex-Context", context);
    }
    /// <summary>Carry the host-issued operation into a Testy worker without writing it to the workspace.</summary>
    public static void BindChild(System.Diagnostics.ProcessStartInfo start)
    {
        if (!Enabled) return;
        start.Environment["TESTY_CORTEX_INTEGRATED"] = "1";
        if (CortexGuestRelay.Enabled)
        {
            start.Environment[CortexGuestRelay.DirectoryVariable] = Environment.GetEnvironmentVariable(CortexGuestRelay.DirectoryVariable);
            start.Environment.Remove(UrlVariable); start.Environment.Remove(TokenVariable); start.Environment.Remove(ContextVariable);
            return;
        }
        start.Environment[UrlVariable] = Endpoint;
        start.Environment[TokenVariable] = Token;
        if (Context is { } context) start.Environment[ContextVariable] = context;
    }
    /// <summary>Application targets never inherit Cortex's private bridge credential or context.</summary>
    public static void ScrubTarget(System.Diagnostics.ProcessStartInfo start)
    {
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("TESTY_CORTEX_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
    }
    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? callback = restore;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
