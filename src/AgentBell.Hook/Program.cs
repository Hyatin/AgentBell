using AgentBell.Hook;

try
{
    using var handler = new SocketsHttpHandler
    {
        ConnectTimeout = HookEndpointResolver.ResolveTestConnectTimeout()
            ?? TimeSpan.FromMilliseconds(100),
        UseProxy = false,
    };
    using var httpClient = new HttpClient(handler)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    var application = new HookApplication(
        new HookInputResolver(),
        new CodexPayloadParser(),
        new CodexStopHookPayloadParser(),
        new HttpEventForwarder(
            httpClient,
            HookEndpointResolver.ResolveTestForwardTimeout()),
        DiagnosticLoggerFactory.CreateFromEnvironment());

    return await application.RunAsync(
        args,
        Console.OpenStandardInput(),
        Console.Out,
        CancellationToken.None).ConfigureAwait(false);
}
catch
{
    // A notify helper must never disrupt the Codex workflow.
    return 0;
}
