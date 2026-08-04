namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

/// <summary>
/// Options bound from the <c>Kafka</c> configuration section. All values come from
/// configuration, with environment-variable overrides such as <c>Kafka__BootstrapServers</c>.
/// </summary>
public sealed record KafkaConsumerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public string ConsumerGroup { get; set; } = string.Empty;

    /// <summary>
    /// Back-off after a broker-level consume error (no message was consumed, so nothing is
    /// committed or lost) to stop the loop hot-spinning while the broker is unreachable.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a per-message retry: a command that reports failure is never retried
    /// in-process. The offset stays uncommitted, the worker exits non-zero, and the restarted
    /// pod is redelivered the message from the last committed offset.
    /// </remarks>
    public TimeSpan ConsumeErrorBackoff { get; set; } = TimeSpan.FromSeconds(5);
}
