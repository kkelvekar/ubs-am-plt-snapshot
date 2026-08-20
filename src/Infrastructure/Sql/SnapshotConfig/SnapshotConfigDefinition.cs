namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql.SnapshotConfig;

/// <summary>
/// Single declarative source of truth for the per-snapshot-type required-files map.
/// Owned by this library because the org configuration layer cannot carry custom
/// appsettings keys (see AGENTS.md invariant #4 exception). Add a payload type by adding
/// an entry here.
/// </summary>
internal static class SnapshotConfigDefinition
{
    internal static IReadOnlyDictionary<string, SnapshotTypeConfig> Map { get; } =
        new Dictionary<string, SnapshotTypeConfig>(StringComparer.Ordinal)
        {
            ["portfolio"] = new SnapshotTypeConfig
            {
                RequiredFiles = ["header.json", "orders.json", "portfolio.json", "settings.json"],
            },
        };
}
