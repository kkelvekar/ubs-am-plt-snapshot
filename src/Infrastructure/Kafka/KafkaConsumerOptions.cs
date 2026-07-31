namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

/// <summary>
/// Bound from the <c>Kafka</c> configuration section. All values come from configuration
/// (with environment-variable overrides, e.g. <c>Kafka__BootstrapServers</c>) — never
/// from code.
/// </summary>
public sealed record KafkaConsumerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public string ConsumerGroup { get; set; } = string.Empty;

    /// <summary>
    /// The in-process retry ladder for a failing message, per solution design §9
    /// (immediate / 5s / 30s, supplied by appsettings.json): one attempt per entry, with
    /// delay N preceding attempt N. When the last attempt fails with a non-rejection error
    /// the worker logs Critical and exits non-zero — redelivery happens via pod restart,
    /// not in-process retry — so the total must stay well under Kafka's
    /// <c>max.poll.interval.ms</c>. The in-code default must stay empty: the configuration
    /// binder appends configured entries onto a non-empty default array instead of
    /// replacing it, doubling the list.
    /// </summary>
    public TimeSpan[] RetryDelays { get; set; } = [];
}
