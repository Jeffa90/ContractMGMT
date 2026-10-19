using System.Text.Json;
using Ecp.Configuration;
using Ecp.Documents.Storage;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Identity.Providers;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Setup.Stages;

public static class SetupStages
{
    public static IReadOnlyList<Type> All() =>
    [
        typeof(ModeStage), typeof(DatabaseStage), typeof(StorageStage), typeof(TenantStage), typeof(PackStage), typeof(IdentityStage), typeof(AdminStage),
        typeof(OrganisationStage), typeof(BrandingStage), typeof(ClassificationStage), typeof(NotificationsStage), typeof(DemoDataStage), typeof(ReadinessStage), typeof(CompleteStage)
    ];
}

internal sealed class ModeStage : SetupStageBase
{
    public override string Id => "mode";
    public override string Title => "Installation mode";
    public override string Summary => "Confirm the mode this installation runs in and how it is deployed.";

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
    {
        var fields = new List<StageField>
        {
            new("deployment", "Deployment", FieldKind.Select, true, "Where this installation runs. Recorded in the setup report only.",
                [new("Local", "Local development or evaluation"), new("IIS", "Windows Server with IIS"), new("Azure", "Azure App Service"), new("Container", "Container")], "Local"),
            new("tenancyMode", "Tenancy", FieldKind.Select, true, "Single [[organisation|lower]] is the usual choice. This can only be widened later by an installation administrator.",
                [new("SingleOrganisation", "One [[organisation|lower]]"), new("MultiOrganisation", "Several [[organisation|plural|lower]] in one tenant"), new("MultiTenant", "Several tenants")], "SingleOrganisation"),
        };
        if (env.HostMode != EcpMode.Production)
            fields.Add(new("acknowledge", "I understand this installation is for demonstration or testing only and must not hold real confidential data", FieldKind.Checkbox, true));
        return fields;
    }

    public override IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env) =>
    [
        $"This installation runs in {env.HostMode} mode. The mode is set by the host configuration (Ecp:Mode) and cannot be changed from this page.",
        .. (env.GovernanceName is null ? Array.Empty<string>() : [$"Governance: {env.GovernanceLabel}. {env.GovernanceNote}"]),
        env.HostMode == EcpMode.Production
            ? "Production refuses demonstration identity, demonstration data, the lightweight database and ephemeral storage."
            : "Demonstration and Test modes allow synthetic data and a mock email provider. They are not approved for real data.",
    ];

    public override Task<StageResult> ExecuteAsync(StageContext c)
    {
        var deployment = c.Required("deployment", "Deployment");
        if (!new[] { "Local", "IIS", "Azure", "Container" }.Contains(deployment)) throw new DomainException("deployment", "Choose one of the listed deployments.");
        if (!Enum.TryParse<TenancyMode>(c.Required("tenancyMode", "Tenancy"), out var tenancy)) throw new DomainException("tenancy", "Choose one of the listed tenancy modes.");
        if (c.Env.HostMode != EcpMode.Production && !c.Flag("acknowledge")) throw new DomainException("acknowledge", "Acknowledge that this installation is not for real data.");
        if (c.Env.HostMode == EcpMode.Production)
        {
            var violations = ProductionGuard.Evaluate(c.Get<StartupFacts>());
            if (violations.Count > 0) throw new DomainException("production_guard", "Production configuration is not acceptable: " + string.Join(" ", violations.Select(v => $"[{v.CheckId}] {v.Message}")));
        }
        if (c.Doc.TenantId is not null && c.Doc.TenancyMode != tenancy.ToString())
            throw new DomainException("tenancy_locked", "The tenant already exists; tenancy can no longer be changed here.");
        c.Doc.Mode = c.Env.HostMode; c.Doc.Deployment = deployment; c.Doc.TenancyMode = tenancy.ToString();
        c.Remember("deployment", deployment); c.Remember("tenancyMode", tenancy.ToString());
        return Task.FromResult(StageResult.Ok($"{c.Env.HostMode} mode confirmed ({deployment})."));
    }
}

internal sealed class DatabaseStage : SetupStageBase
{
    public override string Id => "database";
    public override string Title => "Database";
    public override string Summary => "Check the database connection and make sure the schema is current.";
    public override string ActionLabel => "Check database";
    public override IReadOnlyList<string> DependsOn => ["mode"];

    public override IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env) =>
    [
        $"Provider: {env.DatabaseProvider}. The connection string is " + (env.ConnectionFromSecretReference ? "read from a secret reference." : "set in host configuration.") + " It is never shown here.",
        env.HostMode == EcpMode.Production
            ? "In Production the schema is applied from the command line (`migrate`) before setup; this step only verifies it."
            : "In Demo and Test modes this step applies the schema for you.",
    ];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var runner = c.Get<MigrationRunner>();
        var status = await runner.GetStatusAsync(c.Ct);
        if (!status.IsCurrent)
        {
            if (c.Env.HostMode == EcpMode.Production)
                return StageResult.Fail("The database schema is not current. Run the `migrate` command, then check again.", "Pending: " + string.Join(", ", status.Pending));
            await runner.RunAsync(c.Ct);
            status = await runner.GetStatusAsync(c.Ct);
            if (!status.IsCurrent) return StageResult.Fail("Migrations were applied but the schema is still not current.", "Pending: " + string.Join(", ", status.Pending));
        }
        c.Record.Data["modules"] = status.Applied.Count.ToString();
        c.Record.Data["provider"] = c.Env.DatabaseProvider;
        return StageResult.Ok($"The database is reachable and all {status.Applied.Count} modules are current.");
    }
}

