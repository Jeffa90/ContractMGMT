using System.Globalization;
using Ecp.Contracts;
using Ecp.Contracts.Register;
using Ecp.Lifecycle;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

/// <summary>
/// The contract register. What is searched, shown and exported comes from one view: the platform's shipped views, those an organisation has published, and the person's own saved views. The person narrows a view
/// with typed filters (field, operator, value) and free text; every filter is checked on the server against the fields they are allowed to use.
/// </summary>
[Authorize(Policy = "perm:contracts.read")]
public sealed class IndexModel(IContractViews views, IContractRegister register, IContractExports exports, ISecuredContractService contracts, IStatusLabels labels,
    IOrganisationStructureService organisations) : EcpPageModel
{
    public const string DefaultView = "system:all-contracts";

    [BindProperty(SupportsGet = true)] public string View { get; set; } = DefaultView;
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Org { get; set; }
    [BindProperty(SupportsGet = true)] public List<string> F { get; set; } = [];
    // The filter rows of the form arrive as three parallel lists; they are folded into F (field|operator|value) so links, saved views and exports all use one form of the filter.
    [BindProperty(SupportsGet = true)] public List<string> Ff { get; set; } = [];
    [BindProperty(SupportsGet = true)] public List<string> Fo { get; set; } = [];
    [BindProperty(SupportsGet = true)] public List<string> Fv { get; set; } = [];
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CategoryId { get; set; }
    [BindProperty(SupportsGet = true)] public bool Owned { get; set; }
    [BindProperty] public string? SaveName { get; set; }
    [BindProperty] public string? ExportFormat { get; set; }
    [BindProperty] public string? ExportStyle { get; set; }
    [BindProperty] public string? ExportReason { get; set; }
    [BindProperty] public bool ExportLarge { get; set; }

    public IReadOnlyDictionary<string, string> StatusLabels { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyList<ViewSummary> Views { get; private set; } = [];
    public ResolvedView? Current { get; private set; }
    public RegisterCatalogue Catalogue { get; private set; } = new([], null, 0, []);
    public RegisterPage Result { get; private set; } = new([], [], 0, 1, 25, []);
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<string> StatusChips { get; private set; } = [];
    public IReadOnlyList<(string Field, string Op, string Value)> Rows { get; private set; } = [];
    public bool CanExport { get; private set; }

    public static readonly IReadOnlyList<(string Op, string Label)> Operators =
    [
        (FilterOps.Eq, "is"), (FilterOps.NotEq, "is not"), (FilterOps.In, "is one of (comma separated)"), (FilterOps.Contains, "contains"), (FilterOps.StartsWith, "starts with"),
        (FilterOps.Gte, "at least / on or after"), (FilterOps.Lte, "at most / on or before"), (FilterOps.Between, "between (two values, comma separated)"), (FilterOps.IsEmpty, "is empty"),
        (FilterOps.IsNotEmpty, "is not empty"), (FilterOps.Is, "is (yes/no)")
    ];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadFrameAsync(ct);
        await RunQueryAsync(ct);
        return Page();
    });

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadFrameAsync(ct);
        var prepared = await views.PrepareAsync(View, Org, 1, Q, ParseFilters(), SortOverride(), ct);
        var q = prepared.Query;
        var def = new ViewDefinition("mine", (SaveName ?? "").Trim(), "", q.Columns ?? prepared.View.Definition.Columns, q.Sorts ?? [], q.Filters ?? [], q.PageSize, prepared.View.Definition.StatusChips,
            prepared.View.Definition.DateFormat, prepared.View.Definition.CurrencyFormat, prepared.View.Definition.GroupBy, q.Text);
        var reference = await views.SaveMineAsync(null, def, ct);
        Flash("Your view is saved.");
        return RedirectToPage(new { view = reference });
    }, () => ReloadAsync());

    public Task<IActionResult> OnPostDeleteAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await views.DeleteMineAsync(View, ct);
        Flash("The view is deleted.");
        return RedirectToPage(new { });
    }, () => ReloadAsync());

    public Task<IActionResult> OnPostExportAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadFrameAsync(ct);
        var prepared = await views.PrepareAsync(View, Org, 1, Q, ParseFilters(), SortOverride(), ct);
        var summary = string.Join("; ", new[] { string.IsNullOrWhiteSpace(Q) ? null : $"text '{Q.Trim()}'" }.Concat((ParseFilters() ?? []).Select(f => $"{f.Field} {f.Op} {string.Join(",", f.Values)}")).Where(x => x is not null));
        var format = string.Equals(ExportFormat, "xlsx", StringComparison.OrdinalIgnoreCase) ? Ecp.Contracts.Register.ExportFormat.Xlsx : Ecp.Contracts.Register.ExportFormat.Csv;
        var result = await exports.ExportAsync(new ExportRequest(prepared.Query, format, null, false, ExportLarge, ExportReason, summary.Length == 0 ? null : summary, prepared.View.Definition.Name,
            string.Equals(ExportStyle, "machine", StringComparison.OrdinalIgnoreCase) ? Ecp.Contracts.Register.ExportStyle.Machine : Ecp.Contracts.Register.ExportStyle.Business), ct);
        Response.Headers.CacheControl = "no-store";
        return File(result.Content, result.ContentType, result.FileName);
    }, () => ReloadAsync());

    private async Task ReloadAsync() { await LoadFrameAsync(CancellationToken.None); await RunQueryAsync(CancellationToken.None); }

    private void FoldFilterRows()
    {
        for (var i = 0; i < Ff.Count; i++)
            if (!string.IsNullOrWhiteSpace(Ff[i])) F.Add($"{Ff[i].Trim()}|{(i < Fo.Count ? Fo[i] : FilterOps.Eq).Trim()}|{(i < Fv.Count ? Fv[i] : "").Trim()}");
        Ff = []; Fo = []; Fv = [];
    }

    private async Task LoadFrameAsync(CancellationToken ct)
    {
        FoldFilterRows();
        if (string.IsNullOrWhiteSpace(View)) View = DefaultView;
        Organisations = await organisations.ListOrganisationsAsync(ct);
        if (Org is { } chosen && Organisations.Any(o => o.Id == chosen)) await Format.UseAsync(chosen, ct);     // dates and amounts follow the organisation being looked at
        Views = await views.ListAsync(Org, ct);
        StatusLabels = await labels.AllAsync(ct);
        Catalogue = await register.CatalogueAsync(Org, ct);
        try { CanExport = (await exports.SchemaAsync(Org, ct)).Columns.Count > 0; } catch (ForbiddenException) { CanExport = false; }
    }

    private async Task RunQueryAsync(CancellationToken ct)
    {
        var filters = ParseFilters();
        Rows = F.Select(Split).Where(r => r.Field.Length > 0).ToList();
        try
        {
            var prepared = await views.PrepareAsync(View, Org, Math.Max(1, PageNumber), Q, filters, SortOverride(), ct);
            Current = prepared.View;
            Result = await register.SearchAsync(prepared.Query, ct);
            Result = Result with { Notes = [.. prepared.Notes, .. Result.Notes] };
        }
        catch (RegisterQueryException ex)
        {
            foreach (var issue in ex.Issues) ModelState.AddModelError(string.Empty, issue.Message);
            Current = await views.GetAsync(View, Org, ct);
        }
        StatusChips = Current?.Definition.StatusChips == true ? (await contracts.CountByStatusAsync(ct)).Keys.Order().ToList() : [];
    }

    private static (string Field, string Op, string Value) Split(string raw)
    {
        var parts = raw.Split('|', 3);
        return parts.Length == 3 ? (parts[0].Trim(), parts[1].Trim(), parts[2].Trim()) : ("", "", "");
    }

    private IReadOnlyList<FilterClause>? ParseFilters()
    {
        var list = new List<FilterClause>();
        foreach (var raw in F)
        {
            var (field, op, value) = Split(raw);
            if (field.Length == 0 || op.Length == 0) continue;
            var values = op is FilterOps.IsEmpty or FilterOps.IsNotEmpty ? [] : op is FilterOps.In or FilterOps.Between ? value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList() : (value.Length == 0 ? [] : new List<string> { value });
            list.Add(new FilterClause(field, op, values));
        }
        // Links from older pages and the home page.
        if (!string.IsNullOrWhiteSpace(Status)) list.Add(new FilterClause("status", FilterOps.In, [Status]));
        if (CategoryId is { } cat) list.Add(new FilterClause("category", FilterOps.In, [cat.ToString("D")]));
        if (Owned) list.Add(new FilterClause("owner", FilterOps.Eq, [FilterTokens.Me]));
        return list.Count == 0 ? null : list;
    }

    private IReadOnlyList<SortClause>? SortOverride() => string.IsNullOrWhiteSpace(Sort) ? null : [new SortClause(Sort.Trim(), Desc)];

    public string LinkTo(int? page = null, string? sort = null, bool? desc = null, string? status = null)
    {
        var pairs = new List<(string, string?)> { ("view", View), ("q", Q), ("org", Org?.ToString("D")), ("sort", sort ?? Sort), ("desc", (desc ?? Desc) ? "true" : null), ("pageNumber", (page ?? 1).ToString(CultureInfo.InvariantCulture)) };
        pairs.AddRange(F.Select(f => ("f", (string?)f)));
        if (status is not null) pairs.Add(("f", "status|In|" + status));
        return "/Contracts?" + string.Join("&", pairs.Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
    }

    public string SortLink(string key) => LinkTo(1, key, Sort == key && !Desc);
}
