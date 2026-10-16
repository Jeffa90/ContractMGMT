using Ecp.DataMigration;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

public sealed class ReconciliationModel(IMigrationReports reports, IMigrationBatches batches) : MigrationPageModel
{
    public Guid BatchId { get; private set; }
    public ReconciliationDto Report { get; private set; } = null!;
    public BatchDetail Batch { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        BatchId = id;
        Batch = await batches.GetAsync(id, ct);
        Report = await reports.GetReconciliationAsync(id, ct);
        return Page();
    });

    public Task<IActionResult> OnGetExportAsync(Guid id, bool xlsx, CancellationToken ct) => RunAsync(async () => Download(await reports.ExportReconciliationAsync(id, xlsx ? ExportFormat.Xlsx : ExportFormat.Csv, ct)));
}
