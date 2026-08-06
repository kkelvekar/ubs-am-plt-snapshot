using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ubs.Advantage.Core.Infrastructure.Commands;

namespace Ubs.Advantage.Core.Messaging.Kafka;

/// <summary>
/// Publishes to one topic and reports the outcome of every send to the registered command.
/// </summary>
/// <remarks>
/// One long-lived producer for the process lifetime (producers are thread-safe and batching
/// only works when reused), flushed on dispose so a graceful shutdown never strands a queued
/// message. <see cref="Publish"/> queues and returns; the delivery outcome reaches the command
/// asynchronously through the delivery report.
/// </remarks>
internal sealed class MessageProducerService<TKey, TValue, TCommand>
    : IProducer<TKey, TValue>, IDisposable
    where TCommand : ACommand<CommandStatusParameter<Models.IMessage<TKey, TValue>, bool>>
{
    private readonly Confluent.Kafka.IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly TCommand _command;
    private readonly ILogger _logger;

    public MessageProducerService(
        string topicKey,
        TCommand command,
        IOptions<KafkaOptions> options,
        ILoggerFactory loggerFactory)
    {
        var kafkaOptions = options.Value;
        _topic = kafkaOptions.ResolveTopic(topicKey);
        _command = command;
        _logger = loggerFactory.CreateLogger($"{typeof(KafkaOptions).Namespace}.MessageProducerService");

        var config = new ProducerConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers,
            // Wait for all in-sync replicas: a broker acknowledging before replication would let
            // a leader failover lose the message silently.
            Acks = Acks.All,
            EnableIdempotence = true,
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => KafkaClientDiagnostics.HandleError(_logger, error))
            .SetLogHandler((_, logMessage) => KafkaClientDiagnostics.HandleLog(_logger, logMessage))
            .Build();
    }

    public void Publish(Models.Message<TKey, TValue> message)
    {
        try
        {
            var payload = new Message<string, string>
            {
                Key = MessageSerialization.FromKey(message.Key),
                Value = MessageSerialization.Serialize(message.Value),
                Headers = ToHeaders(message.Headers),
            };

            _producer.Produce(_topic, payload, report => Report(message, !report.Error.IsError, report.Error.Reason));
        }
        catch (Exception ex)
        {
            // A refusal to queue at all — a full queue, a fatal producer state, an
            // unserialisable value — is reported to the command as a failed send.
            Report(message, status: false, ex.Message);
        }
    }

    /// <summary>
    /// Hands the send outcome to the command and logs what it makes of it. Runs on the
    /// producer's delivery-report thread, so it must never throw.
    /// </summary>
    private void Report(Models.IMessage<TKey, TValue> message, bool status, string reason)
    {
        try
        {
            var result = _command
                .ExecuteAsync(new CommandStatusParameter<Models.IMessage<TKey, TValue>, bool>(message, status))
                .GetAwaiter()
                .GetResult();

            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "Published message to topic {Topic} messageKey={MessageKey}",
                    _topic,
                    message.Key);
                return;
            }

            _logger.LogError(
                "Failed to publish message to topic {Topic} messageKey={MessageKey} error={Error} reason={Reason}",
                _topic,
                message.Key,
                result.Error,
                reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Producer command failed for topic {Topic}", _topic);
        }
    }

    private static Headers ToHeaders(IReadOnlyList<Models.MessageHeader> headers)
    {
        var result = new Headers();

        foreach (var header in headers)
        {
            result.Add(header.Key, System.Text.Encoding.UTF8.GetBytes(header.Value));
        }

        return result;
    }

    public void Dispose()
    {
        // Bounded so a broken broker cannot hang shutdown indefinitely.
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}
