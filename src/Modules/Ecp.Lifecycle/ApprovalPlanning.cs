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

/// <summary>The inputs to the facts of a submission, in one neutral shape. A live submission and a simulation both fill this in and hand it to <see cref="ApprovalFactService"/>, so they cannot drift apart.</summary>
internal sealed record FactInput(
    Guid OrganisationId, decimal? Value, string? Currency, Guid? CategoryId, Guid? BusinessUnitId, Guid? SiteId, Guid? LegalEntityId, string Classification, string? ContractType, string? RiskLevel,
    string? SupplierType, IReadOnlyDictionary<string, string?> Custom);

internal sealed record FactResult(ApprovalFacts Facts, IReadOnlyList<Guid> CategoryPath, Guid? BusinessUnitId, Guid? DivisionId, Guid? SiteId);

/// <summary>
/// The one place the facts an approval matrix is judged on are made: category path as codes, structure as keys, the classifying facts of the contract and the custom values an administrator has
/// made available. Facts the platform does not hold are left to contributors; an unknown fact never satisfies a condition.
/// </summary>
internal sealed class ApprovalFactService(ICategoryAdministration categories, IOrganisationStructureService structure, IEnumerable<IApprovalFactContributor> contributors)
{
    public async Task<FactResult> BuildAsync(FactInput i, string subjectKind, Guid subjectId, CancellationToken ct)
    {
        var all = (await categories.ListAllAsync(ct)).Where(c => c.OrganisationId == i.OrganisationId).ToDictionary(c => c.Id);
        var codes = new List<string>(); var ids = new List<Guid>();
        if (i.CategoryId is { } cid)
            for (Guid? id = cid; id is { } x && all.TryGetValue(x, out var cat) && ids.Count < 50; id = cat.ParentCategoryId) { codes.Add(cat.Code); ids.Add(cat.Id); }
        var s = await structure.GetAsync(i.OrganisationId, ct);
        var bu = i.BusinessUnitId is { } b ? s.BusinessUnits.FirstOrDefault(x => x.Id == b) : null;
        var site = i.SiteId is { } si ? s.Sites.FirstOrDefault(x => x.Id == si) : null;
        var le = i.LegalEntityId is { } l ? s.LegalEntities.FirstOrDefault(x => x.Id == l) : null;
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var custom = i.Custom.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).ToDictionary(kv => kv.Key.Trim(), kv => Clean(kv.Value), StringComparer.OrdinalIgnoreCase);
        var facts = new ApprovalFacts(i.Value, Clean(i.Currency)?.ToUpperInvariant(), codes, Clean(i.ContractType), le?.Key, bu?.Key, site?.Key, i.Classification, Clean(i.RiskLevel), Clean(i.SupplierType), custom);
        foreach (var contributor in contributors) facts = await contributor.EnrichAsync(facts, i.OrganisationId, subjectKind, subjectId, ct);
        return new FactResult(facts, ids, bu?.Id, bu?.DivisionId, site?.Id);
    }
}

/// <summary>Turns a contract or Variation into the facts the approval matrix is judged on, through the same service a simulation uses.</summary>
internal sealed class ApprovalFactsBuilder(ApprovalFactService service, IContractCustomFieldReader customReader)
{
    /// <summary>The fields a Variation can propose that are also facts, and the custom fields it proposes under the "cf." prefix.</summary>
    public async Task<ApprovalFacts> BuildAsync(SubjectView s, CancellationToken ct) => (await service.BuildAsync(await InputAsync(s, ct), s.Kind, s.Id, ct)).Facts;

