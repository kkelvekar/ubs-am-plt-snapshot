namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

/// <summary>
/// Bound from the <c>Database</c> configuration section. All values come from
/// configuration (with environment-variable overrides, e.g.
/// <c>Database__ConnectionString</c>) — never from code.
/// </summary>
public sealed record DatabaseOptions
{
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = string.Empty;
}
