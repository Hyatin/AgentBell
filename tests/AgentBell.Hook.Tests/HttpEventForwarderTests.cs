using System.Net;

namespace AgentBell.Hook.Tests;

public sealed class HttpEventForwarderTests
{
    [Fact]
    public async Task ForwardAsync_Success_PostsExactJsonToLoopbackEndpoint()
    {
        const string Json = "{\"type\":\"agent-turn-complete\",\"message\":\"🔔\"}";
        HttpMethod? method = null;
        Uri? requestUri = null;
        string? mediaType = null;
        string? body = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            method = request.Method;
            requestUri = request.RequestUri;
            mediaType = request.Content?.Headers.ContentType?.MediaType;
            body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var httpClient = new HttpClient(handler);
        var forwarder = new HttpEventForwarder(httpClient);

        var result = await forwarder.ForwardAsync(Json, CancellationToken.None);

        Assert.Equal(ForwardResult.SuccessCode, result.Code);
        Assert.Equal(202, result.HttpStatusCode);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("http://127.0.0.1:17863/api/v1/events/codex", requestUri?.AbsoluteUri);
        Assert.Equal("application/json", mediaType);
        Assert.Equal(Json, body);
    }

    [Fact]
    public void EndpointResolver_TestModeUsesDedicatedPortAndInvalidSettingsFailClosed()
    {
        var isolated = HookEndpointResolver.Resolve(name => name switch
        {
            HookEndpointResolver.TestModeEnvironmentVariable => "1",
            HookEndpointResolver.TestLoopbackPortEnvironmentVariable => "45123",
            _ => null,
        });
        var invalid = HookEndpointResolver.Resolve(name => name switch
        {
            HookEndpointResolver.TestModeEnvironmentVariable => "1",
            HookEndpointResolver.TestLoopbackPortEnvironmentVariable => "17863-invalid",
            _ => null,
        });
        var production = HookEndpointResolver.Resolve(_ => null);

        Assert.Equal("http://127.0.0.1:45123/api/v1/events/codex", isolated.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:1/api/v1/events/codex", invalid.AbsoluteUri);
        Assert.Equal(HookEndpointResolver.ProductionEndpoint, production);
    }

    [Fact]
    public async Task ForwardAsync_NonSuccess_ReturnsRejectedWithoutExceptionText()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var httpClient = new HttpClient(handler);
        var forwarder = new HttpEventForwarder(httpClient);

        var result = await forwarder.ForwardAsync(
            "{\"type\":\"agent-turn-complete\"}",
            CancellationToken.None);

        Assert.Equal(HookErrorCodes.ForwardRejected, result.Code);
        Assert.Equal(503, result.HttpStatusCode);
    }

    [Fact]
    public async Task ForwardAsync_RequestFailure_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("private network details"));
        using var httpClient = new HttpClient(handler);
        var forwarder = new HttpEventForwarder(httpClient);

        var result = await forwarder.ForwardAsync(
            "{\"type\":\"agent-turn-complete\"}",
            CancellationToken.None);

        Assert.Equal(HookErrorCodes.ForwardUnavailable, result.Code);
        Assert.Null(result.HttpStatusCode);
    }

    [Fact]
    public async Task ForwardAsync_HungRequest_ReturnsTimeout()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var httpClient = new HttpClient(handler);
        var forwarder = new HttpEventForwarder(httpClient, TimeSpan.FromMilliseconds(25));

        var result = await forwarder.ForwardAsync(
            "{\"type\":\"agent-turn-complete\"}",
            CancellationToken.None);

        Assert.Equal(HookErrorCodes.ForwardTimeout, result.Code);
        Assert.Null(result.HttpStatusCode);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }
}
