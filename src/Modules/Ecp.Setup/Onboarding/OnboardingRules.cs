using System.Security.Cryptography;
using System.Text;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;

namespace Ecp.Setup.Onboarding;

/// <summary>What already exists in the organisation and tenant, as keys. Everything a validation or preview needs, with no database access of its own.</summary>
public sealed record OnboardingFacts(
    Guid OrganisationId, ISet<string> LegalEntities, ISet<string> Divisions, ISet<string> BusinessUnits, ISet<string> Sites, ISet<string> Categories,
    ISet<string> KnownEmails, ISet<string> ProviderKeys, ISet<string> PackIds, ClassificationScale Scale)
{
    public static OnboardingFacts None(ClassificationScale? scale = null) => new(Guid.Empty, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
        new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), scale ?? ClassificationScale.Default);
}

/// <summary>Roles an onboarding release may assign through identity-group mappings. Administrator roles and approver roles (which need an approver group) are assigned in user administration, under delegation rules.</summary>
public static class OnboardingRoles
{
    public static readonly IReadOnlySet<string> MappableByOnboarding = new HashSet<string>
    {
        RoleCodes.Viewer, RoleCodes.CategoryOwner, RoleCodes.Legal, RoleCodes.Auditor, RoleCodes.ProcurementAdministrator, RoleCodes.DivisionSiteAdministrator
    };
}

