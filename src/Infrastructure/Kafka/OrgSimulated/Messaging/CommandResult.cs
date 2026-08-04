// Simulated org messaging-framework type. At real lift-and-shift: delete this file and
// reference the org messaging NuGet package. Never edit it to fit our needs — the whole point
// of the command pattern is that our command compiles unchanged against the real type.
namespace UBS.Advantage.Messaging;

/// <summary>
/// The only outcome a command can report, and therefore the only lever a command has over the
/// Kafka offset: <see cref="Success"/> commits the message, <see cref="Fail"/> does not.
/// There is no seek, no requeue and no dead-letter verb — recovery is always forward, by
/// redelivery from the last committed offset.
/// </summary>
public sealed class CommandResult
{
    private CommandResult(bool isSuccess, string error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>The message is fully handled; the consumer commits its offset.</summary>
    public static CommandResult Success { get; } = new(true, string.Empty);

    /// <summary>
    /// The message is not handled; the consumer does not commit its offset, so the message is
    /// redelivered after the consumer restarts.
    /// </summary>
    public static CommandResult Fail(string error) => new(false, error);

    public bool IsSuccess { get; }

    /// <summary>Failure reason, for logging. Empty on <see cref="Success"/>.</summary>
    public string Error { get; }
}
