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
    /// In-process retry ladder for a failing message (solution design §9): one attempt per
    /// entry, with delay N preceding attempt N. The total must stay well under Kafka's
    /// <c>max.poll.interval.ms</c>, because after the last attempt the worker exits non-zero
    /// and redelivery comes from the pod restart.
    /// </summary>
    /// <remarks>
    /// The default must stay empty: the configuration binder appends configured entries onto a
    /// non-empty default array rather than replacing it, which would double the ladder.
    /// </remarks>
    public TimeSpan[] RetryDelays { get; set; } = [];
}
