// Simulated org messaging-framework type. At real lift-and-shift: delete this file and
// reference the org messaging NuGet package. Never edit it to fit our needs — adapt in the
// consumer instead.
namespace UBS.Advantage.Messaging;

/// <summary>
/// One consumed message, already deserialised by the framework. <see cref="Value"/> is
/// nullable because deserialisation is the framework's job, not the command's: a tombstone or
/// an empty message body arrives here as <c>null</c>.
/// </summary>
public interface IMessage<out TKey, out TValue>
{
    TKey Key { get; }

    TValue? Value { get; }
}

/// <summary>
/// Framework-side implementation of <see cref="IMessage{TKey, TValue}"/>. Also supplied by the
/// org library at lift-and-shift — it lives here only because our stand-in consumer has to
/// construct the messages it hands to a command.
/// </summary>
public sealed record ConsumedMessage<TKey, TValue>(TKey Key, TValue? Value) : IMessage<TKey, TValue>;
