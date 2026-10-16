using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Identity.Providers;
using Ecp.Operations;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Ecp.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Setup.Stages;

public static class SetupChecks
{
    public static IReadOnlyList<Type> All() =>
    [
        typeof(PlatformCheck), typeof(StorageCheck), typeof(ProductionGuardCheck), typeof(DemoExclusionCheck), typeof(IdentityProviderCheck), typeof(TestSignInCheck),
        typeof(AdministratorCheck), typeof(InstallationAdminCheck), typeof(OrganisationCheck), typeof(ConfigurationCheck), typeof(EmailCheck), typeof(AuditChainCheck)
    ];
}

internal abstract class TenantCheck : ISetupCheck
{
    public abstract string Id { get; }
    public abstract string Description { get; }

    public async Task<CheckOutcome> RunAsync(CheckContext c)
    {
        if (c.Doc.TenantId is not { } tenantId) return CheckOutcome.Fail("The tenant has not been created yet.");
        using var _ = c.Services.GetRequiredService<TenantContext>().Use(tenantId);
        return await RunInTenantAsync(c);
    }

    protected abstract Task<CheckOutcome> RunInTenantAsync(CheckContext c);
}

/// <summary>R-OPS-01: database reachable and current, data directory writable, culture and time-zone data present (the platform's own readiness checks).</summary>
internal sealed class PlatformCheck : ISetupCheck
{
    public string Id => "R-OPS-01";
    public string Description => "Database, data directory and globalisation are healthy";

    public async Task<CheckOutcome> RunAsync(CheckContext c)
    {
        var report = await c.Services.GetRequiredService<ReadinessService>().RunAsync(c.Ct);
        var bad = report.Results.Where(r => r.Status != ReadinessStatus.Healthy).ToList();
        if (bad.Count == 0) return CheckOutcome.Pass("All platform checks are healthy.");
        return CheckOutcome.Fail(string.Join(" ", bad.Select(b => $"{b.Id}: {b.Message}")), bad.Any(b => b.Status == ReadinessStatus.Unhealthy) ? CheckLevel.Blocking : CheckLevel.Warning);
    }
}

internal sealed class StorageCheck : ISetupCheck
{
    public string Id => "R-ST-01";
    public string Description => "Document storage verified";
    public Task<CheckOutcome> RunAsync(CheckContext c)
        => Task.FromResult(c.Doc.Done("storage") ? CheckOutcome.Pass("The storage round trip succeeded.") : CheckOutcome.Fail("The storage check has not been run."));
}

internal sealed class ProductionGuardCheck : ISetupCheck
{
    public string Id => "R-SEC-01";
    public string Description => "Production configuration guard";

    public Task<CheckOutcome> RunAsync(CheckContext c)
    {
        if (c.Env.HostMode != EcpMode.Production) return Task.FromResult(CheckOutcome.Pass($"Not Production ({c.Env.HostMode} mode); the guard does not apply."));
        var violations = ProductionGuard.Evaluate(c.Services.GetRequiredService<StartupFacts>());
        return Task.FromResult(violations.Count == 0 ? CheckOutcome.Pass("The Production guard found no violations.")
            : CheckOutcome.Fail(string.Join(" ", violations.Select(v => $"[{v.CheckId}] {v.Message}"))));
    }
}

internal sealed class DemoExclusionCheck : ISetupCheck
{
    public string Id => "R-DEMO-01";
    public string Description => "No demonstration identity or data in Production";

    public Task<CheckOutcome> RunAsync(CheckContext c)
    {
        if (c.Env.HostMode != EcpMode.Production) return Task.FromResult(CheckOutcome.Pass("Not Production."));
        var problems = new List<string>();
        if (c.Env.DemoIdentityEnabled) problems.Add("demonstration sign-in is enabled");
        if (c.Env.DemoSeedEnabled) problems.Add("demonstration seeding is enabled");
        if (c.Doc.DemoDataApplied) problems.Add("demonstration data was applied");
        return Task.FromResult(problems.Count == 0 ? CheckOutcome.Pass("No demonstration identity or data.") : CheckOutcome.Fail("Production must not have " + string.Join(", ", problems) + "."));
    }
}

internal sealed class IdentityProviderCheck : TenantCheck
{
    public override string Id => "R-ID-01";
    public override string Description => "An enabled, valid identity provider is configured";

    protected override async Task<CheckOutcome> RunInTenantAsync(CheckContext c)
    {
        var providers = await c.Services.GetRequiredService<IProviderRegistry>().ListAsync(c.Ct);
        var enabled = providers.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0) return CheckOutcome.Fail("No identity provider is enabled.");
        var secrets = c.Services.GetRequiredService<ISecretProvider>();
        var problems = enabled.SelectMany(p => IdentityProviderValidator.Validate(p, c.Env.HostMode, secrets).Where(i => i.Blocking).Select(i => $"{p.Key}: {i.Message}")).ToList();
        return problems.Count == 0 ? CheckOutcome.Pass($"{enabled.Count} identity provider(s) configured.") : CheckOutcome.Fail(string.Join(" ", problems));
    }
}

internal sealed class TestSignInCheck : TenantCheck
{
    public override string Id => "R-ID-02";
    public override string Description => "A test sign-in through the identity provider succeeded";

