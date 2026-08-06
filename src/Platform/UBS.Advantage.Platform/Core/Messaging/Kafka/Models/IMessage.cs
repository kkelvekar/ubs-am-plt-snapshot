namespace Ubs.Advantage.Core.Messaging.Kafka.Models;

/// <summary>
/// One message on a topic. <see cref="Value"/> is nullable: a tombstone or an empty body
/// arrives with no value.
/// </summary>
public interface IMessage<out TKey, out TValue>
{
    TKey Key { get; }

    TValue? Value { get; }

    IReadOnlyList<MessageHeader> Headers { get; }
}
