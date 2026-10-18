using Ecp.Operations.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Operations.Persistence;

/// <summary>Installation-level operational state: leases, the maintenance flag and operation history. All rows belong to the system tenant.</summary>
public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<OperationLease> Leases => Set<OperationLease>();
    public DbSet<MaintenanceState> Maintenance => Set<MaintenanceState>();
    public DbSet<OperationRun> Runs => Set<OperationRun>();
    public DbSet<MigrationRecord> MigrationRecords => Set<MigrationRecord>();
    protected override string SchemaName => OperationsModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<OperationLease>(e =>
        {
            e.ToTable("OperationLease");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.ScopeKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.OwnerId).HasMaxLength(200).IsRequired();
            e.Property(x => x.OwnerDescription).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.Name, x.ScopeKey }).IsUnique();
        });
        mb.Entity<MaintenanceState>(e =>
        {
            e.ToTable("MaintenanceState");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Cause).HasMaxLength(40).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.ChangedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion);
        });
        mb.Entity<MigrationRecord>(e =>
        {
            e.ToTable("MigrationRecord");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Module).HasMaxLength(100).IsRequired();
            e.Property(x => x.MigrationId).HasMaxLength(150).IsRequired();
            e.Property(x => x.Checksum).HasMaxLength(64).IsRequired();
            e.Property(x => x.Generator).HasMaxLength(30).IsRequired();
            e.Property(x => x.PlatformVersion).HasMaxLength(40).IsRequired();
            e.Property(x => x.Origin).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Module, x.MigrationId }).IsUnique();
        });
        mb.Entity<OperationRun>(e =>
        {
            e.ToTable("OperationRun");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(30).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.Reference).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.OperatorId).HasMaxLength(200).IsRequired();
            e.Property(x => x.OperatorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(1000).IsRequired();
            e.Property(x => x.DetailJson).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.Kind, x.StartedUtc });
            e.HasIndex(x => new { x.TenantId, x.Reference });
        });
    }
}

public sealed class OperationsDbContextFactory : IDesignTimeDbContextFactory<OperationsDbContext>
{
    public OperationsDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<OperationsDbContext>(OperationsModule.Schema);
        return new OperationsDbContext(options, services);
    }
}
