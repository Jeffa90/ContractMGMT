using Ecp.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Users;

[Authorize(Policy = "perm:users.manage")]
public sealed class IndexModel(IPeopleDirectory people, IUserAdministration admin) : EcpPageModel
{
    public IReadOnlyList<PersonItem> People { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await RunAsync(async () => { People = await people.ManageableUsersAsync(ct); return Page(); });

    public Task<IActionResult> OnPostInviteAsync(string displayName, string email, CancellationToken ct) => RunAsync(async () =>
    {
        var id = await admin.InviteAsync(displayName, email, ct);
        Flash("Invitation recorded. The person can sign in once an identity provider vouches for their email address.");
        return RedirectToPage("Details", new { id });
    }, reload: async () => People = await people.ManageableUsersAsync());
}
