using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

public sealed class FilesModel(IMigrationBatches batches) : MigrationPageModel
{
    public Guid BatchId { get; private set; }
    public PagedResult<StagedFileDto> Result { get; private set; } = new([], 0, 1, 50);
    public IReadOnlyList<ManifestEntryDto> Manifest { get; private set; } = [];
    public IReadOnlyList<ContractChoice> Choices { get; private set; } = [];
    public BatchCapabilities Can { get; private set; } = null!;
    [BindProperty(SupportsGet = true)] public FileState? State { get; set; }
    [BindProperty(SupportsGet = true)] public bool QueueOnly { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty] public Guid FileId { get; set; }
    [BindProperty] public Guid ContractRowId { get; set; }
    [BindProperty] public string? Reason { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    public Task<IActionResult> OnPostMatchAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await batches.ResolveFileAsync(id, FileId, ContractRowId, ct); Flash("The file was matched to the contract you chose. Run the dry run again to apply it."); });
    public Task<IActionResult> OnPostReleaseAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await batches.ReleaseHeldFileAsync(id, FileId, ct); Flash("The file was released and will be imported as a separate document. Run the dry run again to apply it."); });
    public Task<IActionResult> OnPostDiscardAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await batches.DiscardFileAsync(id, FileId, Reason ?? "", ct); Flash("The file was discarded. The discard is recorded; the file stays in staging, marked discarded."); });

    private Task<IActionResult> Act(Guid id, CancellationToken ct, Func<Task> action) => RunAsync(async () => { await action(); return RedirectToPage(new { id, queueOnly = true }); }, () => LoadAsync(id, ct));

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        BatchId = id;
        Can = (await batches.GetAsync(id, ct)).Can;
        Result = await batches.ListFilesAsync(id, new FileFilter(State, QueueOnly, Math.Max(1, PageNumber), 50), ct);
        Manifest = await batches.ListManifestAsync(id, 100, ct);
        Choices = await batches.ListContractChoicesAsync(id, null, 200, ct);
    }

    public string PageLink(int page) => $"/Migration/Batches/{BatchId}/Files?" + string.Join("&", new[] { ("state", State?.ToString()), ("queueOnly", QueueOnly ? "true" : null), ("pageNumber", page.ToString()) }
        .Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
}
