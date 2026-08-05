using System.Text;

namespace AgentBell.Hook;

/// <summary>Resolves the Hook input source without parsing the JSON payload.</summary>
public interface IHookInputResolver
{
    /// <summary>
    /// Resolves command-line arguments into the argument list consumed by the normal Codex payload parser.
    /// </summary>
    ValueTask<HookInputResult> ResolveAsync(
        IReadOnlyList<string> arguments,
        Stream standardInput,
        CancellationToken cancellationToken);
}

/// <summary>Contains resolved parser arguments or a stable input error code.</summary>
/// <param name="IsSuccess">Whether input resolution succeeded.</param>
/// <param name="Arguments">Arguments to pass to the normal Codex payload parser.</param>
/// <param name="Mode">The source-specific parser mode.</param>
/// <param name="ErrorCode">A stable error code when input resolution failed.</param>
public sealed record HookInputResult(
    bool IsSuccess,
    IReadOnlyList<string>? Arguments,
    HookInputMode Mode,
    string? ErrorCode);

/// <summary>Identifies which validated source schema should parse the resolved JSON.</summary>
public enum HookInputMode
{
    /// <summary>Legacy Codex notify command-line JSON.</summary>
    CodexNotify,

    /// <summary>Codex Stop command Hook JSON read from standard input.</summary>
    CodexStopHook,
}

/// <summary>
/// Preserves normal Codex command-line input and supports a bounded UTF-8 payload file for manual tests.
/// </summary>
public sealed class HookInputResolver : IHookInputResolver
{
    /// <summary>The manual-test-only command-line option for reading JSON from a file.</summary>
    public const string PayloadFileOption = "--payload-file";

    /// <summary>The production command-line option for reading a Codex Stop Hook object from standard input.</summary>
    public const string CodexStopHookOption = "--codex-stop-hook";

    /// <summary>The maximum number of UTF-8 bytes accepted from a file or standard input.</summary>
    public const int MaxInputBytes = 1024 * 1024;

    /// <summary>The maximum manual payload-file size, retained as a descriptive compatibility alias.</summary>
    public const int MaxPayloadFileBytes = MaxInputBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <inheritdoc />
    public async ValueTask<HookInputResult> ResolveAsync(
        IReadOnlyList<string> arguments,
        Stream standardInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardInput);

        if (arguments.Count > 0
            && string.Equals(arguments[0], CodexStopHookOption, StringComparison.Ordinal))
        {
            if (arguments.Count != 1)
            {
                return Failure(HookErrorCodes.StopHookArgumentsInvalid, HookInputMode.CodexStopHook);
            }

            return await ReadUtf8JsonAsync(
                standardInput,
                HookInputMode.CodexStopHook,
                HookErrorCodes.StopHookEmptyInput,
                HookErrorCodes.StopHookInputTooLarge,
                HookErrorCodes.StopHookInvalidUtf8,
                cancellationToken).ConfigureAwait(false);
        }

        if (arguments.Count == 0
            || !string.Equals(arguments[0], PayloadFileOption, StringComparison.Ordinal))
        {
            return Success(arguments, HookInputMode.CodexNotify);
        }

        if (arguments.Count < 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            return Failure(HookErrorCodes.PayloadFilePathMissing, HookInputMode.CodexNotify);
        }

        if (arguments.Count != 2)
        {
            return Failure(HookErrorCodes.PayloadFileArgumentsInvalid, HookInputMode.CodexNotify);
        }

        try
        {
            await using var stream = new FileStream(
                arguments[1],
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });

            if (stream.Length > MaxPayloadFileBytes)
            {
                return Failure(HookErrorCodes.PayloadFileTooLarge, HookInputMode.CodexNotify);
            }

            return await ReadUtf8JsonAsync(
                stream,
                HookInputMode.CodexNotify,
                HookErrorCodes.PayloadFileEmpty,
                HookErrorCodes.PayloadFileTooLarge,
                HookErrorCodes.PayloadFileInvalidUtf8,
                cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return Failure(HookErrorCodes.PayloadFileNotFound, HookInputMode.CodexNotify);
        }
        catch (DirectoryNotFoundException)
        {
            return Failure(HookErrorCodes.PayloadFileNotFound, HookInputMode.CodexNotify);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException)
        {
            return Failure(HookErrorCodes.PayloadFileUnreadable, HookInputMode.CodexNotify);
        }
    }

    private static async ValueTask<HookInputResult> ReadUtf8JsonAsync(
        Stream stream,
        HookInputMode mode,
        string emptyInputErrorCode,
        string inputTooLargeErrorCode,
        string invalidUtf8ErrorCode,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxInputBytes + 1];
        var bytesRead = 0;

        while (bytesRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(bytesRead, buffer.Length - bytesRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        if (bytesRead > MaxInputBytes)
        {
            return Failure(inputTooLargeErrorCode, mode);
        }

        var offset = HasUtf8ByteOrderMark(buffer, bytesRead) ? 3 : 0;
        if (bytesRead == offset)
        {
            return Failure(emptyInputErrorCode, mode);
        }

        try
        {
            var rawJson = StrictUtf8.GetString(buffer, offset, bytesRead - offset);
            return Success([rawJson], mode);
        }
        catch (DecoderFallbackException)
        {
            return Failure(invalidUtf8ErrorCode, mode);
        }
    }

    private static bool HasUtf8ByteOrderMark(byte[] buffer, int length) =>
        length >= 3
        && buffer[0] == 0xEF
        && buffer[1] == 0xBB
        && buffer[2] == 0xBF;

    private static HookInputResult Success(
        IReadOnlyList<string> arguments,
        HookInputMode mode) =>
        new(true, arguments, mode, null);

    private static HookInputResult Failure(string errorCode, HookInputMode mode) =>
        new(false, null, mode, errorCode);
}
