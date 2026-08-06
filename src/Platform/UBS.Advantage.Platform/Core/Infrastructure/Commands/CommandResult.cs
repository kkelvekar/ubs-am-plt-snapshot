namespace Ubs.Advantage.Core.Infrastructure.Commands;

/// <summary>
/// Outcome of a <see cref="ACommand{TParameter}"/>. For a consumed message it decides the
/// offset: <see cref="Success"/> commits it, <see cref="Fail"/> leaves it uncommitted.
/// </summary>
public sealed class CommandResult
{
    private CommandResult(bool isSuccess, string error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static CommandResult Success { get; } = new(true, string.Empty);

    public static CommandResult Fail(string error) => new(false, error);

    public bool IsSuccess { get; }

    /// <summary>Failure reason, for logging. Empty on <see cref="Success"/>.</summary>
    public string Error { get; }
}
