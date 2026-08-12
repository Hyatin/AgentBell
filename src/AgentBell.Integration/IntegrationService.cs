namespace AgentBell.Integration;

/// <summary>Coordinates Codex home resolution and safe hooks.json operations.</summary>
public sealed class IntegrationService
{
    private readonly CodexHomeResolver _homeResolver;
    private readonly HooksJsonManager _hooksManager;
    private readonly HookCommandBuilder _commandBuilder;
    private readonly string _hookExecutablePath;

    /// <summary>Initializes the service for the installed Hook path.</summary>
    public IntegrationService(
        string hookExecutablePath,
        CodexHomeResolver? homeResolver = null,
        HooksJsonManager? hooksManager = null,
        HookCommandBuilder? commandBuilder = null)
    {
        _hookExecutablePath = WindowsPathCanonicalizer.Canonicalize(hookExecutablePath);
        _homeResolver = homeResolver ?? new CodexHomeResolver();
        _hooksManager = hooksManager ?? new HooksJsonManager();
        _commandBuilder = commandBuilder ?? new HookCommandBuilder();
    }

    /// <summary>Executes install, repair, status, verification, or uninstall.</summary>
    public async Task<CodexIntegrationResult> ExecuteAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        var resolution = _homeResolver.Resolve();
        if (!resolution.IsAvailable
            || string.IsNullOrWhiteSpace(resolution.HomePath)
            || string.IsNullOrWhiteSpace(resolution.HooksPath))
        {
            return Failure(resolution.Code, null);
        }

        HookCommands commands;
        try
        {
            commands = _commandBuilder.Build(_hookExecutablePath);
        }
        catch (ArgumentException)
        {
            return Failure("hook_path_invalid", resolution.HooksPath) with
            {
                CodexHomePath = resolution.HomePath,
                Stage = "hook_validation",
                CompletedStages = ["codex_home_resolved"],
            };
        }

        if (operation is "install" or "repair" or "verify")
        {
            if (!File.Exists(_hookExecutablePath))
            {
                return new CodexIntegrationResult
                {
                    Success = false,
                    Changed = false,
                    State = CodexIntegrationState.NeedsRepair,
                    Code = "hook_executable_missing",
                    HooksPath = resolution.HooksPath,
                    AgentBellHookCount = 0,
                    TrustReviewRequired = false,
                    CodexHomePath = resolution.HomePath,
                    Stage = "hook_validation",
                    CompletedStages = ["codex_home_resolved"],
                };
            }

            try
            {
                Directory.CreateDirectory(resolution.HomePath);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                return Failure("codex_home_unavailable", resolution.HooksPath) with
                {
                    CodexHomePath = resolution.HomePath,
                    Stage = "parent_create",
                    CompletedStages = ["codex_home_resolved"],
                };
            }
        }

        var result = operation switch
        {
            "install" => await _hooksManager.InstallAsync(
                resolution.HooksPath,
                commands,
                cancellationToken).ConfigureAwait(false),
            "repair" => await _hooksManager.RepairAsync(
                resolution.HooksPath,
                commands,
                cancellationToken).ConfigureAwait(false),
            "status" => await _hooksManager.StatusAsync(
                resolution.HooksPath,
                commands,
                cancellationToken).ConfigureAwait(false),
            "verify" => await VerifyAsync(
                resolution.HooksPath,
                commands,
                cancellationToken).ConfigureAwait(false),
            "uninstall" => await _hooksManager.UninstallAsync(
                resolution.HooksPath,
                commands,
                cancellationToken).ConfigureAwait(false),
            _ => Failure("invalid_operation", resolution.HooksPath),
        };

        if (operation == "status"
            && result.State == CodexIntegrationState.Installed
            && !File.Exists(_hookExecutablePath))
        {
            return result with
            {
                Success = true,
                State = CodexIntegrationState.NeedsRepair,
                Code = "hook_executable_missing",
            };
        }

        return result with
        {
            CodexHomePath = resolution.HomePath,
        };
    }

    private async Task<CodexIntegrationResult> VerifyAsync(
        string hooksPath,
        HookCommands commands,
        CancellationToken cancellationToken)
    {
        var status = await _hooksManager.StatusAsync(hooksPath, commands, cancellationToken)
            .ConfigureAwait(false);
        return status.State == CodexIntegrationState.Installed
            && status.AgentBellHookCount == commands.All.Count
            ? status with
            {
                Success = true,
                Code = "verified",
                Stage = "completed",
                CompletedStages = ["loaded", "analyzed", "verified"],
            }
            : status with
            {
                Success = false,
                Code = "verification_failed",
                Stage = "verify",
            };
    }

    private static CodexIntegrationResult Failure(string code, string? hooksPath) =>
        new()
        {
            Success = false,
            Changed = false,
            State = CodexIntegrationState.Unknown,
            Code = code,
            HooksPath = hooksPath,
            AgentBellHookCount = 0,
            TrustReviewRequired = false,
            Stage = "resolve",
        };
}
