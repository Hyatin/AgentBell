using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentBell.Integration;

/// <summary>Safely inspects and mutates only AgentBell's managed Codex command Hooks.</summary>
public sealed partial class HooksJsonManager
{
    private const long MaximumHooksFileBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider;
    private readonly HooksFileSystem _fileSystem;

    /// <summary>Initializes a manager with a replaceable clock for backup tests.</summary>
    public HooksJsonManager(TimeProvider? timeProvider = null)
        : this(timeProvider, new HooksFileSystem())
    {
    }

    internal HooksJsonManager(TimeProvider? timeProvider, HooksFileSystem fileSystem)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>Returns integration state without modifying any file.</summary>
    public Task<CodexIntegrationResult> StatusAsync(
        string hooksPath,
        HookCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(HooksOperation.Status, hooksPath, commands, cancellationToken);

    /// <summary>Installs AgentBell or safely migrates one clear development Hook.</summary>
    public Task<CodexIntegrationResult> InstallAsync(
        string hooksPath,
        HookCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(HooksOperation.Install, hooksPath, commands, cancellationToken);

    /// <summary>Repairs AgentBell idempotently using the same safe merge rules as install.</summary>
    public Task<CodexIntegrationResult> RepairAsync(
        string hooksPath,
        HookCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(HooksOperation.Repair, hooksPath, commands, cancellationToken);

    /// <summary>Removes exactly one strictly identified AgentBell Hook and nothing else.</summary>
    public Task<CodexIntegrationResult> UninstallAsync(
        string hooksPath,
        HookCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(HooksOperation.Uninstall, hooksPath, commands, cancellationToken);

    private async Task<CodexIntegrationResult> ExecuteAsync(
        HooksOperation operation,
        string hooksPath,
        HookCommands commands,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksPath);
        ArgumentNullException.ThrowIfNull(commands);
        var path = WindowsPathCanonicalizer.Canonicalize(hooksPath);
        cancellationToken.ThrowIfCancellationRequested();
        var hooksFileExistedBefore = _fileSystem.FileExists(path);
        var backupCandidateCount = CountBackupCandidates(path);

        var result = await ExecuteCoreAsync(operation, path, commands, cancellationToken)
            .ConfigureAwait(false);
        return result with
        {
            HooksFileExistedBefore = hooksFileExistedBefore,
            BackupCandidateCount = backupCandidateCount,
        };
    }

    private async Task<CodexIntegrationResult> ExecuteCoreAsync(
        HooksOperation operation,
        string path,
        HookCommands commands,
        CancellationToken cancellationToken)
    {

        var load = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!load.Success)
        {
            return Result(
                success: false,
                changed: false,
                CodexIntegrationState.Unknown,
                load.Code,
                path) with
            {
                Stage = "load",
                CompletedStages = [],
            };
        }

        if (!load.Exists && operation is HooksOperation.Status or HooksOperation.Uninstall)
        {
            return Result(true, false, CodexIntegrationState.Missing, "hook_missing", path) with
            {
                Stage = "skipped_missing",
                CompletedStages = ["loaded", "skipped_missing"],
            };
        }

        var root = load.Root ?? new JsonObject();
        var analysis = Analyze(root, commands);
        if (!analysis.StructureValid)
        {
            return Result(
                success: false,
                changed: false,
                CodexIntegrationState.Unknown,
                "hooks_structure_invalid",
                path,
                analysis.Candidates.Count) with
            {
                Stage = "analyze",
                CompletedStages = ["loaded"],
            };
        }

        var state = DetermineState(analysis.Candidates, commands);
        if (operation == HooksOperation.Status)
        {
            var code = state switch
            {
                CodexIntegrationState.Installed => "installed",
                CodexIntegrationState.Missing => "hook_missing",
                CodexIntegrationState.NeedsRepair => "needs_repair",
                CodexIntegrationState.NeedsManualReview => "manual_review_required",
                _ => "unknown",
            };
            return Result(
                success: state != CodexIntegrationState.Unknown,
                changed: false,
                state,
                code,
                path,
                analysis.Candidates.Count);
        }

        if (state == CodexIntegrationState.NeedsManualReview)
        {
            return Result(
                success: false,
                changed: false,
                state,
                "manual_review_required",
                path,
                analysis.Candidates.Count) with
            {
                Stage = "analyze",
                CompletedStages = ["loaded", "analyzed"],
            };
        }

        var trustReviewRequired = false;
        if (operation == HooksOperation.Uninstall)
        {
            if (analysis.Candidates.Count == 0)
            {
                return Result(true, false, CodexIntegrationState.Missing, "hook_missing", path) with
                {
                    Stage = "skipped_missing",
                    CompletedStages = ["loaded", "analyzed", "skipped_missing"],
                };
            }

            foreach (var candidate in analysis.Candidates)
            {
                RemoveCandidate(root, candidate);
                CleanupAgentBellEmptyContainers(root, candidate);
            }
        }
        else
        {
            if (state == CodexIntegrationState.Installed)
            {
                return Result(
                    true,
                    false,
                    CodexIntegrationState.Installed,
                    "installed",
                    path,
                    commands.All.Count);
            }

            foreach (var definition in commands.All)
            {
                var candidate = analysis.Candidates.SingleOrDefault(
                    item => item.HookType == definition.EventName);
                if (candidate is null)
                {
                    AddAgentBellHandler(root, definition);
                }
                else
                {
                    RepairHandler(candidate.Handler, definition);
                }
            }

            trustReviewRequired = true;
        }

        var write = await WriteAsync(
            operation,
            path,
            root,
            commands,
            load.Exists,
            cancellationToken).ConfigureAwait(false);
        if (!write.Success)
        {
            return Result(
                success: false,
                changed: false,
                CodexIntegrationState.Unknown,
                write.Code,
                path,
                analysis.Candidates.Count) with
            {
                BackupPath = write.BackupPath,
                TemporaryPath = write.TemporaryPath,
                Stage = write.Stage,
                CompletedStages = write.CompletedStages,
                RollbackAttempted = write.RollbackAttempted,
                RollbackSucceeded = write.RollbackSucceeded,
            };
        }

        if (operation == HooksOperation.Uninstall && write.Code == "hook_missing")
        {
            return Result(true, false, CodexIntegrationState.Missing, "hook_missing", path) with
            {
                BackupPath = write.BackupPath,
                TemporaryPath = write.TemporaryPath,
                Stage = write.Stage,
                CompletedStages = write.CompletedStages,
            };
        }

        return new CodexIntegrationResult
        {
            Success = true,
            Changed = true,
            State = operation == HooksOperation.Uninstall
                ? CodexIntegrationState.Missing
                : CodexIntegrationState.Installed,
            Code = operation == HooksOperation.Uninstall ? "uninstalled" : "installed",
            HooksPath = path,
            BackupPath = write.BackupPath,
            TemporaryPath = write.TemporaryPath,
            AgentBellHookCount = operation == HooksOperation.Uninstall ? 0 : commands.All.Count,
            AgentBellHookCountBefore = analysis.Candidates.Count,
            TrustReviewRequired = trustReviewRequired,
            Stage = write.Stage,
            CompletedStages = write.CompletedStages,
            RollbackAttempted = write.RollbackAttempted,
            RollbackSucceeded = write.RollbackSucceeded,
        };
    }

    private static HooksAnalysis Analyze(JsonObject root, HookCommands commands)
    {
        if (!root.TryGetPropertyValue("hooks", out var hooksNode) || hooksNode is null)
        {
            return new HooksAnalysis(true, []);
        }

        if (hooksNode is not JsonObject hooksObject)
        {
            return new HooksAnalysis(false, []);
        }

        var candidates = new List<HookCandidate>();
        foreach (var eventProperty in hooksObject)
        {
            if (eventProperty.Value is null)
            {
                continue;
            }

            if (eventProperty.Value is not JsonArray groups)
            {
                return new HooksAnalysis(false, []);
            }

            foreach (var groupNode in groups)
            {
                if (groupNode is not JsonObject group)
                {
                    continue;
                }

                if (!group.TryGetPropertyValue("hooks", out var handlersNode)
                    || handlersNode is not JsonArray handlers)
                {
                    continue;
                }

                foreach (var handlerNode in handlers)
                {
                    if (handlerNode is not JsonObject handler
                        || !string.Equals(
                            GetString(handler, "type"),
                            "command",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var command = GetString(handler, "command");
                    var commandWindows = GetString(handler, "commandWindows");
                    if (!LooksLikeAgentBell(command) && !LooksLikeAgentBell(commandWindows))
                    {
                        continue;
                    }

                    var paths = new[] { command, commandWindows }
                        .Select(TryExtractHookPath)
                        .Where(candidate => candidate is not null)
                        .Cast<string>()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var kind = paths.Length == 1
                        ? ClassifyPath(paths[0], commands.HookExecutablePath)
                        : CandidateKind.Other;
                    var hookType = IdentifyHookType(command, commandWindows);
                    var definition = commands.All.SingleOrDefault(
                        item => item.EventName == hookType);
                    var exact = kind == CandidateKind.Current
                        && definition is not null
                        && eventProperty.Key == definition.EventName
                        && string.Equals(command, definition.Command, StringComparison.Ordinal)
                        && string.Equals(
                            commandWindows,
                            definition.CommandWindows,
                            StringComparison.Ordinal)
                        && handler["timeout"]?.GetValue<int>() == 3;
                    candidates.Add(new HookCandidate(
                        eventProperty.Key,
                        group,
                        handlers,
                        handler,
                        hookType,
                        kind,
                        exact));
                }
            }
        }

        return new HooksAnalysis(true, candidates);
    }

    private static CodexIntegrationState DetermineState(
        IReadOnlyList<HookCandidate> candidates,
        HookCommands commands)
    {
        if (candidates.Any(item => item.Kind == CandidateKind.Other || item.HookType is null)
            || candidates.GroupBy(item => item.HookType).Any(group => group.Count() > 1)
            || candidates.Any(item => item.EventName != item.HookType))
        {
            return CodexIntegrationState.NeedsManualReview;
        }

        if (candidates.Count == 0)
        {
            return CodexIntegrationState.Missing;
        }

        if (commands.All.All(definition =>
                candidates.Any(candidate =>
                    candidate.HookType == definition.EventName && candidate.IsExact)))
        {
            return CodexIntegrationState.Installed;
        }

        return CodexIntegrationState.NeedsRepair;
    }

    private static void AddAgentBellHandler(
        JsonObject root,
        HookCommandDefinition definition)
    {
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var groups = hooks[definition.EventName] as JsonArray;
        if (groups is null)
        {
            groups = [];
            hooks[definition.EventName] = groups;
        }

        groups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(CreateHandler(definition)),
        });
    }

    private static JsonObject CreateHandler(HookCommandDefinition definition) =>
        new()
        {
            ["type"] = "command",
            ["command"] = definition.Command,
            ["commandWindows"] = definition.CommandWindows,
            ["timeout"] = 3,
            ["statusMessage"] = definition.StatusMessage,
        };

    private static void RepairHandler(
        JsonObject handler,
        HookCommandDefinition definition)
    {
        handler["type"] = "command";
        handler["command"] = definition.Command;
        handler["commandWindows"] = definition.CommandWindows;
        handler["timeout"] = 3;
        handler["statusMessage"] = definition.StatusMessage;
    }

    private static void RemoveCandidate(JsonObject root, HookCandidate candidate)
    {
        _ = root;
        candidate.Handlers.Remove(candidate.Handler);
    }

    private static void CleanupAgentBellEmptyContainers(JsonObject root, HookCandidate candidate)
    {
        if (root["hooks"] is not JsonObject hooks
            || hooks[candidate.EventName] is not JsonArray groups)
        {
            return;
        }

        if (candidate.Handlers.Count == 0 && candidate.Group.Count == 1)
        {
            groups.Remove(candidate.Group);
        }

        if (groups.Count == 0)
        {
            hooks.Remove(candidate.EventName);
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }
    }

    private async Task<HooksLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_fileSystem.FileExists(path))
            {
                return new HooksLoadResult(true, false, new JsonObject(), "success");
            }

            if (_fileSystem.GetFileLength(path) > MaximumHooksFileBytes)
            {
                return new HooksLoadResult(false, true, null, "hooks_file_too_large");
            }

            var bytes = await _fileSystem.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(
                bytes,
                new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            return node is JsonObject root
                ? new HooksLoadResult(true, true, root, "success")
                : new HooksLoadResult(false, true, null, "hooks_root_invalid");
        }
        catch (JsonException)
        {
            return new HooksLoadResult(false, true, null, "hooks_json_invalid");
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return new HooksLoadResult(false, true, null, "hooks_read_failed");
        }
    }

