# UBS.Advantage.Platform

Local build of the Advantage platform contracts this service is written against:

| Namespace | Provided in the platform by |
| --- | --- |
| `Ubs.Advantage.Core.Infrastructure.Commands` | `Ubs.Advantage.Core.Infrastructure` |
| `Ubs.Advantage.Core.Messaging.Kafka[.Models/.Extensions]` | `Ubs.Advantage.Core.Messaging.Kafka` |
| `UBS.Advantage.CommunicationModels.Snapshot` | `UBS.Advantage.CommunicationModels` |

The types, namespaces and method signatures match those packages, so the snapshot code
compiles against either: swap this project reference for the package references and delete
this project. Nothing outside this folder needs to change.

Keep it that way — this project exists to satisfy the platform contracts during local
development, so it must never gain a type or a member that the platform packages do not have.

## Known drift from the real org platform

Two gaps are known and deliberately left as-is rather than "fixed" here, since fixing either
would give this mirror behaviour the real library does not have:

- **Headers.** `Models.MessageHeader` carries `IReadOnlyList<MessageHeader>` decoded as UTF-8;
  the real org library exposes headers as a `Dictionary<string, string>` decoded as ASCII. No
  code in this repository reads more than the `eventType`-adjacent header fields today, so this
  is deferred rather than reconciled.
- **Undeserialisable message commit.** `MessageConsumerService.TryConsumeMessage` commits a
  message's offset away on `ConsumeException` (a record that failed to deserialise at the broker
  layer) instead of leaving it stuck — this mirrors the real org library's own behaviour. It is
  an accepted gap, not a bug: recovery for a message lost this way is by key and timestamp from
  Kafka retention, not redelivery.
