namespace Testy.IdleFixture;

/// <summary>Sleeps for the given number of milliseconds (30 s by default; at most 5 minutes) and exits with 0. It opens no window and reads no input.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--cortex-environment-report")
        {
            var names = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(name => name.StartsWith("TESTY_CORTEX_", StringComparison.OrdinalIgnoreCase)).Order().ToArray();
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new
            {
                bridgeNames = names,
                unicode = Environment.GetEnvironmentVariable("TESTY_ENVIRONMENT_TEST_UNICODE"),
                removed = Environment.GetEnvironmentVariable("TESTY_ENVIRONMENT_TEST_REMOVED")
            }));
            Thread.Sleep(500);
            return 0;
        }
        var milliseconds = args.Length > 0 && int.TryParse(args[0], out var requested) ? Math.Clamp(requested, 0, 300_000) : 30_000;
        Thread.Sleep(milliseconds);
        return 0;
    }
}
