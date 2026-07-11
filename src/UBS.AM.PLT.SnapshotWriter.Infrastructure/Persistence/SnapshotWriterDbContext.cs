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

    public SnapshotWriterDbContext(DbContextOptions<SnapshotWriterDbContext> options)
        : base(options)
    {
    }

    public DbSet<SnapshotTrackingEntry> SnapshotTracking => Set<SnapshotTrackingEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SnapshotTrackingEntry>();

        entity.ToTable("snapshot_tracking", "dbo");
        entity.HasKey(e => e.SnapshotId);

        entity.Property(e => e.SnapshotId)
            .HasColumnName("snapshot_id")
            .HasColumnType("varchar(50)");

        entity.Property(e => e.AccountId)
            .HasColumnName("account_id")
            .HasColumnType("varchar(20)");

        entity.Property(e => e.SnapshotType)
            .HasColumnName("snapshot_type")
            .HasColumnType("varchar(50)");

        entity.Property(e => e.AdlsRootPath)
            .HasColumnName("adls_root_path")
            .HasColumnType("varchar(500)");

        entity.Property(e => e.ReceivedFiles)
            .HasColumnName("received_files")
            .HasColumnType("nvarchar(1000)")
            .HasConversion(FileListConverter, FileListComparer);

        entity.Property(e => e.MissingFiles)
            .HasColumnName("missing_files")
            .HasColumnType("nvarchar(1000)")
            .HasConversion(FileListConverter!, FileListComparer);

        entity.Property(e => e.Status)
            .HasColumnName("status")
            .HasColumnType("varchar(20)")
            .HasConversion(StatusConverter);

        entity.Property(e => e.FirstReceivedAt)
            .HasColumnName("first_received_at")
            .HasColumnType("datetime2");

        entity.Property(e => e.LastUpdatedAt)
            .HasColumnName("last_updated_at")
            .HasColumnType("datetime2");

        entity.Property(e => e.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("datetime2");

        entity.Property(e => e.DeclaredFailedAt)
            .HasColumnName("declared_failed_at")
            .HasColumnType("datetime2");

        entity.Property(e => e.Alerted)
            .HasColumnName("alerted")
            .HasColumnType("bit");
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
