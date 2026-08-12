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
    public void ForwardTimeoutResolver_RequiresTestModeAndAcceptsOnlyBoundedMilliseconds()
    {
        static string? Read(
            string name,
            string? testMode,
            string? timeout) => name switch
            {
                HookEndpointResolver.TestModeEnvironmentVariable => testMode,
                HookEndpointResolver.TestForwardTimeoutEnvironmentVariable => timeout,
                _ => null,
            };

        var isolated = HookEndpointResolver.ResolveTestForwardTimeout(
            name => Read(name, "1", "5000"));
        var production = HookEndpointResolver.ResolveTestForwardTimeout(
            name => Read(name, null, "5000"));
        var tooLarge = HookEndpointResolver.ResolveTestForwardTimeout(
            name => Read(name, "1", "10001"));
        var invalid = HookEndpointResolver.ResolveTestForwardTimeout(
            name => Read(name, "1", "invalid"));

        Assert.Equal(TimeSpan.FromSeconds(5), isolated);
        Assert.Null(production);
        Assert.Null(tooLarge);
        Assert.Null(invalid);
    }

    [Fact]
    public void ProcessTimeoutResolver_RequiresTestModeAndKeepsProductionDeadlineUnchanged()
    {
        static string? ReadIsolated(string name) => name switch
        {
            HookEndpointResolver.TestModeEnvironmentVariable => "1",
            HookEndpointResolver.TestProcessTimeoutEnvironmentVariable => "8000",
            _ => null,
        };

        var isolated = HookEndpointResolver.ResolveTestProcessTimeout(ReadIsolated);
        var production = HookEndpointResolver.ResolveTestProcessTimeout(name =>
            name == HookEndpointResolver.TestProcessTimeoutEnvironmentVariable ? "8000" : null);
        var tooLarge = HookEndpointResolver.ResolveTestProcessTimeout(name => name switch
        {
            HookEndpointResolver.TestModeEnvironmentVariable => "1",
            HookEndpointResolver.TestProcessTimeoutEnvironmentVariable => "15001",
            _ => null,
        });

        Assert.Equal(TimeSpan.FromSeconds(8), isolated);
        Assert.Null(production);
        Assert.Null(tooLarge);
    }

    [Fact]
    public void ConnectTimeoutResolver_RequiresTestModeAndAcceptsOnlyBoundedMilliseconds()
    {
        static string? Read(
            string name,
            string? testMode,
            string? timeout) => name switch
            {
                HookEndpointResolver.TestModeEnvironmentVariable => testMode,
                HookEndpointResolver.TestConnectTimeoutEnvironmentVariable => timeout,
                _ => null,
            };

        var isolated = HookEndpointResolver.ResolveTestConnectTimeout(
            name => Read(name, "1", "2000"));
        var production = HookEndpointResolver.ResolveTestConnectTimeout(
            name => Read(name, null, "2000"));
        var tooLarge = HookEndpointResolver.ResolveTestConnectTimeout(
            name => Read(name, "1", "5001"));
        var invalid = HookEndpointResolver.ResolveTestConnectTimeout(
            name => Read(name, "1", "invalid"));

        Assert.Equal(TimeSpan.FromSeconds(2), isolated);
        Assert.Null(production);
        Assert.Null(tooLarge);
        Assert.Null(invalid);
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
