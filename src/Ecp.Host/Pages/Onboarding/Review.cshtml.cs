using Ecp.Configuration;
using Ecp.Setup.Onboarding;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Onboarding;

public sealed class ReviewModel(IOnboardingService onboarding, IOrganisationStructureService organisations) : OnboardingPageModel(onboarding, organisations)
{
    public OnboardingState? State { get; private set; }
    public IReadOnlyList<PlanLine> Plan { get; private set; } = [];
    public OnboardingPreview? Preview { get; private set; }
    public IReadOnlyList<OnboardingRelease> Releases { get; private set; } = [];

    [BindProperty] public string? Note { get; set; }
    [BindProperty] public int? ToRelease { get; set; }
    [BindProperty] public string? Reason { get; set; }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (!await SelectOrganisationAsync(ct)) return false;
        State = await Onboarding.GetAsync(OrganisationId, ct);
        Plan = await Onboarding.PlanAsync(OrganisationId, ct);
        Preview = await Onboarding.PreviewAsync(OrganisationId, ct);
        Releases = await Onboarding.ReleasesAsync(OrganisationId, ct);
        return true;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        => await GuardedAsync(async () => await LoadAsync(ct) ? Page() : NotFound());

    public async Task<IActionResult> OnPostValidateAsync(CancellationToken ct)
        => await GuardedAsync(async () =>
        {
            if (!await SelectOrganisationAsync(ct)) return NotFound();
            await Onboarding.ValidateAsync(OrganisationId, ct);
            return RedirectToPage(new { org = OrganisationId });
        }, () => LoadAsync(ct));

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken ct)
        => await GuardedAsync(async () =>
        {
            if (!await SelectOrganisationAsync(ct)) return NotFound();
            var release = await Onboarding.PublishAsync(OrganisationId, string.IsNullOrWhiteSpace(Note) ? "Published from the onboarding wizard" : Note.Trim(), ct);
            Flash($"Release {release.Number} was published and is now in effect.");
            return RedirectToPage(new { org = OrganisationId });
        }, () => LoadAsync(ct));

    public async Task<IActionResult> OnPostRollbackAsync(CancellationToken ct)
        => await GuardedAsync(async () =>
        {
            if (!await SelectOrganisationAsync(ct)) return NotFound();
            if (ToRelease is not { } n) throw new Ecp.SharedKernel.DomainException("release_required", "Choose the release to go back to.");
            var release = await Onboarding.RollbackAsync(OrganisationId, n, Reason ?? "", ct);
            Flash($"Release {release.Number} was published with the content of release {n}.");
            return RedirectToPage(new { org = OrganisationId });
        }, () => LoadAsync(ct));

    public async Task<IActionResult> OnPostDiscardAsync(CancellationToken ct)
        => await GuardedAsync(async () =>
        {
            if (!await SelectOrganisationAsync(ct)) return NotFound();
            await Onboarding.DiscardDraftAsync(OrganisationId, ct);
            Flash("The draft was discarded. A new draft starts from the current release.");
            return RedirectToPage(new { org = OrganisationId });
        }, () => LoadAsync(ct));
}
