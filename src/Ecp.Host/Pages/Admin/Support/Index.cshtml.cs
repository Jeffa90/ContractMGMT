using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Tenancy;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Support;

/// <summary>
/// Support and emergency access for installation administrators (IDN-03). Opening or confirming access needs a recent sign-in, a reason and a time limit; the tenant's own
/// administrators see it while it is active and afterwards. The person keeps their own identity: nothing here impersonates anyone or reveals a credential.
/// </summary>
[Authorize(Policy = "perm:support.elevate")]
public sealed class IndexModel(IElevationService elevations, ITenantDirectory tenants, TenantContext tenant, SessionIssuer sessions, CurrentUser current) : EcpPageModel
{
    public IReadOnlyList<TenantInfo> Tenants { get; private set; } = [];
    public IReadOnlyList<ElevationDto> Open { get; private set; } = [];
    public Guid? Me => current.UserId;
    public IReadOnlyDictionary<Guid, string> TenantNames { get; private set; } = new Dictionary<Guid, string>();
    public int MaxMinutes => Elevation.MaxDurationMinutes;

    private async Task LoadAsync(CancellationToken ct)
    {
        using (tenant.UseInstallationScope()) Tenants = (await tenants.ListAsync(ct)).Where(t => t.Id != SystemTenant.Id && t.Status == Ecp.Tenancy.Domain.TenantStatus.Active).OrderBy(t => t.Name).ToList();
        TenantNames = Tenants.ToDictionary(t => t.Id, t => t.Name);
        Open = await elevations.ListOpenAcrossTenantsAsync(ct);
    }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostOpenAsync(Guid tenantId, ElevationKind kind, string reason, int minutes, string? ceiling, CancellationToken ct) => RunAsync(async () =>
    {
        var e = await elevations.OpenAsync(new OpenElevationRequest(tenantId, kind, reason, TimeSpan.FromMinutes(minutes), ceiling), ct);
        if (e.State == ElevationState.Active)
        {
            await sessions.BeginElevationAsync(HttpContext, tenantId, e.Id);
            return Redirect("/");
        }
        Flash("Emergency access is waiting for a second installation administrator to confirm it. It starts when they do.");
        return RedirectToPage();
    }, reload: () => LoadAsync(default));

    public Task<IActionResult> OnPostConfirmAsync(Guid tenantId, Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await elevations.ConfirmAsync(tenantId, id, ct);
        Flash("Access confirmed. The administrator who asked for it can now start working in the tenant.");
        return RedirectToPage();
    }, reload: () => LoadAsync(default));

    /// <summary>Starts working in a tenant under an elevation that is already active and is yours.</summary>
    public Task<IActionResult> OnPostStartAsync(Guid tenantId, Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var grant = current.UserId is { } me ? await elevations.ResolveActiveAsync(tenantId, id, me, ct) : null;
        if (grant is null) { Flash("That access is not active, or it is not yours."); return RedirectToPage(); }
        await sessions.BeginElevationAsync(HttpContext, tenantId, id);
        return Redirect("/");
    }, reload: () => LoadAsync(default));

    public Task<IActionResult> OnPostEndAsync(Guid tenantId, Guid id, string? reason, CancellationToken ct) => RunAsync(async () =>
    {
        await elevations.EndAsync(tenantId, id, reason, ct);
        Flash("Access ended.");
        return RedirectToPage();
    }, reload: () => LoadAsync(default));
}
