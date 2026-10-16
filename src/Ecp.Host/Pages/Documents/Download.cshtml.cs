using Ecp.Contracts;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Documents;

/// <summary>Downloads one document. Access is decided by the Contracts module against the parent record and the document's classification; absent and out-of-scope documents are both 404.</summary>
[Authorize]
public sealed class DownloadModel(ISecuredContractDocuments documents) : EcpPageModel
{
    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var download = await documents.OpenAsync(id, ct);
        if (download is null) return StatusCode(StatusCodes.Status409Conflict, "This document cannot be downloaded: it has not been cleared by a malware scan or it is no longer active.");
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(download.Content, download.MimeType, download.FileName);
    });
}
