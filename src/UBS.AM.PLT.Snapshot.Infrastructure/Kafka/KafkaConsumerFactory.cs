using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

public sealed class KafkaConsumerFactory : IKafkaConsumerFactory
{
    private readonly KafkaConsumerOptions _options;
    private readonly ILogger<KafkaConsumerFactory> _logger;

    public KafkaConsumerFactory(
        IOptions<KafkaConsumerOptions> options,
        ILogger<KafkaConsumerFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IConsumer<string, string> Create()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            // Offsets are committed manually, only after all writes for a message succeed.
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };

        return new ConsumerBuilder<string, string>(config)
            // Surface librdkafka's internal error/log signal into application logging;
            // without these handlers broker-unreachable / auth failures only reach stderr
            // via librdkafka defaults ("nothing consuming, zero log signal" in prod).
            .SetErrorHandler((_, error) => HandleError(_logger, error))
            .SetLogHandler((_, logMessage) => HandleLog(_logger, logMessage))
            .Build();
    }

    /// <summary>
    /// Fatal librdkafka errors are unrecoverable and warrant an operations alert;
    /// non-fatal errors are transient and internally retried by librdkafka, so they log at
    /// Warning to avoid false-alarming ops on every broker blip. Static + guarded so it is
    /// unit-testable and can never throw out of the callback thread.
    /// </summary>
    internal static void HandleError(ILogger logger, Error error)
    {
        try
        {
            logger.Log(
                LevelForError(error),
                "Kafka client error code={ErrorCode} reason={ErrorReason} isFatal={IsFatal}",
                error.Code,
                error.Reason,
                error.IsFatal);
        }
        catch
        {
            // A logging failure must never crash the librdkafka callback thread.
        }
    }

    /// <summary>
    /// Emits a librdkafka log message at the Microsoft.Extensions.Logging level it
    /// declares. Static + guarded so it is unit-testable and can never throw out of the
    /// callback thread.
    /// </summary>
    internal static void HandleLog(ILogger logger, LogMessage logMessage)
    {
        try
        {
            logger.Log(
                LevelForLog(logMessage),
                "Kafka client log facility={Facility} name={Name} message={Message}",
                logMessage.Facility,
                logMessage.Name,
                logMessage.Message);
        }
        catch
        {
            // A logging failure must never crash the librdkafka callback thread.
        }
    }

    /// <summary>Fatal → Critical (ops alert); non-fatal → Warning (transient, retried).</summary>
    internal static LogLevel LevelForError(Error error)
        => error.IsFatal ? LogLevel.Critical : LogLevel.Warning;

    /// <summary>Maps a librdkafka log message onto its Microsoft.Extensions.Logging level.</summary>
    internal static LogLevel LevelForLog(LogMessage logMessage)
        => (LogLevel)logMessage.LevelAs(LogLevelType.MicrosoftExtensionsLogging);
}
