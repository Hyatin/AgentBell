namespace AgentBell.Integration.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessIsolatedIntegrationCollection
{
    public const string Name = "Process-isolated integration tests";
}
