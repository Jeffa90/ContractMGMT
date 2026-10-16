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

/// <summary>
/// Records which items an onboarding release <em>created</em> (as opposed to items that already existed and were merely matched), so rolling back to an earlier
/// release ends only what onboarding introduced and never touches anything created by other means.
/// </summary>
public sealed class OnboardingItem : ITenantOwned
{
    private OnboardingItem() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string ItemType { get; private set; } = "";
    public string ItemKey { get; private set; } = "";
    public Guid? RefId { get; private set; }
    public int CreatedRelease { get; private set; }
    public int? EndedRelease { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static OnboardingItem Create(Guid tenantId, Guid organisationId, string type, string key, Guid? refId, int release, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, OrganisationId = organisationId, ItemType = type, ItemKey = key, RefId = refId, CreatedRelease = release, UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Reopen(Guid? refId, int release, DateTime nowUtc) { RefId = refId ?? RefId; CreatedRelease = release; EndedRelease = null; UpdatedUtc = nowUtc; }
    public void End(int release, DateTime nowUtc) { EndedRelease = release; UpdatedUtc = nowUtc; }
}

public sealed class SetupDbContext(DbContextOptions<SetupDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<SetupRecord> Records => Set<SetupRecord>();
    public DbSet<OnboardingItem> OnboardingItems => Set<OnboardingItem>();
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
        mb.Entity<OnboardingItem>(e =>
        {
            e.ToTable("OnboardingItem");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ItemType).HasMaxLength(30).IsRequired();
            e.Property(x => x.ItemKey).HasMaxLength(450).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.OrganisationId, x.ItemType, x.ItemKey }).IsUnique();
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
