using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>Classifies provider events by their provider-neutral meaning.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SemanticEventKind>))]
public enum SemanticEventKind
{
    /// <summary>An agent turn completed.</summary>
    TurnCompleted,

    /// <summary>A permission request was observed without proof that human action is required.</summary>
    PermissionObserved,

    /// <summary>A provider contract establishes that a permission decision is required.</summary>
    PermissionRequired,

    /// <summary>The agent requires user input.</summary>
    InputRequired,

    /// <summary>The agent requires user confirmation.</summary>
    ConfirmationRequired,

    /// <summary>The agent is blocked and requires user attention.</summary>
    AttentionRequired,
}

/// <summary>States whether the normalized event establishes a need for user action.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionRequirement>))]
public enum ActionRequirement
{
    /// <summary>The event does not require user action.</summary>
    None,

    /// <summary>The source does not establish whether user action is required.</summary>
    Unknown,

    /// <summary>The source establishes that user action is required.</summary>
    Required,
}

/// <summary>Describes confidence in the meaning of one normalized event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SemanticReliability>))]
public enum SemanticReliability
{
    /// <summary>The event meaning follows a stable source contract.</summary>
    Reliable,

    /// <summary>The event meaning was derived conservatively from an available source.</summary>
    BestEffort,
}

/// <summary>Describes coarse confidence in a bounded classification rule.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassificationConfidence>))]
public enum ClassificationConfidence
{
    /// <summary>The rule has low confidence.</summary>
    Low,

    /// <summary>The rule has medium confidence.</summary>
    Medium,

    /// <summary>The rule has high confidence.</summary>
    High,
}

/// <summary>Records safe classifier provenance without matched text or numeric pseudo-precision.</summary>
public sealed record EventClassification
{
    /// <summary>Initializes safe classification provenance.</summary>
    [JsonConstructor]
    public EventClassification(string ruleId, ClassificationConfidence confidence)
    {
        RuleId = ContractValueValidation.ValidateStableKey(ruleId, nameof(ruleId));
        Confidence = ContractValueValidation.ValidateEnum(confidence, nameof(confidence));
    }

    /// <summary>Gets the bounded, stable classifier rule identifier.</summary>
    [JsonPropertyName("ruleId")]
    public string RuleId { get; }

    /// <summary>Gets coarse, non-probabilistic confidence in the classification.</summary>
    [JsonPropertyName("confidence")]
    public ClassificationConfidence Confidence { get; }
}

/// <summary>
/// Represents one immutable provider-neutral event with only bounded, sanitized information.
/// </summary>
public sealed record NormalizedAgentEvent
{
    /// <summary>Initializes a provider-neutral event and enforces semantic and privacy invariants.</summary>
    [JsonConstructor]
    public NormalizedAgentEvent(
        ProviderId providerId,
        SourceEventKind sourceEventKind,
        SemanticEventKind semanticEventKind,
        DateTimeOffset occurredAt,
        string eventId,
        string? sessionIdHash,
        string? turnIdHash,
        string? toolUseIdHash,
        string? project,
        string? safeSummary,
        string toolCategory,
        EventClassification? classification,
        SemanticReliability semanticReliability,
        ActionRequirement actionRequirement)
    {
        _ = providerId.Value;
        _ = sourceEventKind.Value;
        ProviderId = providerId;
        SourceEventKind = sourceEventKind;
        SemanticEventKind = ContractValueValidation.ValidateEnum(semanticEventKind, nameof(semanticEventKind));
        OccurredAt = occurredAt != default
            ? occurredAt
            : throw new ArgumentOutOfRangeException(nameof(occurredAt), "The occurrence time is required.");
        EventId = ContractValueValidation.ValidateEventId(eventId, nameof(eventId));
        SessionIdHash = ContractValueValidation.ValidateOptionalHash(sessionIdHash, nameof(sessionIdHash));
        TurnIdHash = ContractValueValidation.ValidateOptionalHash(turnIdHash, nameof(turnIdHash));
        ToolUseIdHash = ContractValueValidation.ValidateOptionalHash(toolUseIdHash, nameof(toolUseIdHash));
        Project = ContractValueValidation.ValidateOptionalProject(project, nameof(project));
        SafeSummary = ContractValueValidation.ValidateOptionalSafeSummary(safeSummary, nameof(safeSummary));
        ToolCategory = ContractValueValidation.ValidateToolCategory(toolCategory, nameof(toolCategory));
        Classification = classification;
        SemanticReliability = ContractValueValidation.ValidateEnum(
            semanticReliability,
            nameof(semanticReliability));
        ActionRequirement = ContractValueValidation.ValidateEnum(actionRequirement, nameof(actionRequirement));
        ValidateSemanticActionRelationship(SemanticEventKind, ActionRequirement);
    }