internal sealed class StorageStage : SetupStageBase
{
    public override string Id => "storage";
    public override string Title => "[[document]] storage";
    public override string Summary => "Prove that [[document|plural|lower]] can be written, read back and removed.";
    public override string ActionLabel => "Run storage check";
    public override IReadOnlyList<string> DependsOn => ["database"];
    public override bool Amendable => true;

    public override IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env) =>
    [$"[[document|plural]] are stored under the data directory, which must be on a persistent volume{(env.HostMode == EcpMode.Production ? " (a temporary location is refused)" : "")}."];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var storage = c.Get<IDocumentStorageProvider>();
        var payload = System.Text.Encoding.UTF8.GetBytes("setup storage check " + Guid.NewGuid().ToString("N"));
        await using var input = new MemoryStream(payload);
        var stored = await storage.PutAsync(SystemTenant.Id, input, 4096, c.Ct);
        try
        {
            await using var read = await storage.OpenReadAsync(SystemTenant.Id, stored.StorageKey, c.Ct);
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy, c.Ct);
            if (!copy.ToArray().AsSpan().SequenceEqual(payload)) return StageResult.Fail("The stored object did not read back identically.");
            if (!await storage.ExistsAsync(SystemTenant.Id, stored.StorageKey, c.Ct)) return StageResult.Fail("The stored object could not be found again.");
        }
        finally { await storage.DeleteAsync(SystemTenant.Id, stored.StorageKey, c.Ct); }
        if (await storage.ExistsAsync(SystemTenant.Id, stored.StorageKey, c.Ct)) return StageResult.Fail("The test object could not be removed.");
        c.Record.Data["provider"] = storage.ProviderKey;
        return StageResult.Ok($"Write, read, existence and delete succeeded with the '{storage.ProviderKey}' provider.");
    }
}

internal sealed class TenantStage : SetupStageBase
{
    public override string Id => "tenant";
    public override string Title => "Tenant";
    public override string Summary => "Create the tenant that holds your [[organisation|lower]]. Administrators are created before any [[organisation|lower]] configuration.";
    public override IReadOnlyList<string> DependsOn => ["database", "mode"];

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
        => [new("tenantName", "Tenant name", FieldKind.Text, true, "Usually your company or group name. It identifies the tenant; it is not shown to [[supplier|plural|lower]].", Value: doc.TenantName)];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var name = c.Required("tenantName", "Tenant name");
        if (name.Length > 120) throw new DomainException("tenant_name", "The tenant name must be at most 120 characters.");
        var directory = c.Get<ITenantDirectory>();
        if (c.Doc.TenantId is { } existingId)
        {
            var info = await directory.FindAsync(existingId, c.Ct);
            if (info is not null) return StageResult.Ok($"Tenant '{info.Name}' already exists. Nothing changed.");
        }
        var adopted = await directory.FindByNameAsync(name, c.Ct); // resume after an interrupted run
        Guid tenantId;
        if (adopted is not null) tenantId = adopted.Id;
        else
        {
            var tenancy = Enum.TryParse<TenancyMode>(c.Doc.TenancyMode, out var m) ? m : TenancyMode.SingleOrganisation;
            tenantId = await c.Get<ITenantProvisioningService>().CreateTenantAsync(name, tenancy, c.Env.HostMode, c.Ct);
        }
        c.Doc.TenantId = tenantId; c.Doc.TenantName = name;
        c.Remember("tenantName", name);
        await c.AuditAsync("setup.tenant.created", "Tenant", tenantId.ToString());
        return StageResult.Ok($"Tenant '{name}' is ready.");
    }
}

internal sealed class PackStage : SetupStageBase
{
    public override string Id => "pack";
    public override string Title => "[[solution.pack|sentence]]";
    public override string Summary => "Choose the starting configuration. A pack is data only; you can change everything it sets.";
    public override IReadOnlyList<string> DependsOn => ["tenant"];
    private readonly ISolutionPackCatalogue _catalogue;
    public PackStage(ISolutionPackCatalogue catalogue) => _catalogue = catalogue;

    private static string Label(Ecp.Configuration.PackCatalogueEntry e)
        => $"{e.Name()} (version {e.Version}) from {e.PublisherName}" + (e.Trust.IsTrusted ? ", signed" : ", UNSIGNED (Demo and Test only)");

    // Nothing is chosen for the administrator: the starting pack is an explicit decision, and the choices come from the pack catalogue (external, verified packages), never from a name in code.
    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
        => [new("packId", "[[solution.pack|sentence]]", FieldKind.Select, true,
            "Choose the starting configuration for this [[organisation|lower]]. A pack is data only and you can change everything it sets. More packs can be installed later under Administration, [[solution.pack|plural|sentence]].",
            [new FieldOption("", "Choose a starting pack"), .. _catalogue.ListEntries().Select(p => new FieldOption(p.PackKey, Label(p)))], doc.PackId ?? "")];

