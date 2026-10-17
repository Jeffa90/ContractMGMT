using System.Globalization;
using System.Text.Json;
using Ecp.Audit;
using Ecp.Formats;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.Extensions.Options;

namespace Ecp.Contracts.Register;

public enum ExportFormat { Csv, Xlsx }

/// <summary>
/// A request to export what a search finds. The query is the same typed query the register runs; the export adds the person's right to export, the reportable configured fields, a row limit,
/// and a record of who asked for what. A result over the limit is never cut short: the request fails and says how to proceed (narrow it, or ask for a large export with a reason).
/// </summary>
public sealed record ExportRequest(ContractQuery Query, ExportFormat Format = ExportFormat.Csv, IReadOnlyList<string>? Columns = null, bool KeysAsHeaders = false, bool Large = false, string? Reason = null,
    string? FilterSummary = null, string? ViewName = null);

public sealed record ExportResult(string FileName, string ContentType, byte[] Content, int Rows, int TotalMatching, string Summary);

/// <summary>
/// One column of the reporting projection. <see cref="Name"/> is the stable identifier a downstream consumer relies on: the key of a core field, or <c>cf_</c> plus the key of a configured field.
/// Names never change meaning; a field that is retired keeps its column, and a new field adds one. <see cref="SchemaVersion"/> changes only when a column is removed or its type changes.
/// </summary>
public sealed record ReportingColumn(string Name, string Label, string Type, string Source, string Classification, bool Nullable = true);

public sealed record ReportingSchema(string SchemaVersion, int CustomFieldRelease, Guid? OrganisationId, IReadOnlyList<ReportingColumn> Columns);

public interface IContractExports
{
    Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken ct = default);
    /// <summary>The columns of the reporting projection for what the signed-in person may export, in order. A later reporting integration reads this contract.</summary>
    Task<ReportingSchema> SchemaAsync(Guid? organisationId = null, CancellationToken ct = default);
}

