using Ecp.Contracts.Domain;
using Ecp.Contracts.Register;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Contracts.Persistence;

public sealed class ContractsDbContext(DbContextOptions<ContractsDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<CategoryAssignment> CategoryAssignments => Set<CategoryAssignment>();
    public DbSet<ReferenceCounter> ReferenceCounters => Set<ReferenceCounter>();
    public DbSet<ContractCustomValue> CustomValues => Set<ContractCustomValue>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
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

        mb.Entity<CategoryAssignment>(e =>
        {
            e.ToTable("CategoryAssignment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.AssignedByLabel).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.RowVersion);
            e.HasOne<Category>().WithMany().HasForeignKey(x => new { x.TenantId, x.CategoryId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.CategoryId, x.EffectiveToUtc });
            e.HasIndex(x => new { x.TenantId, x.UserId });
        });

        mb.Entity<ContractCustomValue>(e =>
        {
            e.ToTable("ContractCustomValue");
            e.HasKey(x => new { x.TenantId, x.ContractId, x.FieldKey, x.Ordinal });
            e.Property(x => x.FieldKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.TextValue).HasMaxLength(450);
            e.Property(x => x.LongTextValue);
            e.Property(x => x.NumberValue).HasPrecision(28, 8);
            e.Property(x => x.UpdatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceSystem).HasMaxLength(100);
            e.Property(x => x.SourceValue).HasMaxLength(450);
            // Composite foreign key including TenantId: a value can never point at another tenant's contract.
            e.HasOne<Contract>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContractId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            // One narrow index per typed column: a filter or sort on a field is a seek on (tenant, field, value). Each holds only the rows that use that column.
            e.HasIndex(x => new { x.TenantId, x.FieldKey, x.TextValue }).HasFilter("[TextValue] IS NOT NULL").HasDatabaseName("IX_CustomValue_Text");
            e.HasIndex(x => new { x.TenantId, x.FieldKey, x.NumberValue }).HasFilter("[NumberValue] IS NOT NULL").HasDatabaseName("IX_CustomValue_Number");
            e.HasIndex(x => new { x.TenantId, x.FieldKey, x.DateValue }).HasFilter("[DateValue] IS NOT NULL").HasDatabaseName("IX_CustomValue_Date");
            e.HasIndex(x => new { x.TenantId, x.FieldKey, x.DateTimeValue }).HasFilter("[DateTimeValue] IS NOT NULL").HasDatabaseName("IX_CustomValue_DateTime");
            e.HasIndex(x => new { x.TenantId, x.FieldKey, x.BoolValue }).HasFilter("[BoolValue] IS NOT NULL").HasDatabaseName("IX_CustomValue_Bool");
            e.HasIndex(x => new { x.TenantId, x.FieldKey, x.RefValue }).HasFilter("[RefValue] IS NOT NULL").HasDatabaseName("IX_CustomValue_Ref");
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.FieldKey });
            e.HasIndex(x => new { x.TenantId, x.MigrationBatchId }).HasFilter("[MigrationBatchId] IS NOT NULL");
        });

        mb.Entity<SavedView>(e =>
        {
            e.ToTable("SavedView");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.DefinitionJson).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.Name }).IsUnique();
        });

        mb.Entity<ReferenceCounter>(e =>
        {
            e.ToTable("ReferenceCounter");
            e.HasKey(x => new { x.TenantId, x.OrganisationId });
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
            e.Property(x => x.SupplierType).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.SourceSystem).HasMaxLength(100);
            e.Property(x => x.SourceRecordId).HasMaxLength(200);
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.NormalisedName });
            e.HasIndex(x => new { x.TenantId, x.MigrationBatchId });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.SupplierType });
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
            e.Property(x => x.ContractType).HasMaxLength(100);
            e.Property(x => x.AgreementType).HasMaxLength(100);
            e.Property(x => x.RiskLevel).HasMaxLength(50);
            e.Property(x => x.FunctionCode).HasMaxLength(100);
            e.Property(x => x.SapOutlineAgreementNumber).HasMaxLength(100);
            e.Property(x => x.NoticePeriodUnit).HasMaxLength(10);
            e.Property(x => x.RegisteredBy).HasMaxLength(200);
            e.Property(x => x.RegisteredDateProvenance).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SourceRegisteredValue).HasMaxLength(200);
            e.Property(x => x.SourceSystem).HasMaxLength(100);
            e.Property(x => x.SourceRecordId).HasMaxLength(200);
            e.Property(x => x.SourceStatus).HasMaxLength(200);
            e.Property(x => x.ArchiveReason).HasMaxLength(1000);
            e.Property(x => x.LockReason).HasMaxLength(200);
            e.Property(x => x.LockedBy).HasMaxLength(200);
            e.Property(x => x.DocumentPolicy).HasMaxLength(20).IsRequired().HasDefaultValue(DocumentPolicies.Open);
            e.Property(x => x.WorkflowRef).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.MigrationBatchId });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.PredecessorContractId });
            e.HasIndex(x => new { x.TenantId, x.SuccessorContractId });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.ContractType });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.RiskLevel });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.BusinessUnitId });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.SiteId });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.LegalEntityId });
            e.HasIndex(x => new { x.TenantId, x.EndDate });
            e.HasIndex(x => new { x.TenantId, x.ReviewDate });
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
