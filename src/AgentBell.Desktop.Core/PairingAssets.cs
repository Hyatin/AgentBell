using System.Net;
using System.Reflection;
using QRCoder;

namespace AgentBell.Desktop;

/// <summary>Loads the local, framework-free pairing page from the Desktop assembly.</summary>
public static class PairingPageProvider
{
    private const string ResourceName = "AgentBell.Desktop.PairingPage.html";

    /// <summary>Reads the embedded UTF-8 pairing page.</summary>
    public static string ReadHtml()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Pairing page resource is unavailable.");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>Builds the fragment-only pairing URL used by the browser and QR code.</summary>
public static class PairingUrlBuilder
{
    /// <summary>Creates a URL whose initial HTTP request never contains the token.</summary>
    public static string Build(
        IPAddress address,
        int port,
        PairingConfigurationSession pairing)
        => BuildCore(address, port, pairing, requirePrivateAddress: true);

    internal static string BuildForTesting(
        IPAddress address,
        int port,
        PairingConfigurationSession pairing)
        => BuildCore(address, port, pairing, requirePrivateAddress: false);

    private static string BuildCore(
        IPAddress address,
        int port,
        PairingConfigurationSession pairing,
        bool requirePrivateAddress)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(pairing);
        if (requirePrivateAddress && !LanAddressResolver.IsPrivateIpv4(address))
        {
            throw new ArgumentException("Pairing requires an RFC1918 IPv4 address.", nameof(address));
        }

        var deviceName = Uri.EscapeDataString(pairing.Configuration.DeviceName ?? "Windows PC");
        return $"http://{address}:{port}/pair#token={pairing.Token.Value}&device={deviceName}&v=1";
    }
}

/// <summary>Atomically creates the local pairing QR PNG without logging its contents.</summary>
public sealed class PairingQrCodeWriter
{
    /// <summary>Generates and atomically replaces a QR PNG.</summary>
    public async Task<bool> WriteAsync(
        string pairingUrl,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var path = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            var png = PngByteQRCodeHelper.GetQRCode(
                pairingUrl,
                QRCodeGenerator.ECCLevel.Q,
                8);
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(png, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // QR cleanup cannot affect either listener.
            }
        }
    }
}
