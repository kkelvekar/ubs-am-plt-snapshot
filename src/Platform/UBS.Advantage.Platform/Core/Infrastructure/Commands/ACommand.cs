namespace Ubs.Advantage.Core.Infrastructure.Commands;

/// <summary>
/// A unit of work invoked with one parameter and reporting a <see cref="CommandResult"/>.
/// Messaging commands are invoked by the consumer and producer services for each message.
/// </summary>
public abstract class ACommand<TParameter>
{
    public abstract Task<CommandResult> ExecuteAsync(TParameter parameter);
}
