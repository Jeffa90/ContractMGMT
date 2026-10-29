using System.Text.Json;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.SharedKernel;
using Ecp.Tenancy;

namespace Ecp.Setup.Onboarding;

/// <summary>Loads what already exists, as keys, for validation and preview.</summary>
internal sealed class OnboardingFactsLoader(IOrganisationStructureService structure, ICategoryAdministration categories, IUserService users, IProviderRegistry providers,
    IPackLoader packs, IClassificationScaleProvider scales, ICapabilityResolver capabilities)
{
    public async Task<OnboardingFacts> LoadAsync(Guid organisationId, CancellationToken ct)
    {
        var s = await structure.GetAsync(organisationId, ct);
        var cats = (await categories.ListAllAsync(ct)).Where(c => c.OrganisationId == organisationId);
        var emails = (await users.ListAsync(ct)).Select(u => u.Email.Trim().ToLowerInvariant()).ToHashSet();
        return new OnboardingFacts(organisationId, Set(s.LegalEntities.Select(x => x.Key)), Set(s.Divisions.Select(x => x.Key)), Set(s.BusinessUnits.Select(x => x.Key)),
            Set(s.Sites.Select(x => x.Key)), Set(cats.Select(c => c.Code)), emails, Set((await providers.ListAsync(ct)).Select(p => p.Key)), Set((await packs.ListAsync(ct)).Where(p => p.IsUsable && p.Info is not null).Select(p => p.PackKey)),
            await scales.GetAsync(ct), await capabilities.GetAsync(organisationId, ct));
    }

    private static ISet<string> Set(IEnumerable<string> v) => v.ToHashSet(StringComparer.Ordinal);
}

/// <summary>The onboarding document as a configuration kind: draft, validate, publish, version, roll back and audit all come from the Configuration module.</summary>
internal sealed class OrganisationOnboardingKind(OnboardingFactsLoader facts, OnboardingApplier applier) : IConfigurationKind
{
    public const string KindName = "OrganisationOnboarding";
    public string Kind => KindName;

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        if (organisationId == Guid.Empty) return new ValidationReport([new(ValidationSeverity.Error, "$", "organisation_required", "Onboarding belongs to one [[organisation|lower]].")]);
        OnboardingDocument document;
        try { document = OnboardingDocument.Parse(contentJson); }
        catch (DomainException ex) { return new ValidationReport([new(ValidationSeverity.Error, "$", ex.Code, ex.Message)]); }
        return OnboardingValidator.Validate(document, await facts.LoadAsync(organisationId, ct));
    }

    public async Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct)
        => await applier.ApplyAsync(organisationId, OnboardingDocument.Parse(contentJson), previousContentJson is null ? null : OnboardingDocument.Parse(previousContentJson), releaseNumber, ct);
}

public sealed record OnboardingState(Guid OrganisationId, Guid DraftId, OnboardingDocument Document, ValidationReport? Validation, bool IsPublishable, int? BasedOnRelease,
    int? CurrentRelease, DateTime UpdatedUtc, string LastEditedBy);

public sealed record PlanLine(string Section, int ToCreate, int AlreadyPresent, int ToEnd);
public sealed record SectionSave(OnboardingState State, IReadOnlyList<CsvIssue> Issues);
public sealed record OnboardingRelease(int Number, DateTime PublishedUtc, string PublishedBy, string Note, int? RollbackOf, int Items);