    public override IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env)
    {
        var entries = _catalogue.ListEntries();
        var notes = new List<string>();
        if (entries.Count == 0)
            notes.Add("No [[solution.pack|lower]] is available. Packs ship as signed packages next to the application; in Production only packs signed by a trusted publisher are offered. Ask the person who installed the application to check Administration, [[solution.pack|plural|sentence]], or see docs/PACK_SIGNING.md.");
        else if (entries.Count == 1)
            notes.Add("Only one pack is available. Additional packs can be installed later under Administration, [[solution.pack|plural|sentence]].");
        foreach (var p in entries)
        {
            notes.Add($"{p.Name()}: {p.DescriptionText()}" + (p.Synthetic ? " Contains synthetic example data only." : "") + (p.Capabilities.Count > 0 ? $" Capabilities: {string.Join(", ", p.Capabilities)}." : "")
                + (p.Trust.IsTrusted ? "" : " This package is unsigned and is accepted only because this is a Demo or Test environment."));
            notes.Add($"{p.Name()} wording: {ExampleWording(p.PackKey)}");
            notes.Add($"{p.Name()} optional steps: {OptionalSteps(p.PackKey)}");
        }
        notes.Add("Optional steps ([[legal.review|lower]], [[registration.approval|lower]], [[variation|plural|lower]], [[renewal|plural|lower]] and signature requests) are switched on or off by the pack as a default and by the [[organisation|lower]] later under Administration, Capabilities. Switching a step off removes it from the pages and lifecycle; it never changes who may do anything.");
        notes.Add("Wording (what the pack calls a [[contract|lower]], a [[supplier|lower]] and so on) is data in the pack. It applies as the default for the [[organisation|lower]] you create, and you can change any label later under Administration, Terminology. Nothing is published, and labels never change what anyone may do.");
        return notes;
    }

    /// <summary>Which optional steps the pack switches off, in the pack's own data (never a pack or business named in this code).</summary>
    private string OptionalSteps(string packKey)
    {
        var json = _catalogue.CapabilitiesOf(packKey);
        if (json is null) return "all on (the platform default; this pack gives none).";
        try
        {
            var set = CapabilityDocument.Parse(json).AsSet();
            return set.Disabled.Count == 0 ? "all on." : "off: " + string.Join(", ", set.Disabled.Select(k => CapabilityCatalogue.ByKey[k].Label)) + "; the rest are on.";
        }
        catch (DomainException) { return "the pack's capability defaults could not be read; every optional step is on."; }
    }

    /// <summary>A few headline labels from the pack's terminology, as plain text for the notes (the pack's own words, never wording written into this code).</summary>
    private string ExampleWording(string packKey)
    {
        var json = _catalogue.TerminologyOf(packKey);
        if (json is null) return "the platform's neutral wording (this pack carries none).";
        try
        {
            var set = TermSet.Build("en", null, TerminologyDocument.Parse(json).Terms, null, null);
            return string.Join("; ", new[] { "contract", "supplier", "category", "contract.owner", "legal.review", "registration.approval", "primary.approver" }.Select(k => $"{k} = {set.Singular(k)}")) + ".";
        }
        catch (DomainException) { return "the pack's wording could not be read; the platform's neutral wording applies."; }
    }

    public override Task<StageResult> ExecuteAsync(StageContext c)
    {
        var id = c.Required("packId", "[[solution.pack|sentence]]");
        var pack = _catalogue.Find(id) ?? throw new DomainException("unknown_pack", "That [[solution.pack|lower]] is not in the catalogue.");
        if (c.Doc.OrganisationId is not null && c.Doc.PackId != id) throw new DomainException("pack_locked", "The [[organisation|lower]] already exists; change its configuration through onboarding instead.");
        c.Doc.PackId = pack.PackId;
        c.Remember("packId", pack.PackId);
        return Task.FromResult(StageResult.Ok($"{pack.Name} selected."));
    }
}

internal sealed class IdentityStage : SetupStageBase
{
    public override string Id => "identity";
    public override string Title => "Identity provider";
    public override string Summary => "Configure how people sign in. Client secrets are referenced, never typed here.";
    public override IReadOnlyList<string> DependsOn => ["tenant"];
    public override bool Amendable => true;

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
    {
        var kinds = new List<FieldOption> { new("Entra", "Microsoft Entra ID"), new("Oidc", "Other OpenID Connect provider") };
        if (env.HostMode != EcpMode.Production && env.DemoIdentityEnabled) kinds.Add(new("Demo", "Demonstration sign-in (no credentials; Demo and Test only)"));
        return
        [
            new("kind", "Provider type", FieldKind.Select, true, Options: kinds, Value: kinds[0].Value),
            new("key", "Provider key", FieldKind.Text, true, "Lower-case letters, digits and hyphens, for example 'entra'.", Value: "entra"),
            new("displayName", "Name shown on the sign-in page", FieldKind.Text, true, Value: "Company sign-in"),
            new("entraTenantId", "Entra directory (tenant) id", FieldKind.Text, false, "Microsoft Entra only. A GUID; 'common' and 'organizations' are refused."),
            new("authority", "Authority (issuer URL)", FieldKind.Text, false, "Generic OpenID Connect only. Must be HTTPS."),
            new("clientId", "Client id", FieldKind.Text, false),
            new("clientSecretRef", "Client secret reference", FieldKind.SecretReference, false, "For example env:ECP_OIDC_SECRET or keyvault:ecp-oidc. Never paste the secret itself."),
            new("groupsClaim", "Groups claim", FieldKind.Text, false, "The claim that carries identity groups, normally 'groups'.", Value: "groups"),
        ];
    }

