using Ecp.Audit;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy.Domain;
using Ecp.Tenancy.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Tenancy;

public sealed record ProvisionTenantRequest(
    string TenantName, TenancyMode TenancyMode, EcpMode Mode,
    string OrganisationName, string TimeZoneId, string CultureCode, string DefaultCurrencyCode,
    string ReferencePrefix, string? SolutionPackKey = null);

public sealed record ProvisionedTenant(Guid TenantId, Guid OrganisationId);

public sealed record AddOrganisationRequest(string Name, string TimeZoneId, string CultureCode, string DefaultCurrencyCode, string ReferencePrefix, string? SolutionPackKey = null);

public interface ITenantProvisioningService
{
    /// <summary>Creates a tenant and its first organisation (installation-level operation, audited).</summary>
    Task<ProvisionedTenant> ProvisionAsync(ProvisionTenantRequest request, CancellationToken ct = default);

    /// <summary>Adds an organisation to the current tenant (requires an established tenant scope). The reference prefix must be unique in the tenant.</summary>
    Task<Guid> AddOrganisationAsync(AddOrganisationRequest request, CancellationToken ct = default);
}

public interface ITenantDirectory
{
    Task<TenantInfo?> FindByNameAsync(string name, CancellationToken ct = default);
    Task<TenantInfo?> FindAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>True if the organisation exists in the current tenant scope (organisations of other tenants are invisible).</summary>
    Task<bool> OrganisationExistsAsync(Guid organisationId, CancellationToken ct = default);
}

public sealed record TenantInfo(Guid Id, string Name, TenantStatus Status, Guid? FirstOrganisationId);

internal sealed class TenantProvisioningService(
    TenantContext tenant, IUnitOfWork uow, TenancyDbContext db, IAuditWriter audit, IClock clock) : ITenantProvisioningService
{
    public async Task<ProvisionedTenant> ProvisionAsync(ProvisionTenantRequest r, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var created = Tenant.Create(r.TenantName, r.TenancyMode, r.Mode, now);
        var rule = ReferenceNumberingRule.Create(r.ReferencePrefix);

        using (tenant.UseInstallationScope())
        {
            await uow.ExecuteAsync(async c =>
            {
                db.Tenants.Add(created);
                await db.SaveChangesAsync(c);
                audit.Record(new AuditEntry("tenant.created", nameof(Tenant), created.Id.ToString(),
                    NewValuesJson: $"{{\"name\":\"{created.Name.Replace("\"", "'")}\",\"mode\":\"{r.Mode}\"}}"));
            }, ct);
        }

        using (tenant.Use(created.Id))
        {
            var org = Organisation.Create(created.Id, r.OrganisationName, r.TimeZoneId, r.CultureCode, r.DefaultCurrencyCode, rule, r.SolutionPackKey, now);
            await uow.ExecuteAsync(async c =>
            {
                db.Organisations.Add(org);
                await db.SaveChangesAsync(c);
                audit.Record(new AuditEntry("organisation.created", nameof(Organisation), org.Id.ToString(), OrganisationId: org.Id));
            }, ct);
            return new ProvisionedTenant(created.Id, org.Id);
        }
    }

    public async Task<Guid> AddOrganisationAsync(AddOrganisationRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var rule = ReferenceNumberingRule.Create(r.ReferencePrefix);
        var org = Organisation.Create(tenantId, r.Name, r.TimeZoneId, r.CultureCode, r.DefaultCurrencyCode, rule, r.SolutionPackKey, clock.UtcNow);
        return await uow.ExecuteAsync(async c =>
        {
            db.Organisations.Add(org);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
            { throw new DomainException("organisation_conflict", "An organisation with that name or reference prefix already exists in this tenant."); }
            audit.Record(new AuditEntry("organisation.created", nameof(Organisation), org.Id.ToString(), OrganisationId: org.Id));
            return org.Id;
        }, ct);
    }
}

internal sealed class TenantDirectory(TenantContext tenant, TenancyDbContext db) : ITenantDirectory
{
    public Task<TenantInfo?> FindByNameAsync(string name, CancellationToken ct = default)
        => LookupAsync(db.Tenants.Where(t => t.Name == name), ct);

    public Task<TenantInfo?> FindAsync(Guid tenantId, CancellationToken ct = default)
        => LookupAsync(db.Tenants.Where(t => t.Id == tenantId), ct);

    public Task<bool> OrganisationExistsAsync(Guid organisationId, CancellationToken ct = default)
        => db.Organisations.AsNoTracking().AnyAsync(o => o.Id == organisationId, ct);

    // A narrow, read-only registry lookup in installation scope; the previous scope is restored afterwards.
    private async Task<TenantInfo?> LookupAsync(IQueryable<Tenant> query, CancellationToken ct)
    {
        using (tenant.UseInstallationScope())
        {
            var t = await query.AsNoTracking().FirstOrDefaultAsync(ct);
            if (t is null) return null;
            using (tenant.Use(t.Id))
            {
                var orgId = await db.Organisations.AsNoTracking().OrderBy(o => o.CreatedUtc).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
                return new TenantInfo(t.Id, t.Name, t.Status, orgId);
            }
        }
    }
}

public static class TenancyModule
{
    public const string Name = "Tenancy";
    public const string Schema = "tenancy";
    public const int Tier = 2;

    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<TenancyDbContext>(Name, Tier, Schema);
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        return services;
    }
}
