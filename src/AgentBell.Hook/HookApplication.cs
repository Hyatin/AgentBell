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
    private readonly IEventForwarder _forwarder;
    private readonly IDiagnosticLogger _diagnosticLogger;

    /// <summary>Initializes the Hook application with replaceable collaborators.</summary>
    public HookApplication(
        IHookInputResolver inputResolver,
        ICodexPayloadParser parser,
        ICodexStopHookPayloadParser stopHookParser,
        IEventForwarder forwarder,
        IDiagnosticLogger diagnosticLogger)
    {
        _inputResolver = inputResolver ?? throw new ArgumentNullException(nameof(inputResolver));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _stopHookParser = stopHookParser ?? throw new ArgumentNullException(nameof(stopHookParser));
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

            if (!inputResult.IsSuccess || inputResult.Arguments is null)
            {
                result = ForwardResult.Failed(
                    inputResult.ErrorCode ?? HookErrorCodes.UnexpectedError);
                return 0;
            }

            string? rawJson = null;

            if (inputResult.Mode == HookInputMode.CodexStopHook)
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
                result = await _forwarder.ForwardAsync(
                    rawJson,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            result = ForwardResult.Failed(HookErrorCodes.UnexpectedError);
        }
        finally
        {
            stopwatch.Stop();
            TryRecordDiagnostic(HookDiagnosticEvent.Create(eventMetadata, result, stopwatch.Elapsed));

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
