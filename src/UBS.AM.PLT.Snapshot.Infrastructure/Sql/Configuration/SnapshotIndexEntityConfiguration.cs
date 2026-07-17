using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
    // display_data travels as a single camelCase JSON object string (design §7) so the
    // persisted keys match the wire/header naming convention (benchmark, baseCcy, ...).
    private static readonly JsonSerializerOptions DisplayDataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly ValueConverter<SnapshotIndexDisplayData, string> DisplayDataConverter = new(
        v => JsonSerializer.Serialize(v, DisplayDataJsonOptions),
        v => JsonSerializer.Deserialize<SnapshotIndexDisplayData>(v, DisplayDataJsonOptions)!);

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
            .HasColumnType("nvarchar(max)")
            .HasConversion(DisplayDataConverter);

        indexEntity.Property(e => e.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2");
    }
}
