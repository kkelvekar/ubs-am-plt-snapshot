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
    // display_data (design §7) is the header.json text itself, already a string on the
    // entity — stored verbatim, with no conversion, renaming or re-serialisation, so the
    // column is byte-identical to the blob and new header fields need no code change.
    public void Configure(EntityTypeBuilder<SnapshotIndexEntity> indexEntity)
    {
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
            .HasColumnType("nvarchar(max)");

        indexEntity.Property(e => e.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2");
    }
}
