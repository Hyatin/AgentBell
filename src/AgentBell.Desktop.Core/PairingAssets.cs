using System.Net;
using System.Reflection;
using System.Globalization;
using System.Text.Json;
using AgentBell.Localization;
using QRCoder;

namespace AgentBell.Desktop;

/// <summary>Loads the local, framework-free pairing page from the Desktop assembly.</summary>
public static class PairingPageProvider
{
    private const string ResourceName = "AgentBell.Desktop.PairingPage.html";

    /// <summary>Reads the embedded UTF-8 pairing page.</summary>
    public static string ReadHtml(CultureInfo? culture = null)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Pairing page resource is unavailable.");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var effectiveCulture = string.Equals(
            (culture ?? CultureInfo.CurrentUICulture).Name,
            AppLanguageValues.ChineseSimplified,
            StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo(AppLanguageValues.ChineseSimplified)
                : CultureInfo.GetCultureInfo(AppLanguageValues.English);
        var localizer = new ResourceAppLocalizer(() => effectiveCulture);
        var localizedText = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["browserReceived"] = localizer.Get("PairingPage_BrowserReceived"),
            ["codexCompleted"] = localizer.Get("PairingPage_CodexCompleted"),
            ["computer"] = localizer.Get("PairingPage_Computer"),
            ["connected"] = localizer.Get("PairingPage_Connected"),
            ["connectionFailed"] = localizer.Get("PairingPage_ConnectionFailed"),
            ["disconnected"] = localizer.Get("PairingPage_Disconnected"),
            ["missingCredential"] = localizer.Get("PairingPage_MissingCredential"),
            ["project"] = localizer.Get("PairingPage_Project"),
            ["protocolStatus"] = localizer.Get("PairingPage_ProtocolStatus"),
            ["reconnectDelay"] = localizer.Get("PairingPage_ReconnectDelay"),
            ["turnEnded"] = localizer.Get("PairingPage_TurnEnded"),
            ["waitingForServer"] = localizer.Get("PairingPage_WaitingForServer"),
        });
        return reader.ReadToEnd()
            .Replace("__LANGUAGE__", effectiveCulture.Name, StringComparison.Ordinal)
            .Replace("__HTML_CONNECTING__", WebUtility.HtmlEncode(localizer.Get("PairingPage_Connecting")), StringComparison.Ordinal)
            .Replace("__HTML_COMPUTER_EMPTY__", WebUtility.HtmlEncode(localizer.Format("PairingPage_Computer", "—")), StringComparison.Ordinal)
            .Replace("__HTML_PROTOCOL__", WebUtility.HtmlEncode(localizer.Get("PairingPage_Protocol")), StringComparison.Ordinal)
            .Replace("__HTML_LAST_SEQUENCE__", WebUtility.HtmlEncode(localizer.Get("PairingPage_LastSequence")), StringComparison.Ordinal)
            .Replace("__HTML_RECENT_EVENTS__", WebUtility.HtmlEncode(localizer.Get("PairingPage_RecentEvents")), StringComparison.Ordinal)
            .Replace("__LOCALIZED_TEXT__", localizedText, StringComparison.Ordinal);
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
