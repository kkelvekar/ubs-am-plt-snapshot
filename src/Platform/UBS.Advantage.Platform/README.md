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
