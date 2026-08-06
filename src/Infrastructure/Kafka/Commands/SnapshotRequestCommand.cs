using Microsoft.Extensions.Logging;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.Advantage.Messaging;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Mapping;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;

/// <summary>
/// The command the consumer runs for one snapshot request: map the org DTO onto our domain
/// envelope, call the Application-layer handler, report the outcome. No business logic, no
/// branching on payload type, no orchestration — at lift-and-shift this class is registered
/// with the org consumer library unchanged, and only the consumer around it is deleted.
/// </summary>
/// <remarks>
/// A command's only lever is the <see cref="CommandResult"/> it returns, so every outcome
/// collapses onto commit-or-don't:
/// <list type="bullet">
/// <item><description><b>Handled</b> — <see cref="CommandResult.Success"/>: the write pipeline
/// completed, so the offset is committed.</description></item>
/// <item><description><b>Rejected</b> (<see cref="SnapshotMessageRejectedException"/>) —
/// <see cref="CommandResult.Success"/> as well. The rejection is fully handled: the handler has
/// already written the FAILED tracking row and published the Failed response, and the same bytes
/// would fail identically forever, so the offset must move past the message rather than block
/// its partition.</description></item>
/// <item><description><b>Anything else</b> — <see cref="CommandResult.Fail"/>: the offset stays
/// uncommitted and the consumer stops, so the restarted pod is redelivered the message.</description></item>
/// </list>
/// </remarks>
public sealed class SnapshotRequestCommand : ACommand<IMessage<string, SnapshotRequest>>
{
    private readonly ILogger<SnapshotRequestCommand> _logger;
    private readonly ISnapshotMessageHandler _handler;

    public SnapshotRequestCommand(
        ILogger<SnapshotRequestCommand> logger,
        ISnapshotMessageHandler handler)
    {
        _logger = logger;
        _handler = handler;
    }

    public override async Task<CommandResult> ExecuteAsync(IMessage<string, SnapshotRequest> message)
    {
        var snapshotRequest = message.Value;

        if (snapshotRequest is null)
        {
            _logger.LogWarning("Received null snapshot request messageKey={MessageKey}", message.Key);
            return CommandResult.Fail("Snapshot request is null");
        }

        _logger.LogInformation(
            "Message received for snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
            snapshotRequest.SnapshotId,
            snapshotRequest.AccountId,
            snapshotRequest.PayloadType);

        try
        {
            var snapshotMessage = SnapshotRequestMapper.ToDomain(snapshotRequest);

            // No cancellation token: the framework's command contract has none, and a token
            // cancelled mid-write would abandon the message between two of the four ordered
            // writes. The uncommitted offset is what makes that safe — the restarted pod is
            // redelivered the message and every write is idempotent.
            await _handler.HandleAsync(snapshotMessage, CancellationToken.None);

            _logger.LogInformation(
                "Message processed for snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                snapshotRequest.SnapshotId,
                snapshotRequest.AccountId,
                snapshotRequest.PayloadType);

            return CommandResult.Success;
        }
        catch (SnapshotMessageRejectedException ex)
        {
            _logger.LogError(
                ex,
                "Rejected snapshot message, committing past it reasonCode={ReasonCode} snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                ex.ReasonCode,
                snapshotRequest.SnapshotId,
                snapshotRequest.AccountId,
                snapshotRequest.PayloadType);

            return CommandResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                snapshotRequest.SnapshotId,
                snapshotRequest.AccountId,
                snapshotRequest.PayloadType);

            return CommandResult.Fail(ex.Message);
        }
    }
}