    public override IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env)
        => ["After saving, use 'Test sign-in' on this step. The first administrator is linked to the provider by their first successful sign-in.",
            "The provider is registered twice: once for the tenant and once for installation administrators. Register both callback addresses with the provider: /auth/callback/oidc-<tenant id without hyphens>-<key> (shown after saving) and the installation equivalent."];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        if (!Enum.TryParse<IdentityProviderKind>(c.Required("kind", "Provider type"), out var kind)) throw new DomainException("kind", "Choose one of the listed provider types.");
        if (kind == IdentityProviderKind.Demo && (c.Env.HostMode == EcpMode.Production || !c.Env.DemoIdentityEnabled))
            return StageResult.Fail("Demonstration sign-in is refused here (it is available only in Demo and Test modes when enabled in host configuration).");
        var settings = kind == IdentityProviderKind.Demo
            ? new IdentityProviderSettings(SetupDemo.ProviderKey, kind, "Demonstration sign-in", null, null, null, null)
            : new IdentityProviderSettings(c.Required("key", "Provider key"), kind, c.Required("displayName", "Name"), c.Value("authority"), c.Value("clientId"),
                c.Value("clientSecretRef"), c.Value("entraTenantId"), GroupsClaim: c.Value("groupsClaim") ?? "groups");
        using var tenantScope = c.UseTenant();
        var registry = c.Get<IProviderRegistry>();
        var issues = await registry.SaveAsync(settings, c.Ct);
        var blocking = issues.Where(i => i.Blocking).Select(i => i.Message).ToList();
        if (blocking.Count > 0) return StageResult.Fail("The provider settings are not valid.", [.. blocking]);
        if (kind == IdentityProviderKind.Demo) await registry.RecordTestSignInAsync(settings.Key, c.Ct);
        // Installation administrators live in the installation scope and sign in through the same provider (registered there too, with its own callback address).
        await using (var installationScope = c.NewScope())
        {
            using var systemTenant = installationScope.ServiceProvider.GetRequiredService<TenantContext>().Use(SystemTenant.Id);
            var installationRegistry = installationScope.ServiceProvider.GetRequiredService<IProviderRegistry>();
            var installation = await installationRegistry.SaveAsync(settings, c.Ct);
            if (kind == IdentityProviderKind.Demo) await installationRegistry.RecordTestSignInAsync(settings.Key, c.Ct);
            if (installation.Any(i => i.Blocking)) return StageResult.Fail("The provider could not be registered for installation administrators.", [.. installation.Where(i => i.Blocking).Select(i => i.Message)]);
        }
        c.Remember("kind", kind.ToString()); c.Remember("key", settings.Key); c.Remember("displayName", settings.DisplayName);
        c.Remember("entraTenantId", settings.EntraTenantId); c.Remember("authority", settings.Authority); c.Remember("clientId", settings.ClientId);
        c.Remember("clientSecretRef", settings.ClientSecretRef); c.Remember("groupsClaim", settings.GroupsClaim);
        c.Record.Data["providerKey"] = settings.Key;
        return StageResult.Ok($"Identity provider '{settings.DisplayName}' saved.", [.. issues.Where(i => !i.Blocking).Select(i => i.Message)]);
    }
}

internal sealed class AdminStage : SetupStageBase
{
    public override string Id => "admin";
    public override string Title => "Initial administrator";
    public override string Summary => "Name the person who will administer the tenant. They sign in through the provider you configured.";
    public override IReadOnlyList<string> DependsOn => ["identity"];
    public override bool Amendable => false;

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
        =>
        [
            new("displayName", "Full name", FieldKind.Text, true),
            new("email", "Email address", FieldKind.Text, true, "Must match the address your identity provider asserts for this person. Accounts link by provider and subject, not by email alone."),
            new("installationAdmin", "Also make this person an installation administrator", FieldKind.Checkbox, false,
                "Only installation administrators can reopen setup later (permission setup.run). Recommended.", Value: "true"),
        ];

    public override IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env)
        => ["The administrator is invited and becomes active at their first verified sign-in. With the demonstration provider they are active immediately.",
            "A person who is both a tenant administrator and an installation administrator has two independent accounts, one in each scope."];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var name = c.Required("displayName", "Full name");
        var email = c.Required("email", "Email address");
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _)) throw new DomainException("email", "Enter a valid email address.");
        using var tenantScope = c.UseTenant();
        var users = c.Get<IUserService>();
        var assignments = c.Get<IRoleAssignmentService>();
        var existing = await users.FindByEmailAsync(email, c.Ct);
        var demo = c.Doc.Get("identity", "in.kind") == nameof(IdentityProviderKind.Demo);
        var userId = existing?.Id ?? (demo
            ? await users.CreateActiveTrustedAsync(name, email, SetupDemo.ProviderKey, SetupDemo.Issuer, email, "setup", c.Ct)
            : await users.InviteAsync(name, email, c.Ct));
        var held = (await assignments.ListForUserAsync(userId, includeEnded: false, c.Ct)).Any(a => a.RoleCode == RoleCodes.TenantAdministrator && a.IsActive);
        if (!held)
        {
            var scale = await c.Get<IClassificationScaleProvider>().GetAsync(c.Ct);
            await assignments.GrantTrustedAsync(new GrantRequest(userId, RoleCodes.TenantAdministrator, null, ScopeSet.Tenant(scale.Levels[^1]),
                Reason: "Initial administrator named during setup", ExplicitHighCeiling: true), AssignmentSource.Setup, "setup:" + c.Actor.Label, null, c.Ct);
        }
        if (c.Flag("installationAdmin"))
        {
            await using var installationServices = c.NewScope();
            using var installationScope = installationServices.ServiceProvider.GetRequiredService<TenantContext>().Use(SystemTenant.Id);
            var sysUsers = installationServices.ServiceProvider.GetRequiredService<IUserService>();
            var sysAssignments = installationServices.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
            var sysExisting = await sysUsers.FindByEmailAsync(email, c.Ct);
            var sysId = sysExisting?.Id ?? (demo
                ? await sysUsers.CreateActiveTrustedAsync(name, email, SetupDemo.ProviderKey, SetupDemo.Issuer, email, "setup", c.Ct)
                : await sysUsers.InviteAsync(name, email, c.Ct));
            if (!(await sysAssignments.ListForUserAsync(sysId, includeEnded: false, c.Ct)).Any(a => a.RoleCode == RoleCodes.SystemAdministrator && a.IsActive))
                await sysAssignments.GrantTrustedAsync(new GrantRequest(sysId, RoleCodes.SystemAdministrator, null, ScopeSet.Installation("Public"),
                    Reason: "Installation administrator named during setup"), AssignmentSource.Setup, "setup:" + c.Actor.Label, null, c.Ct);
            c.Record.Data["installationAdmin"] = "true";
        }
        c.Remember("installationAdmin", c.Flag("installationAdmin") ? "true" : "false");
        var names = (c.Record.Data.GetValueOrDefault("names") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!names.Contains(name)) names.Add(name);
        c.Record.Data["names"] = string.Join('|', names);
        c.Record.Data["userId"] = userId.ToString();
        c.Remember("displayName", name);
        // The email address is personal data: it is held by the identity module, not repeated in setup state.
        return StageResult.Ok(existing is null ? $"{name} was set up as tenant administrator." : $"{name} already existed; the administrator role is in place.");
    }
}

