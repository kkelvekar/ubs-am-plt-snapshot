using Ubs.Advantage.Core.Messaging.Kafka.Models;

namespace Ubs.Advantage.Core.Messaging.Kafka;

/// <summary>
/// Publishes messages to the topic the producer service was registered for.
/// </summary>
/// <remarks>
/// <see cref="Publish"/> queues the message and returns; it does not wait for the broker. The
/// outcome of the send is reported to the producer command registered alongside this producer.
/// </remarks>
public interface IProducer<TKey, TValue>
{
    void Publish(Message<TKey, TValue> message);
}
