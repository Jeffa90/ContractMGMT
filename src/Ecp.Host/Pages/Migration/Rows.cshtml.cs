using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

public sealed class RowsModel(IMigrationBatches batches) : MigrationPageModel
{
    public Guid BatchId { get; private set; }
    public PagedResult<RowDto> Result { get; private set; } = new([], 0, 1, 50);
    public BatchCapabilities Can { get; private set; } = null!;
    [BindProperty(SupportsGet = true)] public RowOutcome? Outcome { get; set; }
    [BindProperty(SupportsGet = true)] public bool ReviewOnly { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty] public Guid RowId { get; set; }
    [BindProperty] public ReviewDecision Decision { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    public Task<IActionResult> OnPostDecideAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await batches.DecideRowAsync(id, RowId, Decision, ct);
        Flash("Your decision was recorded. Run the dry run again to apply it.");
        return RedirectToPage(new { id, reviewOnly = true });
    }, () => LoadAsync(id, ct));

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        BatchId = id;
        Can = (await batches.GetAsync(id, ct)).Can;
        Result = await batches.ListRowsAsync(id, new RowFilter(Outcome, ReviewOnly, Math.Max(1, PageNumber), 50), ct);
    }

    public string PageLink(int page) => $"/Migration/Batches/{BatchId}/Rows?" + string.Join("&", new[] { ("outcome", Outcome?.ToString()), ("reviewOnly", ReviewOnly ? "true" : null), ("pageNumber", page.ToString()) }
        .Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
}
