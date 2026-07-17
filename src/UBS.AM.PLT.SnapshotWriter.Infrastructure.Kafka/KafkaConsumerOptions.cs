namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;

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
    /// Delay before retry attempt N after the Nth consecutive failure of the same
    /// message, per solution design §9 (immediate / 5s / 30s, supplied by
    /// appsettings.json). Once the last delay is reached the consumer keeps retrying at
    /// that delay indefinitely; an operations alert is logged when the final attempt in
    /// this list fails. The in-code default must stay empty: the configuration binder
    /// appends configured entries onto a non-empty default array instead of replacing
    /// it, doubling the list.
    /// </summary>
    public TimeSpan[] RetryDelays { get; set; } = [];

    /// <summary>
    /// While a partition stays blocked past the alert threshold
    /// (<c>Max(RetryDelays.Length, 1)</c> failures of the same message), the operations
    /// alert is repeated every this-many additional failures so a long-blocked partition
    /// keeps surfacing. Zero or negative means the alert fires once only.
    /// </summary>
    public int AlertRepeatEveryFailures { get; set; } = 10;
}
