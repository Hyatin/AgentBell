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
        _hookExecutablePath = Path.GetFullPath(hookExecutablePath);
        _homeResolver = homeResolver ?? new CodexHomeResolver();
        _hooksManager = hooksManager ?? new HooksJsonManager();
        _commandBuilder = commandBuilder ?? new HookCommandBuilder();
    }

    /// <summary>Executes install, repair, status, or uninstall.</summary>
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
            return Failure("hook_path_invalid", resolution.HooksPath);
        }

        if (operation is "install" or "repair")
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
                return Failure("codex_home_unavailable", resolution.HooksPath);
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

        return result;
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
        };
}
