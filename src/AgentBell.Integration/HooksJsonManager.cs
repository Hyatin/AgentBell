using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentBell.Integration;

/// <summary>Safely inspects and mutates only AgentBell's user-level Codex Stop Hook.</summary>
public sealed partial class HooksJsonManager
{
    private const long MaximumHooksFileBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a manager with a replaceable clock for backup tests.</summary>
    public HooksJsonManager(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var path = Path.GetFullPath(hooksPath);
        cancellationToken.ThrowIfCancellationRequested();

        var load = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!load.Success)
        {
            return Result(
                success: false,
                changed: false,
                CodexIntegrationState.Unknown,
                load.Code,
                path);
        }

        if (!load.Exists && operation is HooksOperation.Status or HooksOperation.Uninstall)
        {
            return Result(true, false, CodexIntegrationState.Missing, "hook_missing", path);
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
                analysis.Candidates.Count);
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
                analysis.Candidates.Count);
        }

        var trustReviewRequired = false;
        if (operation == HooksOperation.Uninstall)
        {
            if (analysis.Candidates.Count == 0)
            {
                return Result(true, false, CodexIntegrationState.Missing, "hook_missing", path);
            }

            RemoveCandidate(root, analysis.Candidates[0]);
            CleanupAgentBellEmptyContainers(root, analysis.Candidates[0]);
        }
        else if (analysis.Candidates.Count == 0)
        {
            AddAgentBellHandler(root, commands);
            trustReviewRequired = true;
        }
        else
        {
            RepairHandler(analysis.Candidates[0].Handler, commands);
            trustReviewRequired = state != CodexIntegrationState.Installed;
            if (!trustReviewRequired)
            {
                return Result(
                    true,
                    false,
                    CodexIntegrationState.Installed,
                    "installed",
                    path,
                    1);
            }
        }

        var write = await WriteAsync(path, root, load.Exists, cancellationToken).ConfigureAwait(false);
        if (!write.Success)
        {
            return Result(
                success: false,
                changed: false,
                CodexIntegrationState.Unknown,
                write.Code,
                path,
                analysis.Candidates.Count);
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
            AgentBellHookCount = operation == HooksOperation.Uninstall ? 0 : 1,
            TrustReviewRequired = trustReviewRequired,
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

        if (!hooksObject.TryGetPropertyValue("Stop", out var stopNode) || stopNode is null)
        {
            return new HooksAnalysis(true, []);
        }

        if (stopNode is not JsonArray stopGroups)
        {
            return new HooksAnalysis(false, []);
        }

        var candidates = new List<HookCandidate>();
        foreach (var groupNode in stopGroups)
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
                        handler["type"]?.GetValue<string>(),
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
                var exact = kind == CandidateKind.Current
                    && string.Equals(command, commands.Command, StringComparison.Ordinal)
                    && string.Equals(commandWindows, commands.CommandWindows, StringComparison.Ordinal)
                    && handler["timeout"]?.GetValue<int>() == 3;
                candidates.Add(new HookCandidate(group, handlers, handler, kind, exact));
            }
        }

        return new HooksAnalysis(true, candidates);
    }

    private static CodexIntegrationState DetermineState(
        IReadOnlyList<HookCandidate> candidates,
        HookCommands commands)
    {
        _ = commands;
        if (candidates.Count == 0)
        {
            return CodexIntegrationState.Missing;
        }

        if (candidates.Count > 1 || candidates[0].Kind == CandidateKind.Other)
        {
            return CodexIntegrationState.NeedsManualReview;
        }

        return candidates[0].Kind == CandidateKind.Current && candidates[0].IsExact
            ? CodexIntegrationState.Installed
            : CodexIntegrationState.NeedsRepair;
    }

    private static void AddAgentBellHandler(JsonObject root, HookCommands commands)
    {
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var stop = hooks["Stop"] as JsonArray;
        if (stop is null)
        {
            stop = [];
            hooks["Stop"] = stop;
        }

        stop.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(CreateHandler(commands)),
        });
    }

    private static JsonObject CreateHandler(HookCommands commands) =>
        new()
        {
            ["type"] = "command",
            ["command"] = commands.Command,
            ["commandWindows"] = commands.CommandWindows,
            ["timeout"] = 3,
            ["statusMessage"] = "Sending completion to AgentBell",
        };

    private static void RepairHandler(JsonObject handler, HookCommands commands)
    {
        handler["type"] = "command";
        handler["command"] = commands.Command;
        handler["commandWindows"] = commands.CommandWindows;
        handler["timeout"] = 3;
        handler["statusMessage"] = "Sending completion to AgentBell";
    }

    private static void RemoveCandidate(JsonObject root, HookCandidate candidate)
    {
        _ = root;
        candidate.Handlers.Remove(candidate.Handler);
    }

    private static void CleanupAgentBellEmptyContainers(JsonObject root, HookCandidate candidate)
    {
        if (root["hooks"] is not JsonObject hooks
            || hooks["Stop"] is not JsonArray stop)
        {
            return;
        }

        if (candidate.Handlers.Count == 0 && candidate.Group.Count == 1)
        {
            stop.Remove(candidate.Group);
        }

        if (stop.Count == 0)
        {
            hooks.Remove("Stop");
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
        if (!File.Exists(path))
        {
            return new HooksLoadResult(true, false, new JsonObject(), "success");
        }

        try
        {
            if (new FileInfo(path).Length > MaximumHooksFileBytes)
            {
                return new HooksLoadResult(false, true, null, "hooks_file_too_large");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
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
        string path,
        JsonObject root,
        bool existed,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new HooksWriteResult(false, null, "hooks_path_invalid");
        }

        string? backupPath = null;
        var temporaryPath = $"{path}.agentbell-tmp-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(directory);
            if (existed)
            {
                backupPath = NextBackupPath(path);
                File.Copy(path, backupPath, overwrite: false);
            }

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    root,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (existed)
            {
                File.Replace(temporaryPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            return new HooksWriteResult(true, backupPath, "success");
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
            return new HooksWriteResult(false, backupPath, "hooks_write_failed");
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
                // Temporary cleanup cannot change the operation result.
            }
        }
    }

    private string NextBackupPath(string hooksPath)
    {
        var timestamp = _timeProvider.GetUtcNow();
        while (true)
        {
            var candidate = $"{hooksPath}.agentbell-backup-{timestamp:yyyyMMdd-HHmmss}";
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            timestamp = timestamp.AddSeconds(1);
        }
    }

    private static CandidateKind ClassifyPath(string candidatePath, string desiredPath)
    {
        if (string.Equals(
            Path.GetFullPath(candidatePath),
            Path.GetFullPath(desiredPath),
            StringComparison.OrdinalIgnoreCase))
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
        && StopOptionRegex().IsMatch(command);

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
            TrustReviewRequired = false,
        };

    [GeneratedRegex("(?:^|\\s)--codex-stop-hook(?:\\s|\"|$)", RegexOptions.CultureInvariant)]
    private static partial Regex StopOptionRegex();

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
        JsonObject Group,
        JsonArray Handlers,
        JsonObject Handler,
        CandidateKind Kind,
        bool IsExact);

    private sealed record HooksAnalysis(bool StructureValid, IReadOnlyList<HookCandidate> Candidates);

    private sealed record HooksLoadResult(bool Success, bool Exists, JsonObject? Root, string Code);

    private sealed record HooksWriteResult(bool Success, string? BackupPath, string Code);
}
