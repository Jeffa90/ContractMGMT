using Ecp.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

/// <summary>Which pack each organisation works from, and giving an organisation a pack. Assigning prepares drafts only; nothing is published.</summary>
[Authorize(Policy = "perm:packs.read")]
public sealed class AssignModel(IPackAssignments assignments, IPackLoader loader, IAuthorizationService authorisation) : PackPage(authorisation)
{
    public IReadOnlyList<OrganisationPackStatus> Organisations { get; private set; } = [];
    public IReadOnlyList<PackCatalogueEntry> Choices { get; private set; } = [];
    public PackAssignmentPreview? Preview { get; private set; }
    public PackAssignmentResult? Result { get; private set; }

    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    [BindProperty(SupportsGet = true)] public string? Version { get; set; }
    /// <summary>"pack key|version", as chosen on the form.</summary>
    [BindProperty(SupportsGet = true)] public string? Choice { get; set; }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (Choice is { Length: > 0 } && Choice.Split('|') is [var key, var version]) { Key = key; Version = version; }
        Organisations = await assignments.OrganisationsAsync(ct);
        Choices = (await loader.ListAsync(ct)).Where(e => e.IsUsable).OrderBy(e => e.PackKey, StringComparer.Ordinal).ThenByDescending(e => e.Version, StringComparer.Ordinal).ToList();
        if (OrganisationId is { } org && Key is not null && Version is not null) Preview = await assignments.PreviewAsync(org, Key, Version, ct);
    }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        if (!await HoldsAsync("packs.assign")) return Forbid();
        if (OrganisationId is not { } org || Key is null || Version is null) return BadRequest();
        Result = await assignments.AssignAsync(org, Key, Version, ct);
        await LoadAsync(ct);
        Preview = null;
        Flash("The pack was assigned. Drafts were prepared where there was nothing waiting. Review and publish them.");
        return Page();
    }, () => LoadAsync(ct));
}