internal sealed class OrganisationStage : SetupStageBase
{
    public override string Id => "organisation";
    public override string Title => "Organisation";
    public override string Summary => "Create your [[organisation|lower]] with its locale, time zone and currency.";
    public override IReadOnlyList<string> DependsOn => ["pack", "admin"];
    private readonly ISolutionPackCatalogue _catalogue;
    private readonly LocalisationOptions _localisation;
    public OrganisationStage(ISolutionPackCatalogue catalogue, LocalisationOptions localisation) { _catalogue = catalogue; _localisation = localisation; }

    private RegionalProfileDocument? PackRegional(string? packId)
    {
        if (packId is null || _catalogue.RegionalOf(packId) is not { } json) return null;
        try { return RegionalProfileDocument.Parse(json); } catch (DomainException) { return null; }
    }

    /// <summary>The identifier profiles the installed packs carry, by key, for the organisation to choose from. Nothing is chosen for it.</summary>
    private IReadOnlyList<IdentifierTypeProfile> AvailableIdentifierProfiles()
        => _catalogue.ListEntries().Select(e => PackRegional(e.PackKey)).Where(r => r?.Identifiers is not null).SelectMany(r => r!.Identifiers!).GroupBy(p => p.Key, StringComparer.Ordinal).Select(g => g.First()).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
    {
        // The pack proposes; the organisation decides. A pack that proposes nothing (the Blank pack) leaves the fields empty, because no country, locale, currency or time zone is a safe default for everybody.
        var d = doc.PackId is null ? null : _catalogue.Find(doc.PackId)?.Defaults;
        var regional = PackRegional(doc.PackId);
        var profiles = AvailableIdentifierProfiles();
        return
        [
            new("organisationName", "[[organisation]] name", FieldKind.Text, true, Value: doc.TenantName),
            new("country", "[[regional.country]]", FieldKind.Select, false, "The country or region the organisation works in. It is a label for people and for the identifier and address profiles; it decides no format by itself.",
                [new FieldOption("", "(not set)"), .. RegionalCatalogue.Countries.Select(c => new FieldOption(c.Key, c.Value + " (" + c.Key + ")"))], regional?.Country ?? ""),
            new("culture", "[[regional.locale]]", FieldKind.Text, true, "A BCP 47 language-and-region tag. It controls date, number and currency formats; it is not the language of the page text.", Value: regional?.Locale ?? d?.Culture),
            new("timeZone", "[[regional.time.zone]]", FieldKind.Text, true, "An IANA time zone identifier (Area/Location). Times are stored in UTC and shown in this zone.", Value: regional?.TimeZone ?? d?.TimeZoneId),
            new("currency", "Default currency", FieldKind.Text, true, "A three-letter ISO 4217 currency code. Every amount keeps its own currency; this is the default offered when an amount is entered.", Value: regional?.Currency ?? d?.CurrencyCode),
            new("identifierProfile", "[[supplier.tax.id]] type", FieldKind.Select, false, "Which kind of tax or registration identifier the organisation records for its counterparties. Only the structure of a value is checked; nothing is looked up in a registry.",
                [new FieldOption("", "(none for now)"), .. profiles.Select(p => new FieldOption(p.Key, p.LabelOr("Tax identifier") + (p.Country is null ? "" : " (" + p.Country + ")")))], regional?.Identifiers?.OrderBy(p => p.Order).FirstOrDefault()?.Key ?? ""),
            new("referencePrefix", "[[contract.reference|sentence]] prefix", FieldKind.Text, true, "Letters and digits, for example CON. References are allocated as PREFIX-0001.", Value: d?.ReferencePrefix ?? "CON"),
        ];
    }

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var name = c.Required("organisationName", "[[organisation]] name");
        var culture = c.Required("culture", "[[regional.locale]]");
        var zone = c.Required("timeZone", "[[regional.time.zone]]");
        var currency = c.Required("currency", "Default currency").ToUpperInvariant();
        var prefix = c.Required("referencePrefix", "[[contract.reference|sentence]] prefix").ToUpperInvariant();
        var country = c.Value("country");
        var identifierKey = c.Value("identifierProfile");
        if (!RegionalCatalogue.IsLocale(culture)) throw new DomainException("culture", "That locale is not recognised.");
        if (!TimeZones.TryFind(zone, out _)) throw new DomainException("time_zone", "That time zone is not recognised.");
        if (!RegionalCatalogue.IsCurrency(currency)) throw new DomainException("currency", "That currency is not an ISO 4217 code the platform offers.");
        if (!string.IsNullOrWhiteSpace(country) && !RegionalCatalogue.IsCountry(country)) throw new DomainException("country", "That country or region is not recognised.");
        var packId = c.Doc.PackId ?? throw new DomainException("pack", "Choose [[solution.pack|lower|a]] first.");

