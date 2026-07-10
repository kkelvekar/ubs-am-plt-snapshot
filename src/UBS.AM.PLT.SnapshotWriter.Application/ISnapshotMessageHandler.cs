using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Application;

/// <summary>
/// Application-layer entry point for one snapshot message. The Kafka adapter calls
/// straight into this port and commits the offset only when it completes successfully.
/// </summary>
public interface ISnapshotMessageHandler
{
    Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken);
}
