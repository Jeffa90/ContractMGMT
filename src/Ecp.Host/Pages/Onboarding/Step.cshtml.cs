using Ecp.Configuration;
using Ecp.Setup.Onboarding;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Onboarding;

public sealed class StepModel(IOnboardingService onboarding, IOrganisationStructureService organisations, IPackLoader packs) : OnboardingPageModel(onboarding, organisations)
{
    public StepHelp Help { get; private set; } = null!;
    public OnboardingState? State { get; private set; }
    public IReadOnlyList<CsvIssue> Issues { get; private set; } = [];
    public IReadOnlyList<PackCatalogueEntry> Packs { get; private set; } = [];
    private async Task LoadPacksAsync(CancellationToken ct)
        => Packs = (await packs.ListAsync(ct)).Where(p => p.IsUsable && p.Info is not null).GroupBy(p => p.PackKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(p => Ecp.Packs.SemVer.TryParse(p.Version, out var v) ? v : new Ecp.Packs.SemVer(0, 0, 0, null)).First()).OrderBy(p => p.PackKey, StringComparer.Ordinal).ToList();

    [BindProperty] public string? Csv { get; set; }
    [BindProperty] public string? PackId { get; set; }
    public string Text { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
        => await GuardedAsync(async () =>
        {
            if (OnboardingHelp.Find(id) is not { } help || id == "review") return NotFound();
            Help = help;
            if (!await SelectOrganisationAsync(ct)) return NotFound();
            State = await Onboarding.GetAsync(OrganisationId, ct);
            Text = Onboarding.Export(State.Document, id);
            PackId = State.Document.PackId;
            if (id == "pack") await LoadPacksAsync(ct);
            return Page();
        });

    public async Task<IActionResult> OnPostAsync(string id, CancellationToken ct)
        => await GuardedAsync(async () =>
        {
            if (OnboardingHelp.Find(id) is not { } help || id == "review") return NotFound();
            Help = help;
            if (!await SelectOrganisationAsync(ct)) return NotFound();
            if (id == "pack")
            {
                State = await Onboarding.SelectPackAsync(OrganisationId, PackId, loadDefaults: Request.Form.ContainsKey("loadDefaults"), ct);
                Flash("The draft was updated.");
                return RedirectToPage(new { id, org = OrganisationId });
            }
            var result = await Onboarding.SaveSectionAsync(OrganisationId, id, Csv, ct);
            State = result.State; Issues = result.Issues;
            if (Issues.Count > 0) { Text = Csv ?? ""; return Page(); }   // nothing was saved: show the lines to fix, with the text as typed
            Flash("The draft was saved. Validate it on the review step before publishing.");
            return RedirectToPage(new { id, org = OrganisationId });
        }, async () => { State = await Onboarding.GetAsync(OrganisationId, ct); Text = Csv ?? ""; if (id == "pack") await LoadPacksAsync(ct); });
}
