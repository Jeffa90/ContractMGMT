using Ecp.Contracts;
using Ecp.Lifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class AddDocumentModel(ISecuredContractService contracts, ISecuredContractDocuments documents, ILifecycleService lifecycle) : EcpPageModel
{
    public ContractDetails Contract { get; private set; } = null!;
    public IReadOnlyList<string> Types { get; private set; } = [];
    public IReadOnlyList<ContractDocumentItem> Existing { get; private set; } = [];
    [BindProperty] public string DocumentType { get; set; } = "";
    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? Classification { get; set; }
    [BindProperty] public string? VersionLabel { get; set; }
    [BindProperty] public Guid? NewVersionOf { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Contract = await contracts.GetAsync(id, ct);
        await Terms.UseOrganisationAsync(Contract.OrganisationId, ct);
        Types = (await lifecycle.StateAsync(id, ct)).UploadTypes;
        Existing = await documents.ListAsync(id, ct);
    }

    public Task<IActionResult> OnPostAsync(Guid id, IFormFile? file, CancellationToken ct) => RunAsync(async () =>
    {
        if (file is null || file.Length == 0) throw new Ecp.SharedKernel.DomainException("file_required", "Choose a file to add.");
        await using var stream = file.OpenReadStream();
        var name = Path.GetFileName(file.FileName);
        if (NewVersionOf is { } existing)
            await documents.AddVersionAsync(new AddContractDocumentVersionRequest(existing, name, file.ContentType, stream, VersionLabel), ct);
        else
        {
            if (!(await lifecycle.StateAsync(id, ct)).UploadTypes.Contains(DocumentType)) throw new Ecp.SharedKernel.DomainException("document_type", "Choose one of the [[document|lower]] types listed.");
            await documents.AddAsync(new AddContractDocumentRequest(id, DocumentType, string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(name) : Title!, name, file.ContentType, stream,
                string.IsNullOrWhiteSpace(Classification) ? null : Classification, VersionLabel), ct);
        }
        Flash("The [[document|lower]] is added. It is scanned first and can be downloaded once it has passed.");
        return RedirectToPage(new { id });
    }, () => LoadAsync(id, ct));
}
