namespace AgentBell.Hook;

/// <summary>Stable result codes emitted by the Hook without exposing exception messages.</summary>
public static class HookErrorCodes
{
    /// <summary>No command-line arguments were supplied.</summary>
    public const string NoArguments = "no_arguments";

    /// <summary>No JSON object containing a type field was found.</summary>
    public const string JsonNotFound = "json_not_found";

    /// <summary>A JSON-looking payload was malformed or did not match the supported field types.</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>A JSON object did not contain a usable type string.</summary>
    public const string MissingType = "missing_type";

    /// <summary>The event type is not supported by this release.</summary>
    public const string UnsupportedType = "unsupported_type";

    /// <summary>The manual payload-file option did not include a file path.</summary>
    public const string PayloadFilePathMissing = "payload_file_path_missing";

    /// <summary>The manual payload-file option included unexpected extra arguments.</summary>
    public const string PayloadFileArgumentsInvalid = "payload_file_arguments_invalid";

    /// <summary>The manual payload file does not exist.</summary>
    public const string PayloadFileNotFound = "payload_file_not_found";

    /// <summary>The manual payload file could not be read.</summary>
    public const string PayloadFileUnreadable = "payload_file_unreadable";

    /// <summary>The manual payload file exceeded the 1 MiB limit.</summary>
    public const string PayloadFileTooLarge = "payload_file_too_large";

    /// <summary>The manual payload file was empty.</summary>
    public const string PayloadFileEmpty = "payload_file_empty";

    /// <summary>The manual payload file was not valid UTF-8.</summary>
    public const string PayloadFileInvalidUtf8 = "payload_file_invalid_utf8";

    /// <summary>The Stop Hook option included unexpected command-line arguments.</summary>
    public const string StopHookArgumentsInvalid = "stop_hook_arguments_invalid";

    /// <summary>The Stop Hook standard input was empty.</summary>
    public const string StopHookEmptyInput = "stop_hook_empty_input";

    /// <summary>The Stop Hook standard input exceeded the 1 MiB limit.</summary>
    public const string StopHookInputTooLarge = "stop_hook_input_too_large";

    /// <summary>The Stop Hook standard input was not valid UTF-8.</summary>
    public const string StopHookInvalidUtf8 = "stop_hook_invalid_utf8";

    /// <summary>The Hook JSON object did not contain a usable hook_event_name.</summary>
    public const string MissingHookEventName = "missing_hook_event_name";

    /// <summary>The command Hook event is not the supported Stop event.</summary>
    public const string UnsupportedHookEvent = "unsupported_hook_event";

    /// <summary>The local forward operation exceeded its deadline.</summary>
    public const string ForwardTimeout = "forward_timeout";

    /// <summary>The local desktop endpoint could not be reached.</summary>
    public const string ForwardUnavailable = "forward_unavailable";

    /// <summary>The local desktop endpoint rejected the event.</summary>
    public const string ForwardRejected = "forward_rejected";

    /// <summary>An unexpected failure was contained by the Hook.</summary>
    public const string UnexpectedError = "unexpected_error";
}
