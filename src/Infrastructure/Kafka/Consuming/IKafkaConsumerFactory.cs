using Confluent.Kafka;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Consuming;

/// <summary>
/// Creates the Kafka consumer used by <see cref="KafkaSnapshotConsumer"/>. Exists so the
/// consume/dispatch/commit logic can be unit tested with a fake
/// <see cref="IConsumer{TKey,TValue}"/>.
/// </summary>
public interface IKafkaConsumerFactory
{
    IConsumer<string, string> Create();
}
