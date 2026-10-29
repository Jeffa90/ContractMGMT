using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;

namespace Ecp.Setup.Onboarding;

public enum ReadinessLevel { Required, Recommended, Optional }

/// <summary>Complete is computed from facts (never ticked). Needs attention means started but contradicted by a fact. Not applicable means the organisation does not use that step.</summary>
public enum ReadinessState { Complete, NeedsAttention, NotStarted, NotApplicable }

public sealed record ReadinessItem(string Key, int Order, string Title, string Purpose, ReadinessLevel Level, ReadinessState State, string Detail, string FixHref)
{
    public bool Open => State is ReadinessState.NotStarted or ReadinessState.NeedsAttention;
    public bool BlocksPublish => Level == ReadinessLevel.Required && Open && Key is not ("review" or "publish");
}

public sealed record ReadinessReport(Guid OrganisationId, string? PackKey, bool PackDefinesRequirements, IReadOnlyList<ReadinessItem> Items)
{
    public IReadOnlyList<ReadinessItem> BlockingItems => Items.Where(i => i.BlocksPublish).ToList();
    public bool CanPublish => BlockingItems.Count == 0;
    public IReadOnlyList<ReadinessItem> OpenRecommended => Items.Where(i => i.Level == ReadinessLevel.Recommended && i.Open).ToList();
}

/// <summary>
/// The business-outcome set-up checklist (docs/UX_REDESIGN.md section 8). What each item means and in which order is the platform's; whether an item is Required, Recommended or Optional is the
/// selected solution pack's, because only a pack knows what its business needs. The platform insists on its universal safety items only. Nothing here is ticked by hand: it reads the facts that make
/// each item true, and counts what the waiting onboarding draft will create when published, so publishing can finish the work it is asked to finish.
/// </summary>
public interface IOnboardingReadiness
{
    Task<ReadinessReport> GetAsync(Guid organisationId, OnboardingState state, CancellationToken ct = default);
    /// <summary>Refuses with every open Required item named, in words. The publish gate.</summary>
    Task RequireReadyAsync(Guid organisationId, OnboardingState state, CancellationToken ct = default);
}

public static class ReadinessCatalogue
{
    public const string Organisation = "organisation", SolutionPack = "solutionPack", Regional = "regional", Structure = "structure", Categories = "categories", ContractTypes = "contractTypes",
        People = "people", Owners = "owners", Approvers = "approvers", Workflow = "workflow", Notifications = "notifications", Review = "review", Publish = "publish";

    public static readonly IReadOnlyList<(string Key, string Title, string Purpose, string Href)> Items =
    [
        (Organisation, "[[organisation]] details", "The [[organisation|lower]] exists with its name, locale, time zone and currency.", "/Admin/Organisations"),
        (SolutionPack, "[[solution.pack|sentence]]", "The starting configuration the [[organisation|lower]] works from.", "/Onboarding/Step/pack"),
        (Regional, "Regional settings", "Locale, time zone, currency and formats have been reviewed and published.", "/Admin/Regional"),
        (Structure, "[[organisation]] structure", "Divisions, [[business.unit|plural|lower]] and sites, if the [[organisation|lower]] uses them.", "/Onboarding/Step/structure"),
        (Categories, "[[contract|plural]] portfolios ([[category|plural|lower]])", "The [[category|plural|lower]] [[contract|plural|lower]] are filed under.", "/Admin/Categories"),
        (ContractTypes, "[[contract.type|plural|sentence]]", "The kinds of [[contract|lower]] the [[organisation|lower]] makes.", "/Onboarding/Step/types"),
        (People, "People & Access", "Someone is able to create and manage [[contract|plural|lower]].", "/Admin/People"),
        (Owners, "[[contract.owner|plural|sentence]]", "Every [[category|lower]] has a primary owner who can act as one.", "/Admin/Categories"),
        (Approvers, "Approval authorities", "Someone is chosen to approve.", "/Admin/ApprovalAuthorities"),
        (Workflow, "Workflow and approvals", "The workflow and the approval rules have been reviewed.", "/Admin/Configuration"),
        (Notifications, "Notifications", "What people are told, and when, has been reviewed.", "/Admin/Notifications"),
        (Review, "Review set-up", "The draft is checked and its effect previewed.", "/Onboarding/Review"),
        (Publish, "Publish", "The set-up is published, or sent to a second person to publish.", "/Onboarding/Review"),
    ];