/// <summary>
/// The onboarding wizard's operations. Each call needs the <c>onboarding.run</c> permission for the organisation, and the underlying draft, validation,
/// publication and rollback are authorised again by the Configuration module (config.draft, config.publish, config.rollback).
/// </summary>
public interface IOnboardingService
{
    Task<OnboardingState> GetAsync(Guid organisationId, CancellationToken ct = default);
    Task<SectionSave> SaveSectionAsync(Guid organisationId, string step, string? csv, CancellationToken ct = default);
    Task<OnboardingState> SelectPackAsync(Guid organisationId, string? packId, bool loadDefaults, CancellationToken ct = default);
    Task<OnboardingState> ValidateAsync(Guid organisationId, CancellationToken ct = default);
    Task<OnboardingPreview> PreviewAsync(Guid organisationId, CancellationToken ct = default);
    Task<IReadOnlyList<PlanLine>> PlanAsync(Guid organisationId, CancellationToken ct = default);
    Task<ReleaseDto> PublishAsync(Guid organisationId, string note, CancellationToken ct = default);
    Task<ReleaseDto> RollbackAsync(Guid organisationId, int toRelease, string reason, CancellationToken ct = default);
    /// <summary>Publishes where this person may, otherwise submits the draft for a second person to review and publish.</summary>
    Task<PublishOutcome> SubmitOrPublishAsync(Guid organisationId, string note, CancellationToken ct = default);
    Task<RollbackOutcome> RequestRollbackAsync(Guid organisationId, int toRelease, string reason, CancellationToken ct = default);
    Task<GovernanceInfo?> GovernanceAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>The business-outcome checklist for the organisation: Required, Recommended and Optional items by the selected pack, with their state computed from facts.</summary>
    Task<ReadinessReport> ReadinessAsync(Guid organisationId, CancellationToken ct = default);
    Task<IReadOnlyList<OnboardingRelease>> ReleasesAsync(Guid organisationId, CancellationToken ct = default);
    Task DiscardDraftAsync(Guid organisationId, CancellationToken ct = default);
    string Export(OnboardingDocument document, string step);
}

