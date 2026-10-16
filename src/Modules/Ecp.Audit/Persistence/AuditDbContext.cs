using Ecp.Audit.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Audit.Persistence;

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<AuditEvent> Events => Set<AuditEvent>();
    protected override string SchemaName => AuditModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvent", t => t.HasTrigger("TR_AuditEvent_AppendOnly"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Sequence).ValueGeneratedNever();
            e.Property(x => x.ActorType).HasMaxLength(30);
            e.Property(x => x.ActorId).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(200);
            e.Property(x => x.EntityType).HasMaxLength(200);
            e.Property(x => x.EntityId).HasMaxLength(200);
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.Source).HasMaxLength(50);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.Classification).HasMaxLength(50);
            e.Property(x => x.PreviousHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Hash).HasMaxLength(64).IsFixedLength();
            e.HasIndex(x => new { x.TenantId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            e.HasIndex(x => new { x.TenantId, x.CorrelationId });
        });
    }
}

public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<AuditDbContext>(AuditModule.Schema);
        return new AuditDbContext(options, services);
    }
}
