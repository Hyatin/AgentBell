namespace AgentBell.Hook;

/// <summary>Resolves the fixed production endpoint or an explicitly isolated test endpoint.</summary>
public static class HookEndpointResolver
{
    /// <summary>Enables test-only endpoint overrides when its value is exactly 1.</summary>
    public const string TestModeEnvironmentVariable = "AGENTBELL_TEST_MODE";

    /// <summary>Supplies the isolated loopback test Host port.</summary>
    public const string TestLoopbackPortEnvironmentVariable = "AGENTBELL_TEST_LOOPBACK_PORT";

    /// <summary>Supplies a bounded total forwarding timeout only to isolated test processes.</summary>
    public const string TestForwardTimeoutEnvironmentVariable = "AGENTBELL_TEST_FORWARD_TIMEOUT_MS";

    /// <summary>Supplies a bounded connection timeout only to isolated test processes.</summary>
    public const string TestConnectTimeoutEnvironmentVariable = "AGENTBELL_TEST_CONNECT_TIMEOUT_MS";

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

    /// <summary>
    /// Resolves a bounded timeout only when explicit test mode is enabled. Production
    /// and invalid test values retain the forwarder's established default.
    /// </summary>
    public static TimeSpan? ResolveTestForwardTimeout(
        Func<string, string?>? environmentReader = null)
        => ResolveTestTimeout(
            TestForwardTimeoutEnvironmentVariable,
            maximumMilliseconds: 10_000,
            environmentReader);

    /// <summary>Resolves a bounded loopback connection timeout only in explicit test mode.</summary>
    public static TimeSpan? ResolveTestConnectTimeout(
        Func<string, string?>? environmentReader = null)
        => ResolveTestTimeout(
            TestConnectTimeoutEnvironmentVariable,
            maximumMilliseconds: 5_000,
            environmentReader);

    private static TimeSpan? ResolveTestTimeout(
        string variableName,
        int maximumMilliseconds,
        Func<string, string?>? environmentReader)
    {
        environmentReader ??= Environment.GetEnvironmentVariable;
        if (!string.Equals(
                environmentReader(TestModeEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        var value = environmentReader(variableName);
        return int.TryParse(value, out var milliseconds)
               && milliseconds >= 100
               && milliseconds <= maximumMilliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
    }
}
