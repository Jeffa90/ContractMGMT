using Ecp.Approvals.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Approvals.Persistence;

public sealed class ApprovalsDbContext(DbContextOptions<ApprovalsDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<ApprovalDelegation> Delegations => Set<ApprovalDelegation>();
    protected override string SchemaName => ApprovalsModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<ApprovalDelegation>(e =>
        {
            e.ToTable("ApprovalDelegation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.GroupKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.FromName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ToName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.RevokedBy).HasMaxLength(200);
            e.Property(x => x.RevokeReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.GroupKey, x.ValidToUtc });
            e.HasIndex(x => new { x.TenantId, x.FromUserId, x.ValidToUtc });
            e.HasIndex(x => new { x.TenantId, x.ToUserId, x.ValidToUtc });
        });
    }
}
