using Ecp.Persistence;
using Ecp.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Tenancy.Persistence;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();
    public DbSet<Division> Divisions => Set<Division>();
    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();
    public DbSet<Site> Sites => Set<Site>();
    protected override string SchemaName => TenancyModule.Schema;

    private static void ConfigureStructure<T>(ModelBuilder mb, string table, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T>> extra) where T : StructureItem
    {
        mb.Entity<T>(e =>
        {
            e.ToTable(table);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Key).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion);
            // Composite foreign key keeps the item in the organisation's own tenant (ADR-0003).
            e.HasOne<Organisation>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganisationId }).HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Key }).IsUnique();
            extra(e);
        });
    }

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<Tenant>(e =>
        {
            e.ToTable("Tenant");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TenancyMode).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ConfigurationMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RowVersion);
            e.HasIndex(x => x.Name).IsUnique();
            // Registry: visible in installation scope, or when it is the current tenant.
            e.HasQueryFilter(x => x.Id == CurrentTenantId || InInstallationScope);
        });
        mb.Entity<Organisation>(e =>
        {
            e.ToTable("Organisation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.Property(x => x.CultureCode).HasMaxLength(20).IsRequired();
            e.Property(x => x.DefaultCurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
            e.Property(x => x.ReferencePrefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.ReferenceSeparator).HasMaxLength(1);
            e.Property(x => x.SolutionPackKey).HasMaxLength(100);
            e.Property(x => x.RowVersion);
            e.Ignore(x => x.ReferenceRule);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            // Prefix unique within the tenant so Agreement References cannot collide across organisations (D3).
            e.HasIndex(x => new { x.TenantId, x.ReferencePrefix }).IsUnique();
        });

        ConfigureStructure<LegalEntity>(mb, "LegalEntity", e => e.Property(x => x.RegistrationNumber).HasMaxLength(50));
        ConfigureStructure<Division>(mb, "Division", _ => { });
        ConfigureStructure<BusinessUnit>(mb, "BusinessUnit", e =>
        {
            e.HasOne<Division>().WithMany().HasForeignKey(x => new { x.TenantId, x.DivisionId }).HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        ConfigureStructure<Site>(mb, "Site", e => e.Property(x => x.TimeZoneId).HasMaxLength(100));
        mb.Entity<Division>().HasAlternateKey(x => new { x.TenantId, x.Id });
    }
}

public sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<TenancyDbContext>(TenancyModule.Schema);
        return new TenancyDbContext(options, services);
    }
}
