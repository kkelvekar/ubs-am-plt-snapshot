namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql;

/// <summary>
/// Bound from the <c>Database</c> configuration section. All values come from
/// configuration (with environment-variable overrides, e.g.
/// <c>Database__ConnectionString</c>) — never from code.
/// <para>
/// ADO.NET pool tuning is an operator concern, done on the configured connection string
/// itself: appending <c>Min Pool Size=2;Max Pool Size=20</c> keeps a couple of warm
/// connections through business-hours bursts and sets a generous per-pod ceiling — the
/// sequential consume loop needs ~1–2 live connections, so 4–5 pods peak around 10,
/// well within Azure SQL limits. Nothing is hardcoded here.
/// </para>
/// </summary>
public sealed record DatabaseOptions
{
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = string.Empty;
}