internal sealed class OnboardingService(IConfigurationService configuration, IAccessService access, ITenantContext tenant, OnboardingFactsLoader facts, IClassificationScaleProvider scales,
    IPackLoader packs, IOnboardingReadiness readiness) : IOnboardingService
{
    private const string Kind = OrganisationOnboardingKind.KindName;

    private async Task RequireAsync(Guid organisationId, CancellationToken ct)
        => await access.RequireAsync("onboarding.run", new ResourceDescriptor("Configuration", tenant.RequireTenantId(), organisationId, Id: Kind, Dimensions: ScopeDimensions.OrganisationOnly), ct);

    private async Task<OnboardingState> ToStateAsync(DraftDto draft, CancellationToken ct)
    {
        var latest = await configuration.LatestReleaseAsync(Kind, draft.OrganisationId, ct);
        return new OnboardingState(draft.OrganisationId, draft.Id, OnboardingDocument.Parse(draft.ContentJson), draft.Validation, draft.IsPublishable, draft.BasedOnReleaseNumber,
            latest?.Number, draft.UpdatedUtc, draft.LastEditedBy);
    }

    public async Task<OnboardingState> GetAsync(Guid organisationId, CancellationToken ct = default)
    {
        await RequireAsync(organisationId, ct);
        return await ToStateAsync(await configuration.GetOrCreateDraftAsync(Kind, organisationId, null, ct), ct);
    }

    public async Task<SectionSave> SaveSectionAsync(Guid organisationId, string step, string? csv, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        var d = state.Document;
        IReadOnlyList<CsvIssue> issues;
        switch (step)
        {
            case "structure": { var r = OnboardingCsv.ReadStructure(csv); issues = r.Issues; if (r.IsClean) d = d with { Structure = OnboardingCsv.ToSection(r.Items) }; break; }
            case "categories": { var r = OnboardingCsv.ReadCategories(csv); issues = r.Issues; if (r.IsClean) d = d with { Categories = r.Items }; break; }
            case "types": { var r = OnboardingCsv.ReadTypes(csv); issues = r.Issues; if (r.IsClean) d = d with { ContractTypes = r.Items }; break; }
            case "owners": { var r = OnboardingCsv.ReadOwners(csv); issues = r.Issues; if (r.IsClean) d = d with { CategoryOwners = r.Items }; break; }
            case "viewers": { var r = OnboardingCsv.ReadViewers(csv); issues = r.Issues; if (r.IsClean) d = d with { ViewerScopes = r.Items }; break; }
            case "mappings": { var r = OnboardingCsv.ReadMappings(csv); issues = r.Issues; if (r.IsClean) d = d with { GroupMappings = r.Items }; break; }
            default: throw new DomainException("unknown_step", "That step does not hold a list.");
        }
        if (issues.Count > 0) return new SectionSave(state, issues);
        return new SectionSave(await ToStateAsync(await configuration.SaveDraftAsync(state.DraftId, d.ToJson(), ct), ct), issues);
    }

    public async Task<OnboardingState> SelectPackAsync(Guid organisationId, string? packId, bool loadDefaults, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        var d = state.Document with { PackId = string.IsNullOrWhiteSpace(packId) ? null : packId };
        if (d.PackId is not null)
        {
            var pack = (await packs.FindAsync(d.PackId, ct: ct))?.Info ?? throw new DomainException("unknown_pack", "That [[solution.pack|lower]] is not available here. It may not be installed, may not be trusted in this environment, or may be retired.");
            if (loadDefaults)
            {
                // Merge: anything already in the draft stays; the pack adds what is missing. Nothing is removed.
                var haveCats = d.Categories.Select(c => OnboardingDocument.Key(c.Code)).ToHashSet();
                var haveTypes = d.ContractTypes.Select(c => OnboardingDocument.Key(c.Code)).ToHashSet();
                d = d with
                {
                    Categories = [.. d.Categories, .. pack.Defaults.Categories.Where(c => !haveCats.Contains(OnboardingDocument.Key(c.Code))).Select(c => new CategoryEntry(c.Code, c.Name, c.ParentCode))],
                    ContractTypes = [.. d.ContractTypes, .. pack.Defaults.ContractTypes.Where(c => !haveTypes.Contains(OnboardingDocument.Key(c.Code))).Select(c => new ContractTypeEntry(c.Code, c.Name))],
                };
            }
        }
        return await ToStateAsync(await configuration.SaveDraftAsync(state.DraftId, d.ToJson(), ct), ct);
    }

    public async Task<OnboardingState> ValidateAsync(Guid organisationId, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        return await ToStateAsync(await configuration.ValidateAsync(state.DraftId, ct), ct);
    }

    public async Task<OnboardingPreview> PreviewAsync(Guid organisationId, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        return OnboardingSimulator.Run(state.Document, await scales.GetAsync(ct));
    }

    public async Task<IReadOnlyList<PlanLine>> PlanAsync(Guid organisationId, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        var f = await facts.LoadAsync(organisationId, ct);
        var latest = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        var previous = latest is null ? OnboardingDocument.Empty : OnboardingDocument.Parse(latest.ContentJson);
        var d = state.Document;
        static PlanLine Line(string name, IEnumerable<string> next, IEnumerable<string> existing, IEnumerable<string> before)
        {
            var n = next.ToHashSet(); var e = existing.ToHashSet();
            return new PlanLine(name, n.Count(k => !e.Contains(k)), n.Count(e.Contains), before.Count(k => !n.Contains(k)));
        }
        static IEnumerable<string> K(IEnumerable<StructureEntry> v) => v.Select(x => OnboardingDocument.Key(x.Key));
        return
        [
            Line("[[legal.entity|plural|sentence]]", K(d.Structure.LegalEntities), f.LegalEntities, K(previous.Structure.LegalEntities)),
            Line("Divisions", K(d.Structure.Divisions), f.Divisions, K(previous.Structure.Divisions)),
            Line("[[business.unit|plural|sentence]]", K(d.Structure.BusinessUnits), f.BusinessUnits, K(previous.Structure.BusinessUnits)),
            Line("Sites", K(d.Structure.Sites), f.Sites, K(previous.Structure.Sites)),
            Line("Categories", d.Categories.Select(c => OnboardingDocument.Key(c.Code)), f.Categories, previous.Categories.Select(c => OnboardingDocument.Key(c.Code))),
            Line("[[contract.owner|plural|sentence]]", d.CategoryOwners.Select(Canonical.Owner), [], previous.CategoryOwners.Select(Canonical.Owner)),
            Line("Viewer scopes", d.ViewerScopes.Select(Canonical.Viewer), [], previous.ViewerScopes.Select(Canonical.Viewer)),
            Line("Group mappings", d.GroupMappings.Select(Canonical.Mapping), [], previous.GroupMappings.Select(Canonical.Mapping)),
        ];
    }

    public async Task<ReleaseDto> PublishAsync(Guid organisationId, string note, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        if (state.IsPublishable) await readiness.RequireReadyAsync(organisationId, state, ct);   // an unvalidated draft is refused by the configuration service with its own reason; a valid one is never published while a Required item of the selected pack is open
        return await configuration.PublishAsync(state.DraftId, note, ct);
    }

    public async Task<PublishOutcome> SubmitOrPublishAsync(Guid organisationId, string note, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        if (state.IsPublishable) await readiness.RequireReadyAsync(organisationId, state, ct);
        return await configuration.SubmitOrPublishAsync(state.DraftId, note, ct);
    }

    public async Task<RollbackOutcome> RequestRollbackAsync(Guid organisationId, int toRelease, string reason, CancellationToken ct = default)
    {
        await RequireAsync(organisationId, ct);
        return await configuration.RequestRollbackAsync(Kind, organisationId, toRelease, reason, ct);
    }

    public async Task<ReadinessReport> ReadinessAsync(Guid organisationId, CancellationToken ct = default)
        => await readiness.GetAsync(organisationId, await GetAsync(organisationId, ct), ct);

    public async Task<GovernanceInfo?> GovernanceAsync(Guid organisationId, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        try { return await configuration.GovernanceAsync(state.DraftId, ct); }
        catch (ResourceNotFoundException) { return null; }
        catch (ForbiddenException) { return null; }
    }

    public async Task<ReleaseDto> RollbackAsync(Guid organisationId, int toRelease, string reason, CancellationToken ct = default)
    {
        await RequireAsync(organisationId, ct);
        return await configuration.RollbackAsync(Kind, organisationId, toRelease, reason, ct);
    }

    public async Task<IReadOnlyList<OnboardingRelease>> ReleasesAsync(Guid organisationId, CancellationToken ct = default)
    {
        await RequireAsync(organisationId, ct);
        return (await configuration.ReleasesAsync(Kind, organisationId, ct)).Select(r =>
        {
            var d = OnboardingDocument.Parse(r.ContentJson);
            var items = d.Structure.Count + d.Categories.Count + d.CategoryOwners.Count + d.ViewerScopes.Count + d.GroupMappings.Count;
            return new OnboardingRelease(r.Number, r.PublishedUtc, r.PublishedBy, r.Note, r.RollbackOfNumber, items);
        }).ToList();
    }

    public async Task DiscardDraftAsync(Guid organisationId, CancellationToken ct = default)
    {
        var state = await GetAsync(organisationId, ct);
        await configuration.DiscardDraftAsync(state.DraftId, ct);
    }

    public string Export(OnboardingDocument d, string step) => step switch
    {
        "structure" => OnboardingCsv.WriteStructure(d.Structure),
        "categories" => OnboardingCsv.WriteCategories(d.Categories),
        "types" => OnboardingCsv.WriteTypes(d.ContractTypes),
        "owners" => OnboardingCsv.WriteOwners(d.CategoryOwners),
        "viewers" => OnboardingCsv.WriteViewers(d.ViewerScopes),
        "mappings" => OnboardingCsv.WriteMappings(d.GroupMappings),
        _ => ""
    };
}
