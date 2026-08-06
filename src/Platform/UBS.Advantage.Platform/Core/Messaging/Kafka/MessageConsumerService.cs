using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ubs.Advantage.Core.Infrastructure.Commands;

namespace Ubs.Advantage.Core.Messaging.Kafka;

/// <summary>
/// Consumes one topic and runs the registered command for each message. Offsets are committed
/// manually and only when the command reports success.
/// </summary>
/// <remarks>
/// A command that reports failure — or throws, or a message body that will not deserialise —
/// leaves the offset uncommitted, logs a single Critical alert and stops the host with a
/// non-zero exit code, so the restarted process is redelivered the message from the last
/// committed offset. There is no seek and no in-process retry: recovery is always forward.
/// </remarks>
internal sealed class MessageConsumerService<TKey, TValue, TCommand> : BackgroundService
    where TCommand : ACommand<Models.IMessage<TKey, TValue>>
{
    private readonly string _topicKey;
    private readonly TCommand _command;
    private readonly KafkaOptions _options;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger _logger;

    public MessageConsumerService(
        string topicKey,
        TCommand command,
        IOptions<KafkaOptions> options,
        IHostApplicationLifetime appLifetime,
        ILoggerFactory loggerFactory)
    {
        _topicKey = topicKey;
        _command = command;
        _options = options.Value;
        _appLifetime = appLifetime;
        _logger = loggerFactory.CreateLogger($"{typeof(KafkaOptions).Namespace}.MessageConsumerService");
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
        var topic = _options.ResolveTopic(_topicKey);

        try
        {
            consumer = CreateConsumer();
            consumer.Subscribe(topic);

            _logger.LogInformation(
                "Consumer subscribed to topic {Topic} as consumer group {ConsumerGroup}",
                topic,
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
                    _logger.LogError(ex, "Consume error on topic {Topic}: {Reason}", topic, ex.Error.Reason);

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
                    // Shutdown requested, or the command failed and the process is exiting.
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
            // rather than rethrowing: a rethrow faults the BackgroundService task, and the host
            // then logs the same exception again under its own category, so operations would
            // see two or three alerts for one incident.
            _logger.LogCritical(
                ex,
                "Consume loop terminated unexpectedly; the process will stop and be restarted. topic={Topic} consumerGroup={ConsumerGroup}",
                topic,
                _options.ConsumerGroup);

            StopForRestart();
        }
        finally
        {
            // Close commits nothing (auto-commit is disabled) but leaves the consumer group
            // cleanly. Both calls are null-guarded because CreateConsumer() may have thrown.
            consumer?.Close();
            consumer?.Dispose();
        }
    }

    /// <summary>
    /// Runs the command for one message and commits the offset only when the command reports
    /// success. Returns false when the consume loop must stop.
    /// </summary>
    private async Task<bool> DispatchAsync(
        IConsumer<string, string> consumer,
        ConsumeResult<string, string> result,
        CancellationToken stoppingToken)
    {
        CommandResult commandResult;
        try
        {
            var message = new Models.Message<TKey, TValue>(
                MessageSerialization.ToKey<TKey>(result.Message.Key),
                MessageSerialization.Deserialize<TValue>(result.Message.Value),
                ToHeaders(result.Message.Headers));

            commandResult = await _command.ExecuteAsync(message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Either the body would not deserialise, or the command threw instead of reporting
            // failure. Both mean the same thing to the offset: not handled.
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
            // The work is durable but the offset is not, so the message is redelivered and
            // reprocessed after the restart.
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
    /// redelivery comes from the process exiting non-zero and the restarted process resuming
    /// from the last committed offset. One Critical, so one alert. Always returns false,
    /// stopping the consume loop.
    /// </summary>
    private bool FailAndStop(ConsumeResult<string, string> result, string reason)
    {
        _logger.LogCritical(
            "Operations alert: message at {TopicPartitionOffset} was not handled; offset not committed, process exiting non-zero so it is restarted and the message is redelivered from the last committed offset. reason={Reason} messageKey={MessageKey}",
            result.TopicPartitionOffset,
            reason,
            result.Message.Key);

        StopForRestart();

        return false;
    }

    private IConsumer<string, string> CreateConsumer()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            // Offsets are committed manually, only after the command reports success.
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };

        return new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => KafkaClientDiagnostics.HandleError(_logger, error))
            .SetLogHandler((_, logMessage) => KafkaClientDiagnostics.HandleLog(_logger, logMessage))
            .Build();
    }

    private static IReadOnlyList<Models.MessageHeader> ToHeaders(Headers? headers)
        => headers is null
            ? []
            : [.. headers.Select(header => new Models.MessageHeader(header.Key, System.Text.Encoding.UTF8.GetString(header.GetValueBytes() ?? [])))];

    /// <summary>
    /// Stops the host with a non-zero exit code. <c>StopApplication()</c> on its own unwinds
    /// <c>host.Run()</c> to exit code 0, which Kubernetes treats as a clean stop rather than a
    /// crash; exit 1 is what makes the pod restart.
    /// </summary>
    private void StopForRestart()
    {
        Environment.ExitCode = 1;
        _appLifetime.StopApplication();
    }

    /// <summary>Returns false when cancelled, signalling the consume loop to stop.</summary>
    private static async Task<bool> TryDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return true;
        }

        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
