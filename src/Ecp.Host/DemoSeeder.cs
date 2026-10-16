using System.Text.Json;
using Ecp.Contracts;
using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;

namespace Ecp.Host;

/// <summary>
/// Seeds the synthetic Demo tenants, organisations, structure, taxonomy, users, role assignments, suppliers and contracts from the repository's
/// sample data (embedded). Idempotent. Never runs in Production (the guard refuses the flag and this class refuses to run).
/// </summary>
public sealed class DemoSeeder(IServiceScopeFactory scopes, EcpHostOptions options, ILogger<DemoSeeder> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static JsonElement Load(string resource)
    {
        using var stream = typeof(DemoSeeder).Assembly.GetManifestResourceStream($"Ecp.Demo.{resource}") ?? throw new InvalidOperationException($"Demo resource {resource} missing.");
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static readonly Dictionary<string, string> RoleByDisplayName = new()
    {
        ["System Administrator"] = RoleCodes.SystemAdministrator, ["Tenant Administrator"] = RoleCodes.TenantAdministrator,
        ["Organisation Administrator"] = RoleCodes.OrganisationAdministrator, ["Division/Site Administrator"] = RoleCodes.DivisionSiteAdministrator,
        ["Business Configuration Administrator"] = RoleCodes.BusinessConfigurationAdministrator, ["Procurement Administrator"] = RoleCodes.ProcurementAdministrator,
        ["Category Owner"] = RoleCodes.CategoryOwner, ["Legal"] = RoleCodes.Legal, ["Approver"] = RoleCodes.Approver, ["Backup Approver"] = RoleCodes.BackupApprover,
        ["Auditor"] = RoleCodes.Auditor, ["Viewer"] = RoleCodes.Viewer,
    };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (options.Mode == EcpMode.Production) throw new InvalidOperationException("Demonstration data must not be seeded in Production.");
        var structure = Load("organisation-structure.json");
        var users = Load("users-and-roles.json");
        var taxonomy = Load("categories-and-assignments.json");
        var contracts = Load("contracts.json");

        // tenant key -> (tenant id, org key -> org id)
        var tenants = new Dictionary<string, (Guid Id, Dictionary<string, Guid> Orgs)>();
        foreach (var t in structure.GetProperty("tenants").EnumerateArray())
            tenants[t.GetProperty("key").GetString()!] = await EnsureTenantAsync(t, ct);

        var siteIds = new Dictionary<string, Guid>(); var buIds = new Dictionary<string, Guid>(); var divIds = new Dictionary<string, Guid>();
        foreach (var t in structure.GetProperty("tenants").EnumerateArray())
        {
            var (tenantId, orgs) = tenants[t.GetProperty("key").GetString()!];
            foreach (var o in t.GetProperty("organisations").EnumerateArray())
            {
                await using var scope = scopes.CreateAsyncScope();
                using var tenantScope = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
                var svc = scope.ServiceProvider.GetRequiredService<IOrganisationStructureService>();
                var orgId = orgs[o.GetProperty("key").GetString()!];
                foreach (var le in o.GetProperty("legalEntities").EnumerateArray())
                    await svc.UpsertLegalEntityAsync(orgId, le.GetProperty("key").GetString()!, le.GetProperty("name").GetString()!, null, ct);
                foreach (var d in o.GetProperty("divisions").EnumerateArray())
                    await svc.UpsertDivisionAsync(orgId, d.GetProperty("key").GetString()!, d.GetProperty("name").GetString()!, ct);
                foreach (var b in o.GetProperty("businessUnits").EnumerateArray())
                    await svc.UpsertBusinessUnitAsync(orgId, b.GetProperty("key").GetString()!, b.GetProperty("name").GetString()!, b.TryGetProperty("divisionKey", out var dk) ? dk.GetString() : null, ct);
                foreach (var s in o.GetProperty("sites").EnumerateArray())
                    await svc.UpsertSiteAsync(orgId, s.GetProperty("code").GetString()!, s.GetProperty("name").GetString()!, s.TryGetProperty("timeZoneId", out var tz) ? tz.GetString() : null, ct);
                var snap = await svc.GetAsync(orgId, ct);
                foreach (var d in o.GetProperty("divisions").EnumerateArray()) divIds[d.GetProperty("key").GetString()!] = snap.Divisions.Single(x => x.Key == Norm(d.GetProperty("key").GetString()!)).Id;
                foreach (var b in o.GetProperty("businessUnits").EnumerateArray()) buIds[b.GetProperty("key").GetString()!] = snap.BusinessUnits.Single(x => x.Key == Norm(b.GetProperty("key").GetString()!)).Id;
                foreach (var s in o.GetProperty("sites").EnumerateArray()) siteIds[s.GetProperty("key").GetString()!] = snap.Sites.Single(x => x.Key == Norm(s.GetProperty("code").GetString()!)).Id;
            }
        }

        // taxonomy (parents before children)
        var categoryIds = new Dictionary<(string Org, string Code), Guid>();
        foreach (var c in taxonomy.GetProperty("categories").EnumerateArray().OrderBy(c => c.GetProperty("parent").ValueKind == JsonValueKind.Null ? 0 : 1))
        {
            var (tenantId, orgs) = tenants[c.GetProperty("tenant").GetString()!];
            var orgKey = c.GetProperty("organisation").GetString()!;
            await using var scope = scopes.CreateAsyncScope();
            using var tenantScope = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var parent = c.GetProperty("parent").ValueKind == JsonValueKind.Null ? null : c.GetProperty("parent").GetString();
            categoryIds[(orgKey, c.GetProperty("code").GetString()!)] = await scope.ServiceProvider.GetRequiredService<ICategoryAdministration>()
                .UpsertCategoryAsync(orgs[orgKey], c.GetProperty("code").GetString()!, c.GetProperty("name").GetString()!, parent, ct);
        }

        // users and roles
        var userIds = new Dictionary<string, Guid>();
        foreach (var u in users.GetProperty("installationUsers").EnumerateArray())
            userIds[u.GetProperty("email").GetString()!] = await EnsureUserAsync(SystemTenant.Id, u, null, null, null, null, ct);
        foreach (var u in users.GetProperty("users").EnumerateArray())
        {
            var (tenantId, orgs) = tenants[u.GetProperty("tenant").GetString()!];
            userIds[u.GetProperty("email").GetString()!] = await EnsureUserAsync(tenantId, u, orgs, buIds, siteIds, (categoryIds, divIds), ct);
        }

        // category ownership (effective-dated, including an assignment that has ended)
        foreach (var a in taxonomy.GetProperty("assignments").EnumerateArray())
        {
            var (tenantId, _) = tenants[a.GetProperty("tenant").GetString()!];
            await using var scope = scopes.CreateAsyncScope();
            using var tenantScope = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var admin = scope.ServiceProvider.GetRequiredService<ICategoryAdministration>();
            var categoryId = categoryIds[(a.GetProperty("organisation").GetString()!, a.GetProperty("category").GetString()!)];
            var userId = userIds[a.GetProperty("user").GetString()!];
            var from = DateTime.SpecifyKind(a.GetProperty("effectiveFrom").GetDateTime(), DateTimeKind.Utc);
            DateTime? to = a.GetProperty("effectiveTo").ValueKind == JsonValueKind.Null ? null : DateTime.SpecifyKind(a.GetProperty("effectiveTo").GetDateTime(), DateTimeKind.Utc);
            var existing = (await admin.ListAllAsync(ct)).Any(c => c.Id == categoryId && c.Owners.Any(o => o.UserId == userId && o.EffectiveFromUtc == from));
            var alreadyEnded = to is not null; // ended assignments are listed only while active; re-seeding would duplicate history, so skip when the tenant already has contracts
            if (!existing && !(alreadyEnded && await HasContractsAsync(scope, ct)))
                await admin.AssignOwnerTrustedAsync(categoryId, userId, a.GetProperty("isPrimary").GetBoolean(), from, to, "demo-seeder", a.TryGetProperty("note", out var n) ? n.GetString() : null, ct);
        }

        // suppliers and contracts
        var supplierIds = new Dictionary<string, Guid>();
        foreach (var s in contracts.GetProperty("suppliers").EnumerateArray())
        {
            var (tenantId, orgs) = tenants[s.GetProperty("tenant").GetString()!];
            await using var scope = scopes.CreateAsyncScope();
            using var tenantScope = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var orgId = orgs[s.GetProperty("organisation").GetString()!];
            var existing = await scope.ServiceProvider.GetRequiredService<ISupplierDirectory>().FindByVendorNumberAsync(orgId, s.GetProperty("vendorNumber").GetString()!, ct);
            supplierIds[s.GetProperty("key").GetString()!] = existing ?? await scope.ServiceProvider.GetRequiredService<ISupplierService>()
                .CreateAsync(new CreateSupplierRequest(orgId, s.GetProperty("name").GetString()!, null, s.GetProperty("vendorNumber").GetString()), ct);
        }
        foreach (var c in contracts.GetProperty("contracts").EnumerateArray())
        {
            var (tenantId, orgs) = tenants[c.GetProperty("tenant").GetString()!];
            await using var scope = scopes.CreateAsyncScope();
            using var tenantScope = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var title = c.GetProperty("title").GetString()!;
            var orgKey = c.GetProperty("organisation").GetString()!;
            var directory = scope.ServiceProvider.GetRequiredService<ISupplierDirectory>();
            if (await directory.ContractTitleExistsAsync(orgs[orgKey], title, ct)) continue; // idempotent
            var reference = await scope.ServiceProvider.GetRequiredService<IAgreementReferenceAllocator>().AllocateAsync(orgs[orgKey], ct);
            string? Str(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = await scope.ServiceProvider.GetRequiredService<IContractService>().CreateDraftAsync(new CreateContractRequest(
                orgs[orgKey], reference, title, supplierIds[c.GetProperty("supplier").GetString()!], categoryIds[(orgKey, c.GetProperty("category").GetString()!)],
                Classification: c.GetProperty("classification").GetString()!, CurrencyCode: Str("currency"),
                EstimatedValue: c.TryGetProperty("estimatedValue", out var ev) ? ev.GetDecimal() : null,
                StartDate: Str("startDate") is { } sd ? DateOnly.Parse(sd) : null, EndDate: Str("endDate") is { } ed ? DateOnly.Parse(ed) : null,
                BusinessUnitId: Str("businessUnit") is { } bu ? buIds[bu] : null, SiteId: Str("site") is { } st ? siteIds[st] : null), ct);
            var status = c.GetProperty("status").GetString()!;
            if (c.GetProperty("locked").GetBoolean())
                await scope.ServiceProvider.GetRequiredService<IContractLifecycleService>().ApplyStateAsync(id, status, locks: true, ct);
        }
        logger.LogInformation("Demo data seeded for {Tenants} tenants", tenants.Count);
    }

    private static string Norm(string key) => StructureKey.Normalise(key);

    private async Task<bool> HasContractsAsync(AsyncServiceScope scope, CancellationToken ct)
        => (await scope.ServiceProvider.GetRequiredService<ISupplierDirectory>().AnyContractAsync(ct));

    private async Task<(Guid Id, Dictionary<string, Guid> Orgs)> EnsureTenantAsync(JsonElement t, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
        var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        var name = t.GetProperty("name").GetString()!;
        var mode = Enum.Parse<TenancyMode>(t.GetProperty("tenancyMode").GetString()!);
        var info = await directory.FindByNameAsync(name, ct);
        var tenantId = info?.Id ?? Guid.Empty;
        var orgIds = new Dictionary<string, Guid>();
        var first = true;
        foreach (var o in t.GetProperty("organisations").EnumerateArray())
        {
            var key = o.GetProperty("key").GetString()!;
            var orgName = o.GetProperty("name").GetString()!;
            var prefix = "DEMO" + key.Replace("org-", "", StringComparison.Ordinal).ToUpperInvariant();
            if (tenantId == Guid.Empty)
            {
                var provisioned = await provisioning.ProvisionAsync(new ProvisionTenantRequest(name, mode, options.Mode, orgName, o.GetProperty("timeZoneId").GetString()!,
                    o.GetProperty("cultureCode").GetString()!, o.GetProperty("defaultCurrencyCode").GetString()!, prefix, o.GetProperty("solutionPackKey").GetString()), ct);
                tenantId = provisioned.TenantId; orgIds[key] = provisioned.OrganisationId;
            }
            else
            {
                using var tenantScope = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
                var structure = scope.ServiceProvider.GetRequiredService<IOrganisationStructureService>();
                var existing = (await structure.ListOrganisationsAsync(ct)).FirstOrDefault(x => x.Name == orgName);
                orgIds[key] = existing?.Id ?? await provisioning.AddOrganisationAsync(new AddOrganisationRequest(orgName, o.GetProperty("timeZoneId").GetString()!,
                    o.GetProperty("cultureCode").GetString()!, o.GetProperty("defaultCurrencyCode").GetString()!, prefix, o.GetProperty("solutionPackKey").GetString()), ct);
            }
            first = false;
        }
        _ = first;
        return (tenantId, orgIds);
    }

    private async Task<Guid> EnsureUserAsync(Guid tenantId, JsonElement u, Dictionary<string, Guid>? orgs, Dictionary<string, Guid>? bus, Dictionary<string, Guid>? sites,
        (Dictionary<(string Org, string Code), Guid> Categories, Dictionary<string, Guid> Divisions)? more, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        using var tenantScope = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var email = u.GetProperty("email").GetString()!;
        var existing = await users.FindByEmailAsync(email, ct);
        var userId = existing?.Id ?? await users.CreateActiveTrustedAsync(DisplayNameOf(email), email, DemoIdentityService.ProviderKey, DemoIdentityService.Issuer, email, "demo-seeder", ct);
        var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
        if ((await assignments.ListForUserAsync(userId, includeEnded: true, ct)).Count > 0) return userId; // already granted
        foreach (var r in u.GetProperty("roles").EnumerateArray())
        {
            var role = RoleByDisplayName[r.GetProperty("role").GetString()!];
            ViewerKind? kind = r.TryGetProperty("viewerKind", out var vk) ? Enum.Parse<ViewerKind>(vk.GetString()!) : null;
            var level = Enum.Parse<ScopeLevel>(r.GetProperty("scopeLevel").GetString()!);
            var ceiling = r.TryGetProperty("classificationCeiling", out var cc) ? cc.GetString()! : "Public";
            List<string> Keys(string name) => r.TryGetProperty(name, out var arr) ? arr.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
            var orgKeys = Keys("organisations");
            ScopeSet scope2 = level switch
            {
                ScopeLevel.Installation => ScopeSet.Installation(ceiling),
                ScopeLevel.Tenant => ScopeSet.Tenant(ceiling),
                _ => ScopeSet.ForOrganisations(orgKeys.Select(k => orgs![k]), ceiling, Keys("businessUnits").Select(k => bus![k]), null, Keys("sites").Select(k => sites![k]),
                    r.TryGetProperty("categories", out _) ? Keys("categories").Select(code => more!.Value.Categories.Single(c => c.Key.Code == code && orgKeys.Contains(c.Key.Org)).Value) : null)
            };
            await assignments.GrantTrustedAsync(new GrantRequest(userId, role, kind, scope2, r.TryGetProperty("approverGroup", out var ag) ? ag.GetString() : null,
                Reason: "Demonstration role assignment", ExplicitHighCeiling: true), AssignmentSource.Demo, "demo-seeder", null, ct);
        }
        return userId;
    }

    private static string DisplayNameOf(string email)
    {
        var local = email.Split('@')[0].Replace('.', ' ').Replace('-', ' ');
        return string.Join(' ', local.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
