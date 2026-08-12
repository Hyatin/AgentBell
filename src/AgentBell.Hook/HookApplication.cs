using System.Diagnostics;
using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Coordinates payload parsing, local forwarding, and sanitized diagnostics.</summary>
public sealed class HookApplication
{
    /// <summary>The exact JSON response emitted for every Codex Stop Hook invocation.</summary>
    public const string StopHookContinueResponse = "{\"continue\":true}";

    private readonly IHookInputResolver _inputResolver;
    private readonly ICodexPayloadParser _parser;
    private readonly ICodexStopHookPayloadParser _stopHookParser;
    private readonly ICodexPermissionRequestPayloadParser _permissionHookParser;
    private readonly IPermissionRequestSanitizer _permissionSanitizer;
    private readonly ICodexPostToolUsePayloadParser _postToolUseParser;
    private readonly IPostToolUseSanitizer _postToolUseSanitizer;
    private readonly IEventForwarder _forwarder;
    private readonly IDiagnosticLogger _diagnosticLogger;

    /// <summary>Initializes the application while preserving the M0 constructor contract.</summary>
    public HookApplication(
        IHookInputResolver inputResolver,
        ICodexPayloadParser parser,
        ICodexStopHookPayloadParser stopHookParser,
        IEventForwarder forwarder,
        IDiagnosticLogger diagnosticLogger)
        : this(
            inputResolver,
            parser,
            stopHookParser,
            new CodexPermissionRequestPayloadParser(),
            new PermissionRequestSanitizer(),
            new CodexPostToolUsePayloadParser(),
            new PostToolUseSanitizer(),
            forwarder,
            diagnosticLogger)
    {
    }

    /// <summary>Initializes the Hook application with replaceable collaborators.</summary>
    public HookApplication(
        IHookInputResolver inputResolver,
        ICodexPayloadParser parser,
        ICodexStopHookPayloadParser stopHookParser,
        ICodexPermissionRequestPayloadParser permissionHookParser,
        IPermissionRequestSanitizer permissionSanitizer,
        IEventForwarder forwarder,
        IDiagnosticLogger diagnosticLogger)
        : this(
            inputResolver,
            parser,
            stopHookParser,
            permissionHookParser,
            permissionSanitizer,
            new CodexPostToolUsePayloadParser(),
            new PostToolUseSanitizer(),
            forwarder,
            diagnosticLogger)
    {
    }

    /// <summary>Initializes all three supported command Hook modes with replaceable collaborators.</summary>
    public HookApplication(
        IHookInputResolver inputResolver,
        ICodexPayloadParser parser,
        ICodexStopHookPayloadParser stopHookParser,
        ICodexPermissionRequestPayloadParser permissionHookParser,
        IPermissionRequestSanitizer permissionSanitizer,
        ICodexPostToolUsePayloadParser postToolUseParser,
        IPostToolUseSanitizer postToolUseSanitizer,
        IEventForwarder forwarder,
        IDiagnosticLogger diagnosticLogger)
    {
        _inputResolver = inputResolver ?? throw new ArgumentNullException(nameof(inputResolver));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _stopHookParser = stopHookParser ?? throw new ArgumentNullException(nameof(stopHookParser));
        _permissionHookParser = permissionHookParser
            ?? throw new ArgumentNullException(nameof(permissionHookParser));
        _permissionSanitizer = permissionSanitizer
            ?? throw new ArgumentNullException(nameof(permissionSanitizer));
        _postToolUseParser = postToolUseParser
            ?? throw new ArgumentNullException(nameof(postToolUseParser));
        _postToolUseSanitizer = postToolUseSanitizer
            ?? throw new ArgumentNullException(nameof(postToolUseSanitizer));
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
    }

