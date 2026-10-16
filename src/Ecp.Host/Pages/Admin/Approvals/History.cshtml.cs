using Ecp.Approvals;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Approvals;

/// <summary>The history of the approval matrix: every release (who published it, when, a checksum, whether it was a rollback) and the audit events about matrices, delegations, simulations and blocked submissions.</summary>
[Authorize(Policy = "perm:approvals.matrix.read")]
public sealed class HistoryModel(IConfigurationService configuration, IAuditBrowser audit, IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    public static readonly IReadOnlyList<(string Key, string Title, string Prefix)> Filters =
    [
        ("all", "Everything about approvals", "approval."), ("matrix", "Matrix publications", "approval.matrix."), ("delegation", "Delegations", "approval.delegation."),
        ("simulation", "Simulations", "approval.simulated"), ("blocked", "Submissions that were blocked", "approval.resolution_blocked"), ("steps", "Steps of plan-based approvals", "approval.step_approved")
    ];

    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty(SupportsGet = true)] public string Show { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<ReleaseDto> Releases { get; private set; } = [];
    public string? ReleasesDenied { get; private set; }
    public PagedResult<AuditEventDto> Events { get; private set; } = new([], 0, 1, 50);
    public string? EventsDenied { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Organisations = await OrganisationChoices.AllowedAsync(organisations, access, ct, "approvals.matrix.read");
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        if (OrganisationId is not { } org) return Page();
        try { Releases = await configuration.ReleasesAsync(ApprovalsModule.MatrixKind, org, ct); } catch (ForbiddenException) { ReleasesDenied = "You cannot read the version history."; }
        var prefix = Filters.FirstOrDefault(f => f.Key == Show).Prefix ?? "approval.";
        try { Events = await audit.QueryAsync(new AuditFilter(Action: prefix, OrganisationId: org, Page: Math.Max(1, PageNumber), PageSize: 50), ct); }
        catch (ForbiddenException) { EventsDenied = "You cannot read the audit trail."; }
        return Page();
    });
}