    /// <summary>Gets the stable provider namespace.</summary>
    [JsonPropertyName("providerId")]
    public ProviderId ProviderId { get; }

    /// <summary>Gets the canonical event kind within the provider namespace.</summary>
    [JsonPropertyName("sourceEventKind")]
    public SourceEventKind SourceEventKind { get; }

    /// <summary>Gets the provider-neutral event meaning.</summary>
    [JsonPropertyName("semanticEventKind")]
    public SemanticEventKind SemanticEventKind { get; }

    /// <summary>Gets when the provider event occurred or was accepted.</summary>
    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Gets the bounded, provider-namespaced normalized event identifier.</summary>
    [JsonPropertyName("eventId")]
    public string EventId { get; }

    /// <summary>Gets an optional deterministic truncated session identifier hash.</summary>
    [JsonPropertyName("sessionIdHash")]
    public string? SessionIdHash { get; }

    /// <summary>Gets an optional deterministic truncated turn identifier hash.</summary>
    [JsonPropertyName("turnIdHash")]
    public string? TurnIdHash { get; }

    /// <summary>Gets an optional deterministic truncated tool-use identifier hash.</summary>
    [JsonPropertyName("toolUseIdHash")]
    public string? ToolUseIdHash { get; }

    /// <summary>Gets a bounded project basename, never a complete path.</summary>
    [JsonPropertyName("project")]
    public string? Project { get; }

    /// <summary>Gets a normalized summary containing at most 160 Unicode text elements.</summary>
    [JsonPropertyName("safeSummary")]
    public string? SafeSummary { get; }

    /// <summary>Gets an existing provider-neutral allow-listed tool category.</summary>
    [JsonPropertyName("toolCategory")]
    public string ToolCategory { get; }

    /// <summary>Gets optional bounded classifier provenance.</summary>
    [JsonPropertyName("classification")]
    public EventClassification? Classification { get; }

    /// <summary>Gets confidence in this event's normalized meaning.</summary>
    [JsonPropertyName("semanticReliability")]
    public SemanticReliability SemanticReliability { get; }

    /// <summary>Gets whether the source establishes a need for user action.</summary>
    [JsonPropertyName("actionRequirement")]
    public ActionRequirement ActionRequirement { get; }

    private static void ValidateSemanticActionRelationship(
        SemanticEventKind semanticEventKind,
        ActionRequirement actionRequirement)
    {
        var expected = semanticEventKind switch
        {
            SemanticEventKind.TurnCompleted => ActionRequirement.None,
            SemanticEventKind.PermissionObserved => ActionRequirement.Unknown,
            SemanticEventKind.PermissionRequired or
                SemanticEventKind.InputRequired or
                SemanticEventKind.ConfirmationRequired or
                SemanticEventKind.AttentionRequired => ActionRequirement.Required,
            _ => throw new ArgumentOutOfRangeException(
                nameof(semanticEventKind),
                semanticEventKind,
                "The semantic event kind is undefined."),
        };

        if (actionRequirement != expected)
        {
            throw new ArgumentException(
                "The action requirement conflicts with the semantic event kind.",
                nameof(actionRequirement));
        }
    }
}

/// <summary>Identifies a provider-neutral lifecycle correlation operation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LifecycleDirectiveKind>))]
public enum LifecycleDirectiveKind
{
    /// <summary>No lifecycle operation is needed.</summary>
    None,

    /// <summary>Registers an event for later lifecycle correlation.</summary>
    Register,

