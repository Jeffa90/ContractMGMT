using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

public sealed class MessagesModel(IMigrationBatches batches, IMigrationReports reports) : MigrationPageModel
{
    public Guid BatchId { get; private set; }
    public PagedResult<MessageDto> Result { get; private set; } = new([], 0, 1, 50);
    [BindProperty(SupportsGet = true)] public MessageSeverity? Severity { get; set; }
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        BatchId = id;
        Result = await batches.ListMessagesAsync(id, new MessageFilter(Severity, Code, Page: Math.Max(1, PageNumber), PageSize: 100), ct);
        return Page();
    });

    public Task<IActionResult> OnGetExportAsync(Guid id, bool xlsx, CancellationToken ct) => RunAsync(async () => Download(await reports.ExportErrorsAsync(id, xlsx ? ExportFormat.Xlsx : ExportFormat.Csv, ct)));

    public string PageLink(int page) => $"/Migration/Batches/{BatchId}/Messages?" + string.Join("&", new[] { ("severity", Severity?.ToString()), ("code", Code), ("pageNumber", page.ToString()) }
        .Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
}
