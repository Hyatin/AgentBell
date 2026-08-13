using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Contracts.Tests;

public sealed class ProviderCapabilitiesTests
{
    [Fact]
    public void CapabilitySet_CopiesInputAndOrdersDeterministically()
    {
        var input = new List<AgentCapability>
        {
            Capability(CapabilityId.ToolLifecycleObservation),
            Capability(CapabilityId.CompletionObservation),
        };
        var set = new AgentProviderCapabilities(ProviderIds.Codex, input);
        input.Clear();

        Assert.Equal(2, set.Capabilities.Count);
        Assert.Equal(CapabilityId.CompletionObservation, set.Capabilities[0].CapabilityId);
        Assert.Equal(CapabilityId.ToolLifecycleObservation, set.Capabilities[1].CapabilityId);
        Assert.IsAssignableFrom<IReadOnlyList<AgentCapability>>(set.Capabilities);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgentCapability>)set.Capabilities).Add(Capability(CapabilityId.InputRequiredDetection)));
    }

    [Fact]
    public void CapabilitySet_DuplicateCapability_IsRejected()
    {
        var duplicate = Capability(CapabilityId.CompletionObservation);

        Assert.Throws<ArgumentException>(() => new AgentProviderCapabilities(
            ProviderIds.Codex,
            [duplicate, duplicate with { }]));
    }

    [Fact]
    public void CapabilitySet_LookupDistinguishesExplicitUnsupportedFromMissing()
    {
        var unsupported = new AgentCapability(
            CapabilityId.EffectiveReviewerDetection,
            CapabilitySupportLevel.Unsupported,
            EvidenceKind.None,
            "capability.codex.effective-reviewer-unavailable");
        var set = new AgentProviderCapabilities(ProviderIds.Codex, [unsupported]);

        Assert.Same(unsupported, set.Get(CapabilityId.EffectiveReviewerDetection));
        Assert.Null(set.Get(CapabilityId.CompletionObservation));
        Assert.Equal(
            CapabilitySupportLevel.Unsupported,
            set.GetSupportLevel(CapabilityId.CompletionObservation));
    }

    [Fact]
    public void CapabilitySet_Json_IsDeterministicAndProviderIdRemainsAString()
    {
        var first = new AgentProviderCapabilities(
            ProviderIds.Codex,
            [
                Capability(CapabilityId.ToolLifecycleObservation),
                Capability(CapabilityId.CompletionObservation),
            ]);
        var second = new AgentProviderCapabilities(
            ProviderIds.Codex,
            [
                Capability(CapabilityId.CompletionObservation),
                Capability(CapabilityId.ToolLifecycleObservation),
            ]);

        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);
        var roundTrip = JsonSerializer.Deserialize<AgentProviderCapabilities>(firstJson);
        using var document = JsonDocument.Parse(firstJson);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal("codex", document.RootElement.GetProperty("providerId").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("capabilities").GetArrayLength());
        Assert.NotNull(roundTrip);
        Assert.Equal(ProviderIds.Codex, roundTrip.ProviderId);
        Assert.Equal(
            CapabilitySupportLevel.Reliable,
            roundTrip.GetSupportLevel(CapabilityId.CompletionObservation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Capability Description")]
    [InlineData("capability_codex")]
    [InlineData("capability..codex")]
    [InlineData("capability.codex.")]
    public void AgentCapability_InvalidLimitationKey_IsRejected(string limitationKey)
    {
        Assert.Throws<ArgumentException>(() => new AgentCapability(
            CapabilityId.InputRequiredDetection,
            CapabilitySupportLevel.BestEffort,
            EvidenceKind.Heuristic,
            limitationKey));
    }

    [Fact]
    public void AgentCapability_OverlongLimitationKey_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new AgentCapability(
            CapabilityId.InputRequiredDetection,
            CapabilitySupportLevel.BestEffort,
            EvidenceKind.Heuristic,
            $"capability.{new string('a', 96)}"));
    }

    [Theory]
    [InlineData(CapabilitySupportLevel.Unsupported, EvidenceKind.StructuredHook)]
    [InlineData(CapabilitySupportLevel.Reliable, EvidenceKind.None)]
    public void AgentCapability_EvidenceMustMatchSupport(
        CapabilitySupportLevel supportLevel,
        EvidenceKind evidenceKind)
    {
        Assert.Throws<ArgumentException>(() => new AgentCapability(
            CapabilityId.CompletionObservation,
            supportLevel,
            evidenceKind,
            null));
    }

    [Fact]
    public void CapabilityContracts_DoNotContainUserPreferences()
    {
        var propertyNames = typeof(AgentProviderCapabilities).GetProperties()
            .Concat(typeof(AgentCapability).GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Policy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Preference", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Notify", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("UserPolicy", Enum.GetNames<EvidenceKind>());
    }

    private static AgentCapability Capability(CapabilityId capabilityId) => new(
        capabilityId,
        CapabilitySupportLevel.Reliable,
        EvidenceKind.StructuredHook,
        null);
}
