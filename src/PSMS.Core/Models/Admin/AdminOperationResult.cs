namespace PSMS.Core.Models.Admin;

public sealed class AdminOperationResult
{
    public bool Succeeded { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = [];
    public long ElapsedMilliseconds { get; init; }
    public string? Error { get; init; }

    public static AdminOperationResult Ok(string message, long elapsedMs = 0, IReadOnlyList<string>? messages = null) => new()
    {
        Succeeded = true,
        Message = message,
        Messages = messages ?? [message],
        ElapsedMilliseconds = elapsedMs
    };

    public static AdminOperationResult Fail(string error, long elapsedMs = 0, IReadOnlyList<string>? messages = null) => new()
    {
        Succeeded = false,
        Error = error,
        Message = error,
        Messages = messages ?? [error],
        ElapsedMilliseconds = elapsedMs
    };
}
