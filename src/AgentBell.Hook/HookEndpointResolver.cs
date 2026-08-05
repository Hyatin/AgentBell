namespace AgentBell.Hook;

/// <summary>Resolves the fixed production endpoint or an explicitly isolated test endpoint.</summary>
public static class HookEndpointResolver
{
    /// <summary>Enables test-only endpoint overrides when its value is exactly 1.</summary>
    public const string TestModeEnvironmentVariable = "AGENTBELL_TEST_MODE";

    /// <summary>Supplies the isolated loopback test Host port.</summary>
    public const string TestLoopbackPortEnvironmentVariable = "AGENTBELL_TEST_LOOPBACK_PORT";

    /// <summary>The immutable production ingestion endpoint.</summary>
    public static Uri ProductionEndpoint { get; } =
        new("http://127.0.0.1:17863/api/v1/events/codex");

    /// <summary>
    /// Resolves a test port only when test mode is explicit. Invalid test settings
    /// fail closed to loopback port 1 and can never fall back to production.
    /// </summary>
    public static Uri Resolve(Func<string, string?>? environmentReader = null)
    {
        environmentReader ??= Environment.GetEnvironmentVariable;
        if (!string.Equals(
            environmentReader(TestModeEnvironmentVariable),
            "1",
            StringComparison.Ordinal))
        {
            return ProductionEndpoint;
        }

        var value = environmentReader(TestLoopbackPortEnvironmentVariable);
        var port = int.TryParse(value, out var parsedPort)
            && parsedPort is >= 1024 and <= 65535
                ? parsedPort
                : 1;
        return new Uri($"http://127.0.0.1:{port}/api/v1/events/codex");
    }
}