    private async Task<HooksWriteResult> WriteAsync(
        HooksOperation operation,
        string path,
        JsonObject root,
        HookCommands commands,
        bool existed,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return HooksWriteResult.Failure("hooks_path_invalid", null, null, "prepare", []);
        }

        string? backupPath = null;
        var temporaryPath = $"{path}.agentbell-tmp-{Guid.NewGuid():N}";
        var completedStages = new List<string>();
        var stage = "prepare";
        var commitStarted = false;
        try
        {
            if (operation == HooksOperation.Uninstall
                && (!_fileSystem.DirectoryExists(directory) || !_fileSystem.FileExists(path)))
            {
                completedStages.Add("skipped_missing");
                return HooksWriteResult.SkippedMissing(temporaryPath, completedStages);
            }

            _fileSystem.CreateDirectory(directory);
            completedStages.Add("parent_ready");
            if (existed)
            {
                stage = "backup";
                backupPath = NextBackupPath(path);
                _fileSystem.CopyFile(path, backupPath, overwrite: false);
                completedStages.Add("backup_created");
            }

            stage = "temporary_write";
            await using (var stream = _fileSystem.CreateWriteThroughFile(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    root,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            completedStages.Add("temporary_flushed");

            stage = existed ? "atomic_replace" : "atomic_create";
            commitStarted = true;
            if (existed)
            {
                _fileSystem.ReplaceFile(temporaryPath, path);
            }
            else
            {
                _fileSystem.MoveFile(temporaryPath, path);
            }
            completedStages.Add(existed ? "formal_replaced" : "formal_created");

            stage = "verify";
            if (!await VerifyPersistedAsync(operation, path, commands, cancellationToken)
                    .ConfigureAwait(false))
            {
                if (operation == HooksOperation.Uninstall && !_fileSystem.FileExists(path))
                {
                    completedStages.Add("skipped_missing");
                    return HooksWriteResult.SkippedMissing(
                        temporaryPath,
                        completedStages,
                        backupPath);
                }

                var rollbackSucceeded = await RollbackAsync(
                    path,
                    backupPath,
                    existed,
                    cancellationToken).ConfigureAwait(false);
                if (rollbackSucceeded)
                {
                    completedStages.Add("rollback_completed");
                }

                return HooksWriteResult.Failure(
                    rollbackSucceeded
                        ? "hooks_verification_failed"
                        : "hooks_verification_failed_rollback_failed",
                    backupPath,
                    temporaryPath,
                    "verify",
                    completedStages,
                    rollbackAttempted: true,
                    rollbackSucceeded);
            }

            completedStages.Add("verified");
            return new HooksWriteResult(
                true,
                backupPath,
                temporaryPath,
                "completed",
                completedStages,
                false,
                false,
                "success");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or JsonException)
        {
            if (operation == HooksOperation.Uninstall && !_fileSystem.FileExists(path))
            {
                completedStages.Add("skipped_missing");
                return HooksWriteResult.SkippedMissing(
                    temporaryPath,
                    completedStages,
                    backupPath);
            }

            var rollbackAttempted = commitStarted;
            var rollbackSucceeded = false;
            if (rollbackAttempted)
            {
                rollbackSucceeded = await RollbackAsync(
                    path,
                    backupPath,
                    existed,
                    cancellationToken).ConfigureAwait(false);
                if (rollbackSucceeded)
                {
                    completedStages.Add("rollback_completed");
                }
            }

            return HooksWriteResult.Failure(
                rollbackAttempted && !rollbackSucceeded
                    ? "hooks_write_failed_rollback_failed"
                    : "hooks_write_failed",
                backupPath,
                temporaryPath,
                stage,
                completedStages,
                rollbackAttempted,
                rollbackSucceeded);
        }
        finally
        {
            try
            {
                if (_fileSystem.FileExists(temporaryPath))
                {
                    _fileSystem.DeleteFile(temporaryPath);
                }
            }
            catch
            {
                // Temporary cleanup cannot change the operation result.
            }
        }
    }

    private async Task<bool> VerifyPersistedAsync(
        HooksOperation operation,
        string path,
        HookCommands commands,
        CancellationToken cancellationToken)
    {
        var load = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!load.Success || !load.Exists || load.Root is null)
        {
            return false;
        }

        var analysis = Analyze(load.Root, commands);
        if (!analysis.StructureValid)
        {
            return false;
        }

        return operation == HooksOperation.Uninstall
            ? analysis.Candidates.Count == 0
            : analysis.Candidates.Count == commands.All.Count
                && DetermineState(analysis.Candidates, commands) == CodexIntegrationState.Installed;
    }