internal sealed class ContractExportService(
    IContractRegister register, IAccessService access, IUnitOfWork uow, IAuditWriter audit, IClock clock, ITenantContext tenant, IPeopleDirectory people, IOptions<ContractsOptions> options,
    IOrganisationStructureService structure, Ecp.Configuration.ITerminologyResolver terminology) : IContractExports
{
    public const string SchemaVersion = "1";

    private async Task RequireAsync(CancellationToken ct)
    {
        if ((await access.GrantsAsync("records.export", ct)).Count == 0) throw new ForbiddenException("You cannot export records.");
    }

    /// <summary>A worksheet name obeys the spreadsheet rules: at most 31 characters and none of : \\ / ? * [ ].</summary>
    private static string SheetName(string name)
    {
        var clean = new string(name.Where(c => ":\\/?*[]".IndexOf(c) < 0).ToArray()).Trim();
        return clean.Length == 0 ? "Records" : clean.Length > 31 ? clean[..31] : clean;
    }

    private static string CustomName(string key) => "cf_" + key;

    public async Task<ReportingSchema> SchemaAsync(Guid? organisationId = null, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var catalogue = await register.CatalogueAsync(organisationId, ct);
        var cols = catalogue.Fields.Where(f => f.Displayable && f.Reportable && f.Key != "legalReviewStatus").Select(f => new ReportingColumn(f.IsCustom ? CustomName(f.Key) : f.Key, f.Label, f.Type, f.IsCustom ? "custom" : "core", f.IsCustom ? "field" : "contract")).ToList();
        return new ReportingSchema(SchemaVersion, catalogue.CustomFieldRelease, catalogue.OrganisationId, cols);
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var o = options.Value;
        if (request.Large && (request.Reason is null || request.Reason.Trim().Length < 10)) throw new DomainException("large_export_reason", "A large export needs a reason of at least ten characters; it is recorded.");
        var limit = request.Large ? o.LargeExportRowLimit : o.ExportRowLimit;
        var catalogue = await register.CatalogueAsync(request.Query.OrganisationId, ct);
        var wanted = request.Columns ?? request.Query.Columns ?? catalogue.Fields.Where(f => f.Displayable && f.Reportable && f.Key != "legalReviewStatus").Select(f => f.Key).ToList();
        var query = request.Query with { Columns = wanted, Page = 1, PageSize = 1000 };

        var first = await register.SearchForExportAsync(query, ct);
        if (first.Total > limit)
            throw new DomainException("export_too_large", request.Large
                ? $"The search matches {first.Total:N0} records, more than the large-export limit of {limit:N0}. Narrow the search."
                : $"The search matches {first.Total:N0} records, more than the export limit of {limit:N0}. Narrow the search, or ask for a large export with a reason.");
        var rows = new List<RegisterRow>(first.Rows);
        for (var page = 2; rows.Count < first.Total && page <= 200; page++)
        {
            var next = await register.SearchForExportAsync(query with { Page = page }, ct);
            if (next.Rows.Count == 0) break;
            rows.AddRange(next.Rows);
        }
        var columns = first.Columns;
        var now = clock.UtcNow;

        var table = new List<IReadOnlyList<object?>> { columns.Select(c => (object?)(request.KeysAsHeaders ? (c.IsCustom ? CustomName(c.Key) : c.Key) : c.Label)).ToList() };
        foreach (var r in rows) table.Add(columns.Select(c => Cell(c, r.Cells.GetValueOrDefault(c.Key))).ToList());

        var user = access.CurrentUserId;
        var userName = user is { } u ? (await people.DisplayNamesAsync([u], ct)).GetValueOrDefault(u) ?? u.ToString("D") : "unknown";
        var orgName = catalogue.OrganisationId is { } oid ? (await structure.GetOrganisationAsync(oid, ct))?.Name : "All organisations you can reach";
        var summary = $"{rows.Count} row(s), {columns.Count} column(s){(string.IsNullOrWhiteSpace(request.FilterSummary) ? "" : ", " + request.FilterSummary)}";
        byte[] content; string contentType, ext;
        if (request.Format == ExportFormat.Xlsx)
        {
            var words = await terminology.GetAsync(catalogue.OrganisationId ?? await terminology.HomeOrganisationAsync(ct), null, ct);
            var meta = new List<IReadOnlyList<object?>>
            {
                new object?[] { "Item", "Value" },
                new object?[] { "Generated (UTC)", now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
                new object?[] { "Tenant", tenant.RequireTenantId().ToString("D") },
                new object?[] { words.Expand("[[organisation]]"), orgName },
                new object?[] { "Exported by", userName },
                new object?[] { "View", request.ViewName },
                new object?[] { "Search and filters", string.IsNullOrWhiteSpace(request.FilterSummary) ? "(none)" : request.FilterSummary },
                new object?[] { "Rows", rows.Count },
                new object?[] { "Reporting schema version", SchemaVersion },
                new object?[] { "Additional field definitions release", catalogue.CustomFieldRelease },
                new object?[] { "Note", "Values you are not permitted to see are left blank. Text that a spreadsheet could read as a formula is prefixed with an apostrophe." }
            };
            content = XlsxWriter.Write([new WorksheetData(SheetName(words.Expand("[[contract|plural]]")), table), new WorksheetData("Export details", meta)]);
            contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; ext = "xlsx";
        }
        else { content = CsvWriter.Write(table); contentType = "text/csv; charset=utf-8"; ext = "csv"; }

        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry("contracts.exported", "ContractExport", Guid.NewGuid().ToString(), OrganisationId: catalogue.OrganisationId, Reason: request.Large ? request.Reason!.Trim() : null,
                NewValuesJson: JsonSerializer.Serialize(new { format = request.Format.ToString(), rows = rows.Count, totalMatching = first.Total, columns = columns.Select(x => x.Key), filters = request.FilterSummary, view = request.ViewName, large = request.Large })));
            return Task.CompletedTask;
        }, ct);
        return new ExportResult($"contracts-{now:yyyyMMdd-HHmmss}Z.{ext}", contentType, content, rows.Count, first.Total, summary);
    }

    /// <summary>A cell as a typed value: numbers stay numbers (so a negative amount is not mistaken for a formula), flags are true or false, everything else is text that the writers make safe.</summary>
    private static object? Cell(RegisterColumn c, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return c.Type switch
        {
            FieldTypes.Integer or FieldTypes.Decimal or FieldTypes.Currency or FieldTypes.Percentage when decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
            _ => raw
        };
    }
}
