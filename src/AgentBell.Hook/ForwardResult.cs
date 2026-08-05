namespace AgentBell.Hook;

/// <summary>Describes the outcome of forwarding an event without exception details.</summary>
/// <param name="Code">A stable result code.</param>
/// <param name="HttpStatusCode">The optional HTTP response status.</param>
public readonly record struct ForwardResult(string Code, int? HttpStatusCode = null)
{
    /// <summary>The successful forward result code.</summary>
    public const string SuccessCode = "success";

    /// <summary>Creates a successful result.</summary>
    public static ForwardResult Accepted(int statusCode) => new(SuccessCode, statusCode);

    /// <summary>Creates a failed result with a stable code.</summary>
    public static ForwardResult Failed(string errorCode, int? statusCode = null) =>
        new(errorCode, statusCode);
}

