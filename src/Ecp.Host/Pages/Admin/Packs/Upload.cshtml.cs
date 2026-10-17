using Ecp.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

/// <summary>Step 1 of 3. The file goes to quarantine and is checked; nothing is installed.</summary>
[Authorize(Policy = "perm:packs.upload")]
[RequestSizeLimit(9 * 1024 * 1024)]
public sealed class UploadModel(IPackManager packs, IAuthorizationService authorisation) : PackPage(authorisation)
{
    public long MaxMegabytes { get; } = 8;

    public void OnGet() { }

    public Task<IActionResult> OnPostAsync(IFormFile? file, CancellationToken ct) => RunAsync(async () =>
    {
        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError("file", "Choose a pack file (.ecpkg) to check.");
            return Page();
        }
        await using var stream = file.OpenReadStream();
        var result = await packs.UploadAsync(stream, file.FileName, ct);
        return RedirectToPage("Review", new { token = result.Token });
    });
}
