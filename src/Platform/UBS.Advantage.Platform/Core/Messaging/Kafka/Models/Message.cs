namespace Ubs.Advantage.Core.Messaging.Kafka.Models;

/// <summary>A message header, carried alongside the key and value.</summary>
public sealed record MessageHeader(string Key, string Value);

/// <summary>The <see cref="IMessage{TKey, TValue}"/> implementation used to publish and consume.</summary>
public sealed record Message<TKey, TValue>(
    TKey Key,
    TValue? Value,
    IReadOnlyList<MessageHeader> Headers) : IMessage<TKey, TValue>;
