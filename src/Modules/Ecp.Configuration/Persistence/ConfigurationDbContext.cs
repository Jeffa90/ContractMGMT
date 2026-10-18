using Ecp.Configuration.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Configuration.Persistence;

public sealed class ConfigurationDbContext(DbContextOptions<ConfigurationDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<ConfigurationDraft> Drafts => Set<ConfigurationDraft>();
    public DbSet<ConfigurationRelease> Releases => Set<ConfigurationRelease>();
    public DbSet<DraftReview> Reviews => Set<DraftReview>();
    public DbSet<ReleaseEvidence> Evidence => Set<ReleaseEvidence>();
    public DbSet<ConfigurationReviewEvent> ReviewEvents => Set<ConfigurationReviewEvent>();
    public DbSet<InstalledPack> InstalledPacks => Set<InstalledPack>();
    public DbSet<PackAssignment> PackAssignments => Set<PackAssignment>();
    public DbSet<TrustedPublisherKey> TrustedKeys => Set<TrustedPublisherKey>();
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

        // The review and the evidence live in tables of their own: the draft and release tables are exactly what they were, so nothing that reads them (backup, inventory, the upgrade checks) changes with this.
        mb.Entity<DraftReview>(e =>
        {
            e.ToTable("DraftReview");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SubmittedBy).HasMaxLength(200);
            e.Property(x => x.SubmittedHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.SubmissionNote).HasMaxLength(1000);
            e.Property(x => x.DecidedBy).HasMaxLength(200);
            e.Property(x => x.DecisionComment).HasMaxLength(2000);
            e.Property(x => x.RollbackReason).HasMaxLength(1000);
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.State });
        });

        mb.Entity<ReleaseEvidence>(e =>
        {
            e.ToTable("ReleaseEvidence");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.LastEditedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.SubmittedBy).HasMaxLength(200);
            e.Property(x => x.SubmittedHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.GovernanceMode).HasMaxLength(20).IsRequired();
        });

        mb.Entity<ConfigurationReviewEvent>(e =>
        {
            e.ToTable("ConfigurationReviewEvent");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(100).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(30).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.DraftId, x.OccurredUtc });
        });

        mb.Entity<InstalledPack>(e =>
        {
            e.ToTable("InstalledPack");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PackKey).HasMaxLength(50).IsRequired();
            e.Property(x => x.Version).HasMaxLength(40).IsRequired();
            e.Property(x => x.PackageId).HasMaxLength(120).IsRequired();
            e.Property(x => x.PublisherId).HasMaxLength(100).IsRequired();
            e.Property(x => x.PublisherName).HasMaxLength(120).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
            e.Property(x => x.PackageSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.ContentDigest).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.ManifestJson).IsRequired();
            e.Property(x => x.SignatureStatus).HasMaxLength(40).IsRequired();
            e.Property(x => x.SignatureAlgorithm).HasMaxLength(20);
            e.Property(x => x.TrustKeyId).HasMaxLength(80);
            e.Property(x => x.VerificationStatus).HasMaxLength(40).IsRequired();
            e.Property(x => x.SourceType).HasMaxLength(30).IsRequired();
            e.Property(x => x.StorageReference).HasMaxLength(200).IsRequired();
            e.Property(x => x.ValidationOutcome).HasMaxLength(40).IsRequired();
            e.Property(x => x.ValidationJson).IsRequired();
            e.Property(x => x.CompatibilityJson).IsRequired();
            e.Property(x => x.UploadedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.InstalledBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.RetiredBy).HasMaxLength(200);
            e.Property(x => x.RetiredReason).HasMaxLength(500);
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.RowVersion);
            // A pack key and version name one immutable package in a tenant; two uploads racing to install it cannot both succeed.
            e.HasIndex(x => new { x.TenantId, x.PackKey, x.Version }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.PackageSha256 });
        });

        mb.Entity<PackAssignment>(e =>
        {
            e.ToTable("PackAssignment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PackKey).HasMaxLength(50).IsRequired();
            e.Property(x => x.Version).HasMaxLength(40).IsRequired();
            e.Property(x => x.PackageSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.SourceType).HasMaxLength(30).IsRequired();
            e.Property(x => x.Origin).HasMaxLength(30).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.AssignedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.OutcomeJson).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.PackKey, x.Version });
        });

        mb.Entity<TrustedPublisherKey>(e =>
        {
            e.ToTable("TrustedPublisherKey");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.KeyId).HasMaxLength(80).IsRequired();
            e.Property(x => x.Algorithm).HasMaxLength(20).IsRequired();
            e.Property(x => x.PublicKeyBase64).HasMaxLength(512).IsRequired();
            e.Property(x => x.Fingerprint).HasMaxLength(32).IsRequired();
            e.Property(x => x.Label).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.RegisteredBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.RevokedBy).HasMaxLength(200);
            e.Property(x => x.RevokedReason).HasMaxLength(500);
            e.Ignore(x => x.PublicKey);
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.KeyId }).IsUnique();
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
