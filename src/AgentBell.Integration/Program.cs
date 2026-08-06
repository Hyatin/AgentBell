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
        var json = false;
        string? operation = null;
        string? explicitCodexHome = null;
        var invalidArguments = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                json = true;
                continue;
            }

            if (string.Equals(argument, "--codex-home", StringComparison.Ordinal))
            {
                if (explicitCodexHome is not null
                    || index + 1 >= arguments.Count
                    || string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    invalidArguments = true;
                    break;
                }

                explicitCodexHome = arguments[++index];
                continue;
            }

            if (operation is not null)
            {
                invalidArguments = true;
                break;
            }

            operation = argument;
        }

        if (invalidArguments
            || operation is not ("install" or "repair" or "status" or "verify" or "uninstall" or "version")
            || (operation == "version" && explicitCodexHome is not null))
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

        if (operation == "version")
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
            var hookPath = ResolveSiblingHookPath(AppContext.BaseDirectory);
            var homeResolver = explicitCodexHome is null
                ? null
                : new CodexHomeResolver(_ => explicitCodexHome);
            var result = await new IntegrationService(hookPath, homeResolver)
                .ExecuteAsync(operation, cancellationToken)
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
                Stage = "unexpected_error",
            };
            await WriteAsync(output, json, result).ConfigureAwait(false);
            return IntegrationExitCodes.LocalOperationFailed;
        }
    }

    internal static string ResolveSiblingHookPath(string integrationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(integrationDirectory);
        return WindowsPathCanonicalizer.Canonicalize(
            Path.Combine(integrationDirectory, "AgentBell.Hook.exe"));
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
