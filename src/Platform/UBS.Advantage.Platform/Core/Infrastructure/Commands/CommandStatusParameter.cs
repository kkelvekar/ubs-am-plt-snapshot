namespace Ubs.Advantage.Core.Infrastructure.Commands;

/// <summary>
/// A command parameter carrying a value together with the status of the operation that
/// produced it — used by the producer service, which reports the outcome of the send alongside
/// the message that was sent.
/// </summary>
public sealed class CommandStatusParameter<TValue, TStatus>
{
    public CommandStatusParameter(TValue value, TStatus status)
    {
        Value = value;
        Status = status;
    }

    public TValue Value { get; }

    public TStatus Status { get; }
}
