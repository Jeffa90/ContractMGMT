using Ecp.Configuration;
using Ecp.Configuration.Domain;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Reviews;

/// <summary>
/// The review queue: configuration that has been put forward for a second person, with plain-language summaries. It lists what the signed-in person may decide on and what they put forward themselves; the service decides
/// that, and it only ever lists configuration of this tenant in organisations the person has rights in.
/// </summary>
[Authorize(Policy = "perm:config.draft|config.publish|terminology.draft|terminology.publish|capabilities.draft|capabilities.publish")]
public sealed class IndexModel(IConfigurationService configuration, IOrganisationStructureService organisations, IPeopleDirectory people, IAuthorizationService authorisation)
    : ReviewPage(configuration, organisations, people, authorisation)
{
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    public IReadOnlyList<ReviewItem> Items { get; private set; } = [];
    public IReadOnlyList<ReviewItem> All { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        All = await Configuration.ReviewQueueAsync(ct);
        Items = Show switch
        {
            "mine" => All.Where(i => i.IsMine).ToList(),
            "returned" => All.Where(i => i.State is ReviewState.Rejected or ReviewState.ReturnedForRework).ToList(),
            "all" => All,
            _ => All.Where(i => i.State == ReviewState.Submitted && i.CanDecide).ToList(),
        };
        await ResolveNamesAsync(All.SelectMany(i => new[] { i.CreatedBy, i.LastEditedBy, i.SubmittedBy, i.DecidedBy }), All.Select(i => i.OrganisationId), ct);
        return Page();
    });
}
