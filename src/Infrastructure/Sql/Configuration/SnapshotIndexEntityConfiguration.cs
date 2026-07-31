using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql.Configuration;

/// <summary>
/// EF Core mapping for <see cref="SnapshotIndexEntity"/> onto the hand-written schema in
/// <c>db/scripts/002_snapshot_index.sql</c> (solution design §7). The SQL script is the
/// source of truth — never add EF migrations; a schema change means updating the script and
/// this mapping together.
/// </summary>
internal sealed class SnapshotIndexEntityConfiguration : IEntityTypeConfiguration<SnapshotIndexEntity>
{
    // DisplayData carries no value conversion on purpose: it holds the header.json text and
    // must stay byte-identical to the blob.
    public void Configure(EntityTypeBuilder<SnapshotIndexEntity> indexEntity)
    {
        indexEntity.ToTable("SnapshotIndex", "dbo");
        indexEntity.HasKey(e => e.SnapshotId);

        // The widths below mirror db/scripts/002 and SnapshotFieldLimits; change all three
        // together.
        indexEntity.Property(e => e.SnapshotId)
            .HasColumnName("SnapshotId")
            .HasColumnType("varchar(100)"); // SnapshotFieldLimits.SnapshotIdMaxLength

        indexEntity.Property(e => e.AccountId)
            .HasColumnName("AccountId")
            .HasColumnType("varchar(100)"); // SnapshotFieldLimits.AccountIdMaxLength

        indexEntity.Property(e => e.SnapshotDate)
            .HasColumnName("SnapshotDate")
            .HasColumnType("datetime2");

        indexEntity.Property(e => e.EventType)
            .HasColumnName("EventType")
            .HasColumnType("varchar(100)"); // SnapshotFieldLimits.EventTypeMaxLength

        indexEntity.Property(e => e.AdlsPath)
            .HasColumnName("AdlsPath")
            .HasColumnType("varchar(max)");

        indexEntity.Property(e => e.DisplayData)
            .HasColumnName("DisplayData")
            .HasColumnType("nvarchar(max)");

        indexEntity.Property(e => e.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2");
    }
}
