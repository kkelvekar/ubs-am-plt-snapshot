using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

/// <summary>
/// Deliberately thin, disposable Kafka adapter: deserialise the envelope, call the
/// Application-layer handler, commit the offset on success — nothing else. No business
/// logic, no branching on payloadType. Replaced wholesale by the org-provided consumer
/// library at lift-and-shift time; that swap must touch only the Infrastructure layer.
///
/// Failure semantics per solution design §8/§9: no commit on any failure path, seek back
/// so the consumer never advances past a failed offset, retry the same message with
/// configured delays, and raise an operations alert after the final configured attempt
/// (repeated periodically per <see cref="KafkaConsumerOptions.AlertRepeatEveryFailures"/>
/// while the partition stays blocked). Failure state is tracked per partition: one
/// consumer instance owns several partitions, and a poison message on one partition must
/// not have its retry/alert cadence disturbed by healthy traffic on another. The blocked
/// partition stays blocked while the message keeps failing — deliberate; recovery is
/// always forward via redelivery.
/// </summary>
public sealed class KafkaSnapshotConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IKafkaConsumerFactory _consumerFactory;
    private readonly ISnapshotMessageHandler _handler;
    private readonly KafkaConsumerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<KafkaSnapshotConsumer> _logger;

    public KafkaSnapshotConsumer(
        IKafkaConsumerFactory consumerFactory,
        ISnapshotMessageHandler handler,
        IOptions<KafkaConsumerOptions> options,
        TimeProvider timeProvider,
        IHostApplicationLifetime appLifetime,
        ILogger<KafkaSnapshotConsumer> logger)
    {
        _consumerFactory = consumerFactory;
        _handler = handler;
        _options = options.Value;
        _timeProvider = timeProvider;
        _appLifetime = appLifetime;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        // Consume() blocks, so run the loop on its own task and let it observe the
        // stopping token itself rather than cancelling the outer task.
        => Task.Run(() => ConsumeLoopAsync(stoppingToken), CancellationToken.None);

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        // Create()/Subscribe() sit inside the guarded region so a bad bootstrap config or
        // subscribe throw is caught by the outer catch and logged Critical, not surfaced as
        // a bare "BackgroundService failed" without our context.
        IConsumer<string, string>? consumer = null;

        // Keyed by partition, validated against the failed offset: an entry left behind
        // by a rebalance is either overwritten on the next failure (offset mismatch
        // starts a fresh count) or inert, so stale state can never mis-fire an alert.
        var failures = new Dictionary<TopicPartition, PartitionFailureState>();

        try
        {
            consumer = _consumerFactory.Create();
            consumer.Subscribe(_options.Topic);

            _logger.LogInformation(
                "Kafka consumer subscribed to topic {Topic} as consumer group {ConsumerGroup}",
                _options.Topic,
                _options.ConsumerGroup);

            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error: {Reason}", ex.Error.Reason);

                    // Broker-level errors are not tied to a message, so back off at the
                    // largest configured retry delay rather than hot-spinning while the
                    // broker keeps erroring.
                    if (!await TryDelayAsync(ConsumeErrorBackoff(), stoppingToken))
                    {
                        break;
                    }

                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                SnapshotMessage? message = null;
                try
                {
                    message = JsonSerializer.Deserialize<SnapshotMessage>(result.Message.Value, SerializerOptions)
                        ?? throw new JsonException("Message envelope deserialised to null.");

                    await _handler.HandleAsync(message, stoppingToken);

                    // Offset committed only after the handler fully succeeded — never on
                    // any failure path.
                    consumer.Commit(result);

                    _logger.LogInformation(
                        "Committed offset {TopicPartitionOffset} for snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                        result.TopicPartitionOffset,
                        message.SnapshotId,
                        message.AccountId,
                        message.PayloadType);

                    // Success clears failure state for this partition only; other
                    // partitions' blocked messages keep their retry/alert cadence.
                    failures.Remove(result.TopicPartition);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var state = NextFailureState(failures, result);
                    failures[result.TopicPartition] = state;

                    LogFailure(ex, result, message, state);

                    // Never advance past a failed offset: seek back so the next Consume
                    // redelivers the same message. The partition stays blocked until the
                    // message succeeds — deliberate.
                    consumer.Seek(result.TopicPartitionOffset);

                    if (!await TryDelayAsync(DelayForFailure(state.FailureCount), stoppingToken))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Clean shutdown (SIGTERM / host stop): exit quietly, no Error/Critical noise.
        }
        catch (Exception ex)
        {
            // The consume loop terminated unexpectedly (bad bootstrap config, seek throw,
            // a bug). Emit exactly ONE Critical with our context, then trigger host shutdown
            // and return cleanly rather than rethrowing.
            //
            // Why not rethrow: with BackgroundServiceExceptionBehavior.StopHost a rethrow
            // faults the BackgroundService task, and Host.TryExecuteBackgroundServiceAsync
            // then independently logs the SAME exception again (a second Critical plus a
            // BackgroundServiceFaulted Error) under its own category — ops would see 2-3
            // alerts for one incident. Returning cleanly after StopApplication() reaches the
            // same outcome (host stops, pod restarts) with a single Critical: the Host's
            // duplicate-log path only runs when the task actually faults, which it now won't.
            //
            // Environment.ExitCode is set so the process exits non-zero: StopApplication()
            // alone unwinds host.Run() to a normal exit 0, which k8s would treat as a clean
            // stop and not a crash. Exit 1 makes the pod restart as intended.
            _logger.LogCritical(
                ex,
                "Kafka consume loop terminated unexpectedly; worker will stop and the pod will restart. topic={Topic} consumerGroup={ConsumerGroup}",
                _options.Topic,
                _options.ConsumerGroup);

            Environment.ExitCode = 1;
            _appLifetime.StopApplication();
        }
        finally
        {
            // Close commits nothing (auto-commit disabled) but leaves the group cleanly;
            // Dispose releases the native handle. Both null-guarded: Create() may have thrown.
            consumer?.Close();
            consumer?.Dispose();
        }
    }

    private PartitionFailureState NextFailureState(
        IReadOnlyDictionary<TopicPartition, PartitionFailureState> failures,
        ConsumeResult<string, string> result)
        => failures.TryGetValue(result.TopicPartition, out var existing)
            && existing.FailedOffset.Equals(result.TopicPartitionOffset)
                ? existing with { FailureCount = existing.FailureCount + 1 }
                : new PartitionFailureState(
                    result.TopicPartitionOffset,
                    FailureCount: 1,
                    BlockedSince: _timeProvider.GetUtcNow());

    private void LogFailure(
        Exception exception,
        ConsumeResult<string, string> result,
        SnapshotMessage? message,
        PartitionFailureState state)
    {
        if (message is not null)
        {
            _logger.LogError(
                exception,
                "Failed to process message at {TopicPartitionOffset} (attempt {FailureCount}) snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                result.TopicPartitionOffset,
                state.FailureCount,
                message.SnapshotId,
                message.AccountId,
                message.PayloadType);
        }
        else
        {
            _logger.LogError(
                exception,
                "Failed to deserialise message envelope at {TopicPartitionOffset} (attempt {FailureCount}) messageKey={MessageKey}",
                result.TopicPartitionOffset,
                state.FailureCount,
                result.Message.Key);
        }

        if (ShouldRaiseOperationsAlert(state.FailureCount))
        {
            // Same template for the first alert and every re-alert so operations can hook
            // a single log pattern.
            _logger.LogCritical(
                exception,
                "Operations alert: message at {TopicPartitionOffset} still failing after {FailureCount} attempts, partition blocked since {BlockedSince}; the consumer keeps retrying at max delay. snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} messageKey={MessageKey}",
                result.TopicPartitionOffset,
                state.FailureCount,
                state.BlockedSince,
                message?.SnapshotId,
                message?.AccountId,
                message?.PayloadType,
                result.Message.Key);
        }
    }

    private bool ShouldRaiseOperationsAlert(int failureCount)
    {
        var alertThreshold = Math.Max(_options.RetryDelays.Length, 1);
        if (failureCount == alertThreshold)
        {
            return true;
        }

        var repeatEvery = _options.AlertRepeatEveryFailures;
        return failureCount > alertThreshold
            && repeatEvery > 0
            && (failureCount - alertThreshold) % repeatEvery == 0;
    }

    private TimeSpan DelayForFailure(int failureCount)
    {
        var delays = _options.RetryDelays;
        if (delays.Length == 0)
        {
            return TimeSpan.Zero;
        }

        return delays[Math.Min(failureCount, delays.Length) - 1];
    }

    private TimeSpan ConsumeErrorBackoff()
        => _options.RetryDelays.Length == 0 ? TimeSpan.Zero : _options.RetryDelays.Max();

    /// <summary>Returns false when cancelled, signalling the consume loop to stop.</summary>
    private async Task<bool> TryDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return true;
        }

        try
        {
            await Task.Delay(delay, _timeProvider, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Retry/alert state for the message currently blocking one partition.
    /// <paramref name="BlockedSince"/> is captured (UTC) when the failure count first
    /// transitions to 1 for that partition and resets with the state.
    /// </summary>
    private sealed record PartitionFailureState(
        TopicPartitionOffset FailedOffset,
        int FailureCount,
        DateTimeOffset BlockedSince);
}
