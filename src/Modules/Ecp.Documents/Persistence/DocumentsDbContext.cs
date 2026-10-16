using Ecp.Documents.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Documents.Persistence;

public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentVersion> Versions => Set<DocumentVersion>();
    protected override string SchemaName => DocumentsModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<Document>(e =>
        {
            e.ToTable("Document");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DocumentTypeCode).HasMaxLength(100).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Classification).HasMaxLength(30).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DocumentSourceId).HasMaxLength(400);
            e.Property(x => x.ArchiveReason).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.MigrationBatchId });
            e.Property(x => x.SourceSystem).HasMaxLength(100);
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.ContractId });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.SourceSystem, x.DocumentSourceId }).IsUnique()
                .HasFilter("[DocumentSourceId] IS NOT NULL");
        });
        mb.Entity<DocumentVersion>(e =>
        {
            e.ToTable("DocumentVersion");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.VersionLabel).HasMaxLength(50);
            e.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            e.Property(x => x.MimeType).HasMaxLength(150).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.StorageProvider).HasMaxLength(50).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(150).IsRequired();
            e.Property(x => x.ScanStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.UploadedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.DocumentId, x.VersionNumber }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Sha256 });
            e.HasIndex(x => new { x.TenantId, x.MigrationBatchId });
            e.HasOne<Document>().WithMany().HasForeignKey(x => new { x.TenantId, x.DocumentId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class DocumentsDbContextFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<DocumentsDbContext>(DocumentsModule.Schema);
        return new DocumentsDbContext(options, services);
    }
}
