namespace Ecp.SharedKernel;

/// <summary>The reserved tenant id used for installation-level (not customer) data and audit events.</summary>
public static class SystemTenant
{
    public static readonly Guid Id = new("00000000-0000-0000-0000-000000000001");
}

/// <summary>Marker for entities owned by a tenant. Every such entity is filtered by TenantId (ADR-0003).</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>Raised when tenant-scoped data is touched without an established tenant scope. Fails closed.</summary>
public sealed class TenantScopeMissingException : InvalidOperationException
{
    public TenantScopeMissingException()
        : base("No tenant scope is established for this operation. Tenant-owned data cannot be accessed without a tenant scope.") { }
}

/// <summary>Raised when a write targets a tenant other than the current scope.</summary>
public sealed class TenantMismatchException : InvalidOperationException
{
    public TenantMismatchException(string entityType)
        : base($"A write to '{entityType}' targets a tenant other than the current tenant scope.") { }
}

public interface ITenantContext
{
    /// <summary>The current tenant, or null when no scope has been established.</summary>
    Guid? TenantId { get; }

    /// <summary>True only for installation-level operations (setup, tenant provisioning).</summary>
    bool IsInstallationScope { get; }

    /// <summary>Incremented whenever the scope changes; lets infrastructure re-apply database session context.</summary>
    long Version { get; }

    /// <summary>Returns the tenant id or throws <see cref="TenantScopeMissingException"/>.</summary>
    Guid RequireTenantId();
}

/// <summary>
/// Mutable per-scope tenant context. The tenant is established by authenticated resolution (requests)
/// or from the job/message record (background work), never from client-supplied values.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public bool IsInstallationScope { get; private set; }
    public long Version { get; private set; }

    public Guid RequireTenantId() => TenantId ?? throw new TenantScopeMissingException();

    public IDisposable Use(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        return Replace(tenantId, installation: false);
    }

    /// <summary>Installation scope: tenant is the system tenant and the registry of tenants is visible.</summary>
    public IDisposable UseInstallationScope() => Replace(SystemTenant.Id, installation: true);

    private IDisposable Replace(Guid? tenant, bool installation)
    {
        var previous = (TenantId, IsInstallationScope);
        TenantId = tenant;
        IsInstallationScope = installation;
        Version++;
        return new Restore(this, previous.TenantId, previous.IsInstallationScope);
    }

    private sealed class Restore(TenantContext owner, Guid? tenant, bool installation) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner.TenantId = tenant;
            owner.IsInstallationScope = installation;
            owner.Version++;
        }
    }
}
