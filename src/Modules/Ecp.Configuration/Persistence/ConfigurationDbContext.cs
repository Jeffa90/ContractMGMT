using Ecp.Configuration.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Configuration.Persistence;

public sealed class ConfigurationDbContext(DbContextOptions<ConfigurationDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<ConfigurationDraft> Drafts => Set<ConfigurationDraft>();
    public DbSet<ConfigurationRelease> Releases => Set<ConfigurationRelease>();
    protected override string SchemaName => ConfigurationModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<ConfigurationDraft>(e =>
        {
            e.ToTable("ConfigurationDraft");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(100).IsRequired();
            e.Property(x => x.ContentJson).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.ValidatedHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.ValidationJson);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastEditedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Kind, x.Status });
        });

        mb.Entity<ConfigurationRelease>(e =>
        {
            e.ToTable("ConfigurationRelease");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(100).IsRequired();
            e.Property(x => x.ContentJson).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Note).HasMaxLength(1000).IsRequired();
            e.Property(x => x.PublishedBy).HasMaxLength(200).IsRequired();
            // Release numbers are unique per tenant, scope and kind: concurrent publishers cannot both take the same number.
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Kind, x.Number }).IsUnique();
        });
    }
}

public sealed class ConfigurationDbContextFactory : IDesignTimeDbContextFactory<ConfigurationDbContext>
{
    public ConfigurationDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<ConfigurationDbContext>(ConfigurationModule.Schema);
        return new ConfigurationDbContext(options, services);
    }
}
