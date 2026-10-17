using Ecp.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

/// <summary>The catalogue of solution packs this installation can offer: those shipped with it and those an administrator has installed.</summary>
[Authorize(Policy = "perm:packs.read")]
public sealed class IndexModel(IPackManager packs, IAuthorizationService authorisation) : PackPage(authorisation)
{
    public IReadOnlyList<PackListItem> Items { get; private set; } = [];
    public string Show { get; private set; } = "all";

    public Task<IActionResult> OnGetAsync(string? show, CancellationToken ct) => RunAsync(async () =>
    {
        Show = show is "shipped" or "installed" ? show : "all";
        var all = await packs.ListAsync(ct);
        Items = Show switch { "shipped" => all.Where(i => i.Entry.IsShipped).ToList(), "installed" => all.Where(i => !i.Entry.IsShipped).ToList(), _ => all };
        return Page();
    });
}