public static class OnboardingValidator
{
    private static readonly System.Text.RegularExpressions.Regex KeyPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,39}$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.NonBacktracking);
    private const int MaxCategoryDepth = 6;

    public static ValidationReport Validate(OnboardingDocument d, OnboardingFacts facts)
    {
        var m = new List<ValidationMessage>();
        void Error(string path, string code, string message) => m.Add(new(ValidationSeverity.Error, path, code, message));
        void Warn(string path, string code, string message) => m.Add(new(ValidationSeverity.Warning, path, code, message));

        if (d.SchemaVersion != OnboardingDocument.CurrentSchema) Error("$.schemaVersion", "schema_version", $"Schema version {d.SchemaVersion} is not supported; use {OnboardingDocument.CurrentSchema}.");
        if (d.PackId is not null && !facts.PackIds.Contains(d.PackId)) Error("$.packId", "unknown_pack", $"Solution pack '{d.PackId}' is not in the catalogue.");

        // --- structure -------------------------------------------------------------------------------------------------------------------
        var legal = Keys("$.structure.legalEntities", d.Structure.LegalEntities);
        var divisions = Keys("$.structure.divisions", d.Structure.Divisions);
        var businessUnits = Keys("$.structure.businessUnits", d.Structure.BusinessUnits);
        var sites = Keys("$.structure.sites", d.Structure.Sites);
        var allDivisions = divisions.Union(facts.Divisions).ToHashSet();
        var allBusinessUnits = businessUnits.Union(facts.BusinessUnits).ToHashSet();
        var allSites = sites.Union(facts.Sites).ToHashSet();
        for (var i = 0; i < d.Structure.BusinessUnits.Count; i++)
            if (d.Structure.BusinessUnits[i].ParentKey is { } parent && !allDivisions.Contains(OnboardingDocument.Key(parent)))
                Error($"$.structure.businessUnits[{i}].parentKey", "division_not_found", $"Division '{parent}' is not defined here or in the organisation.");
        for (var i = 0; i < d.Structure.Sites.Count; i++)
            if (d.Structure.Sites[i].TimeZoneId is { } tz && !Ecp.SharedKernel.Localisation.TimeZones.TryFind(tz, out _))
                Error($"$.structure.sites[{i}].timeZoneId", "invalid_time_zone", $"Time zone '{tz}' was not recognised.");

        // --- categories --------------------------------------------------------------------------------------------------------------------
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < d.Categories.Count; i++)
        {
            var c = d.Categories[i]; var path = $"$.categories[{i}]";
            var code = OnboardingDocument.Key(c.Code);
            if (!KeyPattern.IsMatch(c.Code ?? "")) Error(path + ".code", "invalid_key", "A category code is 1 to 40 letters, digits, '.', '_' or '-'.");
            else if (!codes.Add(code)) Error(path + ".code", "duplicate", $"Category code '{code}' appears more than once.");
            else parentOf[code] = string.IsNullOrWhiteSpace(c.ParentCode) ? null : OnboardingDocument.Key(c.ParentCode);
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 200) Error(path + ".name", "name", "A category name is 1 to 200 characters.");
        }
        var allCategories = codes.Union(facts.Categories).ToHashSet();
        foreach (var (code, parent) in parentOf)
        {
            if (parent is null) continue;
            if (!allCategories.Contains(parent)) { Error("$.categories", "parent_not_found", $"Category '{code}' names parent '{parent}', which is not defined here or in the organisation."); continue; }
            if (parent == code) { Error("$.categories", "cycle", $"Category '{code}' cannot be its own parent."); continue; }
            var depth = 1; var cur = parent; var seen = new HashSet<string> { code };
            while (cur is not null && parentOf.TryGetValue(cur, out var next))
            {
                if (!seen.Add(cur)) { Error("$.categories", "cycle", $"Category '{code}' is part of a parent cycle."); break; }
                depth++; cur = next;
            }
            if (depth > MaxCategoryDepth) Error("$.categories", "depth", $"Category '{code}' is nested more than {MaxCategoryDepth} levels deep.");
        }

        // --- contract types --------------------------------------------------------------------------------------------------------------
        var typeCodes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < d.ContractTypes.Count; i++)
        {
            var t = d.ContractTypes[i];
            if (!KeyPattern.IsMatch(t.Code ?? "")) Error($"$.contractTypes[{i}].code", "invalid_key", "A contract type code is 1 to 40 letters, digits, '.', '_' or '-'.");
            else if (!typeCodes.Add(OnboardingDocument.Key(t.Code))) Error($"$.contractTypes[{i}].code", "duplicate", $"Contract type '{t.Code}' appears more than once.");
            if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 200) Error($"$.contractTypes[{i}].name", "name", "A contract type name is 1 to 200 characters.");
        }

        // --- category owners -----------------------------------------------------------------------------------------------------------------
        var ownerKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < d.CategoryOwners.Count; i++)
        {
            var o = d.CategoryOwners[i]; var path = $"$.categoryOwners[{i}]";
            if (!allCategories.Contains(OnboardingDocument.Key(o.CategoryCode))) Error(path + ".categoryCode", "category_not_found", $"Category '{o.CategoryCode}' is not defined here or in the organisation.");
            RequireUser(path + ".email", o.Email);
            if (!ownerKeys.Add(OnboardingDocument.Key(o.CategoryCode) + "|" + (o.Email ?? "").Trim().ToLowerInvariant())) Error(path, "duplicate", "This owner is listed twice for the category.");
        }
        if (d.Categories.Count > 0 && d.CategoryOwners.Count == 0) Warn("$.categoryOwners", "no_owners", "No category owners are assigned. Categories without an owner can be read but nobody can edit their contracts as owner.");

        // --- viewer scopes -------------------------------------------------------------------------------------------------------------------
        var viewerKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < d.ViewerScopes.Count; i++)
        {
            var v = d.ViewerScopes[i]; var path = $"$.viewerScopes[{i}]";
            RequireUser(path + ".email", v.Email);
            CheckNarrowing(path, v.ViewerKind, v.CategoryCodes, v.BusinessUnitKeys, v.SiteKeys, allCategories, allBusinessUnits, allSites);
            CheckCeiling(path + ".ceiling", v.Ceiling);
            if (!viewerKeys.Add(Canonical.Viewer(v))) Error(path, "duplicate", "This viewer scope is listed twice.");
        }

        // --- identity-group mappings ----------------------------------------------------------------------------------------------------------
        var mappingKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < d.GroupMappings.Count; i++)
        {
            var g = d.GroupMappings[i]; var path = $"$.groupMappings[{i}]";
            if (string.IsNullOrWhiteSpace(g.ProviderKey)) Error(path + ".providerKey", "required", "The identity provider key is required.");
            else if (!facts.ProviderKeys.Contains(g.ProviderKey.Trim())) Warn(path + ".providerKey", "provider_unknown", $"Identity provider '{g.ProviderKey}' is not configured yet; the mapping has no effect until it is.");
            if (string.IsNullOrWhiteSpace(g.GroupKey) || g.GroupKey.Length > 200) Error(path + ".groupKey", "required", "The group (its id or name as the provider sends it) is required, up to 200 characters.");
            if (!RoleCodes.All.Contains(g.RoleCode ?? "")) Error(path + ".role", "unknown_role", $"'{g.RoleCode}' is not one of the twelve roles.");
            else if (!OnboardingRoles.MappableByOnboarding.Contains(g.RoleCode!))
                Error(path + ".role", "role_not_mappable", $"Onboarding cannot map the {g.RoleCode} role. Assign administrator and approver roles in user administration, where delegation rules apply.");
            if (g.RoleCode == RoleCodes.Viewer)
            {
                if (g.ViewerKind is null) Error(path + ".viewerKind", "viewer_kind_required", "A Viewer mapping needs a viewer kind.");
                else CheckNarrowing(path, g.ViewerKind.Value, g.CategoryCodes, g.BusinessUnitKeys, g.SiteKeys, allCategories, allBusinessUnits, allSites);
            }
            else
            {
                if (g.ViewerKind is not null) Error(path + ".viewerKind", "viewer_kind_only_for_viewer", "A viewer kind applies only to the Viewer role.");
                CheckKeys(path, g.CategoryCodes, g.BusinessUnitKeys, g.SiteKeys, allCategories, allBusinessUnits, allSites);
                if (g.RoleCode == RoleCodes.DivisionSiteAdministrator && g.SiteKeys.Count == 0)
                    Error(path + ".sites", "site_required", "A Division/Site Administrator mapping must name at least one site.");
            }
            CheckCeiling(path + ".ceiling", g.Ceiling);
            if (!mappingKeys.Add(Canonical.Mapping(g))) Error(path, "duplicate", "This group mapping is listed twice.");
            if (g.RoleCode is RoleCodes.ProcurementAdministrator or RoleCodes.DivisionSiteAdministrator)
                Warn(path + ".role", "privileged_mapping", $"Everyone in group '{g.GroupKey}' becomes a {g.RoleCode}. Confirm the group's membership is controlled.");
        }
        if (d.GroupMappings.Count == 0 && d.ViewerScopes.Count == 0) Warn("$", "no_access", "Nobody is given access by this release: add viewer scopes or group mappings, or assign roles in user administration.");

        return new ValidationReport(m);

        HashSet<string> Keys(string path, IReadOnlyList<StructureEntry> entries)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!KeyPattern.IsMatch(e.Key ?? "")) Error($"{path}[{i}].key", "invalid_key", "A key is 1 to 40 letters, digits, '.', '_' or '-'.");
                else if (!set.Add(OnboardingDocument.Key(e.Key))) Error($"{path}[{i}].key", "duplicate", $"Key '{e.Key}' appears more than once.");
                if (string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 200) Error($"{path}[{i}].name", "name", "A name is 1 to 200 characters.");
            }
            return set;
        }

        void RequireUser(string path, string? email)
        {
            if (string.IsNullOrWhiteSpace(email) || !System.Net.Mail.MailAddress.TryCreate(email, out _)) Error(path, "email", "Enter a valid e-mail address.");
            else if (!facts.KnownEmails.Contains(email.Trim().ToLowerInvariant()))
                Error(path, "user_not_found", $"No user with the address '{email.Trim()}' exists in this tenant. Invite them in user administration first.");
        }

        void CheckKeys(string path, IReadOnlyList<string> cats, IReadOnlyList<string> bus, IReadOnlyList<string> siteKeys, ISet<string> allCats, ISet<string> allBus, ISet<string> allSit)
        {
            foreach (var c in cats) if (!allCats.Contains(OnboardingDocument.Key(c))) Error(path + ".categories", "category_not_found", $"Category '{c}' is not defined here or in the organisation.");
            foreach (var b in bus) if (!allBus.Contains(OnboardingDocument.Key(b))) Error(path + ".businessUnits", "business_unit_not_found", $"Business unit '{b}' is not defined here or in the organisation.");
            foreach (var s in siteKeys) if (!allSit.Contains(OnboardingDocument.Key(s))) Error(path + ".sites", "site_not_found", $"Site '{s}' is not defined here or in the organisation.");
        }

        void CheckNarrowing(string path, ViewerKind kind, IReadOnlyList<string> cats, IReadOnlyList<string> bus, IReadOnlyList<string> siteKeys, ISet<string> allCats, ISet<string> allBus, ISet<string> allSit)
        {
            CheckKeys(path, cats, bus, siteKeys, allCats, allBus, allSit);
            switch (kind)
            {
                case ViewerKind.AllContracts when cats.Count + bus.Count + siteKeys.Count > 0:
                    Error(path, "viewer_not_narrowable", "An All Contracts viewer cannot be narrowed. Choose Category, BusinessUnit, Site or Restricted instead."); break;
                case ViewerKind.Category when cats.Count == 0: Error(path + ".categories", "category_required", "A Category viewer must name at least one category."); break;
                case ViewerKind.BusinessUnit when bus.Count == 0: Error(path + ".businessUnits", "business_unit_required", "A Business Unit viewer must name at least one business unit."); break;
                case ViewerKind.Site when siteKeys.Count == 0: Error(path + ".sites", "site_required", "A Site viewer must name at least one site."); break;
                case ViewerKind.Restricted:
                    var dimensions = (cats.Count > 0 ? 1 : 0) + (bus.Count > 0 ? 1 : 0) + (siteKeys.Count > 0 ? 1 : 0);
                    if (dimensions == 0) Error(path, "restricted_needs_scope", "A Restricted viewer must be narrowed by at least one of category, business unit or site.");
                    else if (dimensions == 1) Warn(path, "restricted_single", "A Restricted viewer narrowed on one dimension behaves like a Category, Business Unit or Site viewer. Add a second narrowing, or change the kind.");
                    break;
            }
        }

        void CheckCeiling(string path, string? ceiling)
        {
            if (!facts.Scale.IsKnown(ceiling)) Error(path, "invalid_classification", $"Classification ceiling '{ceiling}' is not one of: {string.Join(", ", facts.Scale.Levels)}.");
            else if (facts.Scale.Levels.Count > 1 && facts.Scale.Rank(ceiling) == facts.Scale.Levels.Count - 1)
                Warn(path, "top_ceiling", $"The ceiling '{ceiling}' is the highest level: this person can see everything in their scope, including the most sensitive records.");
        }
    }
}

