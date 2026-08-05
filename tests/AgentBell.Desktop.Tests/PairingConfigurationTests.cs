using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBell.Desktop.Tests;

public sealed class PairingConfigurationTests
{
    [Fact]
    public void PairingToken_Uses32RandomBytesBase64UrlAndFixedTimeMatcher()
    {
        using var first = PairingToken.Generate();
        using var second = PairingToken.Generate();

        Assert.NotEqual(first.Value, second.Value);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.Value);
        Span<byte> decoded = stackalloc byte[PairingToken.ByteLength];
        Assert.True(Base64Url.TryDecodeExact(first.Value, decoded));
        Assert.True(first.Matches(first.Value));
        Assert.False(first.Matches(second.Value));
        Assert.False(first.Matches("not-a-token"));
    }

    [Fact]
    public void WindowsDpapiProtector_CurrentUserRoundTripsWithoutPlaintext()
    {
        var protector = new WindowsDpapiPairingTokenProtector();
        var plaintext = RandomNumberGenerator.GetBytes(PairingToken.ByteLength);

        var ciphertext = protector.Protect(plaintext);
        var recovered = protector.Unprotect(ciphertext);

        Assert.NotEqual(plaintext, ciphertext);
        Assert.Equal(plaintext, recovered);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(recovered);
    }

    [Fact]
    public async Task LoadOrCreate_FirstRunPersistsEncryptedTokenAndRestartRecoversIt()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "config.json");
        var protector = new FakePairingTokenProtector();

        try
        {
            var manager = CreateManager(path, protector);
            var first = await manager.LoadOrCreateAsync(CancellationToken.None);
            Assert.True(first.IsAvailable);
            Assert.True(first.TokenRegenerated);
            using var firstSession = Assert.IsType<PairingConfigurationSession>(first.Session);
            var token = firstSession.Token.Value;
            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
            Assert.Contains("encryptedPairingToken", raw, StringComparison.Ordinal);
            Assert.NotEqual(
                new byte[] { 0xEF, 0xBB, 0xBF },
                File.ReadAllBytes(path).Take(3).ToArray());

            var second = await CreateManager(path, protector)
                .LoadOrCreateAsync(CancellationToken.None);
            using var secondSession = Assert.IsType<PairingConfigurationSession>(second.Session);
            Assert.False(second.TokenRegenerated);
            Assert.Equal(token, secondSession.Token.Value);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadOrCreate_DecryptionFailureGeneratesAndSafelyOverwritesToken()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "config.json");
        var protector = new FakePairingTokenProtector();

        try
        {
            var first = await CreateManager(path, protector)
                .LoadOrCreateAsync(CancellationToken.None);
            using var firstSession = Assert.IsType<PairingConfigurationSession>(first.Session);
            var oldToken = firstSession.Token.Value;
            protector.FailUnprotect = true;

            var second = await CreateManager(path, protector)
                .LoadOrCreateAsync(CancellationToken.None);
            using var secondSession = Assert.IsType<PairingConfigurationSession>(second.Session);

            Assert.True(second.IsAvailable);
            Assert.True(second.TokenRegenerated);
            Assert.NotEqual(oldToken, secondSession.Token.Value);
            Assert.DoesNotContain(oldToken, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadOrCreate_CorruptFileIsQuarantinedAndRecreated()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "config.json");

        try
        {
            await File.WriteAllTextAsync(path, "{invalid", new UTF8Encoding(false));
            var result = await CreateManager(path, new FakePairingTokenProtector())
                .LoadOrCreateAsync(CancellationToken.None);

            using var session = Assert.IsType<PairingConfigurationSession>(result.Session);
            Assert.True(result.IsAvailable);
            Assert.True(result.CorruptFileRecovered);
            Assert.True(File.Exists(path));
            Assert.Single(Directory.GetFiles(directory, "config.json.corrupt-*"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadOrCreate_UnwritablePathReturnsUnavailableWithoutThrowing()
    {
        var directory = CreateDirectory();
        var blockingFile = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block", new UTF8Encoding(false));
        try
        {
            var result = await CreateManager(
                Path.Combine(blockingFile, "config.json"),
                new FakePairingTokenProtector())
                .LoadOrCreateAsync(CancellationToken.None);

            Assert.False(result.IsAvailable);
            Assert.Null(result.Session);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadOrCreate_UnknownOldFieldsAreIgnoredAndLastPortIsRestored()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "config.json");
        var protector = new FakePairingTokenProtector();
        try
        {
            var initial = await CreateManager(path, protector)
                .LoadOrCreateAsync(CancellationToken.None);
            using (var session = Assert.IsType<PairingConfigurationSession>(initial.Session))
            {
                Assert.True(await session.UpdateLanPortAsync(17870, CancellationToken.None));
            }

            var json = await File.ReadAllTextAsync(path);
            json = json.TrimEnd().TrimEnd('}') + ",\n  \"futureField\": true\n}";
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));

            var restored = await CreateManager(path, protector)
                .LoadOrCreateAsync(CancellationToken.None);
            using var restoredSession = Assert.IsType<PairingConfigurationSession>(restored.Session);
            Assert.True(restored.IsAvailable);
            Assert.Equal(17870, restoredSession.Configuration.LastLanPort);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static PairingConfigurationManager CreateManager(
        string path,
        IPairingTokenProtector protector) =>
        new(
            new AgentBellConfigStore(path),
            protector,
            deviceNameProvider: () => "测试电脑 🔔");

    private static string CreateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-Config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakePairingTokenProtector : IPairingTokenProtector
    {
        public bool FailUnprotect { get; set; }

        public byte[] Protect(byte[] plaintext) => Transform(plaintext);

        public byte[] Unprotect(byte[] protectedData)
        {
            if (FailUnprotect)
            {
                throw new CryptographicException("test failure");
            }

            return Transform(protectedData);
        }

        private static byte[] Transform(byte[] value) =>
            value.Select(item => (byte)(item ^ 0xA5)).ToArray();
    }
}
