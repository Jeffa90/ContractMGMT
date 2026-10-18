using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.Setup.Onboarding;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Setup.Organisations;

/// <summary>What a Tenant Administrator enters to create another organisation. Nothing here is a secret; everything is shown back before anything is created.</summary>
public sealed record NewOrganisationInput(
    string Name, string LegalName, string Key, string? Country, string Culture, string TimeZone, string Currency,
    string ReferenceSeparator = "-", int ReferenceDigits = 6, string? PackKey = null, Guid? InitialAdministratorId = null, string? StructureCsv = null);

public sealed record CreationProblem(string Field, string Message, bool IsError = true);

public sealed record OrganisationCreationPlan(IReadOnlyList<CreationProblem> Problems, IReadOnlyList<string> Actions, bool WidenRequired, string? PackVersion, string? SampleReference)
{
    public bool CanCreate => Problems.All(p => !p.IsError);
}

public sealed record NewOrganisationResult(Guid OrganisationId, IReadOnlyList<string> Done, IReadOnlyList<string> NextSteps);

public sealed record TenancyView(Guid TenantId, string TenantName, string Mode, bool IsSingleOrganisation, int Organisations);

public enum ChecklistState { Done, InProgress, NotStarted, Unknown }

public sealed record ChecklistItem(string Key, string Title, ChecklistState State, string Detail, string? Link, string? Owner);

public sealed record OrganisationOverview(Guid Id, string Name, string Key, string? PackKey, string TimeZone, string Culture, string Currency, int Done, int Total);

public sealed record SetupChecklist(OrganisationSummary Organisation, IReadOnlyList<ChecklistItem> Items, IReadOnlyList<OrganisationUsageLine> Usage, bool CanReset)
{
    public int Done => Items.Count(i => i.State == ChecklistState.Done);
}

public sealed record ResetOutcome(int DraftsDiscarded, int StructureItemsEnded, IReadOnlyList<string> Notes);

/// <summary>
/// Creating an organisation is the work of a Tenant Administrator (<c>organisations.manage</c>). It records the organisation and its structure, names its first administrator and assigns a pack, which creates
/// <b>drafts</b> only: nothing the organisation's own administrators have yet to review is published. A Tenant Administrator holds no organisation-level configuration rights, so the configuration that follows is
/// drafted, submitted and published by the people the organisation names, through the ordinary lifecycle.
/// </summary>
public interface IOrganisationCreation
{
    Task<TenancyView> TenancyAsync(CancellationToken ct = default);
    Task WidenTenancyAsync(string reason, CancellationToken ct = default);
    Task<OrganisationCreationPlan> PreviewAsync(NewOrganisationInput input, CancellationToken ct = default);
    Task<NewOrganisationResult> CreateAsync(NewOrganisationInput input, CancellationToken ct = default);
    Task<IReadOnlyList<OrganisationOverview>> ListAsync(CancellationToken ct = default);
    Task<SetupChecklist> ChecklistAsync(Guid organisationId, CancellationToken ct = default);
    Task<ResetOutcome> ResetSetupAsync(Guid organisationId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<PersonName>> CandidateAdministratorsAsync(CancellationToken ct = default);
}

internal sealed class OrganisationCreation(
    IAccessService access, ITenantContext tenant, ITenantDirectory directory, ITenantProvisioningService provisioning, IOrganisationStructureService structure, IRoleAssignmentService assignments,
    IClassificationScaleProvider scales, ISolutionPackCatalogue catalogue, IPackAssignments packAssignments, IConfigurationReleases releases, IConfigurationBootstrap bootstrap,
    IEnumerable<IOrganisationUsageSource> usageSources, IPeopleDirectory people, IApproverGroupService groups, IUnitOfWork uow, IAuditWriter audit, CurrentUser currentUser, ActorContext actor, IClock clock, ReauthenticationContext reauth, ConfigurationOptions options) : IOrganisationCreation
{
    private const string Permission = "organisations.manage";
    private string Who() => currentUser.UserId?.ToString() ?? actor.ActorId ?? "system";

    /// <summary>Widening, creating and resetting change the shape of the tenant, so the person must have signed in recently (the host sets that flag in every environment that has a sign-in).</summary>
    private void StepUp()
    {
        if (options.RequireRecentSignInForRollback && currentUser.UserId is not null) reauth.Require(clock.UtcNow, options.RecentSignInWindow);
    }

    private async Task RequireAsync(CancellationToken ct)
        => await access.RequireAsync(Permission, new ResourceDescriptor("Organisation", tenant.RequireTenantId(), IsTenantLevelDefinition: true, Dimensions: ScopeDimensions.None), ct);

    public async Task<TenancyView> TenancyAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var info = await directory.FindAsync(tenant.RequireTenantId(), ct) ?? throw new ResourceNotFoundException();
        var count = (await structure.ListOrganisationsAsync(ct)).Count;
        return new TenancyView(info.Id, info.Name, info.TenancyMode.ToString(), info.TenancyMode == TenancyMode.SingleOrganisation, count);
    }