/// <summary>Stable text keys that identify an item across releases (used to recognise "the same" viewer scope or mapping).</summary>
public static class Canonical
{
    private static string Set(IEnumerable<string> v) => string.Join(',', v.Select(OnboardingDocument.Key).Distinct().Order(StringComparer.Ordinal));
    public static string Viewer(ViewerEntry v) => $"{v.Email.Trim().ToLowerInvariant()}|{v.ViewerKind}|c={Set(v.CategoryCodes)}|b={Set(v.BusinessUnitKeys)}|s={Set(v.SiteKeys)}|{v.Ceiling.Trim().ToUpperInvariant()}";
    public static string Mapping(MappingEntry g) => $"{g.ProviderKey.Trim().ToLowerInvariant()}|{g.GroupKey.Trim()}|{g.RoleCode}|{g.ViewerKind}|c={Set(g.CategoryCodes)}|b={Set(g.BusinessUnitKeys)}|s={Set(g.SiteKeys)}|{g.Ceiling.Trim().ToUpperInvariant()}";
    public static string Owner(OwnerEntry o) => $"{OnboardingDocument.Key(o.CategoryCode)}|{o.Email.Trim().ToLowerInvariant()}";
}

/// <summary>Builds the real scope of a viewer or mapping from business keys. Used by publication (with real ids) and by the preview (with synthetic ids).</summary>
public static class OnboardingScopes
{
    public static ScopeSet For(Guid organisationId, string ceiling, IReadOnlyList<string> categoryCodes, IReadOnlyList<string> businessUnitKeys, IReadOnlyList<string> siteKeys,
        Func<string, Guid> category, Func<string, Guid> businessUnit, Func<string, Guid> site)
        => ScopeSet.ForOrganisations([organisationId], ceiling.Trim(),
            businessUnits: businessUnitKeys.Select(businessUnit), sites: siteKeys.Select(site), categories: categoryCodes.Count == 0 ? null : categoryCodes.Select(category));
}

