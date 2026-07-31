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
/// Thin Kafka adapter: deserialise the envelope, call the Application-layer handler, commit
/// the offset on success. It holds no business logic and does not branch on payload type,
/// because it is replaced wholesale by the org-provided consumer library and that swap must
/// touch only the Infrastructure layer.
/// </summary>
/// <remarks>
/// Failure semantics per solution design §8/§9: the offset is never committed on a failure
/// path. A failing message is retried in-process, one attempt per
/// <see cref="KafkaConsumerOptions.RetryDelays"/> entry. When the last attempt still fails
/// with anything other than a rejection, the consumer logs a single Critical alert, sets a
/// non-zero exit code and stops the host, so the pod restarts and Kafka redelivers from the
/// last committed offset. Recovery is always forward — the consumer never seeks back, which
/// matches the org consumer wrapper, which has no seek capability.
/// </remarks>
public sealed class KafkaSnapshotConsumer : BackgroundService
{
    // Web defaults enable case-insensitive binding, which is what maps the PascalCase wire
    // contract (docs/snapshot-request.schema.json) onto SnapshotRequest.
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
        // Create() and Subscribe() sit inside the guarded region so a bad bootstrap config is
        // caught by the outer catch and logged with context.
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
                    // largest configured retry delay rather than hot-spinning.
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
            // The loop terminated unexpectedly. Log once, stop the host and return cleanly
            // rather than rethrowing: a rethrow faults the BackgroundService task, and the
            // host then logs the same exception again under its own category, so operations
            // would see two or three alerts for one incident.
            _logger.LogCritical(
                ex,
                "Kafka consume loop terminated unexpectedly; worker will stop and the pod will restart. topic={Topic} consumerGroup={ConsumerGroup}",
                _options.Topic,
                _options.ConsumerGroup);

            StopWorkerForPodRestart();
        }
        finally
        {
            // Close commits nothing (auto-commit is disabled) but leaves the consumer group
            // cleanly. Both calls are null-guarded because Create() may have thrown.
            consumer?.Close();
            consumer?.Dispose();
        }
    }

    /// <summary>
    /// Runs the in-process retry ladder for one message: one attempt per configured retry
    /// delay, delay N preceding attempt N. Returns false when the consume loop must stop,
    /// either because shutdown was requested during a delay or because the ladder was
    /// exhausted and the worker is exiting for a pod restart.
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
                // Deserialisation stays inside the retried block so an envelope failure takes
                // the same ladder-then-crash path as any other non-rejection failure.
                var request = JsonSerializer.Deserialize<SnapshotRequest>(result.Message.Value, SerializerOptions)
                    ?? throw new JsonException("Message envelope deserialised to null.");
                message = SnapshotRequestMapper.ToDomain(request);

                await _handler.HandleAsync(message, stoppingToken);

                // Committed only after the handler fully succeeded, never on a failure path.
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
                // Non-retryable: the same bytes fail identically, so retrying would block the
                // partition forever. Commit past it instead. Logged as an Error because the
                // message's data is dropped; the handler has already recorded the FAILED
                // tracking row and notified the publishing application.
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

                // Ladder exhausted and the offset stays uncommitted: with no seek available,
                // redelivery comes from the process exiting non-zero and the restarted pod
                // resuming from the last committed offset. One Critical, so one alert.
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
    /// Stops the host with a non-zero exit code. <c>StopApplication()</c> on its own unwinds
    /// <c>host.Run()</c> to exit code 0, which Kubernetes treats as a clean stop rather than a
    /// crash; exit 1 is what makes the pod restart.
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
