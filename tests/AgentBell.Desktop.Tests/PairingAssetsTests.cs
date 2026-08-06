using System.Net;
using System.Globalization;
using System.Text;

namespace AgentBell.Desktop.Tests;

public sealed class PairingAssetsTests
{
    [Fact]
    public void PairingPage_IsSelfContainedAndImplementsCoreBrowserProtocol()
    {
        var html = PairingPageProvider.ReadHtml();

        Assert.Contains("location.hash", html, StringComparison.Ordinal);
        Assert.Contains("[1, 2, 5, 10, 30]", html, StringComparison.Ordinal);
        Assert.Contains("localStorage", html, StringComparison.Ordinal);
        Assert.Contains("type: 'resume'", html, StringComparison.Ordinal);
        Assert.Contains("type: 'pong'", html, StringComparison.Ordinal);
        Assert.Contains("displayedEventIds", html, StringComparison.Ordinal);
        Assert.Contains("Authorization", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credentialNode", html, StringComparison.Ordinal);
        Assert.DoesNotContain("credential.textContent", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PairingPage_UsesEnglishFallbackAndExactSimplifiedChinese()
    {
        var english = PairingPageProvider.ReadHtml(CultureInfo.GetCultureInfo("en-US"));
        var unsupportedChinese = PairingPageProvider.ReadHtml(CultureInfo.GetCultureInfo("zh-TW"));
        var simplifiedChinese = PairingPageProvider.ReadHtml(CultureInfo.GetCultureInfo("zh-CN"));

        Assert.Contains("Recent events", english, StringComparison.Ordinal);
        Assert.Contains("Recent events", unsupportedChinese, StringComparison.Ordinal);
        Assert.Contains("最近事件", simplifiedChinese, StringComparison.Ordinal);
        Assert.Contains("lang=\"en-US\"", unsupportedChinese, StringComparison.Ordinal);
        Assert.Contains("lang=\"zh-CN\"", simplifiedChinese, StringComparison.Ordinal);
        Assert.DoesNotContain("__LOCALIZED_TEXT__", english, StringComparison.Ordinal);
        Assert.DoesNotContain("__HTML_", simplifiedChinese, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingUrl_UsesFragmentAndQrWriterAtomicallyCreatesPng()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-QR-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var pairing = await TestPairingFactory.CreateAsync(directory);
            var url = PairingUrlBuilder.Build(IPAddress.Parse("192.168.1.20"), 17864, pairing);
            var beforeFragment = url[..url.IndexOf('#')];
            var path = Path.Combine(directory, "pairing", "agentbell-pairing.png");

            Assert.Equal("http://192.168.1.20:17864/pair", beforeFragment);
            Assert.DoesNotContain(pairing.Token.Value, beforeFragment, StringComparison.Ordinal);
            Assert.Contains($"#token={pairing.Token.Value}", url, StringComparison.Ordinal);
            Assert.True(await new PairingQrCodeWriter().WriteAsync(
                url,
                path,
                CancellationToken.None));
            Assert.True(await new PairingQrCodeWriter().WriteAsync(
                url,
                path,
                CancellationToken.None));

            var png = await File.ReadAllBytesAsync(path);
            Assert.Equal(
                new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                png.Take(4).ToArray());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
            Assert.DoesNotContain(
                pairing.Token.Value,
                Encoding.Latin1.GetString(png),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