        using var tenantScope = c.UseTenant();
        var structure = c.Get<IOrganisationStructureService>();
        var orgId = c.Doc.OrganisationId;
        if (orgId is null)
        {
            var existing = (await structure.ListOrganisationsAsync(c.Ct)).FirstOrDefault(o => o.Name == name);
            orgId = existing?.Id ?? await c.Get<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest(name, zone, culture, currency, prefix, packId), c.Ct);
            c.Doc.OrganisationId = orgId;
            await c.AuditAsync("setup.organisation.created", "Organisation", orgId.Value.ToString());
        }
        // The organisation's regional profile is published at once, as a versioned release that records what was chosen and which pack version proposed it; the pack changes nothing afterwards.
        var packRegional = PackRegional(packId);
        var chosen = string.IsNullOrWhiteSpace(identifierKey) ? null : AvailableIdentifierProfiles().FirstOrDefault(p => p.Key == identifierKey);
        var version = _catalogue.Find(packId)?.Version;
        var profile = new RegionalProfileDocument(RegionalProfileDocument.Overrides, version is null ? null : new RegionalSource(packId, version), string.IsNullOrWhiteSpace(country) ? null : country, culture, [culture], currency, [currency], zone, null,
            string.IsNullOrWhiteSpace(country) ? null : country, chosen is null ? [] : [chosen with { Order = 1 }], packRegional?.Address);
        var configuration = c.Get<IConfigurationService>();
        if (await configuration.LatestReleaseAsync(RegionalKinds.Name, orgId.Value, c.Ct) is null)
            await c.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(RegionalKinds.Name, orgId.Value, profile.ToJson(), "setup:" + c.Actor.Label, "Chosen during setup", c.Ct);
        // A Tenant Administrator's configuration rights cover tenant-level definitions only, so the person named as initial administrator also becomes the Organisation
        // Administrator of this first organisation (a Tenant Administrator could grant themselves that role anyway). This is what lets them run organisation onboarding.
        if (Guid.TryParse(c.Doc.Get("admin", "userId"), out var adminId))
        {
            var assignments = c.Get<IRoleAssignmentService>();
            var organisationId = c.Doc.OrganisationId!.Value;
            var held = (await assignments.ListForUserAsync(adminId, includeEnded: false, c.Ct))
                .Any(a => a.IsActive && a.RoleCode == RoleCodes.OrganisationAdministrator && a.Scope.OrganisationIds.Contains(organisationId));
            if (!held)
            {
                var scale = await c.Get<IClassificationScaleProvider>().GetAsync(c.Ct);
                await assignments.GrantTrustedAsync(new GrantRequest(adminId, RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([organisationId], scale.DefaultLevel),
                    Reason: "Initial administrator of the first [[organisation|lower]], named during setup"), AssignmentSource.Setup, "setup:" + c.Actor.Label, null, c.Ct);
            }
        }
        var latest = await configuration.LatestReleaseAsync("SolutionPack", Guid.Empty, c.Ct);
        if (latest is null || !latest.ContentJson.Contains($"\"{packId}\"", StringComparison.Ordinal))
            await c.Get<IConfigurationBootstrap>().DraftValidatePublishAsync("SolutionPack", Guid.Empty, JsonSerializer.Serialize(new { packId }), "setup:" + c.Actor.Label, "Selected during setup", c.Ct);
        foreach (var (k, v) in new Dictionary<string, string> { ["organisationName"] = name, ["culture"] = culture, ["timeZone"] = zone, ["currency"] = currency, ["referencePrefix"] = prefix, ["country"] = country ?? "", ["identifierProfile"] = identifierKey ?? "" })
            c.Remember(k, v);
        return StageResult.Ok($"[[organisation]] '{name}' is ready.");
    }
}

internal sealed class BrandingStage : SetupStageBase
{
    public override string Id => "branding";
    public override string Title => "Branding";
    public override string Summary => "Set the product name, accent colour and footer shown in the application.";
    public override IReadOnlyList<string> DependsOn => ["organisation"];
    public override bool Amendable => true;

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
        =>
        [
            new("productName", "Product name", FieldKind.Text, true, Value: Branding.Default.ProductName),
            new("accentColour", "Accent colour", FieldKind.Text, true, "Six-digit hex colour. It must have enough contrast with white text (WCAG 2.2 AA).", Value: Branding.Default.AccentColour),
            new("footerText", "Footer text", FieldKind.Text, false),
        ];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var branding = new Branding(c.Required("productName", "Product name"), c.Required("accentColour", "Accent colour"), c.Value("footerText") ?? "", new Dictionary<string, string>());
        var problems = branding.Check();
        if (problems.Count > 0) return StageResult.Fail("The branding is not valid.", [.. problems.Select(p => p.Message)]);
        using var tenantScope = c.UseTenant();
        var latest = await c.Get<IConfigurationService>().LatestReleaseAsync("Branding", Guid.Empty, c.Ct);
        var unchanged = latest is not null && Branding.Parse(latest.ContentJson) is var cur && cur.ToJson() == branding.ToJson();
        if (!unchanged)
            await c.Get<IConfigurationBootstrap>().DraftValidatePublishAsync("Branding", Guid.Empty, branding.ToJson(), "setup:" + c.Actor.Label, "Set during setup", c.Ct);
        c.Remember("productName", branding.ProductName); c.Remember("accentColour", branding.AccentColour); c.Remember("footerText", branding.FooterText);
        return StageResult.Ok(unchanged ? "Branding unchanged." : "Branding published.");
    }
}

