using Ecp.Approvals;
using Ecp.Approvals.Definitions;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Approvals;

/// <summary>The approval matrices in force for an organisation, read-only and in plain language. Editing goes through the configuration pages (draft, validate, publish) so every change is versioned and audited.</summary>
[Authorize(Policy = "perm:approvals.matrix.read")]
public sealed class IndexModel(IApprovalSimulator simulator, IApprovalMatrixStore store, IConfigurationService configuration, IOrganisationStructureService organisations, IApprovalMatrixReferences references, IAccessService access) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<MatrixSummary> Summaries { get; private set; } = [];
    public LoadedMatrices? Published { get; private set; }
    public IReadOnlyList<string> UsedBy { get; private set; } = [];
    public bool HasDraft { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Organisations = await OrganisationChoices.AllowedAsync(organisations, access, ct, "approvals.matrix.read");
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        if (OrganisationId is not { } org) return Page();
        Summaries = await simulator.MatricesAsync(org, MatrixSource.Published, ct);     // checks approvals.matrix.read for this organisation
        Published = await store.TryLoadPublishedAsync(org, ct);
        UsedBy = await references.ReferencedMatrixCodesAsync(org, ct);
        try { HasDraft = await configuration.FindOpenDraftAsync(ApprovalsModule.MatrixKind, org, ct) is not null; } catch (Ecp.SharedKernel.ForbiddenException) { HasDraft = false; }
        return Page();
    });
}