    /// <summary>Processes one Codex invocation and always returns exit code zero.</summary>
    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Stream standardInput,
        TextWriter standardOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);

        var isStopHookMode = arguments.Count > 0
            && string.Equals(
                arguments[0],
                HookInputResolver.CodexStopHookOption,
                StringComparison.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        HookEventMetadata? eventMetadata = null;
        var result = ForwardResult.Failed(HookErrorCodes.UnexpectedError);
        var stage = "input_resolution";
        string? failureStage = null;
        string? exceptionType = null;

        try
        {
            var inputResult = await _inputResolver.ResolveAsync(
                arguments,
                standardInput,
                cancellationToken).ConfigureAwait(false);

            if (inputResult.Mode == HookInputMode.CodexStopHook)
            {
                eventMetadata = HookEventMetadata.ForStopHookInvocation();
            }
            else if (inputResult.Mode == HookInputMode.CodexPermissionRequestHook)
            {
                eventMetadata = HookEventMetadata.ForPermissionHookInvocation();
            }
            else if (inputResult.Mode == HookInputMode.CodexPostToolUseHook)
            {
                eventMetadata = HookEventMetadata.ForPostToolUseHookInvocation();
            }

            if (!inputResult.IsSuccess || inputResult.Arguments is null)
            {
                result = ForwardResult.Failed(
                    inputResult.ErrorCode ?? HookErrorCodes.UnexpectedError);
                return 0;
            }

            string? rawJson = null;
            stage = "payload_parse";

            if (inputResult.Mode == HookInputMode.CodexPostToolUseHook)
            {
                var postToolUseParseResult = _postToolUseParser.Parse(inputResult.Arguments[0]);
                if (!postToolUseParseResult.IsSuccess || postToolUseParseResult.Payload is null)
                {
                    result = ForwardResult.Failed(
                        postToolUseParseResult.ErrorCode ?? HookErrorCodes.UnexpectedError);
                }
                else
                {
                    var sanitized = _postToolUseSanitizer.Sanitize(
                        postToolUseParseResult.Payload);
                    eventMetadata = HookEventMetadata.FromPostToolUse(
                        postToolUseParseResult.Payload,
                        sanitized.Event);
                    rawJson = sanitized.Json;
                }
            }
            else if (inputResult.Mode == HookInputMode.CodexPermissionRequestHook)
            {
                var permissionParseResult = _permissionHookParser.Parse(inputResult.Arguments[0]);
                if (!permissionParseResult.IsSuccess || permissionParseResult.Payload is null)
                {
                    result = ForwardResult.Failed(
                        permissionParseResult.ErrorCode ?? HookErrorCodes.UnexpectedError);
                }
                else
                {
                    var sanitized = _permissionSanitizer.Sanitize(permissionParseResult.Payload);
                    eventMetadata = HookEventMetadata.FromPermissionRequest(
                        permissionParseResult.Payload,
                        sanitized.Event);
                    rawJson = sanitized.Json;
                }
            }
            else if (inputResult.Mode == HookInputMode.CodexStopHook)
            {
                var stopParseResult = _stopHookParser.Parse(inputResult.Arguments[0]);
                if (!stopParseResult.IsSuccess || stopParseResult.Payload is null)
                {
                    result = ForwardResult.Failed(
                        stopParseResult.ErrorCode ?? HookErrorCodes.UnexpectedError);
                }
                else
                {
                    eventMetadata = HookEventMetadata.FromStopHook(stopParseResult.Payload);
                    rawJson = inputResult.Arguments[0];
                }
            }
            else
            {
                var parseResult = _parser.Parse(inputResult.Arguments);
                if (!parseResult.IsSuccess
                    || parseResult.Payload is null
                    || parseResult.RawJson is null)
                {
                    result = ForwardResult.Failed(
                        parseResult.ErrorCode ?? HookErrorCodes.UnexpectedError);
                }
                else
                {
                    eventMetadata = HookEventMetadata.FromNotify(parseResult.Payload);
                    rawJson = parseResult.RawJson;
                }
            }

            if (rawJson is not null)
            {
                stage = "http_forward";
                result = await _forwarder.ForwardAsync(
                    rawJson,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            result = ForwardResult.Failed(ClassifyContainedFailure(stage, exception));
            failureStage = stage;
            exceptionType = exception.GetType().Name;
        }
        finally
        {
            stopwatch.Stop();
            TryRecordDiagnostic(HookDiagnosticEvent.Create(
                eventMetadata,
                result,
                stopwatch.Elapsed,
                failureStage,
                exceptionType));

            if (isStopHookMode)
            {
                TryWriteStopHookResponse(standardOutput);
            }
        }

        return 0;
    }

    /// <summary>Processes an invocation without writing protocol output.</summary>
    public Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Stream standardInput,
        CancellationToken cancellationToken) =>
        RunAsync(arguments, standardInput, TextWriter.Null, cancellationToken);

    /// <summary>Processes legacy notify arguments without reading standard input.</summary>
    public Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunAsync(arguments, Stream.Null, TextWriter.Null, cancellationToken);

    private static void TryWriteStopHookResponse(TextWriter standardOutput)
    {
        try
        {
            standardOutput.Write(StopHookContinueResponse);
            standardOutput.Flush();
        }
        catch
        {
            // Protocol output failures cannot be allowed to disrupt Codex.
        }
    }

    private static string ClassifyContainedFailure(string stage, Exception exception)
    {
        if (string.Equals(stage, "payload_parse", StringComparison.Ordinal))
        {
            return HookErrorCodes.InvalidJson;
        }

        if (string.Equals(stage, "http_forward", StringComparison.Ordinal))
        {
            if (exception is OperationCanceledException or TimeoutException)
            {
                return HookErrorCodes.ForwardTimeout;
            }

            if (exception is HttpRequestException)
            {
                return HookErrorCodes.ForwardUnavailable;
            }
        }

        return HookErrorCodes.UnexpectedError;
    }

    private void TryRecordDiagnostic(HookDiagnosticEvent diagnosticEvent)
    {
        try
        {
            _diagnosticLogger.Record(diagnosticEvent);
        }
        catch
        {
            // A diagnostic implementation cannot be allowed to affect Codex.
        }
    }
}
