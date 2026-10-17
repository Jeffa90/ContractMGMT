using Ecp.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

[Authorize(Policy = "perm:packs.read")]
public sealed class DetailsModel(IPackManager packs, IAuthorizationService authorisation) : PackPage(authorisation)
{
    public PackDetails? Pack { get; private set; }
    [BindProperty] public string? Reason { get; set; }
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Version { get; set; } = "";

    private async Task LoadAsync(CancellationToken ct) => Pack = await packs.GetAsync(Key, Version, ct);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        return Pack is null ? NotFound() : Page();
    });

    public Task<IActionResult> OnPostRetireAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        await packs.RetireAsync(Key, Version, Reason, ct);
        Flash("The pack version was retired. Organisations already using it are not changed.");
        return RedirectToPage(new { key = Key, version = Version });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostReinstateAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        await packs.ReinstateAsync(Key, Version, ct);
        Flash("The pack version can be used again.");
        return RedirectToPage(new { key = Key, version = Version });
    }, () => LoadAsync(ct));
}
