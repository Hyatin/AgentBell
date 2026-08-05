using System.Text.Json;
using AgentBell.Contracts;
using AgentBell.Integration;

return await IntegrationProgram.RunAsync(args, Console.Out, CancellationToken.None)
    .ConfigureAwait(false);

/// <summary>Implements the small machine-readable AgentBell integration CLI.</summary>
public static class IntegrationProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Runs the integration command without exposing exception details.</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        var json = arguments.Any(value => string.Equals(value, "--json", StringComparison.Ordinal));
        var commands = arguments
            .Where(value => !string.Equals(value, "--json", StringComparison.Ordinal))
            .ToArray();

        if (commands.Length != 1
            || commands[0] is not ("install" or "repair" or "status" or "uninstall" or "version"))
        {
            await WriteAsync(
                output,
                json,
                new
                {
                    success = false,
                    code = "invalid_arguments",
                }).ConfigureAwait(false);
            return IntegrationExitCodes.InvalidArguments;
        }

        if (commands[0] == "version")
        {
            await WriteAsync(
                output,
                json,
                new
                {
                    success = true,
                    productVersion = AgentBellProduct.ProductVersion,
                    informationalVersion = AgentBellProduct.InformationalVersion,
                    protocolVersion = AgentBellProtocol.ProtocolVersion,
                }).ConfigureAwait(false);
            return IntegrationExitCodes.Success;
        }

        try
        {
            var hookPath = new AgentBellPathResolver()
                .GetInstalledExecutablePath("AgentBell.Hook.exe");
            var result = await new IntegrationService(hookPath)
                .ExecuteAsync(commands[0], cancellationToken)
                .ConfigureAwait(false);
            await WriteAsync(output, json, result).ConfigureAwait(false);
            return ToExitCode(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var result = new CodexIntegrationResult
            {
                Success = false,
                Changed = false,
                State = CodexIntegrationState.Unknown,
                Code = "unexpected_error",
                AgentBellHookCount = 0,
                TrustReviewRequired = false,
            };
            await WriteAsync(output, json, result).ConfigureAwait(false);
            return IntegrationExitCodes.LocalOperationFailed;
        }
    }

    private static int ToExitCode(CodexIntegrationResult result)
    {
        if (result.Success)
        {
            return IntegrationExitCodes.Success;
        }

        if (result.State == CodexIntegrationState.NeedsManualReview)
        {
            return IntegrationExitCodes.ManualReviewRequired;
        }

        return result.Code is "hooks_json_invalid" or "hooks_root_invalid"
            ? IntegrationExitCodes.InvalidHooksJson
            : IntegrationExitCodes.LocalOperationFailed;
    }

    private static Task WriteAsync(TextWriter output, bool json, object value)
    {
        if (json)
        {
            return output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
        }

        if (value is CodexIntegrationResult result)
        {
            var trust = result.TrustReviewRequired
                ? " Codex will require review of the new stable Hook path."
                : string.Empty;
            return output.WriteLineAsync($"AgentBell integration: {result.State} ({result.Code}).{trust}");
        }

        return output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
    }
}
