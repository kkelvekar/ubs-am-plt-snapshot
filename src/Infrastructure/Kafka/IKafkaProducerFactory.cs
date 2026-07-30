using Confluent.Kafka;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

/// <summary>
/// Creates the Kafka producer used by <see cref="KafkaSnapshotResponsePublisher"/>. Exists
/// so the publish logic can be exercised with a fake <see cref="IProducer{TKey,TValue}"/>.
/// </summary>
public interface IKafkaProducerFactory
{
    IProducer<string, string> Create();
}
