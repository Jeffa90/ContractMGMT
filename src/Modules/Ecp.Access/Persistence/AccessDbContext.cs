using Ecp.Access.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Access.Persistence;

/// <summary>Provenance of guided access changes and Test/Demo preview sessions. Tenant-owned, so row-level security and the tenant filter apply like any other module.</summary>
public sealed class AccessDbContext(DbContextOptions<AccessDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<ProfileApplication> ProfileApplications => Set<ProfileApplication>();
    public DbSet<PreviewSession> PreviewSessions => Set<PreviewSession>();
    public DbSet<CategoryOwnershipPolicy> CategoryOwnershipPolicies => Set<CategoryOwnershipPolicy>();
    protected override string SchemaName => AccessModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<ProfileApplication>(e =>
        {
            e.ToTable("ProfileApplication");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ProfileKey).HasMaxLength(60).IsRequired();
            e.Property(x => x.Origin).HasMaxLength(20).IsRequired();
            e.Property(x => x.AppliedByLabel).HasMaxLength(200).IsRequired();
            e.Property(x => x.GrantIdsJson).IsRequired();
            e.Property(x => x.OwnershipIdsJson).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(300).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.EndedBy).HasMaxLength(200);
            e.Ignore(x => x.GrantIds); e.Ignore(x => x.OwnershipIds); e.Ignore(x => x.IsOpen);
            e.HasIndex(x => new { x.TenantId, x.UserId });
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.ProfileKey });
        });
        mb.Entity<CategoryOwnershipPolicy>(e =>
        {
            e.ToTable("CategoryOwnershipPolicy");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Origin).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedByLabel).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.AssignmentIdsJson).IsRequired();
            e.Property(x => x.EndedBy).HasMaxLength(200);
            e.Property(x => x.EndReason).HasMaxLength(500);
            e.Ignore(x => x.AssignmentIds); e.Ignore(x => x.IsOpen);
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.UserId });
        });
        mb.Entity<PreviewSession>(e =>
        {
            e.ToTable("PreviewSession");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OrganisationIdsJson).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.EndReason).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.PreviewerUserId, x.StartedUtc });
            e.HasIndex(x => new { x.TenantId, x.TargetUserId, x.StartedUtc });
        });
    }
}

public sealed class AccessDbContextFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    public AccessDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<AccessDbContext>(AccessModule.Schema);
        return new AccessDbContext(options, services);
    }
}
