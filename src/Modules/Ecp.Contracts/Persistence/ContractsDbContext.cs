using Ecp.Contracts.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Contracts.Persistence;

public sealed class ContractsDbContext(DbContextOptions<ContractsDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Contract> Contracts => Set<Contract>();
    protected override string SchemaName => ContractsModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<Category>(e =>
        {
            e.ToTable("Category");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Code).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceSystem).HasMaxLength(100);
            e.Property(x => x.SourceRecordId).HasMaxLength(200);
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Code }).IsUnique();
            e.HasOne<Category>().WithMany().HasForeignKey(x => new { x.TenantId, x.ParentCategoryId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<Supplier>(e =>
        {
            e.ToTable("Supplier");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.NormalisedName).HasMaxLength(300).IsRequired();
            e.Property(x => x.LegalName).HasMaxLength(300);
            e.Property(x => x.Abn).HasMaxLength(11).IsFixedLength();
            e.Property(x => x.VendorNumber).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.SourceSystem).HasMaxLength(100);
            e.Property(x => x.SourceRecordId).HasMaxLength(200);
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.NormalisedName });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.VendorNumber }).IsUnique().HasFilter("[VendorNumber] IS NOT NULL");
        });

        mb.Entity<Contract>(e =>
        {
            e.ToTable("Contract");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.AgreementReference).HasMaxLength(100).IsRequired();
            e.Property(x => x.SourceAgreementReference).HasMaxLength(200);
            e.Property(x => x.ContractTitle).HasMaxLength(500).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(100).IsRequired();
            e.Property(x => x.Classification).HasMaxLength(30).IsRequired();
            e.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.EstimatedValue).HasPrecision(19, 4);
            e.Property(x => x.ExtensionType).HasMaxLength(50);
            e.Property(x => x.RegisteredBy).HasMaxLength(200);
            e.Property(x => x.RegisteredDateProvenance).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SourceRegisteredValue).HasMaxLength(200);
            e.Property(x => x.SourceSystem).HasMaxLength(100);
            e.Property(x => x.SourceRecordId).HasMaxLength(200);
            e.Property(x => x.SourceStatus).HasMaxLength(200);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            // Decision D3: Agreement Reference is unique across the whole tenant.
            e.HasIndex(x => new { x.TenantId, x.AgreementReference }).IsUnique();
            // Migration idempotency key (INV-42).
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.SourceSystem, x.SourceRecordId }).IsUnique()
                .HasFilter("[SourceRecordId] IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.CategoryId });
            e.HasIndex(x => new { x.TenantId, x.SupplierId });
            // Composite foreign keys include TenantId so a child can never reference another tenant's parent.
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(x => new { x.TenantId, x.CategoryId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class ContractsDbContextFactory : IDesignTimeDbContextFactory<ContractsDbContext>
{
    public ContractsDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<ContractsDbContext>(ContractsModule.Schema);
        return new ContractsDbContext(options, services);
    }
}
