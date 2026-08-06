using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Contracts.Tests;

public sealed class ProtocolMessagesTests
{
    [Fact]
    public void Serialize_HelloAndStatus_UseCentralProtocolAndCamelCaseFields()
    {
        var hello = new HelloMessage
        {
            DeviceName = "测试电脑",
            DeviceId = "device-id",
            LatestSequence = 42,
            ServerTime = DateTimeOffset.Parse("2026-08-01T16:00:00+08:00"),
        };
        var status = new StatusResponse
        {
            DeviceName = "测试电脑",
            DeviceId = "device-id",
            LanAddress = "192.168.1.20",
            LanPort = 17864,
            LatestSequence = 42,
            EventCount = 2,
        };

        using var helloDocument = JsonDocument.Parse(JsonSerializer.Serialize(hello));
        using var statusDocument = JsonDocument.Parse(JsonSerializer.Serialize(status));

        Assert.Equal("hello", helloDocument.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, helloDocument.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("0.6.0-beta.1", helloDocument.RootElement.GetProperty("serverVersion").GetString());
        Assert.Equal(AgentBellProtocol.WebSocketPath,
            statusDocument.RootElement.GetProperty("webSocketPath").GetString());
        Assert.False(statusDocument.RootElement.TryGetProperty("token", out _));
    }

    [Fact]
    public void Deserialize_Resume_IgnoresUnknownFieldsWithoutPolymorphism()
    {
        var result = JsonSerializer.Deserialize<ResumeMessage>(
            "{\"type\":\"resume\",\"lastSequence\":7,\"future\":true}");

        Assert.NotNull(result);
        Assert.Equal("resume", result.Type);
        Assert.Equal(7, result.LastSequence);
    }
}
