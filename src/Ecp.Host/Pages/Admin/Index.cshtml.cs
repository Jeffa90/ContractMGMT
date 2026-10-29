using Ecp.Host.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

public sealed record AdminCard(string Key, string Title, string Description, IReadOnlyList<AdminCardLink> Links);
public sealed record AdminCardLink(string Href, string Title, string Description);

/// <summary>The Administration landing page: every administration page the person may use, grouped by what they are for. Presentation only; each page still authorises itself.</summary>
[Authorize]
public sealed class IndexModel(NavigationService navigation) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public IReadOnlyList<AdminCard> Cards { get; private set; } = [];
    public IReadOnlyList<ActionQueue> Queues { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var nav = await navigation.GetAsync(ct);
        if (!nav.HasAdministration) return Forbid();
        Queues = nav.Queues;
        var descriptions = NavigationCatalogue.Administration.SelectMany(g => g.Entries).ToDictionary(e => e.Key, e => e.Description);
        var q = Q?.Trim();
        var cards = new List<AdminCard>();
        foreach (var section in nav.Administration)
        {
            var links = section.Items
                .Select(i => new AdminCardLink(i.Href, T(i.Text), T(descriptions.GetValueOrDefault(i.Key, ""))))
                .Where(l => string.IsNullOrEmpty(q) || l.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || l.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || T(section.Title).Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (links.Count > 0) cards.Add(new AdminCard(section.Key, T(section.Title), T(section.Description), links));
        }
        Cards = cards;
        return Page();
    }
}
