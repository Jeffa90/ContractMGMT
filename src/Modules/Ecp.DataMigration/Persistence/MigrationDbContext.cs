using Ecp.DataMigration.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.DataMigration.Persistence;

public sealed class MigrationDbContext(DbContextOptions<MigrationDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<MigrationProject> Projects => Set<MigrationProject>();
    public DbSet<MigrationBatch> Batches => Set<MigrationBatch>();
    protected override string SchemaName => MigrationModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<MigrationProject>(e =>
        {
            e.ToTable("MigrationProject");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceSystem).HasMaxLength(100).IsRequired();
            e.Property(x => x.SourceSystemVersion).HasMaxLength(100);
            e.Property(x => x.DataOwner).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Name }).IsUnique();
        });
        mb.Entity<MigrationBatch>(e =>
        {
            e.ToTable("MigrationBatch");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DuplicateStrategy).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.DocumentDuplicateOption).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.FailureReason).HasMaxLength(1000);
            e.Property(x => x.AcceptedBy).HasMaxLength(200);
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.ProjectId });
            e.HasOne<MigrationProject>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProjectId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class MigrationDbContextFactory : IDesignTimeDbContextFactory<MigrationDbContext>
{
    public MigrationDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<MigrationDbContext>(MigrationModule.Schema);
        return new MigrationDbContext(options, services);
    }
}