    public async Task<FactInput> InputAsync(SubjectView s, CancellationToken ct)
    {
        var c = s.Contract;
        decimal? value = c.EstimatedValue; var currency = c.CurrencyCode; var risk = c.RiskLevel; Guid? bu = c.BusinessUnitId, site = c.SiteId, le = c.LegalEntityId;
        var custom = new Dictionary<string, string?>(await customReader.ApprovalFactsAsync(c.Id, ct), StringComparer.OrdinalIgnoreCase);
        if (s.IsVariation)
        {
            // What a Variation asks to be approved is the contract as it would be once applied: its proposed value and currency, risk, structure and custom values.
            var proposed = JsonSerializer.Deserialize<Dictionary<string, string?>>(s.Variation!.ProposedChangesJson) ?? [];
            if (proposed.TryGetValue("estimatedValue", out var v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) value = d;
            if (proposed.TryGetValue("currencyCode", out var cur) && !string.IsNullOrWhiteSpace(cur)) currency = cur.Trim().ToUpperInvariant();
            if (proposed.TryGetValue("riskLevel", out var r)) risk = string.IsNullOrWhiteSpace(r) ? null : r;
            if (proposed.TryGetValue("businessUnitId", out var b)) bu = Guid.TryParse(b, out var g) ? g : null;
            if (proposed.TryGetValue("siteId", out var st)) site = Guid.TryParse(st, out var g2) ? g2 : null;
            if (proposed.TryGetValue("legalEntityId", out var l)) le = Guid.TryParse(l, out var g3) ? g3 : null;
            var usable = (await customReader.ApprovalFactKeysAsync(c.OrganisationId, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, val) in proposed.Where(kv => kv.Key.StartsWith("cf.", StringComparison.Ordinal)))
                if (usable.Contains(key[3..])) custom[key[3..]] = val;
        }
        return new FactInput(s.OrganisationId, value, currency, c.CategoryId, bu, site, le, s.Classification, c.ContractType, risk, c.SupplierType, custom);
    }
}

/// <summary>What a matrix may be judged on in this platform: every dimension has a live fact, and the custom fields an administrator has marked usable in approvals.</summary>
internal sealed class LifecycleFactSource(IContractCustomFieldReader customReader) : IApprovalFactSource
{
    public async Task<ApprovalFactCatalogue> CatalogueAsync(Guid organisationId, CancellationToken ct = default)
        => new(new HashSet<string>(StringComparer.Ordinal)
        {
            ApprovalFactCatalogue.Value, ApprovalFactCatalogue.Categories, ApprovalFactCatalogue.ContractTypes, ApprovalFactCatalogue.LegalEntities, ApprovalFactCatalogue.BusinessUnits, ApprovalFactCatalogue.Sites,
            ApprovalFactCatalogue.Classifications, ApprovalFactCatalogue.RiskLevels, ApprovalFactCatalogue.SupplierTypes
        }, (await customReader.ApprovalFactKeysAsync(organisationId, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase));
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
    ICategoryAdministration categories, IOrganisationStructureService structure, Ecp.Identity.IAccessService access, Ecp.SharedKernel.ITenantContext tenant, Ecp.Identity.IClassificationScaleProvider scales,
    ApprovalFactService factService, IContractLifecyclePort contractPort, ISecuredContractService securedContracts, IContractCustomFieldReader customReader) : IApprovalSimulationInputs
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

    public async Task<SimulationInputs> FromContractAsync(Guid contractId, CancellationToken ct = default)
    {
        var descriptor = await securedContracts.DescribeAsync(contractId, ct);               // 404 when the contract does not exist for this person
        await access.RequireAsync("contracts.read", descriptor, ct);
        var c = await contractPort.FindAsync(contractId, ct) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
        await RequireAsync(c.OrganisationId, ct);
        return new SimulationInputs(c.OrganisationId, c.EstimatedValue, c.CurrencyCode, c.CategoryId, c.BusinessUnitId, c.SiteId, c.LegalEntityId, c.Classification, c.ContractType, c.RiskLevel, c.SupplierType,
            new Dictionary<string, string?>(await customReader.ApprovalFactsAsync(contractId, ct)));
    }

    public async Task<SimulationSubject> BuildAsync(SimulationInputs i, CancellationToken ct = default)
    {
        await RequireAsync(i.OrganisationId, ct);
        var scale = await scales.GetAsync(ct);
        var classification = scale.Canonical(i.Classification) ?? (string.IsNullOrWhiteSpace(i.Classification) ? scale.DefaultLevel : throw new Ecp.SharedKernel.DomainException("invalid_classification", $"'{i.Classification}' is not a classification level."));
        var result = await factService.BuildAsync(new FactInput(i.OrganisationId, i.Value, i.Currency, i.CategoryId, i.BusinessUnitId, i.SiteId, i.LegalEntityId, classification, i.ContractType, i.RiskLevel, i.SupplierType, i.Custom), "Simulation", Guid.Empty, ct);
        var resource = new Ecp.Identity.Access.ResourceDescriptor("Contract", tenant.RequireTenantId(), i.OrganisationId, result.BusinessUnitId, result.DivisionId, result.SiteId,
            result.CategoryPath.Count > 0 ? result.CategoryPath[0] : null, result.CategoryPath.Count > 0 ? result.CategoryPath : null, classification);
        return new SimulationSubject(result.Facts, resource);
    }
}
