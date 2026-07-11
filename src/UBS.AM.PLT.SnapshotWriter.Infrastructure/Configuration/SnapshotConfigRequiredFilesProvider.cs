using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Configuration;

/// <summary>
/// Config-driven adapter for <see cref="IRequiredFilesProvider"/> — reads the required
/// file list from <c>SnapshotConfig</c> (design §4) so a new payload type needs only a
/// config change, never a code change.
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
