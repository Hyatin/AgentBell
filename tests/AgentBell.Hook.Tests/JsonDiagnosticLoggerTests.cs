using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Hook.Tests;

public sealed class JsonDiagnosticLoggerTests
{
    [Fact]
    public void Factory_DefaultPathUsesKnownFolderAndLeavesWrongEnvironmentUntouched()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"AgentBell-Paths-{Guid.NewGuid():N}");
        var knownFolder = Path.Combine(fixtureRoot, "known");
        var wrongFolder = Path.Combine(fixtureRoot, "wrong");
        Directory.CreateDirectory(knownFolder);
        Directory.CreateDirectory(wrongFolder);
        var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var previousEnabled = Environment.GetEnvironmentVariable(
            DiagnosticLoggerFactory.EnabledEnvironmentVariable);
        var previousPath = Environment.GetEnvironmentVariable(
            DiagnosticLoggerFactory.PathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", wrongFolder);
            Environment.SetEnvironmentVariable(DiagnosticLoggerFactory.EnabledEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(DiagnosticLoggerFactory.PathEnvironmentVariable, null);
            var logger = DiagnosticLoggerFactory.CreateFromEnvironment(
                new AgentBellPathResolver(_ => knownFolder));

            logger.Record(new HookDiagnosticEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Result = ForwardResult.SuccessCode,
                ElapsedMilliseconds = 1,
            });

            Assert.True(File.Exists(Path.Combine(knownFolder, "AgentBell", "logs", "hook.ndjson")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(wrongFolder));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
            Environment.SetEnvironmentVariable(
                DiagnosticLoggerFactory.EnabledEnvironmentVariable,
                previousEnabled);
            Environment.SetEnvironmentVariable(DiagnosticLoggerFactory.PathEnvironmentVariable, previousPath);
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void Record_ValidEvent_WritesOneCompactJsonLine()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AgentBell.Tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "hook.ndjson");

        try
        {
            var logger = new JsonDiagnosticLogger(path);
            var diagnosticEvent = new HookDiagnosticEvent
            {
                Timestamp = new DateTimeOffset(2026, 7, 31, 22, 19, 0, TimeSpan.FromHours(8)),
                EventType = "agent-turn-complete",
                ThreadIdHash = "0123456789ab",
                TurnIdHash = "abcdef012345",
                HasWorkingDirectory = true,
                HasAssistantMessage = true,
                Result = ForwardResult.SuccessCode,
                HttpStatusCode = 202,
                ElapsedMilliseconds = 37,
            };

            logger.Record(diagnosticEvent);

            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            using var document = JsonDocument.Parse(lines[0]);
            Assert.Equal("agent-turn-complete", document.RootElement.GetProperty("eventType").GetString());
            Assert.Equal("0123456789ab", document.RootElement.GetProperty("threadIdHash").GetString());
            Assert.Equal("abcdef012345", document.RootElement.GetProperty("turnIdHash").GetString());
            Assert.Equal("success", document.RootElement.GetProperty("result").GetString());
            Assert.Equal(37, document.RootElement.GetProperty("elapsedMs").GetInt64());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public void Record_UnusablePath_DoesNotThrow()
    {
        var logger = new JsonDiagnosticLogger("invalid\0path" + @"\hook.ndjson");
        var diagnosticEvent = new HookDiagnosticEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Result = HookErrorCodes.UnexpectedError,
            ElapsedMilliseconds = 0,
        };

        var exception = Record.Exception(() => logger.Record(diagnosticEvent));

        Assert.Null(exception);
    }
}
