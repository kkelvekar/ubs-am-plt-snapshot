using Microsoft.Extensions.Logging;
using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;

/// <summary>
/// Runs the write pipeline for one snapshot request: map the request onto the domain envelope,
/// call the Application-layer handler, report the outcome. No business logic, no branching on
/// payload type, no orchestration of its own.
/// </summary>
/// <remarks>
/// The <see cref="CommandResult"/> is the command's only lever over the offset, so every outcome
/// collapses onto commit-or-don't:
/// <list type="bullet">
/// <item><description><b>Handled</b> — <see cref="CommandResult.Success"/>: the write pipeline
/// completed, so the offset is committed.</description></item>
/// <item><description><b>Rejected</b> (<see cref="SnapshotMessageRejectedException"/>) —
/// <see cref="CommandResult.Success"/> as well. The rejection is fully handled: the handler has
/// already written the FAILED tracking row and published the Failed response, and the same bytes
/// would fail identically forever, so the offset must move past the message rather than block
/// its partition.</description></item>
/// <item><description><b>Anything else</b> — the process exits immediately with a non-zero
/// code before control returns to the Kafka library. Kubernetes restarts the worker and Kafka
/// redelivers the uncommitted message.</description></item>
/// </list>
/// </remarks>
public class SnapshotRequestCommand(ILogger<SnapshotRequestCommand> logger, ISnapshotMessageHandler handler)
    : ACommand<IMessage<string, SnapshotRequest>>
{
    private readonly ILogger<SnapshotRequestCommand> _logger = logger;
    private readonly ISnapshotMessageHandler _handler = handler;

    public override async Task<CommandResult> ExecuteAsync(IMessage<string, SnapshotRequest> message)
    {
        SnapshotRequest? snapshotRequest = message.Value;

        if (snapshotRequest is null)
        {
            const string reason = "Snapshot request is null";

            _logger.LogCritical(
                "Operations alert: snapshot message was not handled; process exiting immediately with code 1 so Kubernetes can restart without graceful Kafka shutdown. reason={Reason}",
                reason);

            Environment.Exit(1);

            return CommandResult.Fail(reason);
        }

        _logger.LogInformation(
            "Message received for snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
            snapshotRequest.SnapshotId,
            snapshotRequest.AccountId,
            snapshotRequest.PayloadType);

        try
        {
            SnapshotMessage snapshotMessage = MapSnapshotMessage(snapshotRequest);

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
            _logger.LogCritical(
                ex,
                "Operations alert: snapshot message was not handled; process exiting immediately with code 1 so Kubernetes can restart without graceful Kafka shutdown. reason={Reason} snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                ex.Message,
                snapshotRequest.SnapshotId,
                snapshotRequest.AccountId,
                snapshotRequest.PayloadType);

            Environment.Exit(1);

            return CommandResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Straight 1:1 assignment. <see cref="SnapshotRequest.Payload"/> is carried across as the
    /// same string reference — never parsed, never re-serialised, so the blob write stays
    /// byte-identical to what was published.
    /// </summary>
    private static SnapshotMessage MapSnapshotMessage(SnapshotRequest snapshotRequest)
        => new()
        {
            SnapshotId = snapshotRequest.SnapshotId,
            AccountId = snapshotRequest.AccountId,
            SnapshotType = snapshotRequest.SnapshotType,
            PayloadType = snapshotRequest.PayloadType,
            PublishedAt = snapshotRequest.PublishedAt,
            PublishedBy = snapshotRequest.PublishedBy,
            Payload = snapshotRequest.Payload,
        };
}
