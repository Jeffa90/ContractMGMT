using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecp.Setup.Persistence;

/// <summary>Installation-level setup state mirrored into the database. A single row in the system tenant.</summary>
public sealed class SetupRecord : ITenantOwned
{
    public static readonly Guid RowId = new("00000000-0000-0000-0000-0000000000a1");

    private SetupRecord() { }
    public Guid Id { get; private set; } = RowId;
    public Guid TenantId { get; private set; } = SystemTenant.Id;
    public int Revision { get; private set; }
    public string StateJson { get; private set; } = "{}";
    public DateTime? CompletedUtc { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SetupRecord Create(int revision, string stateJson, DateTime? completedUtc, DateTime nowUtc)
    {
        var r = new SetupRecord();
        r.Apply(revision, stateJson, completedUtc, nowUtc);
        return r;
    }

    public void Apply(int revision, string stateJson, DateTime? completedUtc, DateTime nowUtc)
    {
        Revision = revision; StateJson = stateJson; CompletedUtc = completedUtc; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }
}

public sealed class SetupDbContext(DbContextOptions<SetupDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<SetupRecord> Records => Set<SetupRecord>();
    protected override string SchemaName => SetupModule.Schema;

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<SetupRecord>(e =>
        {
            e.ToTable("SetupRecord");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.StateJson).IsRequired();
            e.Property(x => x.RowVersion);
        });
    }
}

public sealed class SetupDbContextFactory : IDesignTimeDbContextFactory<SetupDbContext>
{
    public SetupDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<SetupDbContext>(SetupModule.Schema);
        return new SetupDbContext(options, services);
    }
}