    /// <summary>What the platform insists on whatever the pack says. Everything else is the pack's call; a pack that says nothing leaves it Recommended.</summary>
    public static ReadinessLevel Universal(string key) => key is Organisation or Review or Publish ? ReadinessLevel.Required : ReadinessLevel.Recommended;

    public static bool TryLevel(string? text, out ReadinessLevel level) => Enum.TryParse(text, ignoreCase: true, out level) && Enum.IsDefined(level);
}

internal sealed class OnboardingReadiness(
    IOrganisationStructureService organisations, ICategoryAdministration categories, IRoleAssignmentService roles, IUserService users, IApproverGroupService groups, IConfigurationReleases releases,
    ICapabilityResolver capabilities, ISolutionPackCatalogue catalogue, IClock clock, IOrganisationPackContent packContent) : IOnboardingReadiness
{
    private const string LifecycleKind = "WorkflowContractLifecycle";

    public async Task<ReadinessReport> GetAsync(Guid organisationId, OnboardingState state, CancellationToken ct = default)
    {
        var org = await organisations.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();
        var doc = state.Document;
        var packKey = doc.PackId ?? org.SolutionPackKey;
        if (packKey is null && await releases.LatestAsync("SolutionPack", Guid.Empty, ct) is { } tenantPack)
        {
            try { using var j = System.Text.Json.JsonDocument.Parse(tenantPack.ContentJson); packKey = j.RootElement.TryGetProperty("packId", out var id) ? id.GetString() : null; }
            catch (System.Text.Json.JsonException) { /* an unreadable record names no pack */ }
        }
        var requirements = packKey is null ? null : catalogue.Find(packKey)?.OnboardingRequirements;
        var now = clock.UtcNow;

        var structure = await organisations.GetAsync(organisationId, ct);
        var existingCats = (await categories.ListAllAsync(ct)).Where(c => c.OrganisationId == organisationId && c.IsActive).ToList();
        var assignments = await roles.ListAsync(activeOnly: true, ct);
        var people = (await users.ListAsync(ct)).Where(u => u.Status != UserStatus.Disabled).ToDictionary(u => u.Id);
        var emailToId = people.Values.ToDictionary(u => u.Email.Trim().ToLowerInvariant(), u => u.Id);
        bool Covers(AssignmentDto a) => a.Scope.Level == Ecp.Identity.Domain.ScopeLevel.Tenant || a.Scope.OrganisationIds.Contains(organisationId);
        var ownerRoleHolders = assignments.Where(a => a.RoleCode == RoleCodes.CategoryOwner && Covers(a) && people.ContainsKey(a.UserId)).Select(a => a.UserId).ToHashSet();
        var workers = assignments.Where(a => a.RoleCode is RoleCodes.CategoryOwner or RoleCodes.ProcurementAdministrator && Covers(a) && people.ContainsKey(a.UserId)).Select(a => a.UserId).Distinct().Count();

        // What publishing the waiting draft will add to what already exists.
        var draftCats = doc.Categories.Select(c => (Code: OnboardingDocument.Key(c.Code), Parent: string.IsNullOrWhiteSpace(c.ParentCode) ? null : OnboardingDocument.Key(c.ParentCode))).ToList();
        var allCats = existingCats.Select(c => (Code: OnboardingDocument.Key(c.Code), Parent: c.ParentCategoryId is { } p ? OnboardingDocument.Key(existingCats.FirstOrDefault(x => x.Id == p)?.Code) : null))
            .Concat(draftCats).GroupBy(c => c.Code).Select(g => g.First()).ToList();
        var topLevel = allCats.Where(c => c.Parent is null || allCats.All(p => p.Code != c.Parent)).Select(c => c.Code).ToList();
        var primaryOwnerOf = new Dictionary<string, Guid>();
        foreach (var c in existingCats)
            foreach (var o in c.Owners.Where(o => o.IsPrimary && o.EffectiveFromUtc <= now && (o.EffectiveToUtc is null || o.EffectiveToUtc > now))) primaryOwnerOf[OnboardingDocument.Key(c.Code)] = o.UserId;
        foreach (var o in doc.CategoryOwners.Where(o => o.IsPrimary))
            if (emailToId.TryGetValue((o.Email ?? "").Trim().ToLowerInvariant(), out var uid)) primaryOwnerOf[OnboardingDocument.Key(o.CategoryCode)] = uid;
        // An owner of a parent covers its sub-categories.
        bool Owned(string code) { var cur = code; var guard = 0; while (cur is not null && guard++ < 20) { if (primaryOwnerOf.ContainsKey(cur)) return true; cur = allCats.FirstOrDefault(c => c.Code == cur).Parent; } return false; }
        var unowned = topLevel.Count(c => !Owned(c));
        var ownedWithoutRole = topLevel.Count(c => primaryOwnerOf.TryGetValue(c, out var uid) && !ownerRoleHolders.Contains(uid));

        var released = await releases.LatestAsync(OrganisationOnboardingKind.KindName, organisationId, ct);
        var releasedTypes = released is null ? 0 : OnboardingDocument.Parse(released.ContentJson).ContractTypes.Count;
        var typeCount = Math.Max(doc.ContractTypes.Count, releasedTypes);
        var approvalsOn = await capabilities.IsEnabledAsync(organisationId, CapabilityKeys.RegisterAuthorisation, ct);
        var approverGroups = (await groups.ListAsync(ct)).Where(g => g.IsActive && (g.OrganisationId is null || g.OrganisationId == organisationId)).ToList();
        var groupsWithPeople = approverGroups.Count(g => g.Approvers > 0);
        var regional = await releases.LatestAsync("RegionalProfile", organisationId, ct);
        var lifecycle = await releases.LatestAsync(LifecycleKind, organisationId, ct);
        var matrix = await releases.LatestAsync("ApprovalMatrix", organisationId, ct);
        // Nothing published yet is not "not reviewed" when the selected pack supplies a complete standard workflow: that is the safe default the organisation starts from.
        string? packWorkflow = null;
        if (lifecycle is null && packKey is not null)
        {
            try { var d = await packContent.GetStartingDocumentAsync(organisationId, packKey, LifecycleKind, ct); packWorkflow = $"{d.PackKey} {d.Version}"; }
            catch (DomainException) { /* no usable pack document, or a draft is waiting: the checklist says the workflow still needs review */ }
        }
        var notifications = await releases.LatestAsync("NotificationPolicy", organisationId, ct);
        var structureCount = structure.LegalEntities.Count + structure.Divisions.Count + structure.BusinessUnits.Count + structure.Sites.Count + doc.Structure.Count;

        string Plural(int n, string one, string many) => n == 1 ? one : many;
        var errors = state.Validation?.Messages.Count(m => m.Severity == ValidationSeverity.Error) ?? 0;

        (ReadinessState, string) Facts(string key) => key switch
        {
            ReadinessCatalogue.Organisation => (ReadinessState.Complete, org.Name),
            ReadinessCatalogue.SolutionPack => packKey is null ? (ReadinessState.NotStarted, "No starting configuration has been chosen.") : (ReadinessState.Complete, packKey),
            ReadinessCatalogue.Regional => regional is null ? (ReadinessState.NotStarted, "Not reviewed yet.") : (ReadinessState.Complete, $"{org.CultureCode}, {org.TimeZoneId}, {org.DefaultCurrencyCode}"),
            ReadinessCatalogue.Structure => structureCount == 0 ? (ReadinessState.NotStarted, "Nothing entered yet.") : (ReadinessState.Complete, $"{structureCount} {Plural(structureCount, "item", "items")}"),
            ReadinessCatalogue.Categories => allCats.Count == 0 ? (ReadinessState.NotStarted, "None yet.") : (ReadinessState.Complete, $"{allCats.Count} {Plural(allCats.Count, "category", "categories")}" + (draftCats.Count > 0 && existingCats.Count < allCats.Count ? " (including those the waiting draft adds)" : "")),
            ReadinessCatalogue.ContractTypes => typeCount == 0 ? (ReadinessState.NotStarted, "None yet.") : (ReadinessState.Complete, $"{typeCount} {Plural(typeCount, "type", "types")}"),
            ReadinessCatalogue.People => workers == 0 ? (ReadinessState.NotStarted, "Nobody can create or manage contracts here yet.") : (ReadinessState.Complete, $"{workers} {Plural(workers, "person", "people")}"),
            ReadinessCatalogue.Owners => allCats.Count == 0 ? (ReadinessState.NotStarted, "Waiting for categories.")
                : ownedWithoutRole > 0 ? (ReadinessState.NeedsAttention, $"{ownedWithoutRole} owner(s) do not hold the role that lets owners act.")
                : unowned > 0 ? (topLevel.Count - unowned == 0 ? ReadinessState.NotStarted : ReadinessState.NeedsAttention, $"{unowned} of {topLevel.Count} top-level {Plural(topLevel.Count, "category", "categories")} still need a primary owner.")
                : (ReadinessState.Complete, "Every category has a primary owner."),
            ReadinessCatalogue.Approvers => !approvalsOn ? (ReadinessState.NotApplicable, "This organisation does not use the approval step.")
                : groupsWithPeople == 0 ? (ReadinessState.NotStarted, "No approver group has anyone in it yet.") : (ReadinessState.Complete, $"{groupsWithPeople} {Plural(groupsWithPeople, "group", "groups")} with people"),
            ReadinessCatalogue.Workflow => lifecycle is null
                    ? (packWorkflow is not null ? (ReadinessState.Complete, $"The standard workflow of {packWorkflow} applies") : (ReadinessState.NotStarted, "The workflow has not been reviewed yet."))
                : approvalsOn && NamesMatrix(lifecycle.ContentJson) && matrix is null ? (ReadinessState.NotStarted, "The workflow names approval rules that have not been reviewed yet.") : (ReadinessState.Complete, $"Workflow release {lifecycle.Number}"),
            ReadinessCatalogue.Notifications => notifications is null ? (ReadinessState.NotStarted, "Not reviewed yet.") : (ReadinessState.Complete, $"Release {notifications.Number}"),
            ReadinessCatalogue.Review => errors > 0 ? (ReadinessState.NeedsAttention, $"{errors} problem(s) to fix.") : state.IsPublishable ? (ReadinessState.Complete, "Checked and previewed.") : (ReadinessState.NotStarted, "Check the draft on the review page."),
            ReadinessCatalogue.Publish => state.CurrentRelease is null ? (ReadinessState.NotStarted, "Nothing published yet.") : (ReadinessState.Complete, $"Release {state.CurrentRelease}"),
            _ => (ReadinessState.NotStarted, "")
        };

        var items = new List<ReadinessItem>();
        foreach (var (key, title, purpose, href) in ReadinessCatalogue.Items)
        {
            var level = ReadinessCatalogue.Universal(key);
            // The pack may raise or relax anything except the platform's own universal items.
            if (requirements is not null && requirements.TryGetValue(key, out var text) && ReadinessCatalogue.TryLevel(text, out var chosen) && !IsUniversal(key)) level = chosen;
            var (st, detail) = Facts(key);
            items.Add(new ReadinessItem(key, items.Count + 1, title, purpose, level, st, detail, href));
        }
        return new ReadinessReport(organisationId, packKey, requirements is { Count: > 0 }, items);
    }

    /// <summary>Whether the workflow's approval step names an approval matrix. Without one the single approver group decides, so no matrix has to be written.</summary>
    private static bool NamesMatrix(string workflowJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(workflowJson);
            bool Find(System.Text.Json.JsonElement e)
            {
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
                foreach (var p in e.EnumerateObject())
                {
                    if (string.Equals(p.Name, "matrix", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString())) return true;
                    if (Find(p.Value)) return true;
                }
                return false;
            }
            return Find(doc.RootElement);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static bool IsUniversal(string key) => OnboardingRequirementVocabulary.Universal.Contains(key, StringComparer.Ordinal);

    public async Task RequireReadyAsync(Guid organisationId, OnboardingState state, CancellationToken ct = default)
    {
        var report = await GetAsync(organisationId, state, ct);
        if (report.CanPublish) return;
        throw new DomainException("required_items_open", "Not ready to publish. Still to do: " + string.Join("; ", report.BlockingItems.Select(i => $"{i.Title} ({i.Detail.TrimEnd('.')})")) + ".");
    }
}
