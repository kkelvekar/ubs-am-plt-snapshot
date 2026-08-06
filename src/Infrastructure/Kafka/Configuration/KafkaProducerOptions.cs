namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Configuration;

/// <summary>
/// Bound from the same <c>Kafka</c> configuration section as
/// <see cref="KafkaConsumerOptions"/> — one broker address for the whole client, one place
/// to override it (<c>Kafka__BootstrapServers</c>). Only the outbound topic is new.
/// </summary>
public sealed record KafkaProducerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Topic the snapshot status responses are published to.</summary>
    public string ResponseTopic { get; set; } = string.Empty;
}
