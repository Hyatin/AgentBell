using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Contracts.Tests;

public sealed class ProviderNeutralEventsTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 8, 13, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Completion_CanBeRepresentedWithoutAction()
    {
        var normalized = Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            safeSummary: "已完成 ✅");

        Assert.Equal(ProviderIds.Codex, normalized.ProviderId);
        Assert.Equal("stop", normalized.SourceEventKind.Value);
        Assert.Equal(SemanticReliability.Reliable, normalized.SemanticReliability);
        Assert.Equal(ActionRequirement.None, normalized.ActionRequirement);
    }

    [Fact]
    public void CodexPermissionObserved_DoesNotClaimRequiredAction()
    {
        var normalized = Event(
            SemanticEventKind.PermissionObserved,
            ActionRequirement.Unknown,
            sourceEventKind: "permission-request");

        Assert.Equal(ProviderIds.Codex, normalized.ProviderId);
        Assert.NotEqual(ActionRequirement.Required, normalized.ActionRequirement);
    }

    [Fact]
    public void FutureReliablePermissionRequired_RequiresAction()
    {
        var normalized = Event(
            SemanticEventKind.PermissionRequired,
            ActionRequirement.Required,
            providerId: ProviderIds.ClaudeCode,
            sourceEventKind: "permission-request");

        Assert.Equal(ProviderIds.ClaudeCode, normalized.ProviderId);
        Assert.Equal(ActionRequirement.Required, normalized.ActionRequirement);
    }

    [Fact]
    public void InputRequired_CanCarryBoundedClassificationProvenance()
    {
        var classification = new EventClassification(
            "codex.input-required.synthetic-choice",
            ClassificationConfidence.Medium);
        var normalized = Event(
            SemanticEventKind.InputRequired,
            ActionRequirement.Required,
            classification: classification,
            reliability: SemanticReliability.BestEffort);

        Assert.Same(classification, normalized.Classification);
        Assert.Equal(SemanticReliability.BestEffort, normalized.SemanticReliability);
    }

    [Fact]
    public void RequiredFields_RejectUninitializedOrMissingValues()
    {
        Assert.Throws<InvalidOperationException>(() => new NormalizedAgentEvent(
            default,
            new SourceEventKind("stop"),
            SemanticEventKind.TurnCompleted,
            FixedTime,
            "codex:event:abcdef012345",
            null,
            null,
            null,
            null,
            null,
            AgentToolCategories.None,
            null,
            SemanticReliability.Reliable,
            ActionRequirement.None));
        Assert.Throws<InvalidOperationException>(() => new NormalizedAgentEvent(
            ProviderIds.Codex,
            default,
            SemanticEventKind.TurnCompleted,
            FixedTime,
            "codex:event:abcdef012345",
            null,
            null,
            null,
            null,
            null,
            AgentToolCategories.None,
            null,
            SemanticReliability.Reliable,
            ActionRequirement.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedAgentEvent(
            ProviderIds.Codex,
            new SourceEventKind("stop"),
            SemanticEventKind.TurnCompleted,
            default,
            "codex:event:abcdef012345",
            null,
            null,
            null,
            null,
            null,
            AgentToolCategories.None,
            null,
            SemanticReliability.Reliable,
            ActionRequirement.None));
        Assert.Throws<ArgumentNullException>(() => new NormalizedAgentEvent(
            ProviderIds.Codex,
            new SourceEventKind("stop"),
            SemanticEventKind.TurnCompleted,
            FixedTime,
            null!,
            null,
            null,
            null,
            null,
            null,
            AgentToolCategories.None,
            null,
            SemanticReliability.Reliable,
            ActionRequirement.None));
    }

    [Theory]
    [InlineData(SemanticEventKind.TurnCompleted, ActionRequirement.Required)]
    [InlineData(SemanticEventKind.PermissionObserved, ActionRequirement.Required)]
    [InlineData(SemanticEventKind.PermissionObserved, ActionRequirement.None)]
    [InlineData(SemanticEventKind.PermissionRequired, ActionRequirement.Unknown)]
    [InlineData(SemanticEventKind.InputRequired, ActionRequirement.None)]
    [InlineData(SemanticEventKind.ConfirmationRequired, ActionRequirement.Unknown)]
    [InlineData(SemanticEventKind.AttentionRequired, ActionRequirement.None)]
    public void ConflictingSemanticAndActionRequirement_IsRejected(
        SemanticEventKind semanticEventKind,
        ActionRequirement actionRequirement)
    {
        Assert.Throws<ArgumentException>(() => Event(semanticEventKind, actionRequirement));
    }

    [Theory]
    [InlineData("ABCDEF012345")]
    [InlineData("abc")]
    [InlineData("abcdef01234g")]
    [InlineData("abcdef0123456")]
    public void IdentifierHash_InvalidFormat_IsRejected(string hash)
    {
        Assert.Throws<ArgumentException>(() => Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            sessionIdHash: hash));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("my project")]
    [InlineData("项目")]
    [InlineData("项目 🚀")]
    [InlineData("foo:bar")]
    [InlineData("foo*bar")]
    [InlineData("foo?bar")]
    public void Project_AcceptsCrossPlatformSafeDisplayComponents(string project)
    {
        var normalized = Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            project: project);

        Assert.Equal(project, normalized.Project);
    }

    [Theory]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("foo\0bar")]
    [InlineData("foo\u001fbar")]
    [InlineData("foo\u007fbar")]
    [InlineData("foo\u0085bar")]
    [InlineData("foo\u2028bar")]
    [InlineData("foo\u2029bar")]
    public void Project_RejectsPathOrUnsafeDisplayComponents(string project)
    {
        Assert.Throws<ArgumentException>(() => Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            project: project));
    }

    [Fact]
    public void SafeSummary_IsBoundedByUnicodeTextElements()
    {
        var exact = string.Concat(Enumerable.Repeat("👩🏽‍💻", 160));
        var tooLong = string.Concat(Enumerable.Repeat("👩🏽‍💻", 161));

        var normalized = Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            safeSummary: exact);

        Assert.Equal(160, new StringInfo(normalized.SafeSummary!).LengthInTextElements);
        Assert.Throws<ArgumentException>(() => Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            safeSummary: tooLong));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("file_change")]
    [InlineData("network_access")]
    [InlineData("external_tool")]
    [InlineData("computer_control")]
    [InlineData("other")]
    [InlineData("none")]
    public void ToolCategory_ReusesExistingAllowList(string toolCategory)
    {
        Assert.Equal(toolCategory, Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            toolCategory: toolCategory).ToolCategory);
    }

    [Fact]
    public void ToolCategory_RejectsProviderRawValue()
    {
        Assert.Throws<ArgumentException>(() => Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            toolCategory: "Bash"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Codex:event")]
    [InlineData("codex event")]
    [InlineData("codex/event")]
    [InlineData("codex\\event")]
    [InlineData("codex\nevent")]
    [InlineData("codex\0event")]
    [InlineData("codex_event")]
    [InlineData("codex.event")]
    public void EventId_InvalidValue_IsRejected(string eventId)
    {
        Assert.Throws<ArgumentException>(() => Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            eventId: eventId));
    }

    [Theory]
    [InlineData(
        "codex:e18c78136e8e:04d186e38f9a",
        SemanticEventKind.TurnCompleted,
        ActionRequirement.None)]
    [InlineData(
        "codex-action:fa4afc83bf185000bb870ddb",
        SemanticEventKind.PermissionObserved,
        ActionRequirement.Unknown)]
    [InlineData(
        "codex-action:b6d7891a91c10840802e2b6c",
        SemanticEventKind.InputRequired,
        ActionRequirement.Required)]
    [InlineData(
        "codex-action:ce06fb39f016f59e85fe225a",
        SemanticEventKind.ConfirmationRequired,
        ActionRequirement.Required)]
    [InlineData(
        "codex-action:9e7e29b6848adae754f5c3c5",
        SemanticEventKind.AttentionRequired,
        ActionRequirement.Required)]
    public void EventId_AcceptsExisting07GoldenValueWithoutChange(
        string eventId,
        SemanticEventKind semanticEventKind,
        ActionRequirement actionRequirement)
    {
        var normalized = Event(
            semanticEventKind,
            actionRequirement,
            eventId: eventId);

        Assert.Equal(eventId, normalized.EventId);
    }

    [Fact]
    public void EventIdAndProject_EnforceDocumentedBounds()
    {
        var exactProject = string.Concat(Enumerable.Repeat("👩🏽‍💻", 128));
        var overlongProject = string.Concat(Enumerable.Repeat("👩🏽‍💻", 129));
        var exactEventId = new string('a', 128);

        Assert.Equal(exactEventId, Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            eventId: exactEventId).EventId);
        Assert.Equal(exactProject, Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            project: exactProject).Project);
        Assert.Throws<ArgumentException>(() => Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            eventId: new string('a', 129)));
        Assert.Throws<ArgumentException>(() => Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            project: overlongProject));
    }

    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("line\nbreak")]
    [InlineData("line\rbreak")]
    [InlineData("\t")]
    [InlineData("text\0tail")]
    [InlineData("text\u001ftail")]
    [InlineData("text\u007ftail")]
    [InlineData("text\u0085tail")]
    [InlineData("text\u2028tail")]
    [InlineData("text\u2029tail")]
    public void SafeSummary_RejectsUnnormalizedText(string value)
    {
        Assert.Throws<ArgumentException>(() => Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            safeSummary: value));
    }

    [Fact]
    public void NormalizedEvent_HasNoRawOrArbitraryMetadataEscapeHatch()
    {
        const string Sentinel = "AGENTBELL_SECRET_SHOULD_NEVER_ESCAPE_7F3A";
        string[] forbidden =
        [
            "Prompt", "Command", "ToolInput", "ToolOutput", "RawPayload", "RawJson", "Metadata",
            "WorkingDirectory", "Cwd", "SessionId", "TurnId", "ToolUseId", "ProviderData",
        ];
        var properties = typeof(NormalizedAgentEvent).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var json = JsonSerializer.Serialize(Event(
            SemanticEventKind.PermissionObserved,
            ActionRequirement.Unknown,
            sourceEventKind: "permission-request"));

        Assert.DoesNotContain(
            properties,
            property => forbidden.Contains(property.Name, StringComparer.Ordinal));
        Assert.DoesNotContain(Sentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(properties, property =>
            typeof(System.Collections.IDictionary).IsAssignableFrom(property.PropertyType)
            || property.PropertyType == typeof(JsonElement)
            || property.PropertyType == typeof(object));
    }

    [Fact]
    public void NormalizedEvent_Json_IsDeterministicAndRoundTrips()
    {
        var normalized = Event(
            SemanticEventKind.TurnCompleted,
            ActionRequirement.None,
            safeSummary: "Done 🔔",
            classification: new EventClassification("codex.stop.structured", ClassificationConfidence.High));

        var firstJson = JsonSerializer.Serialize(normalized);
        var secondJson = JsonSerializer.Serialize(normalized);
        var roundTrip = JsonSerializer.Deserialize<NormalizedAgentEvent>(firstJson);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(normalized, roundTrip);
        using var document = JsonDocument.Parse(firstJson);
        Assert.Equal("codex", document.RootElement.GetProperty("providerId").GetString());
        Assert.Equal("stop", document.RootElement.GetProperty("sourceEventKind").GetString());
    }

    [Fact]
    public void LifecycleDirective_RepresentsCurrentBoundedOperations()
    {
        var register = new LifecycleDirective(
            LifecycleDirectiveKind.Register,
            "codex-action:abc123",
            "123456789abc",
            "abcdef012345",
            "fedcba987654",
            AgentToolCategories.Command);
        var resolveOne = new LifecycleDirective(
            LifecycleDirectiveKind.ResolveOne,
            null,
            "123456789abc",
            "abcdef012345",
            "fedcba987654",
            AgentToolCategories.Command);
        var resolveTurn = new LifecycleDirective(
            LifecycleDirectiveKind.ResolveAllInTurn,
            null,
            "123456789abc",
            "abcdef012345",
            null,
            null);

        Assert.Equal(LifecycleDirectiveKind.Register, register.Kind);
        Assert.Equal(LifecycleDirectiveKind.ResolveOne, resolveOne.Kind);
        Assert.Equal(LifecycleDirectiveKind.ResolveAllInTurn, resolveTurn.Kind);

        var json = JsonSerializer.Serialize(resolveOne);
        var roundTrip = JsonSerializer.Deserialize<LifecycleDirective>(json);
        Assert.Equal(resolveOne, roundTrip);
    }

    [Fact]
    public void LifecycleDirective_RejectsAmbiguousOrUnboundedShapes()
    {
        Assert.Throws<ArgumentException>(() => new LifecycleDirective(
            LifecycleDirectiveKind.None,
            "event:1",
            null,
            null,
            null,
            null));
        Assert.Throws<ArgumentException>(() => new LifecycleDirective(
            LifecycleDirectiveKind.ResolveOne,
            null,
            null,
            "abcdef012345",
            null,
            AgentToolCategories.None));
        Assert.Throws<ArgumentException>(() => new LifecycleDirective(
            LifecycleDirectiveKind.ResolveAllInTurn,
            null,
            null,
            null,
            null,
            null));
    }

    private static NormalizedAgentEvent Event(
        SemanticEventKind semanticEventKind,
        ActionRequirement actionRequirement,
        ProviderId? providerId = null,
        string sourceEventKind = "stop",
        string? eventId = null,
        string? sessionIdHash = "123456789abc",
        string? turnIdHash = "abcdef012345",
        string? project = "AgentBell",
        string? safeSummary = null,
        string toolCategory = AgentToolCategories.None,
        EventClassification? classification = null,
        SemanticReliability reliability = SemanticReliability.Reliable) => new(
            providerId ?? ProviderIds.Codex,
            new SourceEventKind(sourceEventKind),
            semanticEventKind,
            FixedTime,
            eventId ?? $"{(providerId ?? ProviderIds.Codex).Value}:event:abcdef012345",
            sessionIdHash,
            turnIdHash,
            null,
            project,
            safeSummary,
            toolCategory,
            classification,
            reliability,
            actionRequirement);
}