public sealed record PreviewRow(string Who, string Role, int Visible, int Total, IReadOnlyList<string> HiddenExamples, string Note);
public sealed record OnboardingPreview(int SampleRecords, IReadOnlyList<PreviewRow> Rows, IReadOnlyList<string> Notes);

/// <summary>
/// Synthetic preview and access simulation for a draft that has not been published: builds the proposed viewer scopes and group mappings with stand-in
/// identifiers, creates sample records across the proposed categories, sites and classification levels, and asks the real access evaluator what each
/// person (or group member) could read. Nothing is read from or written to the database.
/// </summary>
public static class OnboardingSimulator
{
    private const int MaxCategories = 12, MaxSites = 4, MaxBusinessUnits = 3;
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Synthetic("tenant", "t");
    private static readonly Guid Organisation = Synthetic("org", "o");

    public static Guid Synthetic(string type, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"onboarding-preview:{type}:{OnboardingDocument.Key(key)}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static OnboardingPreview Run(OnboardingDocument d, ClassificationScale scale)
    {
        var categories = d.Categories.Select(c => OnboardingDocument.Key(c.Code)).Take(MaxCategories).ToList();
        var parents = d.Categories.ToDictionary(c => OnboardingDocument.Key(c.Code), c => string.IsNullOrWhiteSpace(c.ParentCode) ? null : OnboardingDocument.Key(c.ParentCode));
        IReadOnlyList<Guid> Ancestry(string code)
        {
            var path = new List<Guid>(); string? cur = code; var guard = 0;
            while (cur is not null && guard++ < 10) { path.Add(Synthetic("category", cur)); cur = parents.GetValueOrDefault(cur); }
            return path;
        }
        var sites = d.Structure.Sites.Select(s => OnboardingDocument.Key(s.Key)).Take(MaxSites).Select(s => (string?)s).DefaultIfEmpty(null).ToList();
        var units = d.Structure.BusinessUnits.Select(b => OnboardingDocument.Key(b.Key)).Take(MaxBusinessUnits).Select(b => (string?)b).DefaultIfEmpty(null).ToList();
        var levels = scale.Levels.Count <= 2 ? scale.Levels : [scale.Levels[0], scale.Levels[^1]];
        var cats = categories.Count == 0 ? new List<string?> { null } : categories.Select(c => (string?)c).ToList();

        var samples = new List<(string Label, ResourceDescriptor Resource)>();
        foreach (var c in cats) foreach (var b in units) foreach (var s in sites) foreach (var level in levels)
        {
            var label = $"{(c is null ? "no category" : "category " + c)}{(b is null ? "" : ", unit " + b)}{(s is null ? "" : ", site " + s)}, {level}";
            samples.Add((label, new ResourceDescriptor("Contract", Tenant, Organisation, b is null ? null : Synthetic("bu", b), null, s is null ? null : Synthetic("site", s),
                c is null ? null : Synthetic("category", c), c is null ? null : Ancestry(c), level, false, new HashSet<Guid>(), Id: "preview")));
        }

        var rows = new List<PreviewRow>();
        ScopeSet Scope(string ceiling, IReadOnlyList<string> cs, IReadOnlyList<string> bs, IReadOnlyList<string> ss)
            => OnboardingScopes.For(Organisation, ceiling, cs, bs, ss, k => Synthetic("category", k), k => Synthetic("bu", k), k => Synthetic("site", k));

        PreviewRow Evaluate(string who, string role, string note, AssignmentView assignment)
        {
            var subject = new AccessSubject(Synthetic("user", who), Tenant, UserStatus.Active, [assignment]);
            var hidden = new List<string>(); var visible = 0;
            foreach (var (label, resource) in samples)
            {
                var decision = AccessEvaluator.Default.Evaluate(subject, scale, Now, "contracts.read", resource);
                if (decision.Allowed) visible++; else if (hidden.Count < 3) hidden.Add(label);
            }
            return new PreviewRow(who, role, visible, samples.Count, hidden, note);
        }

        foreach (var v in d.ViewerScopes.Where(v => scale.IsKnown(v.Ceiling)))
            rows.Add(Evaluate(v.Email, $"Viewer ({v.ViewerKind})", $"ceiling {v.Ceiling}",
                new AssignmentView(Synthetic("assignment", Canonical.Viewer(v)), RoleCodes.Viewer, v.ViewerKind, Scope(v.Ceiling, v.CategoryCodes, v.BusinessUnitKeys, v.SiteKeys), null, Now.AddYears(-1), null)));
        foreach (var g in d.GroupMappings.Where(g => scale.IsKnown(g.Ceiling) && RoleCodes.All.Contains(g.RoleCode)))
            rows.Add(Evaluate($"members of {g.GroupKey}", g.RoleCode + (g.ViewerKind is { } k ? $" ({k})" : ""), $"ceiling {g.Ceiling}",
                new AssignmentView(Synthetic("assignment", Canonical.Mapping(g)), g.RoleCode, g.ViewerKind, Scope(g.Ceiling, g.CategoryCodes, g.BusinessUnitKeys, g.SiteKeys), null, Now.AddYears(-1), null)));

        var notes = new List<string>
        {
            "Each row asks the real access evaluator whether that person could open the contract in each sample. The samples are synthetic: nothing is read from your contracts.",
            "A record above a person's classification ceiling is never visible, even when it is in the person's scope.",
        };
        if (d.CategoryOwners.Count > 0) notes.Add("Category owners can read every contract in their organisation within their ceiling and edit those in the categories they own; ownership comes from the owner list, the role from a mapping or user administration.");
        return new OnboardingPreview(samples.Count, rows, notes);
    }
}
