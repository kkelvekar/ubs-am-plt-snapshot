using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;

/// <summary>
/// Runs the write pipeline for one snapshot request: map the request onto the domain envelope,
/// call the Application-layer handler, report the outcome. No business logic, no branching on
/// payload type beyond outcome classification, no orchestration of its own.
/// </summary>
/// <remarks>
/// Under the real org platform library, <see cref="CommandResult.Fail"/> only logs — the
/// consume loop does not stop and does not seek, and because Kafka commits are positional, the
/// next successfully-handled message commits a HIGHER offset, permanently skipping the failed
/// one. So this command, not the library, owns which outcome is safe to let ride on
/// <c>Fail</c>. Every outcome resolves to exactly one of three buckets:
/// <list type="bullet">
/// <item><description><b>Handled</b> — <see cref="CommandResult.Success"/>: the write pipeline
/// completed, so the offset is committed.</description></item>
/// <item><description><b>Rejected</b> (<see cref="SnapshotMessageRejectedException"/>) or
/// <b>poison</b> (anything else that is not a recognised infrastructure failure) —
/// <see cref="CommandResult.Success"/> as well. A poison message's FAILED tracking row and
/// response are best effort: failures are logged, but the same bytes would fail identically
/// forever, so the offset moves past the message rather than blocking its partition.</description></item>
/// <item><description><b>Transient infrastructure failure</b> (recognised by an
/// <see cref="ITransientFailureClassifier"/>) — <see cref="CommandResult.Fail"/>, but only after
/// this command has already logged one Critical alert, set a non-zero exit code, called
/// <see cref="IHostApplicationLifetime.StopApplication"/>, and — because that call only signals
/// shutdown without suspending this thread — parked for <see cref="TransientPark"/> so the
/// consume loop cannot take a NEXT message and commit a higher offset before the process
/// actually exits. The restarted process resumes from the last committed offset and Kafka
/// redelivers this message. A recognised SQL or blob failure is transient unless its error is
/// one this message's own content could have caused; every other recognised infrastructure
/// error is treated as transient, since an allow-list of transient codes can never be
/// complete.</description></item>
/// </list>
/// A <c>null</c> <see cref="IMessage{TKey, TValue}.Value"/> is a true Kafka tombstone (a present
/// envelope with only <c>SnapshotId</c> missing is a rejection, handled like any other invalid
/// field) — there is no <c>SnapshotId</c> to record a tracking row against, so it logs one
/// Critical and returns <see cref="CommandResult.Success"/> to commit past it.
/// </remarks>
public class SnapshotRequestCommand(
    ILogger<SnapshotRequestCommand> logger,
    ISnapshotMessageHandler handler,
    IHostApplicationLifetime appLifetime,
    IEnumerable<ITransientFailureClassifier> transientFailureClassifiers,
    TimeProvider timeProvider)
    : ACommand<IMessage<string, SnapshotRequest>>
{
    /// <summary>
    /// How long to hold the consume loop open after signalling shutdown, so no later message can
    /// commit a higher offset before the process actually exits. 30s sits comfortably inside
    /// <c>max.poll.interval.ms</c> (default 300000ms / 5 minutes), so there is no rebalance risk
    /// — do not remove this park, and do not raise it past 5 minutes.
    /// </summary>
    private static readonly TimeSpan TransientPark = TimeSpan.FromSeconds(30);

    private readonly ILogger<SnapshotRequestCommand> _logger = logger;
    private readonly ISnapshotMessageHandler _handler = handler;
    private readonly IHostApplicationLifetime _appLifetime = appLifetime;
    private readonly IEnumerable<ITransientFailureClassifier> _transientFailureClassifiers = transientFailureClassifiers;
    private readonly TimeProvider _timeProvider = timeProvider;

    public override async Task<CommandResult> ExecuteAsync(IMessage<string, SnapshotRequest> message)
    {
        SnapshotRequest? snapshotRequest = message.Value;

        if (snapshotRequest is null)
        {
            _logger.LogCritical(
                "Received a null snapshot request (Kafka tombstone); committing past it, nothing to record messageKey={MessageKey}",
                message.Key);
            return CommandResult.Success;
        }

        _logger.LogInformation(
            "Message received for snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
            snapshotRequest.SnapshotId,
            snapshotRequest.AccountId,
            snapshotRequest.PayloadType);

        // Pure assignment, cannot throw, so it is safe to compute before the try -- the poison
        // path below needs the mapped message too.
        SnapshotMessage snapshotMessage = MapSnapshotMessage(snapshotRequest);

        try
        {
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
        catch (Exception ex) when (IsTransient(ex))
        {
            return await ParkAndStopAsync(ex, message, snapshotRequest);
        }
        catch (Exception ex)
        {
            await _handler.RecordUnexpectedFailureAsync(snapshotMessage, ex, CancellationToken.None);

            _logger.LogCritical(
                ex,
                "Poison message, committing past it messageKey={MessageKey} topic=snapshot-request consumerGroup=snapshot-writer-api snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} reasonCode={ReasonCode} headers={Headers}",
                message.Key,
                snapshotRequest.SnapshotId,
                snapshotRequest.AccountId,
                snapshotRequest.PayloadType,
                SnapshotMessageHandler.UnexpectedErrorReasonCode,
                message.Headers);

            return CommandResult.Success;
        }
    }

    private bool IsTransient(Exception exception)
        => _transientFailureClassifiers.Any(classifier => classifier.IsTransient(exception));

    /// <summary>
    /// Logs one Critical alert, sets a non-zero exit code, and signals host shutdown, then parks
    /// for <see cref="TransientPark"/> before returning <see cref="CommandResult.Fail"/>. See the
    /// class <see cref="SnapshotRequestCommand">remarks</see> for why the park is required:
    /// <see cref="IHostApplicationLifetime.StopApplication"/> only signals shutdown, it does not
    /// suspend this thread, so without the park the consume loop would take the NEXT message,
    /// succeed, and commit a HIGHER offset — Kafka commits are positional, so the uncommitted
    /// failed message would be skipped permanently. <see cref="CancellationToken.None"/> is
    /// deliberate: the shutdown token must NOT cut the park short.
    /// </summary>
    private async Task<CommandResult> ParkAndStopAsync(
        Exception ex,
        IMessage<string, SnapshotRequest> message,
        SnapshotRequest snapshotRequest)
    {
        _logger.LogCritical(
            ex,
            "Transient infrastructure failure; offset not committed, parking so Kafka redelivers after restart. messageKey={MessageKey} snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
            message.Key,
            snapshotRequest.SnapshotId,
            snapshotRequest.AccountId,
            snapshotRequest.PayloadType);

        Environment.ExitCode = 1;
        _appLifetime.StopApplication();

        await Task.Delay(TransientPark, _timeProvider, CancellationToken.None);

        return CommandResult.Fail(ex.Message);
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
