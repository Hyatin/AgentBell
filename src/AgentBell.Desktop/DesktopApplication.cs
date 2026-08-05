namespace AgentBell.Desktop;

/// <summary>Runs the headless M1-M3-compatible development host.</summary>
public static class DesktopApplication
{
    /// <summary>Starts the shared Desktop Core and waits for process shutdown.</summary>
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await using var runtime = new AgentBellRuntime(
            diagnosticLogger: DesktopDiagnosticLoggerFactory.CreateFromEnvironment());

        try
        {
            await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine(
                $"M1 listener: http://127.0.0.1:{runtime.RuntimeOptions.LoopbackPort}");
            if (snapshot.LanService == RuntimeServiceStatus.Available)
            {
                Console.WriteLine($"LAN status: Available ({snapshot.LanAddress}:{snapshot.LanPort})");
                Console.WriteLine("Pairing URL (contains credential; do not share publicly):");
                Console.WriteLine(runtime.GetPairingUrl());
                Console.WriteLine(snapshot.PairingQrAvailable
                    ? $"Pairing QR: {snapshot.PairingQrCodePath}"
                    : "Pairing QR: Unavailable");
            }
            else
            {
                Console.WriteLine($"LAN status: Unavailable ({snapshot.LanResultCode})");
            }

            await runtime.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
