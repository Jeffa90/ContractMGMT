using Ecp.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

/// <summary>The publishers this tenant trusts to sign packs. Only public keys are ever shown or stored. Restricted to people who hold the trust permission, with a recent sign-in.</summary>
[Authorize(Policy = "perm:packs.trust.manage")]
public sealed class TrustKeysModel(IPackTrustKeys keys, IAuthorizationService authorisation) : PackPage(authorisation)
{
    public IReadOnlyList<TrustKeyView> Items { get; private set; } = [];
    [BindProperty] public string KeyId { get; set; } = "";
    [BindProperty] public string Label { get; set; } = "";
    [BindProperty] public string PublicKeyPem { get; set; } = "";
    [BindProperty] public DateTime? NotBeforeUtc { get; set; }
    [BindProperty] public DateTime? NotAfterUtc { get; set; }
    [BindProperty] public string? Reason { get; set; }

    private async Task LoadAsync(CancellationToken ct) => Items = await keys.ListAsync(ct);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => StepUpAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostRegisterAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        await keys.RegisterAsync(KeyId, Label, PublicKeyPem, NotBeforeUtc, NotAfterUtc, ct);
        Flash("The publisher key was registered. Packs signed with it are now trusted here.");
        return RedirectToPage();
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostRevokeAsync(string keyId, CancellationToken ct) => StepUpAsync(async () =>
    {
        await keys.RevokeAsync(keyId, Reason, ct);
        Flash("The publisher key was revoked. Packs signed only with it are no longer trusted.");
        return RedirectToPage();
    }, () => LoadAsync(ct));
}