internal sealed class ClassificationStage : SetupStageBase
{
    public override string Id => "classification";
    public override string Title => "Classification levels";
    public override string Summary => "Set the ordered information-classification levels. Access never exceeds a person's classification ceiling.";
    public override IReadOnlyList<string> DependsOn => ["organisation"];
    public override bool Amendable => true;
    private readonly ISolutionPackCatalogue _catalogue;
    public ClassificationStage(ISolutionPackCatalogue catalogue) => _catalogue = catalogue;

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
    {
        var d = doc.PackId is null ? null : _catalogue.Find(doc.PackId)?.Defaults.Classification;
        return
        [
            new("levels", "Levels, lowest first", FieldKind.TextArea, true, "One level per line, from the least to the most sensitive.", Value: string.Join('\n', d?.Levels ?? ClassificationScale.Default.Levels)),
            new("defaultLevel", "Default level for new records", FieldKind.Text, true, Value: d?.DefaultLevel ?? ClassificationScale.Default.DefaultLevel),
            new("privilegedLevels", "Privileged levels", FieldKind.TextArea, false, "Levels whose records need an explicit privileged-access grant as well as a high enough ceiling (one per line; leave empty for none).",
                Value: string.Join('\n', d?.PrivilegedLevels ?? ClassificationScale.Default.PrivilegedLevels)),
        ];
    }

    public override IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env)
        => ["The final level names are an open decision for the owner (Q-06); the placeholder scale can be replaced here at any time."];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var levels = c.Required("levels", "Levels").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var privileged = (c.Value("privilegedLevels") ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var policy = new ClassificationPolicyDocument(levels, c.Required("defaultLevel", "Default level"), privileged);
        _ = new ClassificationScale(policy.Levels, policy.DefaultLevel, policy.PrivilegedLevels); // validates
        using var tenantScope = c.UseTenant();
        var latest = await c.Get<IConfigurationService>().LatestReleaseAsync("ClassificationPolicy", Guid.Empty, c.Ct);
        var unchanged = latest is not null && ClassificationPolicyDocument.Parse(latest.ContentJson) is var cur && cur.Levels.SequenceEqual(policy.Levels) && cur.DefaultLevel == policy.DefaultLevel && cur.PrivilegedLevels.SequenceEqual(policy.PrivilegedLevels);
        if (!unchanged)
            await c.Get<IConfigurationBootstrap>().DraftValidatePublishAsync("ClassificationPolicy", Guid.Empty, policy.ToJson(), "setup:" + c.Actor.Label, "Set during setup", c.Ct);
        c.Remember("levels", string.Join('\n', levels)); c.Remember("defaultLevel", policy.DefaultLevel); c.Remember("privilegedLevels", string.Join('\n', privileged));
        return StageResult.Ok(unchanged ? "Classification levels unchanged." : $"{levels.Count} classification levels published.");
    }
}

internal sealed class NotificationsStage : SetupStageBase
{
    public override string Id => "notifications";
    public override string Title => "Email [[notification|plural|lower]]";
    public override string Summary => "Choose how the platform sends email. SMTP credentials are referenced, never typed here.";
    public override IReadOnlyList<string> DependsOn => ["tenant"];
    public override bool Amendable => true;

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
    {
        var providers = new List<FieldOption> { new("Smtp", "SMTP server") };
        if (env.HostMode != EcpMode.Production) { providers.Add(new("Mock", "Development capture (simulated; delivers nothing)")); providers.Add(new("Disabled", "Disabled (no email is sent)")); }
        return
        [
            new("provider", "Provider", FieldKind.Select, true, Options: providers, Value: providers[0].Value),
            new("from", "From address", FieldKind.Text, false),
            new("host", "SMTP host", FieldKind.Text, false, "Host name only, for example smtp.example.com."),
            new("port", "SMTP port", FieldKind.Number, false, Value: "587"),
            new("useTls", "Use TLS", FieldKind.Checkbox, false, Value: "true"),
            new("userRef", "SMTP user reference", FieldKind.SecretReference, false, "For example env:ECP_SMTP_USER."),
            new("passwordRef", "SMTP password reference", FieldKind.SecretReference, false, "For example keyvault:ecp-smtp-password."),
            new("testRecipient", "Send a test message to", FieldKind.Text, false, "Required for SMTP and Mock; the test proves the provider works."),
        ];
    }

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        if (!Enum.TryParse<EmailProviderKind>(c.Required("provider", "Provider"), out var kind)) throw new DomainException("provider", "Choose one of the listed providers.");
        _ = int.TryParse(c.Value("port"), out var port);
        var settings = new EmailSettings(kind, c.Value("from"), c.Value("host"), port == 0 ? 587 : port, c.Flag("useTls"), c.Value("userRef"), c.Value("passwordRef"));
        var providerKey = kind switch { EmailProviderKind.Smtp => EmailProviderKeys.Smtp, EmailProviderKind.Mock => EmailProviderKeys.DevCapture, _ => EmailProviderKeys.Disabled };
        var issues = EmailRules.Validate(c.Get<IEmailSenderFactory>(), settings, providerKey, c.Env.HostMode);
        if (issues.Count > 0) return StageResult.Fail("The email settings are not valid.", [.. issues.Select(i => i.Message)]);
        var email = c.Get<IEmailConfigurationService>();
        var by = new OperatorIdentity("setup:" + c.Actor.Label, c.Actor.Label);
        var details = new List<string>();
        await email.SaveAsync(providerKey, settings, by, c.Ct);
        StageResult result;
        if (kind != EmailProviderKind.Disabled)
        {
            var to = c.Required("testRecipient", "Test recipient");
            if (!System.Net.Mail.MailAddress.TryCreate(to, out _)) throw new DomainException("recipient", "Enter a valid test recipient address.");
            // The test really connects to the provider and delivers a synthetic message; saving settings alone proves nothing (G-12).
            var test = await email.TestAsync(to, by, c.Ct);
            if (!test.Succeeded)
            {
                var (code, why) = test.ConnectivityOutcome == "Failed" ? (test.ConnectivityCode, test.ConnectivityDetail) : (test.DeliveryCode, test.DeliveryDetail);
                return StageResult.Fail("The email test did not pass, so email is not ready. " + why, [$"Diagnostic code {code}; reference {test.CorrelationId}.", "Correct the settings and run the step again. Nothing was sent to any real person."]);
            }
            details.Add($"Test reference {test.CorrelationId}.");
            result = test.Simulated
                ? StageResult.Ok("The simulated provider accepted the test message. Nothing was delivered to a real mailbox, so email is Not Verified until a real provider is configured and tested.", [.. details])
                : StageResult.Ok("The mail server was reached and accepted the test message.", [.. details]);
        }
        else
        {
            details.Add("Email is disabled: no [[notification|plural|lower]] will be delivered.");
            result = StageResult.Ok("Email [[notification|plural|lower]] are disabled.", [.. details]);
        }
        foreach (var (k, v) in new Dictionary<string, string?> { ["provider"] = kind.ToString(), ["from"] = settings.From, ["host"] = settings.Host, ["port"] = settings.Port.ToString(),
            ["useTls"] = settings.UseTls ? "true" : "false", ["userRef"] = settings.UserRef, ["passwordRef"] = settings.PasswordRef })
            c.Remember(k, v);
        return result;
    }
}

