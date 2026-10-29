using Ecp.Access;
using Ecp.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Account;

/// <summary>Leaves a Test/Demo preview and returns the administrator to their own session and to the person's page. Reachable from the banner on every page while previewing.</summary>
[Authorize]
public sealed class ExitPreviewModel(IPreviewService previews, SessionIssuer sessions, Ecp.SharedKernel.PreviewContext preview) : EcpPageModel
{
    public IActionResult OnGet() => NotFound(); // changes state: POST only

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var target = preview.TargetUserId;
        var id = SessionIssuer.IdentifyPreview(User);
        if (id is { } previewId) await previews.EndAsync(previewId, "Exited by the administrator", ct);
        await sessions.EndPreviewAsync(HttpContext);
        Flash("You have left the preview. You are yourself again.");
        return LocalRedirect(preview.Active ? "/Admin/People/Person/" + target : "/Admin/People");
    }
}
