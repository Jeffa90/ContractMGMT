using System.Globalization;
using System.Text.Json;
using Ecp.Approvals;
using Ecp.Approvals.Definitions;
using Ecp.Approvals.Domain;
using Ecp.Approvals.Resolution;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Lifecycle.Definitions;
using Ecp.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Lifecycle;

/// <summary>What is stored with a plan-based submission: the plan itself, how it was reached and why each person was or was not included. Fixed at submission.</summary>
public sealed record StoredPlan(ApprovalPlan Plan, IReadOnlyList<TraceEntry> Trace, IReadOnlyList<ApproverExplanation> Explanations);

public sealed record PlanApproverView(Guid UserId, string Name, string Role, string? OnBehalfOfName, string Why);
public sealed record PlanResponseView(string Name, string Role, string? OnBehalfOfName, bool Approved, string? Comment, DateTime RespondedUtc);
public sealed record PlanStepView(
    string Key, string Display, string RowCode, int Stage, string ApproverGroup, string? BackupGroup, string Mode, string Status, int? TimeoutDays, IReadOnlyList<PlanApproverView> Approvers,
    IReadOnlyList<PlanResponseView> Responses, IReadOnlyList<string> Outstanding);

/// <summary>How a plan-based request stands, for the approval page. The resolution trace and the reasons people were left out are shown only to readers of the approval matrix.</summary>
public sealed record PlanView(
    string MatrixCode, string MatrixName, int? Release, DateOnly EffectiveDate, string Status, int? ActiveStage, IReadOnlyList<PlanStepView> Steps, string? YourPart, string? YourPartNote,
    IReadOnlyList<TraceEntry>? Trace, IReadOnlyList<ApproverExplanation>? Explanations);

