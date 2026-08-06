using Confluent.Kafka;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Publishing;

/// <summary>
/// Creates the Kafka producer the publish command produces through. Exists so the publish
/// logic can be exercised with a fake <see cref="IProducer{TKey,TValue}"/>.
/// </summary>
public interface IKafkaProducerFactory
{
    IProducer<string, string> Create();
}
