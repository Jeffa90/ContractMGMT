using Ecp.Lifecycle.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle.Persistence;

public sealed class LifecycleDbContext(DbContextOptions<LifecycleDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<StatusHistory> History => Set<StatusHistory>();
    public DbSet<LegalReview> LegalReviews => Set<LegalReview>();
    public DbSet<LegalRound> LegalRounds => Set<LegalRound>();
    public DbSet<LegalComment> LegalComments => Set<LegalComment>();
    public DbSet<LegalAttachment> LegalAttachments => Set<LegalAttachment>();
    public DbSet<LegalEvent> LegalEvents => Set<LegalEvent>();
    public DbSet<ExecutionRecord> Executions => Set<ExecutionRecord>();
    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();
    public DbSet<RecordUnlock> Unlocks => Set<RecordUnlock>();
    public DbSet<Variation> Variations => Set<Variation>();
    public DbSet<Renewal> Renewals => Set<Renewal>();
    public DbSet<LifecycleEvent> Events => Set<LifecycleEvent>();
    public DbSet<SubjectDocument> SubjectDocuments => Set<SubjectDocument>();
    protected override string SchemaName => LifecycleModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<StatusHistory>(e =>
        {
            e.ToTable("StatusHistory");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SubjectKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.FromStatus).HasMaxLength(100).IsRequired();
            e.Property(x => x.ToStatus).HasMaxLength(100).IsRequired();
            e.Property(x => x.TransitionCode).HasMaxLength(100).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(4000);
            e.Property(x => x.WorkflowRef).HasMaxLength(200);
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ClientRequestId).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.SubjectId, x.OccurredUtc });
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.SubjectId, x.ClientRequestId }).IsUnique().HasFilter("[ClientRequestId] IS NOT NULL");
        });

        mb.Entity<LegalReview>(e =>
        {
            e.ToTable("LegalReview");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SubjectKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AssignedToName).HasMaxLength(200);
            e.Property(x => x.SubmittedByName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ClearedBy).HasMaxLength(200);
            e.Property(x => x.LastOutcome).HasMaxLength(30);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.SubjectKind, x.SubjectId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status, x.LastActivityUtc });
            e.HasIndex(x => new { x.TenantId, x.ContractId });
        });

        mb.Entity<LegalRound>(e =>
        {
            e.ToTable("LegalRound");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SubmittedByName).HasMaxLength(200).IsRequired();
            e.Property(x => x.SubmissionNote).HasMaxLength(4000);
            e.Property(x => x.ReviewerName).HasMaxLength(200);
            e.Property(x => x.ReworkReason).HasMaxLength(4000);
            e.Property(x => x.ClosedBy).HasMaxLength(200);
            e.Property(x => x.DocumentsJson).IsRequired();
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.ReviewId, x.Number }).IsUnique();
            e.HasOne<LegalReview>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReviewId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<LegalComment>(e =>
        {
            e.ToTable("LegalComment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.AuthorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Side).HasMaxLength(10).IsRequired();
            e.Property(x => x.Body).HasMaxLength(8000).IsRequired();
            e.Property(x => x.Classification).HasMaxLength(30).IsRequired();
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.RoundId, x.CreatedUtc });
            e.HasOne<LegalRound>().WithMany().HasForeignKey(x => new { x.TenantId, x.RoundId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<LegalAttachment>(e =>
        {
            e.ToTable("LegalAttachment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.AddedBy).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.RoundId });
            e.HasOne<LegalRound>().WithMany().HasForeignKey(x => new { x.TenantId, x.RoundId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<LegalEvent>(e =>
        {
            e.ToTable("LegalEvent");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.ReviewId, x.Sequence }).IsUnique();
            e.HasOne<LegalReview>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReviewId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<ExecutionRecord>(e =>
        {
            e.ToTable("ExecutionRecord");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SubjectKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.EnvelopeId).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.StartedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.SubjectKind, x.SubjectId, x.IsCurrent });
            e.HasIndex(x => new { x.TenantId, x.Provider, x.EnvelopeId }).HasFilter("[EnvelopeId] IS NOT NULL");
        });

        mb.Entity<ApprovalRequest>(e =>
        {
            e.ToTable("ApprovalRequest");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SubjectKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ApproverGroupKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.SubmittedByName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ExecutedDocumentSha256).HasMaxLength(64);
            e.Property(x => x.SnapshotJson).IsRequired();
            e.Property(x => x.SnapshotSha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.EligibleApproversJson).IsRequired();
            e.Property(x => x.DecidedByName).HasMaxLength(200);
            e.Property(x => x.DecidedAsRole).HasMaxLength(50);
            e.Property(x => x.OnBehalfOfName).HasMaxLength(200);
            e.Property(x => x.DecisionComment).HasMaxLength(4000);
            e.HasIndex(x => new { x.TenantId, x.Status, x.SubmittedUtc });
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.SubmittedUtc });
            // One request waiting per subject at a time: a second submission while one is pending cannot be stored.
            e.HasIndex(x => new { x.TenantId, x.SubjectKind, x.SubjectId }).IsUnique().HasFilter("[Status] = 'Pending'");
        });

        mb.Entity<RecordUnlock>(e =>
        {
            e.ToTable("RecordUnlock");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OpenedByName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.FieldsJson).IsRequired();
            e.Property(x => x.ClosedBy).HasMaxLength(200);
            e.Property(x => x.CloseKind).HasMaxLength(20);
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.OpenedUtc });
            e.HasIndex(x => new { x.TenantId, x.ClosedUtc, x.ExpiresUtc });
        });

        mb.Entity<Variation>(e =>
        {
            e.ToTable("Variation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Reference).HasMaxLength(120).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Reason).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(100).IsRequired();
            e.Property(x => x.FinancialImpactAmount).HasPrecision(18, 2);
            e.Property(x => x.FinancialImpactCurrency).HasMaxLength(3);
            e.Property(x => x.FinancialImpactNote).HasMaxLength(1000);
            e.Property(x => x.ProposedChangesJson).IsRequired();
            e.Property(x => x.BeforeJson).IsRequired();
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.OutcomeReason).HasMaxLength(4000);
            e.Property(x => x.WorkflowRef).HasMaxLength(200);
            e.Property(x => x.ParentStatusBefore).HasMaxLength(100).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.Sequence }).IsUnique();
            // At most one open Variation per agreement: enforced by the database, not only by the check beforehand.
            e.HasIndex(x => new { x.TenantId, x.ContractId }).IsUnique().HasFilter("[IsOpen] = 1").HasDatabaseName("UX_Variation_OneOpen");
            e.HasIndex(x => new { x.TenantId, x.IsOpen, x.UpdatedUtc });
        });

        mb.Entity<Renewal>(e =>
        {
            e.ToTable("Renewal");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.StartedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.CopiedFieldsJson).IsRequired();
            e.Property(x => x.CopiedDocumentsJson).IsRequired();
            e.Property(x => x.ClosedBy).HasMaxLength(200);
            e.Property(x => x.CloseReason).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.PredecessorContractId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.SuccessorContractId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.PredecessorContractId }).IsUnique().HasFilter("[Status] = 'InProgress'").HasDatabaseName("UX_Renewal_OneInProgress");
        });

        mb.Entity<LifecycleEvent>(e =>
        {
            e.ToTable("LifecycleEvent");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.SubjectKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Classification).HasMaxLength(30).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.DispatchedUtc, x.CreatedUtc });
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.CreatedUtc });
        });

        mb.Entity<SubjectDocument>(e =>
        {
            e.ToTable("SubjectDocument");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SubjectKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.AddedBy).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.SubjectKind, x.SubjectId, x.DocumentId }).IsUnique();
        });
    }
}