/// <summary>Turns a contract or Variation into the facts the approval matrix is judged on. Facts the platform does not hold are left to contributors; an unknown fact never satisfies a condition.</summary>
internal sealed class ApprovalFactsBuilder(ICategoryAdministration categories, IOrganisationStructureService structure, IEnumerable<IApprovalFactContributor> contributors)
{
    public async Task<ApprovalFacts> BuildAsync(SubjectView s, CancellationToken ct)
    {
        var c = s.Contract;
        decimal? value = c.EstimatedValue; var currency = c.CurrencyCode;
        if (s.IsVariation)
        {
            // What a Variation asks to be approved is its proposed value (in its proposed currency), when it proposes one.
            var proposed = JsonSerializer.Deserialize<Dictionary<string, string?>>(s.Variation!.ProposedChangesJson) ?? [];
            if (proposed.TryGetValue("estimatedValue", out var v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) value = d;
            if (proposed.TryGetValue("currencyCode", out var cur) && !string.IsNullOrWhiteSpace(cur)) currency = cur.Trim().ToUpperInvariant();
        }
        var all = await categories.ListAllAsync(ct);
        var byId = all.ToDictionary(x => x.Id);
        var path = new List<string>();
        for (Guid? id = c.CategoryId; id is { } i && byId.TryGetValue(i, out var cat) && path.Count < 50; id = cat.ParentCategoryId) path.Add(cat.Code);
        var structureNow = await structure.GetAsync(s.OrganisationId, ct);
        var facts = new ApprovalFacts(value, currency, path, null, null, structureNow.BusinessUnits.FirstOrDefault(b => b.Id == c.BusinessUnitId)?.Key, structureNow.Sites.FirstOrDefault(x => x.Id == c.SiteId)?.Key,
            s.Classification, null, null, new Dictionary<string, string?>());
        foreach (var contributor in contributors) facts = await contributor.EnrichAsync(facts, s.OrganisationId, s.Kind, s.Id, ct);
        return facts;
    }
}

/// <summary>Reference data and workflow references for the approval matrix. Lives here because only this module knows both the categories and the workflows.</summary>
internal sealed class LifecycleMatrixReferences(ICategoryAdministration categories, IOrganisationStructureService structure, IServiceProvider services) : IApprovalReferenceData, IApprovalMatrixReferences
{
    public async Task<MatrixReferenceData> LoadAsync(Guid organisationId, CancellationToken ct = default)
    {
        var all = (await categories.ListAllAsync(ct)).Where(c => c.OrganisationId == organisationId).ToList();
        var byId = all.ToDictionary(c => c.Id);
        var ancestry = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in all)
        {
            var path = new List<string>();
            for (Guid? id = c.Id; id is { } i && byId.TryGetValue(i, out var cat) && path.Count < 50; id = cat.ParentCategoryId) path.Add(cat.Code);
            ancestry[c.Code] = path;
        }
        var s = await structure.GetAsync(organisationId, ct);
        return new MatrixReferenceData(ancestry, s.BusinessUnits.Select(b => b.Key).ToHashSet(StringComparer.OrdinalIgnoreCase), s.Sites.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
            s.LegalEntities.Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<string>> ReferencedMatrixCodesAsync(Guid organisationId, CancellationToken ct = default)
    {
        var releases = services.GetRequiredService<IConfigurationReleases>();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { WorkflowKinds.ContractLifecycle, WorkflowKinds.Variation })
            if (await releases.LatestAsync(kind, organisationId, ct) is { } latest && WorkflowDefinitionParser.Parse(latest.ContentJson).Definition?.Settings.Approval.Matrix is { } code) codes.Add(code);
        return codes.ToList();
    }
}

/// <summary>Lets an administrator describe a sample submission by choosing a category, business unit, site and so on; the codes and ids come from stored reference data, not from the request.</summary>
internal sealed class ApprovalSimulationInputs(
    ICategoryAdministration categories, IOrganisationStructureService structure, Ecp.Identity.IAccessService access, Ecp.SharedKernel.ITenantContext tenant, Ecp.Identity.IClassificationScaleProvider scales) : IApprovalSimulationInputs
{
    private async Task RequireAsync(Guid organisationId, CancellationToken ct)
        => await access.RequireAsync("approvals.simulate", new Ecp.Identity.Access.ResourceDescriptor("ApprovalMatrix", tenant.RequireTenantId(), organisationId, Dimensions: Ecp.Identity.Access.ScopeDimensions.OrganisationOnly), ct);

    public async Task<SimulationChoices> ChoicesAsync(Guid organisationId, CancellationToken ct = default)
    {
        await RequireAsync(organisationId, ct);
        var s = await structure.GetAsync(organisationId, ct);
        var cats = (await categories.ListAllAsync(ct)).Where(c => c.OrganisationId == organisationId && c.IsActive).OrderBy(c => c.Code).Select(c => new ChoiceItem(c.Id, c.Code, c.Name, c.ParentCategoryId)).ToList();
        return new SimulationChoices(cats, s.BusinessUnits.Where(b => b.IsActive).Select(b => new ChoiceItem(b.Id, b.Key, b.Name, b.DivisionId)).ToList(), s.Sites.Where(x => x.IsActive).Select(x => new ChoiceItem(x.Id, x.Key, x.Name, null)).ToList(),
            s.LegalEntities.Where(l => l.IsActive).Select(l => new ChoiceItem(l.Id, l.Key, l.Name, null)).ToList(), (await scales.GetAsync(ct)).Levels);
    }

    public async Task<SimulationSubject> BuildAsync(SimulationInputs i, CancellationToken ct = default)
    {
        await RequireAsync(i.OrganisationId, ct);
        var scale = await scales.GetAsync(ct);
        var s = await structure.GetAsync(i.OrganisationId, ct);
        var all = (await categories.ListAllAsync(ct)).Where(c => c.OrganisationId == i.OrganisationId).ToDictionary(c => c.Id);
        var codePath = new List<string>(); var idPath = new List<Guid>();
        if (i.CategoryId is { } cid && all.ContainsKey(cid))
            for (Guid? id = cid; id is { } x && all.TryGetValue(x, out var cat) && idPath.Count < 50; id = cat.ParentCategoryId) { codePath.Add(cat.Code); idPath.Add(cat.Id); }
        var bu = i.BusinessUnitId is { } b ? s.BusinessUnits.FirstOrDefault(x => x.Id == b) : null;
        var site = i.SiteId is { } si ? s.Sites.FirstOrDefault(x => x.Id == si) : null;
        var le = i.LegalEntityId is { } l ? s.LegalEntities.FirstOrDefault(x => x.Id == l) : null;
        var classification = scale.Canonical(i.Classification) ?? (string.IsNullOrWhiteSpace(i.Classification) ? scale.DefaultLevel : throw new Ecp.SharedKernel.DomainException("invalid_classification", $"'{i.Classification}' is not a classification level."));
        var facts = new ApprovalFacts(i.Value, string.IsNullOrWhiteSpace(i.Currency) ? null : i.Currency.Trim().ToUpperInvariant(), codePath, Clean(i.ContractType), le?.Key, bu?.Key, site?.Key, classification, Clean(i.RiskLevel), Clean(i.SupplierType),
            i.Custom.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value));
        var resource = new Ecp.Identity.Access.ResourceDescriptor("Contract", tenant.RequireTenantId(), i.OrganisationId, bu?.Id, bu?.DivisionId, site?.Id, idPath.Count > 0 ? idPath[0] : null, idPath.Count > 0 ? idPath : null, classification);
        return new SimulationSubject(facts, resource);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
