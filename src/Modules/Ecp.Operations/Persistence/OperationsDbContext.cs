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
    public DbSet<AcceptedException> AcceptedExceptions => Set<AcceptedException>();
    public DbSet<EmailConfiguration> EmailConfigurations => Set<EmailConfiguration>();
    public DbSet<EmailTestResult> EmailTestResults => Set<EmailTestResult>();
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
        mb.Entity<AcceptedException>(e =>
        {
            e.ToTable("AcceptedException");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.Scope).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.AcceptedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.AcceptedByName).HasMaxLength(200).IsRequired();
            e.Property(x => x.RevokedBy).HasMaxLength(200);
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Kind, x.Code });
        });
        mb.Entity<EmailConfiguration>(e =>
        {
            e.ToTable("EmailConfiguration");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ProviderKey).HasMaxLength(20).IsRequired();
            e.Property(x => x.FromAddress).HasMaxLength(320);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.UserRef).HasMaxLength(300);
            e.Property(x => x.PasswordRef).HasMaxLength(300);
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion);
        });
        mb.Entity<EmailTestResult>(e =>
        {
            e.ToTable("EmailTestResult");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ConfigFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.ProviderKey).HasMaxLength(20).IsRequired();
            e.Property(x => x.TestedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.RecipientMasked).HasMaxLength(320).IsRequired();
            e.Property(x => x.ConnectivityOutcome).HasMaxLength(20).IsRequired();
            e.Property(x => x.ConnectivityCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.ConnectivityDetail).HasMaxLength(400).IsRequired();
            e.Property(x => x.DeliveryOutcome).HasMaxLength(20).IsRequired();
            e.Property(x => x.DeliveryCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.DeliveryDetail).HasMaxLength(400).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.TestedUtc });
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
