using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;

namespace Ecp.Host;

/// <summary>Seeds the synthetic demonstration tenant and organisation. Never runs in Production (guard) and is idempotent.</summary>
public sealed class DemoSeeder(ITenantDirectory directory, ITenantProvisioningService provisioning, EcpHostOptions options, ILogger<DemoSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (options.Mode == EcpMode.Production) throw new InvalidOperationException("Demonstration data must not be seeded in Production.");
        if (await directory.FindByNameAsync(options.DemoTenantName, ct) is not null) return;
        var result = await provisioning.ProvisionAsync(new ProvisionTenantRequest(
            options.DemoTenantName, TenancyMode.SingleOrganisation, options.Mode, "Demo Organisation",
            "Australia/Melbourne", "en-AU", "AUD", "DEMO", SolutionPackKey: null), ct);
        logger.LogInformation("Seeded demonstration tenant {TenantId} with organisation {OrganisationId}", result.TenantId, result.OrganisationId);
    }
}
