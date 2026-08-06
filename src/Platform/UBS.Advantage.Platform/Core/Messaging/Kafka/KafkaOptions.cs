namespace Ubs.Advantage.Core.Messaging.Kafka;

/// <summary>
/// Bound from the <c>Kafka</c> configuration section, with the usual environment-variable
/// overrides (<c>Kafka__BootstrapServers</c>).
/// </summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public string ConsumerGroup { get; set; } = string.Empty;

    /// <summary>
    /// Topic name per topic key — the key is what a consumer or producer service is registered
    /// with, so the topic itself is configuration rather than code.
    /// </summary>
    public Dictionary<string, string> Topics { get; set; } = [];

    /// <summary>
    /// Back-off after a broker-level consume error. Not a per-message retry: a command that
    /// reports failure is never retried in-process.
    /// </summary>
    public TimeSpan ConsumeErrorBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public string ResolveTopic(string topicKey)
        => Topics.TryGetValue(topicKey, out var topic) && !string.IsNullOrWhiteSpace(topic)
            ? topic
            : throw new InvalidOperationException(
                $"Kafka:Topics:{topicKey} must be configured (non-empty) for the '{topicKey}' messaging service.");
}