    private async Task<bool> RollbackAsync(
        string path,
        string? backupPath,
        bool existed,
        CancellationToken cancellationToken)
    {
        var rollbackTemporaryPath = $"{path}.agentbell-rollback-{Guid.NewGuid():N}";
        try
        {
            if (!existed)
            {
                if (_fileSystem.FileExists(path))
                {
                    _fileSystem.DeleteFile(path);
                }

                return !_fileSystem.FileExists(path);
            }

            if (string.IsNullOrWhiteSpace(backupPath) || !_fileSystem.FileExists(backupPath))
            {
                return false;
            }

            var backupBytes = await _fileSystem.ReadAllBytesAsync(backupPath, cancellationToken)
                .ConfigureAwait(false);
            if (_fileSystem.FileExists(path))
            {
                var currentBytes = await _fileSystem.ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (currentBytes.AsSpan().SequenceEqual(backupBytes))
                {
                    return true;
                }
            }

            await using (var stream = _fileSystem.CreateWriteThroughFile(rollbackTemporaryPath))
            {
                await stream.WriteAsync(backupBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (_fileSystem.FileExists(path))
            {
                _fileSystem.ReplaceFile(rollbackTemporaryPath, path);
            }
            else
            {
                _fileSystem.MoveFile(rollbackTemporaryPath, path);
            }

            var restoredBytes = await _fileSystem.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return restoredBytes.AsSpan().SequenceEqual(backupBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (_fileSystem.FileExists(rollbackTemporaryPath))
                {
                    _fileSystem.DeleteFile(rollbackTemporaryPath);
                }
            }
            catch
            {
                // Rollback temporary cleanup cannot expose file content or change the result.
            }
        }
    }

    private string NextBackupPath(string hooksPath)
    {
        var timestamp = _timeProvider.GetUtcNow();
        while (true)
        {
            var candidate = $"{hooksPath}.agentbell-backup-{timestamp:yyyyMMdd-HHmmss}";
            if (!_fileSystem.FileExists(candidate))
            {
                return candidate;
            }

            timestamp = timestamp.AddSeconds(1);
        }
    }

    private int CountBackupCandidates(string hooksPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(hooksPath);
            var fileName = Path.GetFileName(hooksPath);
            return string.IsNullOrWhiteSpace(directory)
                || string.IsNullOrWhiteSpace(fileName)
                || !_fileSystem.DirectoryExists(directory)
                ? 0
                : _fileSystem.EnumerateFiles(directory, $"{fileName}.*backup-*").Count();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return 0;
        }
    }

    private static CandidateKind ClassifyPath(string candidatePath, string desiredPath)
    {
        if (WindowsPathCanonicalizer.AreEquivalent(candidatePath, desiredPath))
        {
            return CandidateKind.Current;
        }

        var normalized = candidatePath.Replace('/', '\\');
        return normalized.Contains(
            "\\AgentBell\\artifacts\\m0-hook\\AgentBell.Hook.exe",
            StringComparison.OrdinalIgnoreCase)
            ? CandidateKind.Development
            : CandidateKind.Other;
    }

    private static bool LooksLikeAgentBell(string? command) =>
        !string.IsNullOrWhiteSpace(command)
        && command.Contains("AgentBell.Hook.exe", StringComparison.OrdinalIgnoreCase)
        && ManagedOptionRegex().IsMatch(command);

    private static string? IdentifyHookType(string? command, string? commandWindows)
    {
        var combined = $"{command}\n{commandWindows}";
        var isStop = StopOptionRegex().IsMatch(combined);
        var isPermission = PermissionOptionRegex().IsMatch(combined);
        var isPostToolUse = PostToolUseOptionRegex().IsMatch(combined);
        return (isStop, isPermission, isPostToolUse) switch
        {
            (true, false, false) => "Stop",
            (false, true, false) => "PermissionRequest",
            (false, false, true) => "PostToolUse",
            _ => null,
        };
    }

    private static string? TryExtractHookPath(string? command)
    {
        if (!LooksLikeAgentBell(command))
        {
            return null;
        }

        var match = HookPathRegex().Match(command!);
        var value = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["plain"].Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            return string.Equals(
                Path.GetFileName(fullPath),
                "AgentBell.Hook.exe",
                StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonObject value, string propertyName)
    {
        try
        {
            return value[propertyName]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static CodexIntegrationResult Result(
        bool success,
        bool changed,
        CodexIntegrationState state,
        string code,
        string hooksPath,
        int hookCount = 0) =>
        new()
        {
            Success = success,
            Changed = changed,
            State = state,
            Code = code,
            HooksPath = hooksPath,
            AgentBellHookCount = hookCount,
            AgentBellHookCountBefore = hookCount,
            TrustReviewRequired = false,
            Stage = "completed",
            CompletedStages = ["loaded", "analyzed"],
        };

    [GeneratedRegex("(?:^|\\s)--codex-stop-hook(?:\\s|\"|$)", RegexOptions.CultureInvariant)]
    private static partial Regex StopOptionRegex();

    [GeneratedRegex(
        "(?:^|\\s)--codex-permission-request-hook(?:\\s|\"|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PermissionOptionRegex();

    [GeneratedRegex(
        "(?:^|\\s)--codex-post-tool-use-hook(?:\\s|\"|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PostToolUseOptionRegex();

    [GeneratedRegex(
        "(?:^|\\s)--codex-(?:stop|permission-request|post-tool-use)-hook(?:\\s|\"|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ManagedOptionRegex();

    [GeneratedRegex(
        "(?:\\\"(?<quoted>[^\\\"]*AgentBell\\.Hook\\.exe)\\\"|(?<plain>(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\s\\\"]*AgentBell\\.Hook\\.exe))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HookPathRegex();

    private enum HooksOperation
    {
        Status,
        Install,
        Repair,
        Uninstall,
    }

    private enum CandidateKind
    {
        Current,
        Development,
        Other,
    }

    private sealed record HookCandidate(
        string EventName,
        JsonObject Group,
        JsonArray Handlers,
        JsonObject Handler,
        string? HookType,
        CandidateKind Kind,
        bool IsExact);

    private sealed record HooksAnalysis(bool StructureValid, IReadOnlyList<HookCandidate> Candidates);

    private sealed record HooksLoadResult(bool Success, bool Exists, JsonObject? Root, string Code);

    private sealed record HooksWriteResult(
        bool Success,
        string? BackupPath,
        string? TemporaryPath,
        string Stage,
        IReadOnlyList<string> CompletedStages,
        bool RollbackAttempted,
        bool RollbackSucceeded,
        string Code)
    {
        internal static HooksWriteResult SkippedMissing(
            string? temporaryPath,
            IReadOnlyList<string> completedStages,
            string? backupPath = null) =>
            new(
                true,
                backupPath,
                temporaryPath,
                "skipped_missing",
                completedStages,
                false,
                false,
                "hook_missing");

        internal static HooksWriteResult Failure(
            string code,
            string? backupPath,
            string? temporaryPath,
            string stage,
            IReadOnlyList<string> completedStages,
            bool rollbackAttempted = false,
            bool rollbackSucceeded = false) =>
            new(
                false,
                backupPath,
                temporaryPath,
                stage,
                completedStages,
                rollbackAttempted,
                rollbackSucceeded,
                code);
    }
}