    public async Task WidenTenancyAsync(string reason, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        StepUp();
        await provisioning.WidenTenancyAsync(TenancyMode.MultiOrganisation, reason, ct);
    }

    public async Task<IReadOnlyList<PersonName>> CandidateAdministratorsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var managed = await people.ManageableUsersAsync(ct);
        return managed.Where(p => p.Status == UserStatus.Active).Select(p => new PersonName(p.Id, p.DisplayName + " (" + p.Email + ")")).ToList();
    }

    // ---- preview ------------------------------------------------------------------------------------------------------------------------

    private (PackCatalogueEntry? Entry, string? Problem) ChosenPack(string? packKey)
    {
        if (string.IsNullOrWhiteSpace(packKey)) return (null, null);
        var entry = catalogue.ListEntries().Where(e => e.PackKey == packKey && e.IsUsable && !e.Retired).OrderByDescending(e => e.Version, new VersionComparer()).FirstOrDefault();
        return entry is null ? (null, "That pack is not available here. It may not be installed, may not be trusted in this environment, or may be retired.") : (entry, null);
    }

    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
            => Version.TryParse(x?.Split('-', '+')[0], out var a) && Version.TryParse(y?.Split('-', '+')[0], out var b) ? a.CompareTo(b) : string.CompareOrdinal(x, y);
    }

    public async Task<OrganisationCreationPlan> PreviewAsync(NewOrganisationInput i, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return await PlanAsync(i, ct);
    }

    private async Task<OrganisationCreationPlan> PlanAsync(NewOrganisationInput i, CancellationToken ct)
    {
        var problems = new List<CreationProblem>();
        void Error(string field, string message) => problems.Add(new CreationProblem(field, message));
        var actions = new List<string>();

        var name = (i.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > 200) Error("Name", "Enter the organisation's display name (up to 200 characters).");
        if (string.IsNullOrWhiteSpace(i.LegalName) || i.LegalName.Trim().Length > 200) Error("LegalName", "Enter the organisation's registered legal name (up to 200 characters). It becomes the first legal entity.");

        var existing = await structure.ListOrganisationsAsync(ct);
        if (existing.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))) Error("Name", "An organisation with that name already exists in this tenant. Names must be unique.");

        // Each part of the reference rule is checked on its own so that one wrong part does not hide another.
        string? Check(Func<ReferenceNumberingRule> make)
        {
            try { make(); return null; } catch (DomainException ex) { return ex.Message; }
        }
        var separator = i.ReferenceSeparator ?? "-";
        if (Check(() => ReferenceNumberingRule.Create(i.Key, "-", 6)) is { } keyProblem) Error("Key", keyProblem);
        if (Check(() => ReferenceNumberingRule.Create("AB", separator, 6)) is { } separatorProblem) Error("ReferenceSeparator", separatorProblem);
        if (Check(() => ReferenceNumberingRule.Create("AB", "-", i.ReferenceDigits)) is { } digitsProblem) Error("ReferenceDigits", digitsProblem);
        ReferenceNumberingRule? rule = null;
        try { rule = ReferenceNumberingRule.Create(i.Key, separator, i.ReferenceDigits); } catch (DomainException) { /* reported above, field by field */ }
        if (rule is not null && existing.Any(o => string.Equals(o.ReferencePrefix, rule.Prefix, StringComparison.OrdinalIgnoreCase)))
            Error("Key", $"The key '{rule.Prefix}' is already used by another organisation in this tenant. Keys must be unique because they begin every reference number.");

        if (!string.IsNullOrWhiteSpace(i.Country) && !RegionalCatalogue.IsCountry(i.Country.Trim().ToUpperInvariant())) Error("Country", "That country or region is not recognised. Use the list.");
        if (!RegionalCatalogue.IsLocale(i.Culture)) Error("Culture", "That locale is not recognised. Use a language-and-region tag such as en-AU or en-US.");
        if (!TimeZones.TryFind(i.TimeZone ?? "", out _)) Error("TimeZone", "That time zone is not recognised. Use an IANA name such as Australia/Sydney.");
        if (!RegionalCatalogue.IsCurrency((i.Currency ?? "").ToUpperInvariant())) Error("Currency", "That currency is not an ISO 4217 code the platform offers.");

        var info = await directory.FindAsync(tenant.RequireTenantId(), ct);
        var widen = info?.TenancyMode == TenancyMode.SingleOrganisation && existing.Count >= 1;
        if (widen) Error("Tenancy", "This installation is set up for a single organisation. Widen it to several organisations first (it cannot be narrowed again).");

        var (pack, packProblem) = ChosenPack(i.PackKey);
        if (packProblem is not null) Error("PackKey", packProblem);

        if (i.InitialAdministratorId is { } adminId)
        {
            var candidates = await people.ManageableUsersAsync(ct);
            if (candidates.All(p => p.Id != adminId || p.Status != UserStatus.Active)) Error("InitialAdministratorId", "Choose an active person from the list. Invite them first under Users if they are not listed.");
        }
        else problems.Add(new CreationProblem("InitialAdministratorId", "No initial administrator is named. Nobody will be able to configure this organisation until an Organisation Administrator is granted under Users.", IsError: false));

        var csv = OnboardingCsv.ReadStructure(i.StructureCsv);
        foreach (var issue in csv.Issues) Error("StructureCsv", $"Line {issue.Line}: {issue.Message}");
        var section = csv.IsClean ? OnboardingCsv.ToSection(csv.Items) : new StructureSection([], [], [], []);
        if (csv.IsClean)
        {
            foreach (var (kind, entries) in new[] { ("legal entity", section.LegalEntities), ("division", section.Divisions), ("business unit", section.BusinessUnits), ("site", section.Sites) })
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    try { StructureKey.Normalise(e.Key); }
                    catch (DomainException ex) { Error("StructureCsv", $"The {kind} key '{e.Key}': {ex.Message}"); }
                    if (!seen.Add(e.Key ?? "")) Error("StructureCsv", $"The {kind} key '{e.Key}' appears more than once.");
                }
            }
            var divisionKeys = section.Divisions.Select(d => d.Key.ToUpperInvariant()).ToHashSet();
            foreach (var bu in section.BusinessUnits.Where(b => !string.IsNullOrWhiteSpace(b.ParentKey) && !divisionKeys.Contains(b.ParentKey!.ToUpperInvariant())))
                Error("StructureCsv", $"The business unit '{bu.Key}' names division '{bu.ParentKey}', which is not listed. List the division on its own line.");
            foreach (var s in section.Sites.Where(s => !string.IsNullOrWhiteSpace(s.TimeZoneId) && !TimeZones.TryFind(s.TimeZoneId!, out _)))
                Error("StructureCsv", $"The site '{s.Key}' has a time zone that is not recognised.");
            if (section.LegalEntities.Any(l => string.Equals(l.Key, "MAIN", StringComparison.OrdinalIgnoreCase)))
                Error("StructureCsv", "The key MAIN is used for the legal entity made from the legal name. Choose another key for your own line.");
        }

        if (problems.All(p => !p.IsError))
        {
            actions.Add($"Create the organisation '{name}' with reference numbers like {rule!.Format(1)}.");
            actions.Add($"Record '{i.LegalName.Trim()}' as its first legal entity (key MAIN)" + (section.LegalEntities.Count + section.Divisions.Count + section.BusinessUnits.Count + section.Sites.Count == 0 ? "." : $", plus {section.LegalEntities.Count} more legal entities, {section.Divisions.Count} divisions, {section.BusinessUnits.Count} business units and {section.Sites.Count} sites."));
            actions.Add(i.InitialAdministratorId is null ? "Name no initial administrator (grant one later under Users)." : "Grant the chosen person the Organisation Administrator role for this organisation only.");
            actions.Add(pack is null ? "Assign no pack for now (choose one later under Packs)." : $"Assign pack '{pack.PackKey}' {pack.Version}: its documents become drafts and nothing is published.");
            actions.Add("Publish nothing. Every setting stays a draft until a second person publishes it.");
        }
        return new OrganisationCreationPlan(problems, actions, widen, pack?.Version, rule?.Format(1));
    }

    // ---- create -------------------------------------------------------------------------------------------------------------------------

    public async Task<NewOrganisationResult> CreateAsync(NewOrganisationInput i, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        StepUp();
        var plan = await PlanAsync(i, ct);
        if (!plan.CanCreate) throw new DomainException("organisation_invalid", plan.Problems.First(p => p.IsError).Message);

        var done = new List<string>();
        var next = new List<string>();
        var name = i.Name.Trim();
        var country = string.IsNullOrWhiteSpace(i.Country) ? null : i.Country.Trim().ToUpperInvariant();
        var orgId = await provisioning.AddOrganisationAsync(new AddOrganisationRequest(name, i.TimeZone.Trim(), i.Culture.Trim(), i.Currency.Trim().ToUpperInvariant(), i.Key.Trim(), string.IsNullOrWhiteSpace(i.PackKey) ? null : i.PackKey, i.ReferenceSeparator ?? "-", i.ReferenceDigits), ct);
        done.Add($"Organisation '{name}' created.");

        await structure.UpsertLegalEntityAsync(orgId, "MAIN", i.LegalName.Trim(), null, ct);
        var section = OnboardingCsv.ToSection(OnboardingCsv.ReadStructure(i.StructureCsv).Items);
        foreach (var e in section.LegalEntities) await structure.UpsertLegalEntityAsync(orgId, e.Key, e.Name, e.RegistrationNumber, ct);
        foreach (var e in section.Divisions) await structure.UpsertDivisionAsync(orgId, e.Key, e.Name, ct);
        foreach (var e in section.BusinessUnits) await structure.UpsertBusinessUnitAsync(orgId, e.Key, e.Name, e.ParentKey, ct);
        foreach (var e in section.Sites) await structure.UpsertSiteAsync(orgId, e.Key, e.Name, e.TimeZoneId, ct);
        done.Add($"Structure recorded: {section.LegalEntities.Count + 1} legal entities, {section.Divisions.Count} divisions, {section.BusinessUnits.Count} business units, {section.Sites.Count} sites.");

        if (i.InitialAdministratorId is { } adminId)
        {
            var scale = await scales.GetAsync(ct);
            await assignments.GrantTrustedAsync(new GrantRequest(adminId, RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([orgId], scale.DefaultLevel),
                Reason: "Initial administrator named when the organisation was created"), AssignmentSource.Setup, "organisation-wizard:" + Who(), null, ct);
            done.Add("Initial administrator granted the Organisation Administrator role for this organisation.");
        }
        else next.Add("Grant an Organisation Administrator under Users: nobody can configure this organisation until then.");

        if (plan.PackVersion is not null)
        {
            try
            {
                var result = await packAssignments.AssignAsync(orgId, i.PackKey!, plan.PackVersion, ct);
                var created = result.Kinds.Count(k => !k.NeedsAttention && k.Outcome.StartsWith("Draft created", StringComparison.Ordinal));
                var notCreated = result.Kinds.Count(k => k.Outcome.StartsWith("Not created", StringComparison.Ordinal));
                done.Add($"Pack {result.PackKey} {result.Version} recorded for this organisation; {created} drafts prepared, none published.");
                // A Tenant Administrator holds no organisation-level configuration rights by design, so the drafts for this organisation are prepared by its own administrator.
                if (notCreated > 0) next.Add($"The organisation's administrator opens Packs, chooses Assign for pack '{result.PackKey}' and confirms. That prepares the organisation's {notCreated} remaining drafts from the pack; it is safe to repeat and publishes nothing.");
            }
            catch (Exception ex) when (ex is DomainException or ForbiddenException)
            {
                next.Add($"The pack could not be assigned now ({ex.Message}). The Organisation Administrator can assign it under Packs.");
            }
        }
        else next.Add("Choose a pack under Packs when you are ready.");

        if (country is not null) next.Add($"Review the regional profile for {country} under Regional before publishing anything else.");
        next.Add("Open the setup checklist to follow what remains. The organisation's administrator drafts, submits and publishes each item; a second person publishes it.");

        await uow.ExecuteAsync(_ =>
        {
            audit.Record(new AuditEntry("organisation.setup.created", "Organisation", orgId.ToString(), OrganisationId: orgId,
                NewValuesJson: JsonSerializer.Serialize(new { name, key = i.Key.Trim().ToUpperInvariant(), pack = i.PackKey, administratorNamed = i.InitialAdministratorId is not null, structureItems = section.LegalEntities.Count + section.Divisions.Count + section.BusinessUnits.Count + section.Sites.Count + 1 }),
                Reason: "Created through the organisation wizard"));
            return Task.CompletedTask;
        }, ct);
        return new NewOrganisationResult(orgId, done, next);
    }

    // ---- list and checklist -------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OrganisationOverview>> ListAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var result = new List<OrganisationOverview>();
        foreach (var o in await structure.ListOrganisationsAsync(ct))
        {
            var list = await BuildItemsAsync(o, ct);
            result.Add(new OrganisationOverview(o.Id, o.Name, o.ReferencePrefix, o.SolutionPackKey, o.TimeZoneId, o.CultureCode, o.DefaultCurrencyCode, list.Count(x => x.State == ChecklistState.Done), list.Count));
        }
        return result;
    }

    public async Task<SetupChecklist> ChecklistAsync(Guid organisationId, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var resource = new ResourceDescriptor("Organisation", tenantId, organisationId, Dimensions: ScopeDimensions.OrganisationOnly);
        var allowed = (await access.CheckSilentAsync(Permission, new ResourceDescriptor("Organisation", tenantId, IsTenantLevelDefinition: true, Dimensions: ScopeDimensions.None), ct)).Allowed
            || (await access.CheckSilentAsync("onboarding.run", resource, ct)).Allowed;
        if (!allowed) throw new ForbiddenException("You cannot view this organisation's setup.");
        var org = await structure.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();
        var items = await BuildItemsAsync(org, ct);
        var usage = await UsageAsync(organisationId, ct);
        return new SetupChecklist(org, items, usage, usage.Count == 0);
    }

    private async Task<IReadOnlyList<OrganisationUsageLine>> UsageAsync(Guid organisationId, CancellationToken ct)
    {
        var lines = new List<OrganisationUsageLine>();
        foreach (var s in usageSources) lines.AddRange(await s.UsageAsync(organisationId, ct));
        return lines;
    }

    private async Task<IReadOnlyList<ChecklistItem>> BuildItemsAsync(OrganisationSummary org, CancellationToken ct)
    {
        var drafts = (await releases.OpenDraftsAsync(org.Id, ct)).ToDictionary(d => d.Kind, StringComparer.Ordinal);
        var items = new List<ChecklistItem>();
        async Task<ChecklistItem> Kind(string key, string title, string kind, string link, string owner, string whenNone)
        {
            var latest = await releases.LatestAsync(kind, org.Id, ct);
            drafts.TryGetValue(kind, out var draft);
            if (draft is { Submitted: true }) return new(key, title, ChecklistState.InProgress, "Submitted: waiting for a second person to publish it.", "/Admin/Reviews", owner);
            if (draft is not null) return new(key, title, ChecklistState.InProgress, "A draft is open and not yet submitted.", link, owner);
            if (latest is not null) return new(key, title, ChecklistState.Done, $"Release {latest.Number} is published.", link, owner);
            return new(key, title, ChecklistState.NotStarted, whenNone, link, owner);
        }

        items.Add(new("organisation", "Organisation created", ChecklistState.Done, $"{org.Name} ({org.ReferencePrefix}), {org.CultureCode}, {org.TimeZoneId}, {org.DefaultCurrencyCode}.", null, "Tenant Administrator"));

        ChecklistState adminState = ChecklistState.Unknown; var adminDetail = "Not visible to you.";
        try
        {
            var holders = (await assignments.ListAsync(activeOnly: true, ct)).Count(a => a.RoleCode == RoleCodes.OrganisationAdministrator && a.Scope.OrganisationIds.Contains(org.Id));
            adminState = holders > 0 ? ChecklistState.Done : ChecklistState.NotStarted;
            adminDetail = holders > 0 ? $"{holders} Organisation Administrator(s) hold the role." : "Nobody holds the Organisation Administrator role. Grant it under Users.";
        }
        catch (ForbiddenException) { }
        items.Add(new("administrator", "Initial administrator", adminState, adminDetail, "/Admin/Users", "Tenant Administrator"));

        var st = await structure.GetAsync(org.Id, ct);
        var count = st.LegalEntities.Count + st.Divisions.Count + st.BusinessUnits.Count + st.Sites.Count;
        items.Add(new("structure", "Structure", count > 1 ? ChecklistState.Done : ChecklistState.InProgress, $"{st.LegalEntities.Count} legal entities, {st.Divisions.Count} divisions, {st.BusinessUnits.Count} business units, {st.Sites.Count} sites.", $"/Onboarding?org={org.Id}", "Organisation Administrator"));

        var drafted = drafts.Count > 0 || await releases.LatestAsync("WorkflowContractLifecycle", org.Id, ct) is not null;
        items.Add(new("pack", "Pack", org.SolutionPackKey is null ? ChecklistState.NotStarted : drafted ? ChecklistState.Done : ChecklistState.InProgress,
            org.SolutionPackKey is null ? "No pack is assigned." : drafted ? $"Working from pack '{org.SolutionPackKey}'." : $"Pack '{org.SolutionPackKey}' is recorded. Its administrator chooses Assign under Packs to prepare the drafts.",
            "/Admin/Packs/Assign?organisationId=" + org.Id, "Organisation Administrator"));
        items.Add(await Kind("regional", "Regional profile", RegionalKinds.Name, "/Admin/Regional", "Organisation Administrator", "Not published: the organisation falls back to the values entered at creation."));
        items.Add(await Kind("terminology", "Terminology", TerminologyKinds.Name, "/Admin/Terminology", "Organisation Administrator", "Not published: the pack's wording is used."));
        items.Add(await Kind("capabilities", "Capabilities", CapabilityKinds.Name, "/Admin/Capabilities", "Organisation Administrator", "Not published: the pack's defaults apply."));

        var groupDetail = ""; var groupState = ChecklistState.Unknown;
        try
        {
            var mine = (await groups.ListAsync(ct)).Where(g => g.OrganisationId == org.Id || g.OrganisationId is null).ToList();
            groupState = mine.Any(g => g.Approvers > 0) ? ChecklistState.Done : mine.Count > 0 ? ChecklistState.InProgress : ChecklistState.NotStarted;
            groupDetail = mine.Count == 0 ? "No approver groups yet." : $"{mine.Count} group(s); {mine.Count(g => g.Approvers > 0)} with approvers.";
        }
        catch (ForbiddenException) { groupDetail = "Not visible to you."; }
        items.Add(new("groups", "Approver groups", groupState, groupDetail, "/Admin/ApproverGroups", "Organisation Administrator"));

        items.Add(await Kind("workflow", "Contract workflow", "WorkflowContractLifecycle", $"/Admin/Configuration?kind=WorkflowContractLifecycle&organisationId={org.Id}", "Organisation Administrator", "Not published: contracts cannot move through a lifecycle yet."));
        items.Add(await Kind("matrix", "Approval matrix", "ApprovalMatrix", "/Admin/Approvals", "Organisation Administrator", "Not published: approvals use the workflow's own approver group."));
        items.Add(await Kind("notifications", "Notification policy", "NotificationPolicy", "/Admin/Notifications", "Organisation Administrator", "Not published: the pack's default policy applies."));
        var waiting = drafts.Values.Count(d => d.Submitted);
        items.Add(new("reviews", "Submitted changes waiting", waiting == 0 ? ChecklistState.Done : ChecklistState.InProgress, waiting == 0 ? "Nothing is waiting for a second person." : $"{waiting} submission(s) wait for a second person.", "/Admin/Reviews", "A second Business Administrator"));
        return items;
    }

    // ---- reset --------------------------------------------------------------------------------------------------------------------------

    public async Task<ResetOutcome> ResetSetupAsync(Guid organisationId, string reason, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        StepUp();
        var why = Guard.NotBlank(reason, "Reason", 500);
        var org = await structure.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();
        var usage = await UsageAsync(organisationId, ct);
        if (usage.Count > 0) throw new DomainException("organisation_in_use", "This organisation already holds " + string.Join(" and ", usage.Select(u => $"{u.Count} {u.What}")) + ", so its setup cannot be reset.");

        var discarded = await bootstrap.DiscardOpenDraftsAsync(organisationId, Who(), why, ct);
        var structureNow = await structure.GetAsync(organisationId, ct);
        var ended = 0;
        foreach (var e in structureNow.LegalEntities.Where(x => x.IsActive)) { await structure.DeactivateAsync(StructureKind.LegalEntity, organisationId, e.Key, ct); ended++; }
        foreach (var e in structureNow.BusinessUnits.Where(x => x.IsActive)) { await structure.DeactivateAsync(StructureKind.BusinessUnit, organisationId, e.Key, ct); ended++; }
        foreach (var e in structureNow.Divisions.Where(x => x.IsActive)) { await structure.DeactivateAsync(StructureKind.Division, organisationId, e.Key, ct); ended++; }
        foreach (var e in structureNow.Sites.Where(x => x.IsActive)) { await structure.DeactivateAsync(StructureKind.Site, organisationId, e.Key, ct); ended++; }

        await uow.ExecuteAsync(_ =>
        {
            audit.Record(new AuditEntry("organisation.setup.reset", "Organisation", organisationId.ToString(), OrganisationId: organisationId, Reason: why,
                NewValuesJson: JsonSerializer.Serialize(new { draftsDiscarded = discarded, structureItemsEnded = ended })));
            return Task.CompletedTask;
        }, ct);
        return new ResetOutcome(discarded, ended, [
            $"{org.Name} is still listed: organisations are never deleted, so that its audit history stays whole.",
            "Published configuration, role assignments and the pack choice are untouched. Retire role assignments under Users and publish new releases through the ordinary lifecycle."]);
    }
}
