using System.Reflection;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecp.Persistence;

/// <summary>Services every module context needs. Registered per scope.</summary>
public sealed class DbContextServices(ITenantContext tenant, IScopedConnection connection, IClock clock)
{
    public ITenantContext Tenant { get; } = tenant;
    public IScopedConnection Connection { get; } = connection;
    public IClock Clock { get; } = clock;
}

/// <summary>
/// Base for module contexts. Applies a global tenant query filter to every <see cref="ITenantOwned"/> entity (fails closed
/// when no scope is set), refuses writes that target another tenant, stamps optimistic-concurrency tokens on providers
/// without <c>rowversion</c>, and enlists in the scope's unit-of-work transaction.
/// </summary>
public abstract class TenantDbContext : DbContext
{
    private static readonly MethodInfo ApplyFilterMethod =
        typeof(TenantDbContext).GetMethod(nameof(ApplyFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly DbContextServices _services;

    protected TenantDbContext(DbContextOptions options, DbContextServices services) : base(options)
    {
        _services = services;
    }

    /// <summary>The module schema (SQL Server). SQLite ignores schemas.</summary>
    protected abstract string SchemaName { get; }

    /// <summary>Current tenant; throws <see cref="TenantScopeMissingException"/> when none is set. Referenced by query filters.</summary>
    public Guid CurrentTenantId => _services.Tenant.RequireTenantId();

    /// <summary>True in installation scope. Referenced by the tenant registry filter.</summary>
    public bool InInstallationScope => _services.Tenant.IsInstallationScope;

    protected IClock Clock => _services.Clock;
    protected bool IsSqlServer => Database.IsSqlServer();

    protected abstract void ConfigureModule(ModelBuilder modelBuilder);

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);
        ConfigureModule(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => t.BaseType is null).ToList())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
                ApplyFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);

            if (entityType.FindProperty(RowVersionName) is not null)
            {
                var property = modelBuilder.Entity(entityType.ClrType).Property(RowVersionName);
                if (IsSqlServer) property.IsRowVersion();
                else property.IsConcurrencyToken().ValueGeneratedNever();
            }
        }
    }

    public const string RowVersionName = "RowVersion";

    private void ApplyFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned
        => modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

    private void PrepareForSave()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
        {
            if (entry.Entity is ITenantOwned owned)
            {
                if (owned.TenantId != CurrentTenantId)
                    throw new TenantMismatchException(entry.Metadata.ClrType.Name);
                if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.TenantId)).IsModified)
                    throw new TenantMismatchException(entry.Metadata.ClrType.Name);
            }

            if (!IsSqlServer && entry.Metadata.FindProperty(RowVersionName) is not null)
                entry.Property(RowVersionName).CurrentValue = Guid.NewGuid().ToByteArray();
        }
    }

    /// <summary>Joins the scope's open transaction, if any. Call before raw SQL that must be transactional.</summary>
    public async Task EnlistAsync(CancellationToken ct = default)
    {
        var tx = _services.Connection.Transaction;
        var current = Database.CurrentTransaction?.GetDbTransaction();
        if (ReferenceEquals(current, tx)) return;
        if (current is not null) await Database.UseTransactionAsync(null, ct);
        if (tx is not null) await Database.UseTransactionAsync(tx, ct);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareForSave();
        await EnlistAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => throw new NotSupportedException("Use SaveChangesAsync inside IUnitOfWork.ExecuteAsync so changes and audit commit together.");
}
