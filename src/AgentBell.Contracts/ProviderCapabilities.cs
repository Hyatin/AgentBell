using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>Identifies a finite semantic capability understood by AgentBell.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityId>))]
public enum CapabilityId
{
    /// <summary>Observes completion of an agent turn.</summary>
    CompletionObservation,

    /// <summary>Observes that a provider emitted a permission request.</summary>
    PermissionRequestObservation,

    /// <summary>Determines whether an effective human reviewer is actually required.</summary>
    EffectiveReviewerDetection,

    /// <summary>Detects that the agent needs user input.</summary>
    InputRequiredDetection,

    /// <summary>Detects that the agent needs confirmation.</summary>
    ConfirmationDetection,

    /// <summary>Observes tool execution lifecycle events.</summary>
    ToolLifecycleObservation,

    /// <summary>Correlates a permission observation with later lifecycle events.</summary>
    PermissionLifecycleCorrelation,

    /// <summary>Supports installation through a user-level hook.</summary>
    UserLevelHookInstallation,

    /// <summary>Supports installation through a project-level hook.</summary>
    ProjectLevelHookInstallation,
}

/// <summary>Describes the strength of a provider capability and its semantic contract.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilitySupportLevel>))]
public enum CapabilitySupportLevel
{
    /// <summary>No stable source supports the capability.</summary>
    Unsupported,

    /// <summary>A candidate source exists but has no stability commitment.</summary>
    Experimental,

    /// <summary>The source is usable but its meaning requires conservative inference.</summary>
    BestEffort,

    /// <summary>Both the source and its meaning have a stable contract.</summary>
    Reliable,
}

/// <summary>Describes technical evidence for a provider capability.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvidenceKind>))]
public enum EvidenceKind
{
    /// <summary>No technical evidence source exists for an unsupported capability.</summary>
    None,

    /// <summary>A structured hook supplies the source event.</summary>
    StructuredHook,

    /// <summary>A documented provider lifecycle supplies the source event.</summary>
    DocumentedLifecycle,

    /// <summary>A conservative heuristic derives the capability.</summary>
    Heuristic,

    /// <summary>Another reviewed technical source establishes the capability.</summary>
    OtherVerifiedSource,
}

/// <summary>Describes one provider capability without runtime or user-policy state.</summary>
public sealed record AgentCapability
{
    /// <summary>Initializes one immutable provider capability.</summary>
    [JsonConstructor]
    public AgentCapability(
        CapabilityId capabilityId,
        CapabilitySupportLevel supportLevel,
        EvidenceKind evidenceKind,
        string? limitationKey)
    {
        CapabilityId = ContractValueValidation.ValidateEnum(capabilityId, nameof(capabilityId));
        SupportLevel = ContractValueValidation.ValidateEnum(supportLevel, nameof(supportLevel));
        EvidenceKind = ContractValueValidation.ValidateEnum(evidenceKind, nameof(evidenceKind));
        LimitationKey = ContractValueValidation.ValidateOptionalStableKey(limitationKey, nameof(limitationKey));
        if (supportLevel == CapabilitySupportLevel.Unsupported && evidenceKind != EvidenceKind.None
            || supportLevel != CapabilitySupportLevel.Unsupported && evidenceKind == EvidenceKind.None)
        {
            throw new ArgumentException("The evidence kind conflicts with the capability support level.");
        }
    }

    /// <summary>Gets the AgentBell capability taxonomy member.</summary>
    [JsonPropertyName("capabilityId")]
    public CapabilityId CapabilityId { get; }

    /// <summary>Gets the provider's support level for the capability.</summary>
    [JsonPropertyName("supportLevel")]
    public CapabilitySupportLevel SupportLevel { get; }

    /// <summary>Gets the reviewed technical evidence category.</summary>
    [JsonPropertyName("evidenceKind")]
    public EvidenceKind EvidenceKind { get; }

    /// <summary>Gets an optional stable localization key describing a limitation.</summary>
    [JsonPropertyName("limitationKey")]
    public string? LimitationKey { get; }
}

/// <summary>Contains an immutable, deterministically ordered provider capability set.</summary>
public sealed record AgentProviderCapabilities
{
    private readonly IReadOnlyDictionary<CapabilityId, AgentCapability> _lookup;

    /// <summary>Initializes a capability set for a provider.</summary>
    /// <exception cref="ArgumentException">A capability identifier occurs more than once.</exception>
    [JsonConstructor]
    public AgentProviderCapabilities(ProviderId providerId, IReadOnlyList<AgentCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _ = providerId.Value;
        ProviderId = providerId;
        if (capabilities.Any(capability => capability is null))
        {
            throw new ArgumentException("The capability set cannot contain null entries.", nameof(capabilities));
        }

        var ordered = capabilities.OrderBy(capability => capability.CapabilityId).ToArray();
        var duplicate = ordered
            .GroupBy(capability => capability.CapabilityId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException("The capability set contains a duplicate identifier.", nameof(capabilities));
        }

        Capabilities = new ReadOnlyCollection<AgentCapability>(ordered);
        _lookup = new ReadOnlyDictionary<CapabilityId, AgentCapability>(
            ordered.ToDictionary(capability => capability.CapabilityId));
    }

    /// <summary>Gets the provider described by this capability set.</summary>
    [JsonPropertyName("providerId")]
    public ProviderId ProviderId { get; }

    /// <summary>Gets the capabilities in deterministic identifier order.</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<AgentCapability> Capabilities { get; }

    /// <summary>Gets a declared capability, or <see langword="null"/> when it is absent.</summary>
    public AgentCapability? Get(CapabilityId capabilityId)
    {
        ContractValueValidation.ValidateEnum(capabilityId, nameof(capabilityId));
        return _lookup.GetValueOrDefault(capabilityId);
    }

    /// <summary>
    /// Gets the declared support level, treating a missing declaration as unsupported while
    /// <see cref="Get"/> preserves the distinction from an explicit declaration.
    /// </summary>
    public CapabilitySupportLevel GetSupportLevel(CapabilityId capabilityId) =>
        Get(capabilityId)?.SupportLevel ?? CapabilitySupportLevel.Unsupported;
}
