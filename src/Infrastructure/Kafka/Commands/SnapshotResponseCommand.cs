using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;

/// <summary>
/// Reports the outcome of publishing one snapshot response: the producer service hands over the
/// message together with whether the send succeeded, and this turns that into a
/// <see cref="CommandResult"/>.
/// </summary>
public class SnapshotResponseCommand : ACommand<CommandStatusParameter<IMessage<string, SnapshotResponse>, bool>>
{
    public override async Task<CommandResult> ExecuteAsync(
        CommandStatusParameter<IMessage<string, SnapshotResponse>, bool> parameter)
    {
        CommandResult commandResult = parameter.Status
            ? CommandResult.Success
            : CommandResult.Fail($"Unable to produce payload: {parameter.Value.Value}");

        return await Task.FromResult(commandResult);
    }
}
