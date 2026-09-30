using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class ProductShellChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("CSV preserves quoted Unicode, multiline values and formula-like text literally", () =>
        {
            var rows = TestDataCsv.Parse("name,note\r\n\"Zoë, QA\",\"line1\n\"\"line2\"\"\"\r\n=1+1,\r\n");
            Require(rows.Count == 2 && rows[0]["name"] == "Zoë, QA" && rows[0]["note"] == "line1\n\"line2\"" && rows[1]["name"] == "=1+1" && rows[1]["note"] == "");
            return Task.CompletedTask;
        });
        yield return ("CSV rejects malformed quotes, duplicate headers, missing cells and extra columns", () =>
        {
            foreach (var value in new[] { "a,a\nx,y", "a,b\nx", "a\nx,y", "a\n\"unterminated", "a\n\"x\"bad", "a\nx\"y" })
            {
                try { TestDataCsv.Parse(value); throw new Exception("Malformed CSV was accepted."); } catch (InvalidDataException) { }
            }
            return Task.CompletedTask;
        });
        yield return ("credential target identity is bound to exact endpoint and key slot", () =>
        {
            var settings = new ProviderSettings { Kind = ProviderKind.Compatible, Endpoint = "https://example.invalid/v1/chat/completions", ApiKeyEnvironmentVariable = "TESTY_TEST" };
            Require(ProviderCredentialStore.TryTarget(settings, out var first));
            settings.Endpoint = "https://another.invalid/v1/chat/completions";
            Require(ProviderCredentialStore.TryTarget(settings, out var second) && first != second);
            settings.Endpoint = "http://example.invalid/api"; Require(!ProviderCredentialStore.TryTarget(settings, out _));
            settings.Endpoint = "https://user:secret@example.invalid/api"; Require(!ProviderCredentialStore.TryTarget(settings, out _));
            return Task.CompletedTask;
        });
        yield return ("Windows vault round-trip keeps provider settings secret-free and deletes owned test credential", () =>
        {
            var settings = new ProviderSettings { Kind = ProviderKind.Compatible, Endpoint = "https://testy-vault-test.invalid/" + Guid.NewGuid().ToString("N"), ApiKeyEnvironmentVariable = "TESTY_TEST_" + Guid.NewGuid().ToString("N") };
            const string secret = "owned-vault-test-credential-東京";
            try
            {
                ProviderCredentialStore.Save(settings, secret);
                Require(ProviderCredentialStore.HasStored(settings) && ProviderCredentialStore.Resolve(settings) == secret);
                Require(!JsonSerializer.Serialize(settings, TestyJson.Options).Contains(secret, StringComparison.Ordinal));
                var other = TestyJson.Clone(settings); other.Endpoint += "/other"; Require(ProviderCredentialStore.Resolve(other) is null);
            }
            finally { ProviderCredentialStore.Delete(settings); }
            Require(!ProviderCredentialStore.HasStored(settings)); return Task.CompletedTask;
        });
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Product shell contract was not preserved."); }
}
