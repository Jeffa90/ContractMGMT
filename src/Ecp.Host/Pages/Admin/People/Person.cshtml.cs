using Ecp.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.People;

/// <summary>One person: what they can do today in plain English, where each piece of access came from, and the way to change or end it.</summary>
[Authorize(Policy = "perm:users.manage")]
public sealed class PersonModel(IEffectiveAccess effective, IAccessProfileService profiles, IPreviewService previews, Ecp.Host.Security.SessionIssuer sessions) : EcpPageModel
{
    public EffectiveAccessReport Report { get; private set; } = null!;
    public IReadOnlyList<ProfileApplicationInfo> Applications { get; private set; } = [];
    public bool PreviewOffered => previews.Available;
    public PreviewEligibility Eligibility { get; private set; } = new(false, null);
    public int PreviewMinutes => (int)previews.Lifetime.TotalMinutes;

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    public Task<IActionResult> OnPostEndAsync(Guid id, Guid applicationId, string? reason, CancellationToken ct) => StepUpRunAsync(async () =>
    {
        await profiles.EndApplicationAsync(applicationId, reason ?? "", ct);
        Flash("That access was ended. Anything given separately was left alone.");
        return RedirectToPage(new { id });
    }, reload: () => LoadAsync(id, ct));

    /// <summary>Test/Demo only. Needs a recent sign-in and a reason; the service decides who may preview whom.</summary>
    public Task<IActionResult> OnPostPreviewAsync(Guid id, string? reason, CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var grant = await previews.StartAsync(id, reason ?? "", ct);
        await sessions.BeginPreviewAsync(HttpContext, grant.PreviewId);
        return LocalRedirect("/");
    }, reload: () => LoadAsync(id, ct));

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Report = await effective.ForUserAsync(id, ct);
        Applications = await profiles.ApplicationsOfAsync(id, includeEnded: false, ct);
        if (previews.Available) Eligibility = await previews.CanPreviewAsync(id, ct);
    }
}
