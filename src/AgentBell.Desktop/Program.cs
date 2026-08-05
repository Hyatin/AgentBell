using AgentBell.Desktop;

try
{
    return await DesktopApplication.RunAsync(CancellationToken.None).ConfigureAwait(false);
}
catch
{
    // Startup failures, including a port conflict, must not expose private exception details.
    return 1;
}