internal sealed class DemoDataStage : SetupStageBase
{
    public override string Id => "demo";
    public override string Title => "Demonstration data";
    public override string Summary => "Optionally add the synthetic demonstration [[organisation|plural|lower]], users and [[contract|plural|lower]]. Never available in Production.";
    public override string ActionLabel => "Add demonstration data";
    public override IReadOnlyList<string> DependsOn => ["organisation"];
    public override bool Optional => true;
    public override bool AppliesTo(SetupEnvironment env) => env.HostMode != EcpMode.Production;
    public override IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env) => ["Adds synthetic tenants alongside yours. Nothing real is included. Skip this step to start empty."];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        if (c.Env.HostMode == EcpMode.Production) return StageResult.Fail("Demonstration data is refused in Production.");
        var applier = c.Services.GetService<IDemoDataApplier>();
        if (applier is null) return StageResult.Fail("Demonstration data is not available in this host.");
        await applier.ApplyAsync(c.Ct);
        c.Doc.DemoDataApplied = true;
        await c.AuditAsync("setup.demo_data.applied", "Installation", "demo");
        return StageResult.Ok("Synthetic demonstration data was added.");
    }
}

internal sealed class ReadinessStage : SetupStageBase
{
    public override string Id => "readiness";
    public override string Title => "Readiness review";
    public override string Summary => "Run the readiness checks. Blocking failures must be fixed; warnings need a recorded reason to continue.";
    public override string ActionLabel => "Run checks";
    public override IReadOnlyList<string> DependsOn => ["storage", "organisation", "branding", "classification", "notifications"];
    public override bool Amendable => true;

    public override IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered)
        => [.. doc.LastReadiness.Where(r => r.Status == "Fail" && r.Severity == nameof(CheckLevel.Warning))
            .Select(r => new StageField("ack." + r.Id, $"Acknowledge {r.Id}: {r.Description}", FieldKind.Text, false, r.Message))];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var results = await c.RunChecks(c.Ct);
        c.Doc.LastReadiness = [.. results];
        var blocking = results.Where(r => r.Status == "Fail" && r.Severity == nameof(CheckLevel.Blocking)).ToList();
        if (blocking.Count > 0) return StageResult.Fail($"{blocking.Count} blocking check(s) failed.", [.. blocking.Select(b => $"{b.Id}: {b.Message}")]);
        var missing = new List<string>();
        foreach (var w in results.Where(r => r.Status == "Fail" && r.Severity == nameof(CheckLevel.Warning)))
        {
            var reason = c.Value("ack." + w.Id);
            if (reason is null) { missing.Add($"{w.Id}: {w.Message}"); continue; }
            c.Doc.Acknowledgements.RemoveAll(a => a.CheckId == w.Id);
            c.Doc.Acknowledgements.Add(new Acknowledgement(w.Id, reason.Length > 300 ? reason[..300] : reason, c.Actor.Label, c.Get<IClock>().UtcNow));
        }
        if (missing.Count > 0) return StageResult.Fail("Acknowledge each warning with a reason, then run the checks again.", [.. missing]);
        return StageResult.Ok(results.Any(r => r.Status == "Fail") ? "All blocking checks passed; warnings were acknowledged." : "All readiness checks passed.");
    }
}

internal sealed class CompleteStage : SetupStageBase
{
    public override string Id => "complete";
    public override string Title => "Finish setup";
    public override string Summary => "Finish installation. Setup pages then close; a signed-in administrator who re-authenticates can reopen the configuration.";
    public override string ActionLabel => "Finish setup";
    public override IReadOnlyList<string> DependsOn => ["readiness"];

    public override async Task<StageResult> ExecuteAsync(StageContext c)
    {
        var results = await c.RunChecks(c.Ct);
        c.Doc.LastReadiness = [.. results];
        var open = results.Where(r => r.Status == "Fail" && (r.Severity == nameof(CheckLevel.Blocking) || !c.Doc.Acknowledgements.Any(a => a.CheckId == r.Id))).ToList();
        if (open.Count > 0) return StageResult.Fail("Setup cannot finish while checks are unresolved. Return to the readiness review.", [.. open.Select(o => $"{o.Id}: {o.Message}")]);
        await c.AuditAsync("setup.completed", "Installation", "setup", System.Text.Json.JsonSerializer.Serialize(new { mode = c.Env.HostMode.ToString(), pack = c.Doc.PackId }));
        c.Doc.CompletedUtc = c.Get<IClock>().UtcNow; c.Doc.CompletedBy = c.Actor.Label;
        c.Doc.TokenHash = null; c.Doc.TokenExpiresUtc = null; c.Doc.SessionHash = null; c.Doc.SessionExpiresUtc = null;
        return StageResult.Ok("Setup is complete.");
    }
}
