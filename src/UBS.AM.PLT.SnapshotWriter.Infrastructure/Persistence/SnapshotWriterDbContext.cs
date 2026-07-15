using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence.Configuration;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping onto the hand-written schema in <c>db/scripts/001_snapshot_tracking.sql</c>
/// (solution design §6). The SQL script is the source of truth — never add EF migrations;
/// a schema change means updating the script and this mapping together.
/// </summary>
public sealed class SnapshotWriterDbContext : DbContext
{
    public SnapshotWriterDbContext(DbContextOptions<SnapshotWriterDbContext> options)
        : base(options)
    {
    }

    public DbSet<SnapshotTrackingEntity> SnapshotTracking => Set<SnapshotTrackingEntity>();

    public DbSet<SnapshotIndexEntity> SnapshotIndex => Set<SnapshotIndexEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SnapshotTrackingEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SnapshotIndexEntityConfiguration());
    }
}
