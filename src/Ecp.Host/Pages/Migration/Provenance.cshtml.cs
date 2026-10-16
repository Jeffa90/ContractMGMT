using Ecp.DataMigration;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

public sealed class ProvenanceModel(IMigrationReports reports) : MigrationPageModel
{
    public Guid BatchId { get; private set; }
    public PagedResult<ProvenanceDto> Result { get; private set; } = new([], 0, 1, 50);
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { BatchId = id; Result = await reports.ListProvenanceAsync(id, Math.Max(1, PageNumber), 100, ct); return Page(); });
    public Task<IActionResult> OnGetExportAsync(Guid id, CancellationToken ct) => RunAsync(async () => Download(await reports.ExportProvenanceAsync(id, ExportFormat.Xlsx, ct)));
}
