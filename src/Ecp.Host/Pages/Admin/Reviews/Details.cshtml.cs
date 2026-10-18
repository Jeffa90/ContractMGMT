using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Reviews;

/// <summary>
/// One submitted change: what it would change in plain words, the result of the checks, who did what so far, and the decisions open to this person. The page only reads and calls the configuration service; the service
/// decides who may publish, reject, return or withdraw, and a reviewer never becomes the editor by opening or deciding.
/// </summary>
[Authorize(Policy = "perm:config.draft|config.publish|terminology.draft|terminology.publish|capabilities.draft|capabilities.publish")]
public sealed class DetailsModel(IConfigurationService configuration, IOrganisationStructureService organisations, IPeopleDirectory people, IAuthorizationService authorisation)
    : ReviewPage(configuration, organisations, people, authorisation)
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public string? Note { get; set; }
    [BindProperty] public string? Comment { get; set; }
    public ReviewDetail? Review { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Review = await Configuration.GetReviewAsync(Id, ct);
        var d = Review.Draft;
        await ResolveNamesAsync(new[] { d.CreatedBy, d.LastEditedBy, d.SubmittedBy, d.DecidedBy }.Concat(Review.History.Select(h => h.Actor)), [d.OrganisationId], ct);
    }

    /// <summary>Approve and publish: exactly the content that was submitted, by someone other than the people who made it.</summary>
    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        var release = await Configuration.PublishAsync(Id, Note ?? "", ct);
        Flash(release.RollbackOfNumber is { } rb ? $"Published as release {release.Number}: release {rb} is back in force. Earlier releases are unchanged." : $"Published as release {release.Number}. It is in force now.");
        return RedirectToPage("Index");
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostRejectAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await Configuration.RejectAsync(Id, Comment ?? "", ct);
        Flash("Rejected. Your comment is kept with the change.");
        return RedirectToPage("Index");
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostReturnAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await Configuration.ReturnForReworkAsync(Id, Comment ?? "", ct);
        Flash("Sent back for changes. Your comment is kept with the change.");
        return RedirectToPage("Index");
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostWithdrawAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await Configuration.WithdrawSubmissionAsync(Id, Comment, ct);
        Flash("Withdrawn. The draft stays as it is; submit it again when you want it reviewed.");
        return RedirectToPage("Index", new { show = "mine" });
    }, () => LoadAsync(ct));
}
