using Ecp.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

/// <summary>Steps 2 and 3. The validation result and a preview of what installing would do, then an explicit confirmation. The person who uploaded the file is the only one who can install it.</summary>
[Authorize(Policy = "perm:packs.validate")]
public sealed class ReviewModel(IPackManager packs, IAuthorizationService authorisation) : PackPage(authorisation)
{
    public PackInstallPreview? Preview { get; private set; }
    public PackInstallResult? Installed { get; private set; }
    [BindProperty(SupportsGet = true)] public Guid Token { get; set; }
    [BindProperty] public bool Confirmed { get; set; }

    private async Task LoadAsync(CancellationToken ct) => Preview = await packs.PreviewAsync(Token, ct);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        return Preview is null ? NotFound() : Page();
    });

    public async Task<IActionResult> OnGetReportAsync(CancellationToken ct)
    {
        try
        {
            var csv = await packs.ReportCsvAsync(Token, ct);
            return csv is null ? NotFound() : File(csv, "text/csv; charset=utf-8", "pack-validation-report.csv");
        }
        catch (Ecp.SharedKernel.ForbiddenException) { return Forbid(); }
    }

    public Task<IActionResult> OnPostInstallAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        if (!await HoldsAsync("packs.install")) return Forbid();
        if (!Confirmed)
        {
            ModelState.AddModelError("Confirmed", "Tick the box to confirm that you have read the result and want to install this pack.");
            await LoadAsync(ct);
            return Preview is null ? NotFound() : Page();
        }
        Installed = await packs.InstallAsync(Token, ct);
        Flash(Installed.AlreadyAvailable ? "That exact pack was already installed. Nothing was changed." : "The pack was installed. It is available to assign, and nothing in any organisation has changed.");
        return Installed.Entry is { } e ? RedirectToPage("Details", new { key = e.PackKey, version = e.Version }) : RedirectToPage("Index");
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostCancelAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await packs.CancelUploadAsync(Token, ct);
        Flash("The upload was discarded. Nothing was installed.");
        return RedirectToPage("Index");
    });
}
