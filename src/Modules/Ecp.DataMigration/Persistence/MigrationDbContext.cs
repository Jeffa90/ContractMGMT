using Ecp.DataMigration.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.DataMigration.Persistence;

public sealed class MigrationDbContext(DbContextOptions<MigrationDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<MigrationProject> Projects => Set<MigrationProject>();
    public DbSet<MigrationBatch> Batches => Set<MigrationBatch>();
    public DbSet<MappingProfile> Profiles => Set<MappingProfile>();
    public DbSet<MappingProfileVersion> ProfileVersions => Set<MappingProfileVersion>();
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<StagingRow> StagingRows => Set<StagingRow>();
    public DbSet<StagingMessage> Messages => Set<StagingMessage>();
    public DbSet<AttachmentManifestEntry> ManifestEntries => Set<AttachmentManifestEntry>();
    public DbSet<StagedFile> StagedFiles => Set<StagedFile>();
    public DbSet<MatchProposal> Proposals => Set<MatchProposal>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<ProvenanceRecord> Provenance => Set<ProvenanceRecord>();
    public DbSet<ReconciliationReport> Reconciliations => Set<ReconciliationReport>();
    public DbSet<MaskedDataDeclaration> MaskedDeclarations => Set<MaskedDataDeclaration>();
    public DbSet<BatchDataClassification> BatchClassifications => Set<BatchDataClassification>();
    protected override string SchemaName => MigrationModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<MaskedDataDeclaration>(e =>
        {
            e.ToTable("MaskedDataDeclaration");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DatasetOwner).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceSystem).HasMaxLength(200).IsRequired();
            e.Property(x => x.MaskingMethod).HasMaxLength(1000).IsRequired();
            e.Property(x => x.FieldsMasked).HasMaxLength(2000).IsRequired();
            e.Property(x => x.ApprovalReference).HasMaxLength(200).IsRequired();
            e.Property(x => x.ApprovedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.PermittedEnvironment).HasMaxLength(20).IsRequired();
            e.Property(x => x.PermittedUsers).HasMaxLength(2000).IsRequired();
            e.Property(x => x.DeclaredBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.RevokedReason).HasMaxLength(500);
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.Ignore(x => x.PermittedUserList);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.DeclaredUtc });
        });

        mb.Entity<BatchDataClassification>(e =>
        {
            e.ToTable("BatchDataClassification");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DataClass).HasMaxLength(30).IsRequired();
            e.Property(x => x.LinkedBy).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.BatchId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.DeclarationId });
        });

        mb.Entity<MigrationProject>(e =>
        {
            e.ToTable("MigrationProject");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceSystem).HasMaxLength(100).IsRequired();
            e.Property(x => x.SourceSystemVersion).HasMaxLength(100);
            e.Property(x => x.DataOwner).HasMaxLength(200).IsRequired();
            e.Property(x => x.LegacyAllowedStatusCodes).HasMaxLength(1000).IsRequired();
            e.Property(x => x.LegacyOverwriteReason).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ReferencePolicy).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Name }).IsUnique();
        });

        mb.Entity<MigrationBatch>(e =>
        {
            e.ToTable("MigrationBatch");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DuplicateStrategy).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.DocumentDuplicateOption).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.FailureReason).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.AcceptedBy).HasMaxLength(200);
            e.Property(x => x.AcceptanceNote).HasMaxLength(2000);
            e.Property(x => x.ValidationFingerprint).HasMaxLength(100);
            e.Property(x => x.ValidationSummaryJson);
            e.Property(x => x.ConfirmedBy).HasMaxLength(200);
            e.Property(x => x.ConfirmedFingerprint).HasMaxLength(100);
            e.Property(x => x.CommittedBy).HasMaxLength(200);
            e.Property(x => x.LeaseOwner).HasMaxLength(100);
            e.Property(x => x.RollbackReason).HasMaxLength(1000);
            e.Property(x => x.RolledBackBy).HasMaxLength(200);
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.ProjectId });
            e.HasOne<MigrationProject>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProjectId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MappingProfileVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.MappingProfileVersionId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<MappingProfile>(e =>
        {
            e.ToTable("MappingProfile");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceSystem).HasMaxLength(100).IsRequired();
            e.Property(x => x.SourceSystemVersion).HasMaxLength(100);
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Name }).IsUnique();
        });

        mb.Entity<MappingProfileVersion>(e =>
        {
            e.ToTable("MappingProfileVersion");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DefinitionHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.PublishedBy).HasMaxLength(200);
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.ProfileId, x.VersionNumber }).IsUnique();
            e.HasOne<MappingProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<SourceFile>(e =>
        {
            e.ToTable("SourceFile");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Format).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(150).IsRequired();
            e.Property(x => x.SheetName).HasMaxLength(200);
            e.Property(x => x.Encoding).HasMaxLength(40);
            e.Property(x => x.Delimiter).HasMaxLength(5);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.UploadedBy).HasMaxLength(200).IsRequired();
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.Role });
            e.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<StagingRow>(e =>
        {
            e.ToTable("StagingRow");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RowHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(64);
            e.Property(x => x.SourceRecordId).HasMaxLength(400);
            e.Property(x => x.AgreementReference).HasMaxLength(200);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PlannedAction).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ReviewDecision).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PlanSummary).HasMaxLength(1000);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.Outcome });
            e.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<StagingMessage>(e =>
        {
            e.ToTable("StagingMessage");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Subject).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Phase).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Location).HasMaxLength(500).IsRequired();
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            e.Property(x => x.FieldName).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.Phase, x.Severity, x.Code });
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.SubjectId });
            e.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<AttachmentManifestEntry>(e =>
        {
            e.ToTable("AttachmentManifestEntry");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SourceRecordId).HasMaxLength(400);
            e.Property(x => x.AgreementReference).HasMaxLength(200);
            e.Property(x => x.RawRelativePath).HasMaxLength(1000);
            e.Property(x => x.RelativePath).HasMaxLength(500);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.DocumentTypeRaw).HasMaxLength(200);
            e.Property(x => x.DocumentTypeCode).HasMaxLength(100);
            e.Property(x => x.VersionLabel).HasMaxLength(50);
            e.Property(x => x.ParentDocumentSourceId).HasMaxLength(400);
            e.Property(x => x.DeclaredSha256).HasMaxLength(64);
            e.Property(x => x.DocumentSourceId).HasMaxLength(400);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.Sequence }).IsUnique();
            e.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<StagedFile>(e =>
        {
            e.ToTable("StagedFile");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RelativePath).HasMaxLength(500).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.Extension).HasMaxLength(20);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.QuarantineKey).HasMaxLength(150);
            e.Property(x => x.MimeType).HasMaxLength(150);
            e.Property(x => x.Scan).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.QueueReason).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.MatchedBy).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DocumentSourceId).HasMaxLength(400);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.ResolvedBy).HasMaxLength(200);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.RelativePath }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.State });
            e.HasIndex(x => new { x.TenantId, x.Sha256 });
            e.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<MatchProposal>(e =>
        {
            e.ToTable("MatchProposal");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ConfirmedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.StagedFileId });
            e.HasOne<StagedFile>().WithMany().HasForeignKey(x => new { x.TenantId, x.StagedFileId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StagingRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContractStagingRowId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<Checkpoint>(e =>
        {
            e.ToTable("Checkpoint");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Phase).HasMaxLength(40).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.Phase }).IsUnique();
            e.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<ProvenanceRecord>(e =>
        {
            e.ToTable("ProvenanceRecord");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TargetEntityType).HasMaxLength(50).IsRequired();
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SourceSystem).HasMaxLength(100).IsRequired();
            e.Property(x => x.SourceRecordId).HasMaxLength(400).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.SourceFileSha256).HasMaxLength(64);
            e.Property(x => x.SourceStatus).HasMaxLength(200);
            e.Property(x => x.ReversalState).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ReversalNote).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.TargetEntityType, x.TargetEntityId, x.Action }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.TargetEntityType, x.TargetEntityId });
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.ReversalState });
            e.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<ReconciliationReport>(e =>
        {
            e.ToTable("ReconciliationReport");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.GeneratedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.ApprovedBy).HasMaxLength(200);
            e.Property(x => x.ApprovalNote).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.BatchId, x.Sequence }).IsUnique();
            e.HasOne<MigrationBatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.BatchId })
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
