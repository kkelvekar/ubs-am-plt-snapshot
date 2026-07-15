using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping onto the hand-written schema in <c>db/scripts/001_snapshot_tracking.sql</c>
/// (solution design §6). The SQL script is the source of truth — never add EF migrations;
/// a schema change means updating the script and this mapping together.
/// </summary>
public sealed class SnapshotWriterDbContext : DbContext
{
    // Filename lists travel as a JSON array string in a single NVARCHAR column (design §6).
    private static readonly ValueConverter<List<string>, string> FileListConverter = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

    private static readonly ValueComparer<List<string>> FileListComparer = new(
        (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
        v => v.ToList());

    private static readonly ValueConverter<SnapshotTrackingStatus, string> StatusConverter = new(
        v => ToDbStatus(v),
        v => FromDbStatus(v));

    // display_data travels as a single camelCase JSON object string (design §7) so the
    // persisted keys match the wire/header naming convention (benchmark, baseCcy, ...).
    private static readonly JsonSerializerOptions DisplayDataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly ValueConverter<SnapshotIndexDisplayData, string> DisplayDataConverter = new(
        v => JsonSerializer.Serialize(v, DisplayDataJsonOptions),
        v => JsonSerializer.Deserialize<SnapshotIndexDisplayData>(v, DisplayDataJsonOptions)!);

    public SnapshotWriterDbContext(DbContextOptions<SnapshotWriterDbContext> options)
        : base(options)
    {
    }

    public DbSet<SnapshotTrackingEntry> SnapshotTracking => Set<SnapshotTrackingEntry>();

    public DbSet<SnapshotIndexEntry> SnapshotIndex => Set<SnapshotIndexEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SnapshotTrackingEntry>();

        entity.ToTable("SnapshotTracking", "dbo");
        entity.HasKey(e => e.SnapshotId);

        entity.Property(e => e.SnapshotId)
            .HasColumnName("SnapshotId")
            .HasColumnType("varchar(50)");

        entity.Property(e => e.AccountId)
            .HasColumnName("AccountId")
            .HasColumnType("varchar(20)");

        entity.Property(e => e.SnapshotType)
            .HasColumnName("SnapshotType")
            .HasColumnType("varchar(50)");

        entity.Property(e => e.AdlsRootPath)
            .HasColumnName("AdlsRootPath")
            .HasColumnType("varchar(max)");

        entity.Property(e => e.ReceivedFiles)
            .HasColumnName("ReceivedFiles")
            .HasColumnType("nvarchar(max)")
            .HasConversion(FileListConverter, FileListComparer);

        entity.Property(e => e.MissingFiles)
            .HasColumnName("MissingFiles")
            .HasColumnType("nvarchar(max)")
            .HasConversion(FileListConverter!, FileListComparer);

        entity.Property(e => e.Status)
            .HasColumnName("Status")
            .HasColumnType("varchar(20)")
            .HasConversion(StatusConverter);

        entity.Property(e => e.FirstReceivedAt)
            .HasColumnName("FirstReceivedAt")
            .HasColumnType("datetime2");

        entity.Property(e => e.LastUpdatedAt)
            .HasColumnName("LastUpdatedAt")
            .HasColumnType("datetime2");

        entity.Property(e => e.CompletedAt)
            .HasColumnName("CompletedAt")
            .HasColumnType("datetime2");

        entity.Property(e => e.DeclaredFailedAt)
            .HasColumnName("DeclaredFailedAt")
            .HasColumnType("datetime2");

        entity.Property(e => e.Alerted)
            .HasColumnName("Alerted")
            .HasColumnType("bit");

        var indexEntity = modelBuilder.Entity<SnapshotIndexEntry>();

        indexEntity.ToTable("SnapshotIndex", "dbo");
        indexEntity.HasKey(e => e.SnapshotId);

        indexEntity.Property(e => e.SnapshotId)
            .HasColumnName("SnapshotId")
            .HasColumnType("varchar(50)");

        indexEntity.Property(e => e.AccountId)
            .HasColumnName("AccountId")
            .HasColumnType("varchar(20)");

        indexEntity.Property(e => e.SnapshotDate)
            .HasColumnName("SnapshotDate")
            .HasColumnType("datetime2");

        indexEntity.Property(e => e.EventType)
            .HasColumnName("EventType")
            .HasColumnType("varchar(50)");

        indexEntity.Property(e => e.AdlsPath)
            .HasColumnName("AdlsPath")
            .HasColumnType("varchar(max)");

        indexEntity.Property(e => e.DisplayData)
            .HasColumnName("DisplayData")
            .HasColumnType("nvarchar(max)")
            .HasConversion(DisplayDataConverter);

        indexEntity.Property(e => e.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2");
    }

    private static string ToDbStatus(SnapshotTrackingStatus status) => status switch
    {
        SnapshotTrackingStatus.Receiving => "RECEIVING",
        SnapshotTrackingStatus.Complete => "COMPLETE",
        SnapshotTrackingStatus.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown tracking status."),
    };

    private static SnapshotTrackingStatus FromDbStatus(string value) => value switch
    {
        "RECEIVING" => SnapshotTrackingStatus.Receiving,
        "COMPLETE" => SnapshotTrackingStatus.Complete,
        "FAILED" => SnapshotTrackingStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown tracking status value in database."),
    };
}
