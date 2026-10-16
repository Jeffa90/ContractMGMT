using System.Text;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

[Authorize(Policy = "perm:audit.read|audit.read_admin")]
public sealed class AuditModel(IAuditBrowser audit, IAccessService access, LocalisationOptions localisation) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public string? EntityType { get; set; }
    [BindProperty(SupportsGet = true)] public string? Actor { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public PagedResult<AuditEventDto> Result { get; private set; } = new([], 0, 1, 50);
    public bool CanExport { get; private set; }
    private LocaleFormatter _fmt = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await RunAsync(async () =>
    {
        _fmt = new LocaleFormatter(localisation.DefaultCulture, localisation.DefaultTimeZoneId, localisation.DefaultCurrencyCode);
        CanExport = await access.HoldsAnywhereAsync("audit.export", ct);
        Result = await audit.QueryAsync(Filter(), ct);
        return Page();
    });

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct) => await RunAsync(async () =>
    {
        var evidence = await audit.ExportAsync(Filter(), ct);
        var json = System.Text.Json.JsonSerializer.Serialize(evidence, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"audit-evidence-{evidence.ExportedUtc:yyyyMMddTHHmmssZ}.json");
    });

    private AuditFilter Filter() => new(Action, EntityType, Actor, Page: Math.Max(1, PageNumber));
    public string When(DateTime utc) => _fmt.DateTime(utc);

    public string PageLink(int page)
        => "/Admin/Audit?" + string.Join("&", new[] { ("action", Action), ("entityType", EntityType), ("actor", Actor), ("pageNumber", page.ToString()) }
            .Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
}