    /// <summary>Resolves one matching event within a turn.</summary>
    ResolveOne,

    /// <summary>Resolves every matching event within a turn.</summary>
    ResolveAllInTurn,
}

/// <summary>Expresses bounded lifecycle correlation intent without implementing a pipeline.</summary>
public sealed record LifecycleDirective
{
    /// <summary>Initializes one lifecycle directive.</summary>
    [JsonConstructor]
    public LifecycleDirective(
        LifecycleDirectiveKind kind,
        string? eventId,
        string? sessionIdHash,
        string? turnIdHash,
        string? toolUseIdHash,
        string? toolCategory)
    {
        Kind = ContractValueValidation.ValidateEnum(kind, nameof(kind));
        EventId = eventId is null ? null : ContractValueValidation.ValidateEventId(eventId, nameof(eventId));
        SessionIdHash = ContractValueValidation.ValidateOptionalHash(sessionIdHash, nameof(sessionIdHash));
        TurnIdHash = ContractValueValidation.ValidateOptionalHash(turnIdHash, nameof(turnIdHash));
        ToolUseIdHash = ContractValueValidation.ValidateOptionalHash(toolUseIdHash, nameof(toolUseIdHash));
        ToolCategory = toolCategory is null
            ? null
            : ContractValueValidation.ValidateToolCategory(toolCategory, nameof(toolCategory));
        ValidateShape();
    }

    /// <summary>Gets the requested lifecycle operation.</summary>
    [JsonPropertyName("kind")]
    public LifecycleDirectiveKind Kind { get; }

    /// <summary>Gets the normalized event identifier registered for correlation.</summary>
    [JsonPropertyName("eventId")]
    public string? EventId { get; }

    /// <summary>Gets an optional session hash used to narrow correlation.</summary>
    [JsonPropertyName("sessionIdHash")]
    public string? SessionIdHash { get; }

    /// <summary>Gets the turn hash required by resolution operations.</summary>
    [JsonPropertyName("turnIdHash")]
    public string? TurnIdHash { get; }

    /// <summary>Gets an optional exact tool-use hash for one-event resolution.</summary>
    [JsonPropertyName("toolUseIdHash")]
    public string? ToolUseIdHash { get; }

    /// <summary>Gets an optional allow-listed fallback tool category.</summary>
    [JsonPropertyName("toolCategory")]
    public string? ToolCategory { get; }

    private void ValidateShape()
    {
        switch (Kind)
        {
            case LifecycleDirectiveKind.None when
                EventId is not null || SessionIdHash is not null || TurnIdHash is not null
                || ToolUseIdHash is not null || ToolCategory is not null:
                throw new ArgumentException("A no-op lifecycle directive cannot contain correlation values.");
            case LifecycleDirectiveKind.Register when EventId is null:
                throw new ArgumentException("A register directive requires an event identifier.", nameof(EventId));
            case LifecycleDirectiveKind.ResolveOne when TurnIdHash is null:
                throw new ArgumentException("A one-event resolution requires a turn hash.", nameof(TurnIdHash));
            case LifecycleDirectiveKind.ResolveOne when
                ToolUseIdHash is null && (ToolCategory is null or AgentToolCategories.None):
                throw new ArgumentException(
                    "A one-event resolution requires a tool-use hash or a concrete tool category.");
            case LifecycleDirectiveKind.ResolveAllInTurn when TurnIdHash is null:
                throw new ArgumentException("A turn resolution requires a turn hash.", nameof(TurnIdHash));
            case LifecycleDirectiveKind.ResolveAllInTurn when
                EventId is not null || ToolUseIdHash is not null || ToolCategory is not null:
                throw new ArgumentException("A turn resolution cannot contain event or tool correlation values.");
            case LifecycleDirectiveKind.ResolveOne when EventId is not null:
                throw new ArgumentException("A resolution directive cannot register an event identifier.");
            case LifecycleDirectiveKind.None:
            case LifecycleDirectiveKind.Register:
            case LifecycleDirectiveKind.ResolveOne:
            case LifecycleDirectiveKind.ResolveAllInTurn:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "The lifecycle kind is undefined.");
        }
    }
}
