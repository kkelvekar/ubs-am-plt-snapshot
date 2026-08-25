using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ubs.Advantage.Core.Infrastructure.Commands;

namespace Ubs.Advantage.Core.Messaging.Kafka;

/// <summary>
/// Consumes one topic and runs the registered command for each message. Offsets are committed
/// manually by default (see <see cref="KafkaOptions.AutoCommit"/>), only when the command
/// reports success.
/// </summary>
/// <remarks>
/// This is a faithful mirror of the real org platform library's consumer semantics, not a retry
/// policy of its own: a command that reports <see cref="CommandResult.Fail"/> — or throws —
/// is logged and its offset left uncommitted, but the consume loop does NOT stop and does NOT
/// seek; it moves straight on to the next message. Because offsets commit positionally, the
/// next successfully-committed message commits a HIGHER offset than the failed one, permanently
/// skipping it. Deciding whether a given failure is safe to skip — and, if not, stopping the
/// host and parking so the process actually exits before any later message can commit past it —
/// is the calling command's responsibility (see <c>SnapshotRequestCommand</c>), never this
/// library's. This service itself stops the consume loop only on cancellation (clean shutdown)
/// or an unrecoverable broker-level <see cref="KafkaException"/>; it holds no
/// <see cref="IHostApplicationLifetime"/> reference and never calls
/// <see cref="IHostApplicationLifetime.StopApplication"/> itself.
///
/// A message that fails to deserialise is a known, accepted gap mirrored from the real org
/// library: <see cref="TryConsumeMessage"/> commits it away on <see cref="ConsumeException"/>
/// rather than leaving the partition stuck, since there is no way to run a command against a
/// payload that never became a message. This gap, and the headers type/encoding drift from the
/// real org library, are recorded in the Platform README rather than "fixed" here.
///
/// <c>Subscribe()</c> deliberately sits outside any try/catch in <see cref="ConsumeLoopAsync"/>,
/// matching the real org library's own <c>ExecuteAsync</c> shape — do not "restore" a guarded
/// region around it.
/// </remarks>
internal sealed class MessageConsumerService<TKey, TValue, TCommand> : BackgroundService
    where TCommand : ACommand<Models.IMessage<TKey, TValue>>
{
    private readonly string _topicKey;
    private readonly TCommand _command;
    private readonly KafkaOptions _options;
    private readonly ILogger _logger;

    private IConsumer<string, string>? _consumer;

    // Both default to no-op; the constructor wires exactly one of the two to CommitMessage
    // based on KafkaOptions.AutoCommit, so ConsumeLoopAsync never branches on it per message.
    private Action<ConsumeResult<string, string>> _autoCommit = static _ => { };
    private Action<ConsumeResult<string, string>> _commit = static _ => { };

    public MessageConsumerService(
        string topicKey,
        TCommand command,
        IOptions<KafkaOptions> options,
        ILoggerFactory loggerFactory)
    {
        _topicKey = topicKey;
        _command = command;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger($"{typeof(KafkaOptions).Namespace}.MessageConsumerService");

        if (_options.AutoCommit)
        {
            _autoCommit = CommitMessage;
        }
        else
        {
            _commit = CommitMessage;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        // Consume() blocks, so run the loop on its own task and let it observe the
        // stopping token itself rather than cancelling the outer task.
        => Task.Run(() => ConsumeLoopAsync(stoppingToken), CancellationToken.None);

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        var topic = _options.ResolveTopic(_topicKey);

        _consumer = CreateConsumer();
        _consumer.Subscribe(topic);

        _logger.LogInformation(
            "Consumer subscribed to topic {Topic} as consumer group {ConsumerGroup}",
            topic,
            _options.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var (successfulRead, consumeResult) = TryConsumeMessage(stoppingToken);

                if (successfulRead)
                {
                    _autoCommit(consumeResult!);
                    await ExecuteCommand(consumeResult!);
                }
                else
                {
                    await Task.Delay(_options.ConsumeErrorBackoff, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown (SIGTERM / host stop): exit quietly, no Error/Critical noise.
            _logger.LogInformation("Consume loop stopping for topic {Topic}: shutdown requested.", topic);
        }
        catch (KafkaException ex)
        {
            // An unrecoverable broker-level error. Logged once; the loop simply ends — no stop,
            // no seek, no exit code. Faithful to the real org library: this service never decides
            // the process should die, only the calling command does.
            _logger.LogError(ex, "Consume loop terminated by an unrecoverable Kafka error on topic {Topic}: {Reason}", topic, ex.Error.Reason);
        }
    }

    /// <summary>
    /// Reads the next message. A <see cref="ConsumeException"/> — an undeliverable/undeserialisable
    /// record at the broker layer — is logged and its offset committed away immediately: there is
    /// no message to run a command against, so leaving it uncommitted would stall the partition
    /// forever. This is the one documented, deliberate gap versus <c>ExecuteCommand</c>'s
    /// never-commit-on-failure rule.
    /// </summary>
    private (bool SuccessfulRead, ConsumeResult<string, string>? Result) TryConsumeMessage(CancellationToken stoppingToken)
    {
        try
        {
            var result = _consumer!.Consume(stoppingToken);
            return (result?.Message is not null, result);
        }
        catch (ConsumeException ex)
        {
            _logger.LogError(ex, "Error thrown while consuming, commit attempt will be performed on faulty message");
            CommitMessage(ex.ConsumerRecord.TopicPartitionOffset);
            return (false, null);
        }
    }

    /// <summary>
    /// Runs the command for one message. Success commits the offset (unless auto-commit already
    /// did so on read). Failure — a <see cref="CommandResult.Fail"/> or any exception the command
    /// itself throws — is logged only: no commit, no stop. The loop moves on to the next message
    /// regardless.
    /// </summary>
    private async Task ExecuteCommand(ConsumeResult<string, string> consumeResult)
    {
        CommandResult commandResult;
        try
        {
            var message = new Models.Message<TKey, TValue>(
                MessageSerialization.ToKey<TKey>(consumeResult.Message.Key),
                MessageSerialization.Deserialize<TValue>(consumeResult.Message.Value),
                ToHeaders(consumeResult.Message.Headers));

            commandResult = await _command.ExecuteAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Command {Command} threw for message with key {Key}; message will not be committed",
                typeof(TCommand).Name,
                consumeResult.Message.Key);
            return;
        }

        if (commandResult.IsSuccess)
        {
            _commit(consumeResult);

            _logger.LogInformation(
                "Committed offset {TopicPartitionOffset} messageKey={MessageKey}",
                consumeResult.TopicPartitionOffset,
                consumeResult.Message.Key);
        }
        else
        {
            _logger.LogError(
                "Command {Command} failed with error: {Reason}, message with key {Key} will not be committed",
                typeof(TCommand).Name,
                commandResult.Error,
                consumeResult.Message.Key);
        }
    }

    private void CommitMessage(ConsumeResult<string, string> result) => _consumer!.Commit(result);

    private void CommitMessage(TopicPartitionOffset offset) => _consumer!.Commit([offset]);

    private IConsumer<string, string> CreateConsumer()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            EnableAutoCommit = _options.AutoCommit,
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
    /// Close() commits nothing on its own (offsets are committed explicitly, never implicitly on
    /// close) but leaves the consumer group cleanly; only called here, never mid-loop.
    /// </summary>
    public override void Dispose()
    {
        _consumer?.Close();
        _consumer?.Dispose();
        base.Dispose();
    }
}
