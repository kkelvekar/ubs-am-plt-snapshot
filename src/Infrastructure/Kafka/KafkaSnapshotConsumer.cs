using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

/// <summary>
/// Deliberately thin, disposable Kafka adapter: deserialise the envelope, call the
/// Application-layer handler, commit the offset on success — nothing else. No business
/// logic, no branching on payloadType. Replaced wholesale by the org-provided consumer
/// library at lift-and-shift time; that swap must touch only the Infrastructure layer.
///
/// Failure semantics per solution design §8/§9: no commit on any failure path. A failing
/// message is retried in-process, one attempt per <see cref="KafkaConsumerOptions.RetryDelays"/>
/// entry, with delay N preceding attempt N. When the last attempt still fails with anything
/// other than a rejection, the consumer logs a single Critical operations alert, sets a
/// non-zero exit code and stops the host: Kubernetes restarts the pod and Kafka redelivers
/// from the last committed offset. Recovery is always forward via redelivery — the consumer
/// never seeks back. The total ladder duration must therefore stay well under Kafka's
/// <c>max.poll.interval.ms</c>. These are the org consumer wrapper's semantics (it has no
/// Seek capability), so local Mode B testing exercises production behaviour.
/// </summary>
public sealed class KafkaSnapshotConsumer : BackgroundService
{
    // JsonSerializerDefaults.Web sets PropertyNameCaseInsensitive = true, which is what
    // binds the PascalCase org wire contract (docs/snapshot-request.schema.json) onto
    // SnapshotRequest — and camelCase equally. Do not drop it.
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

                if (!await ProcessWithRetriesAsync(consumer, result, stoppingToken))
                {
                    // Shutdown requested during a retry delay, or the retry ladder was
                    // exhausted and the worker is exiting for a pod restart.
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Clean shutdown (SIGTERM / host stop): exit quietly, no Error/Critical noise.
        }
        catch (Exception ex)
        {
            // The consume loop terminated unexpectedly (bad bootstrap config, a bug). Emit
            // exactly ONE Critical with our context, then trigger host shutdown and return
            // cleanly rather than rethrowing.
            //
            // Why not rethrow: with BackgroundServiceExceptionBehavior.StopHost a rethrow
            // faults the BackgroundService task, and Host.TryExecuteBackgroundServiceAsync
            // then independently logs the SAME exception again (a second Critical plus a
            // BackgroundServiceFaulted Error) under its own category — ops would see 2-3
            // alerts for one incident. Returning cleanly after StopApplication() reaches the
            // same outcome (host stops, pod restarts) with a single Critical: the Host's
            // duplicate-log path only runs when the task actually faults, which it now won't.
            _logger.LogCritical(
                ex,
                "Kafka consume loop terminated unexpectedly; worker will stop and the pod will restart. topic={Topic} consumerGroup={ConsumerGroup}",
                _options.Topic,
                _options.ConsumerGroup);

            StopWorkerForPodRestart();
        }
        finally
        {
            // Close commits nothing (auto-commit disabled) but leaves the group cleanly;
            // Dispose releases the native handle. Both null-guarded: Create() may have thrown.
            consumer?.Close();
            consumer?.Dispose();
        }
    }

    /// <summary>
    /// Runs the in-process retry ladder for one message: one attempt per configured retry
    /// delay, delay N preceding attempt N. Returns false when the consume loop must stop —
    /// either shutdown was requested during a delay, or the ladder was exhausted and the
    /// worker is exiting non-zero so the pod restarts and Kafka redelivers.
    /// </summary>
    private async Task<bool> ProcessWithRetriesAsync(
        IConsumer<string, string> consumer,
        ConsumeResult<string, string> result,
        CancellationToken stoppingToken)
    {
        var attempts = Math.Max(_options.RetryDelays.Length, 1);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1 && !await TryDelayAsync(DelayBeforeAttempt(attempt), stoppingToken))
            {
                return false;
            }

            SnapshotMessage? message = null;
            try
            {
                // Deserialisation stays inside the retried block: a deterministic envelope
                // failure simply burns through the ladder and lands on the crash path, like
                // any other non-rejection failure.
                var request = JsonSerializer.Deserialize<SnapshotRequest>(result.Message.Value, SerializerOptions)
                    ?? throw new JsonException("Message envelope deserialised to null.");
                message = SnapshotRequestMapper.ToDomain(request);

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

                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (SnapshotMessageRejectedException ex)
            {
                // Non-retryable: the same bytes redelivered fail identically, so retrying
                // (or crashing for redelivery) would block this partition forever. Commit
                // past it instead and let the partition keep moving. Nothing durable was
                // written — the rejection is raised before the first write.
                //
                // Error, not Warning: a rejected message is dropped, and today the
                // publishing application is not told. Notifying it over the response
                // topic is a later slice; until then this log is the only record.
                _logger.LogError(
                    ex,
                    "Rejected snapshot message, committing past it reasonCode={ReasonCode} offset={TopicPartitionOffset} snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                    ex.ReasonCode,
                    result.TopicPartitionOffset,
                    message?.SnapshotId,
                    message?.AccountId,
                    message?.PayloadType);

                consumer.Commit(result);

                return true;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                LogFailure(ex, result, message, attempt);
            }
            catch (Exception ex)
            {
                LogFailure(ex, result, message, attempt);

                // Ladder exhausted. The offset is NOT committed: the org consumer wrapper
                // has no Seek, so redelivery is achieved by the process exiting non-zero,
                // Kubernetes restarting the pod, and the new consumer resuming from the
                // last committed offset. One Critical only, so operations see one alert.
                _logger.LogCritical(
                    ex,
                    "Operations alert: message at {TopicPartitionOffset} failed all {Attempts} in-process attempts; worker exiting non-zero so the pod restarts and Kafka redelivers from the last committed offset. snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} messageKey={MessageKey}",
                    result.TopicPartitionOffset,
                    attempts,
                    message?.SnapshotId,
                    message?.AccountId,
                    message?.PayloadType,
                    result.Message.Key);

                StopWorkerForPodRestart();

                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Stops the host with a non-zero exit code. <c>StopApplication()</c> alone unwinds
    /// <c>host.Run()</c> to a normal exit 0, which k8s would treat as a clean stop and not a
    /// crash; exit 1 makes the pod restart as intended.
    /// </summary>
    private void StopWorkerForPodRestart()
    {
        Environment.ExitCode = 1;
        _appLifetime.StopApplication();
    }

    private void LogFailure(
        Exception exception,
        ConsumeResult<string, string> result,
        SnapshotMessage? message,
        int attempt)
    {
        if (message is not null)
        {
            _logger.LogError(
                exception,
                "Failed to process message at {TopicPartitionOffset} (attempt {Attempt}) snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                result.TopicPartitionOffset,
                attempt,
                message.SnapshotId,
                message.AccountId,
                message.PayloadType);
        }
        else
        {
            _logger.LogError(
                exception,
                "Failed to deserialise message envelope at {TopicPartitionOffset} (attempt {Attempt}) messageKey={MessageKey}",
                result.TopicPartitionOffset,
                attempt,
                result.Message.Key);
        }
    }

    /// <summary>Delay preceding attempt N (N &gt; 1), i.e. the Nth configured entry.</summary>
    private TimeSpan DelayBeforeAttempt(int attempt)
    {
        var delays = _options.RetryDelays;
        return delays.Length == 0 ? TimeSpan.Zero : delays[attempt - 1];
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
}
