using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.Advantage.Messaging;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

/// <summary>
/// Stand-in for the org consumer library: poll, deserialise the envelope, hand the message to
/// the registered command, and commit the offset only when the command reports success. It
/// knows nothing about snapshots — no payload-type branching, no business logic, no Application
/// types — so at lift-and-shift this class is deleted outright and
/// <see cref="Commands.SnapshotRequestCommand"/> is registered with the org library unchanged.
/// </summary>
/// <remarks>
/// Failure semantics per solution design §8/§9: the offset is never committed on a failure path.
/// A command returning <see cref="CommandResult.Fail"/> — or throwing, or an envelope that will
/// not deserialise — makes the consumer log a single Critical alert, set a non-zero exit code
/// and stop the host. The pod restarts, and Kafka redelivers from the last committed offset.
/// Recovery is always forward: the consumer never seeks back, matching the org consumer wrapper,
/// which has no seek capability and no in-process retry of its own.
/// </remarks>
public sealed class KafkaSnapshotConsumer : BackgroundService
{
    // Web defaults enable case-insensitive binding, which is what maps the PascalCase wire
    // contract (docs/snapshot-request.schema.json) onto SnapshotRequest.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IKafkaConsumerFactory _consumerFactory;
    private readonly ACommand<IMessage<string, SnapshotRequest>> _command;
    private readonly KafkaConsumerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<KafkaSnapshotConsumer> _logger;

    public KafkaSnapshotConsumer(
        IKafkaConsumerFactory consumerFactory,
        ACommand<IMessage<string, SnapshotRequest>> command,
        IOptions<KafkaConsumerOptions> options,
        TimeProvider timeProvider,
        IHostApplicationLifetime appLifetime,
        ILogger<KafkaSnapshotConsumer> logger)
    {
        _consumerFactory = consumerFactory;
        _command = command;
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

                    // Broker-level errors are not tied to a message, so back off rather than
                    // hot-spinning. Nothing was consumed, so nothing is committed or lost.
                    if (!await TryDelayAsync(_options.ConsumeErrorBackoff, stoppingToken))
                    {
                        break;
                    }

                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                if (!await DispatchAsync(consumer, result, stoppingToken))
                {
                    // Shutdown requested, or the command failed and the worker is exiting for
                    // a pod restart.
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
    /// Runs the command for one message and commits the offset only when the command reports
    /// success. Returns false when the consume loop must stop, either because shutdown was
    /// requested or because the command failed and the worker is exiting for a pod restart.
    /// </summary>
    private async Task<bool> DispatchAsync(
        IConsumer<string, string> consumer,
        ConsumeResult<string, string> result,
        CancellationToken stoppingToken)
    {
        CommandResult commandResult;
        try
        {
            var message = new ConsumedMessage<string, SnapshotRequest>(
                result.Message.Key,
                Deserialize(result.Message.Value));

            commandResult = await _command.ExecuteAsync(message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Either the envelope would not deserialise, or the command threw instead of
            // returning Fail. Both are the framework's problem, and both mean the same thing
            // to the offset: not handled.
            _logger.LogError(
                ex,
                "Failed to dispatch message at {TopicPartitionOffset} messageKey={MessageKey}",
                result.TopicPartitionOffset,
                result.Message.Key);

            commandResult = CommandResult.Fail(ex.Message);
        }

        if (!commandResult.IsSuccess)
        {
            return FailAndStop(result, commandResult.Error);
        }

        try
        {
            // Committed only after the command reported success, never on a failure path.
            consumer.Commit(result);
        }
        catch (KafkaException ex)
        {
            // Design doc §8 scenario 5: the writes are durable but the offset is not, so the
            // message is redelivered and reprocessed idempotently after the restart.
            return FailAndStop(result, $"Offset commit failed: {ex.Error.Reason}");
        }

        _logger.LogInformation(
            "Committed offset {TopicPartitionOffset} messageKey={MessageKey}",
            result.TopicPartitionOffset,
            result.Message.Key);

        return true;
    }

    /// <summary>
    /// Single exit path for a message that was not committed. There is no seek available, so
    /// redelivery comes from the process exiting non-zero and the restarted pod resuming from
    /// the last committed offset. One Critical, so one alert. Always returns false, stopping
    /// the consume loop.
    /// </summary>
    private bool FailAndStop(ConsumeResult<string, string> result, string reason)
    {
        _logger.LogCritical(
            "Operations alert: message at {TopicPartitionOffset} was not handled; offset not committed, worker exiting non-zero so the pod restarts and Kafka redelivers from the last committed offset. reason={Reason} messageKey={MessageKey}",
            result.TopicPartitionOffset,
            reason,
            result.Message.Key);

        StopWorkerForPodRestart();

        return false;
    }

    /// <summary>
    /// Deserialises the message body into the org request DTO. An empty body — a tombstone, or
    /// a producer sending nothing — yields <c>null</c> rather than throwing, and the command
    /// reports it as a failed message through its own null guard.
    /// </summary>
    private static SnapshotRequest? Deserialize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<SnapshotRequest>(value, SerializerOptions);

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