    protected override async Task<CheckOutcome> RunInTenantAsync(CheckContext c)
    {
        var registry = c.Services.GetRequiredService<IProviderRegistry>();
        var enabled = (await registry.ListAsync(c.Ct)).Where(p => p.Enabled).ToList();
        if (enabled.Count == 0) return CheckOutcome.Fail("No identity provider is enabled.");
        foreach (var p in enabled)
            if (await registry.TestSignInAsync(p.Key, c.Ct) is not null) return CheckOutcome.Pass($"A test sign-in through '{p.Key}' succeeded.");
        return CheckOutcome.Fail("No test sign-in has succeeded. Use 'Test sign-in' on the identity step; otherwise the administrator may be locked out.",
            c.Env.HostMode == EcpMode.Production ? CheckLevel.Blocking : CheckLevel.Warning);
    }
}

internal sealed class AdministratorCheck : TenantCheck
{
    public override string Id => "R-ADM-01";
    public override string Description => "An active tenant administrator exists";

    protected override async Task<CheckOutcome> RunInTenantAsync(CheckContext c)
    {
        var assignments = await c.Services.GetRequiredService<IRoleAssignmentService>().ListAsync(activeOnly: true, c.Ct);
        var users = await c.Services.GetRequiredService<IUserService>().ListAsync(c.Ct);
        var admins = assignments.Where(a => a.RoleCode == RoleCodes.TenantAdministrator).Select(a => a.UserId).Distinct().ToList();
        if (admins.Count == 0) return CheckOutcome.Fail("No tenant administrator holds an active role assignment.");
        var invited = admins.All(id => users.Any(u => u.Id == id && u.Status == UserStatus.Invited));
        return invited
            ? CheckOutcome.Fail("The tenant administrator has not signed in yet; the account is still invited.", c.Env.HostMode == EcpMode.Production ? CheckLevel.Blocking : CheckLevel.Warning)
            : CheckOutcome.Pass($"{admins.Count} tenant administrator(s).");
    }
}

internal sealed class InstallationAdminCheck : ISetupCheck
{
    public string Id => "R-ADM-02";
    public string Description => "An installation administrator can reopen setup";

    public Task<CheckOutcome> RunAsync(CheckContext c)
        => Task.FromResult(c.Doc.Get("admin", "installationAdmin") == "true" ? CheckOutcome.Pass("An installation administrator was named.")
            : CheckOutcome.Fail("No installation administrator was named; setup can only be reopened by one (permission setup.run).", CheckLevel.Warning));
}

internal sealed class OrganisationCheck : TenantCheck
{
    public override string Id => "R-ORG-01";
    public override string Description => "The organisation exists";

    protected override async Task<CheckOutcome> RunInTenantAsync(CheckContext c)
        => c.Doc.OrganisationId is { } id && await c.Services.GetRequiredService<ITenantDirectory>().OrganisationExistsAsync(id, c.Ct)
            ? CheckOutcome.Pass("The organisation exists.") : CheckOutcome.Fail("The organisation has not been created.");
}

internal sealed class ConfigurationCheck : TenantCheck
{
    public override string Id => "R-CFG-01";
    public override string Description => "Solution pack, branding and classification are published";

    protected override async Task<CheckOutcome> RunInTenantAsync(CheckContext c)
    {
        var configuration = c.Services.GetRequiredService<IConfigurationService>();
        var missing = new List<string>();
        foreach (var kind in new[] { "SolutionPack", "Branding", "ClassificationPolicy" })
            if (await configuration.LatestReleaseAsync(kind, Guid.Empty, c.Ct) is null) missing.Add(kind);
        return missing.Count == 0 ? CheckOutcome.Pass("All three configuration documents are published.") : CheckOutcome.Fail("Not yet published: " + string.Join(", ", missing) + ".");
    }
}

internal sealed class EmailCheck : ISetupCheck
{
    public string Id => "R-NOT-01";
    public string Description => "Email notifications can be delivered";

    public Task<CheckOutcome> RunAsync(CheckContext c)
    {
        var provider = c.Doc.Get("notifications", "in.provider");
        if (provider is null) return Task.FromResult(CheckOutcome.Fail("Email has not been configured."));
        if (provider == nameof(EmailProviderKind.Smtp) && c.Doc.Done("notifications")) return Task.FromResult(CheckOutcome.Pass("SMTP is configured and the test message was accepted."));
        var level = c.Env.HostMode == EcpMode.Production ? CheckLevel.Blocking : CheckLevel.Warning;
        return Task.FromResult(CheckOutcome.Fail($"The {provider} email provider does not deliver notifications.", level));
    }
}

internal sealed class AuditChainCheck : TenantCheck
{
    public override string Id => "R-AUD-01";
    public override string Description => "The audit chain verifies";

    protected override async Task<CheckOutcome> RunInTenantAsync(CheckContext c)
    {
        var result = await c.Services.GetRequiredService<AuditChainVerifier>().VerifyAsync(c.Ct);
        return result.IsValid ? CheckOutcome.Pass($"{result.EventsChecked} audit events verified.") : CheckOutcome.Fail("The audit chain did not verify: " + result.Reason);
    }
}
