using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Contracts.Tests;

public sealed class ProviderIdentityTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("claude-code")]
    [InlineData("cursor")]
    [InlineData("gemini-cli")]
    [InlineData("github-copilot-cli")]
    [InlineData("a")]
    [InlineData("a1")]
    [InlineData("a-b")]
    public void ProviderId_ValidCanonicalValues_ArePreserved(string value)
    {
        var providerId = new ProviderId(value);

        Assert.Equal(value, providerId.Value);
        Assert.Equal(value, providerId.ToString());
    }

    [Theory]
    [MemberData(nameof(InvalidProviderIds))]
    public void ProviderId_InvalidValues_AreRejected(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ProviderId(value!));
    }

    [Fact]
    public void ProviderId_HasValueEqualityHashingAndDictionarySemantics()
    {
        var first = new ProviderId("codex");
        var second = new ProviderId("codex");
        var values = new Dictionary<ProviderId, string> { [first] = "provider" };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal("provider", values[second]);
        Assert.NotEqual(first, ProviderIds.ClaudeCode);
    }

    [Fact]
    public void ProviderId_JsonShape_IsCanonicalString()
    {
        var json = JsonSerializer.Serialize(ProviderIds.Codex);
        var roundTrip = JsonSerializer.Deserialize<ProviderId>(json);

        Assert.Equal("\"codex\"", json);
        Assert.Equal(ProviderIds.Codex, roundTrip);
    }

    [Fact]
    public void ProviderId_Json_AllowsUnknownValidProvider()
    {
        var providerId = JsonSerializer.Deserialize<ProviderId>("\"future-agent\"");

        Assert.Equal("future-agent", providerId.Value);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("\"Codex\"")]
    [InlineData("\" codex\"")]
    [InlineData("\"codex--cli\"")]
    public void ProviderId_Json_RejectsNullNonStringAndMalformedValues(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProviderId>(json));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("permission-request")]
    [InlineData("post-tool-use")]
    public void SourceEventKind_UsesProviderScopedCanonicalToken(string value)
    {
        var kind = new SourceEventKind(value);
        var json = JsonSerializer.Serialize(kind);

        Assert.Equal(value, kind.Value);
        Assert.Equal($"\"{value}\"", json);
        Assert.Equal(kind, JsonSerializer.Deserialize<SourceEventKind>(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Stop")]
    [InlineData("codex.stop")]
    [InlineData("codex:stop")]
    [InlineData("codex/stop")]
    [InlineData("codex\\stop")]
    [InlineData("stop\nnow")]
    [InlineData("stop\0now")]
    [InlineData("permission_request")]
    [InlineData("permission--request")]
    public void SourceEventKind_RejectsRawOrNoncanonicalTokens(string value)
    {
        Assert.Throws<ArgumentException>(() => new SourceEventKind(value));
    }

    [Fact]
    public void SourceEventKind_RejectsOverlongToken()
    {
        Assert.Throws<ArgumentException>(() => new SourceEventKind(new string('a', 65)));
    }

    public static TheoryData<string?> InvalidProviderIds => new()
    {
        null,
        string.Empty,
        " ",
        "\tcodex",
        "Codex",
        "CLAUDE",
        "_codex",
        "codex_",
        "codex.",
        "codex/",
        "codex\\",
        "codex cli",
        "codex--cli",
        "codex-",
        "-codex",
        new string('a', 33),
        "编程助手",
    };
}
