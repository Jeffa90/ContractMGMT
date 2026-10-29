using Ecp.Access;
using Ecp.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.People;

/// <summary>People & Access: everyone the signed-in administrator may manage, what kind of access they have, and the way to change it. The service decides who appears.</summary>
[Authorize(Policy = "perm:users.manage")]
public sealed class IndexModel(IPersonAccessService people, DemoIdentityService demo) : EcpPageModel
{
    public IReadOnlyList<PersonRow> Rows { get; private set; } = [];
    public bool DemonstrationSignIn => people.DemonstrationSignInAllowed && demo.IsEnabled;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Rows = await people.ListAsync(ct); return Page(); });
}
