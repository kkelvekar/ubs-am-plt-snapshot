using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql.SnapshotConfig;

/// <summary>
/// Adapter for <see cref="IRequiredFilesProvider"/> backed by the library-owned
/// <see cref="SnapshotConfigDefinition"/> map, so a new payload type needs only a single
/// entry in that constant.
/// </summary>
public sealed class SnapshotConfigRequiredFilesProvider : IRequiredFilesProvider
{
    private readonly IOptions<Dictionary<string, SnapshotTypeConfig>> _options;

    public SnapshotConfigRequiredFilesProvider(IOptions<Dictionary<string, SnapshotTypeConfig>> options)
    {
        _options = options;
    }

    public IReadOnlySet<string> GetRequiredFiles(string snapshotType)
    {
        if (!_options.Value.TryGetValue(snapshotType, out var config))
        {
            throw new KeyNotFoundException($"No SnapshotConfig entry configured for snapshotType '{snapshotType}'.");
        }

        return new HashSet<string>(config.RequiredFiles, StringComparer.Ordinal);
    }
}
