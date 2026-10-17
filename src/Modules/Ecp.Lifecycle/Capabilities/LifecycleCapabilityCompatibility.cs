using Ecp.Configuration;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Lifecycle.Capabilities;

/// <summary>
/// Whether a capability set is coherent with what the organisation's process actually does. Two rules, both found through the closed vocabularies (never by a state or pack name):
/// a published workflow must not use a capability that is off (it would offer steps nobody can take), and a capability must not be turned off from under work already in flight. Used when a capability draft is
/// validated, when a pack is assigned, and by the readiness checks.
/// </summary>
internal sealed class LifecycleCapabilityCompatibility(LifecycleDbContext db, IConfigurationReleases releases, IOrganisationStructureService organisations, IOrganisationPackContent packContent,
    IOptions<LifecycleOptions> options) : ICapabilityCompatibility
{
    public async Task<IReadOnlyList<CapabilityFinding>> CheckAsync(Guid organisationId, CapabilitySet proposed, CapabilitySet? current, CancellationToken ct = default)
    {
        var findings = new List<CapabilityFinding>();

        // 1. Workflows must not use a capability that is off. The Variation workflow is only in play when Variations are on.
        var lifecycle = await DefinitionAsync(WorkflowKinds.ContractLifecycle, organisationId, ct);
        var variation = proposed.IsEnabled(CapabilityKeys.Variations) ? await DefinitionAsync(WorkflowKinds.Variation, organisationId, ct) : null;
        foreach (var (name, definition) in new[] { ("lifecycle", lifecycle), ("Variation workflow", variation) })
        {
            if (definition is null) continue;
            foreach (var group in WorkflowCapabilityUse.Of(definition).Where(u => !proposed.IsEnabled(u.Capability)).GroupBy(u => u.Capability))
                findings.Add(new(ValidationSeverity.Error, group.Key, "workflow_uses_disabled_capability",
                    $"The published {name} uses {CapabilityCatalogue.ByKey[group.Key].Label} ({string.Join("; ", group.Select(g => g.What).Distinct().Take(3))}). Publish a {name} without it first, or keep the capability on."));
        }

        // 2. A capability that is on now and off in the proposal must have nothing in flight. A capability that is already off is not re-checked as an error: work under way can still be finished.
        foreach (var key in CapabilityKeys.All.Where(k => !proposed.IsEnabled(k)))
        {
            var inFlight = await InFlightAsync(key, organisationId, ct);
            if (inFlight == 0) continue;
            var turningOff = current is not null && current.IsEnabled(key);
            findings.Add(new(turningOff ? ValidationSeverity.Error : ValidationSeverity.Warning, key, turningOff ? "capability_in_use" : "capability_off_work_in_flight",
                turningOff ? $"{inFlight} item(s) are still under way in {CapabilityCatalogue.ByKey[key].Label}. Finish or cancel them first, then turn it off."
                           : $"{inFlight} item(s) are still under way in {CapabilityCatalogue.ByKey[key].Label}, which is off. They can be finished or cancelled, but nothing new can be started."));
        }
        return findings;
    }

    private async Task<WorkflowDefinition?> DefinitionAsync(string kind, Guid organisationId, CancellationToken ct)
    {
        try
        {
            var latest = await releases.LatestAsync(kind, organisationId, ct);
            string? json = latest?.ContentJson;
            if (json is null)
            {
                // Nothing published yet: the organisation's pack provides the starting definition, so that is what its process will be. Read only; nothing is installed.
                var packId = await organisations.ResolveAsync(organisationId, options.Value.DefaultPackId, ct);
                json = (await packContent.GetStartingDocumentAsync(organisationId, packId, kind, ct)).Json;
            }
            return WorkflowDefinitionParser.Parse(json).Definition;
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return null; }          // no definition can be read: nothing to check
    }

    private async Task<int> InFlightAsync(string capability, Guid org, CancellationToken ct) => capability switch
    {
        CapabilityKeys.LegalReview => await db.LegalReviews.AsNoTracking().CountAsync(r => r.OrganisationId == org && (r.Status == LegalReviewStatus.AwaitingLegal || r.Status == LegalReviewStatus.InReview || r.Status == LegalReviewStatus.ReworkRequired), ct),
        CapabilityKeys.RegisterAuthorisation => await db.Approvals.AsNoTracking().CountAsync(a => a.OrganisationId == org && a.Status == ApprovalStatus.Pending, ct),
        CapabilityKeys.Variations => await db.Variations.AsNoTracking().CountAsync(v => v.OrganisationId == org && v.IsOpen, ct),
        CapabilityKeys.Renewals => await db.Renewals.AsNoTracking().CountAsync(r => r.OrganisationId == org && r.Status == RenewalStatus.InProgress, ct),
        CapabilityKeys.SignatureRequests => await db.SignatureRequests.AsNoTracking().CountAsync(s => s.OrganisationId == org && s.IsCurrent && (s.Status == SignatureRequestStatus.Sent || s.Status == SignatureRequestStatus.Delivered), ct),
        _ => 0
    };
}
