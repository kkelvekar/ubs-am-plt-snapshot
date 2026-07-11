namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Configuration;

/// <summary>
/// Configuration section name for the required-files map. The section root IS the map
/// keyed by snapshotType (matches appsettings.json shape exactly, no wrapper type) —
/// bound as <c>Dictionary&lt;string, SnapshotTypeConfig&gt;</c>.
/// </summary>
public static class SnapshotConfigOptions
{
    public const string SectionName = "SnapshotConfig";
}
