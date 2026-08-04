// Simulated org messaging-framework type. At real lift-and-shift: delete this file and
// reference the org messaging NuGet package. Never edit it to fit our needs.
namespace UBS.Advantage.Messaging;

/// <summary>
/// Base class for a message command: the unit of work the org consumer library invokes for one
/// consumed message. Everything around it — polling, deserialisation, offset commit, restart —
/// belongs to the framework, so a command carries no cancellation token and no consumer handle.
/// Its entire contract is the <see cref="CommandResult"/> it returns.
/// </summary>
public abstract class ACommand<TMessage>
{
    public abstract Task<CommandResult> ExecuteAsync(TMessage message);
}
