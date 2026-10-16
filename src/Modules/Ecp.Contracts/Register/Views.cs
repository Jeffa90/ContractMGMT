using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Contracts.Register;

public static class ViewFormats
{
    public static readonly IReadOnlyList<string> Dates = ["short", "medium", "long", "iso"];
    public static readonly IReadOnlyList<string> Currencies = ["plain", "code"];
    /// <summary>Grouping is by a field every row has one value for. Owner grouping is not offered: a category can have several owners.</summary>
    public static readonly IReadOnlyList<string> Groupings = ["category", "status", "supplier"];
}

/// <summary>A list view: which columns, in which order, sorted and filtered how, shown with which formats. Fields are referred to by their stable keys, so a retired field degrades gracefully.</summary>
public sealed record ViewDefinition(
    string Key, string Name, string Description, IReadOnlyList<string> Columns, IReadOnlyList<SortClause> Sort, IReadOnlyList<FilterClause> Filters, int PageSize = 25, bool StatusChips = true,
    string DateFormat = "short", string CurrencyFormat = "plain", string? GroupBy = null, string? Text = null);

public sealed record ViewSet(int SchemaVersion, IReadOnlyList<ViewDefinition> Views)
{
    public ViewDefinition? Find(string key) => Views.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
}

public static partial class ViewParser
{
    public const int MaxViews = 50, MaxColumns = 30;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, ReadCommentHandling = JsonCommentHandling.Disallow, MaxDepth = 12, NumberHandling = JsonNumberHandling.Strict
    };

    private sealed class RawSet { public int SchemaVersion { get; set; } public List<RawView>? Views { get; set; } }
    private sealed class RawFilter { public string? Field { get; set; } public string? Op { get; set; } public List<string>? Values { get; set; } }
    private sealed class RawSort { public string? Field { get; set; } public bool Descending { get; set; } }
    private sealed class RawView
    {
        public string? Key { get; set; } public string? Name { get; set; } public string? Description { get; set; } public List<string>? Columns { get; set; } public List<RawSort>? Sort { get; set; }
        public List<RawFilter>? Filters { get; set; } public int? PageSize { get; set; } public bool? StatusChips { get; set; } public string? DateFormat { get; set; } public string? CurrencyFormat { get; set; }
        public string? GroupBy { get; set; } public string? Text { get; set; }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,40}$", RegexOptions.NonBacktracking)] private static partial Regex KeyPattern();

    public static (ViewSet? Set, List<ValidationMessage> Messages) Parse(string json)
    {
        var messages = new List<ValidationMessage>();
        RawSet? raw;
        try { raw = JsonSerializer.Deserialize<RawSet>(json, Options); }
        catch (JsonException ex) { messages.Add(new(ValidationSeverity.Error, "$", "invalid_json", "The list views could not be read: " + ex.Message.Split('\n')[0])); return (null, messages); }
        if (raw is null) { messages.Add(new(ValidationSeverity.Error, "$", "invalid_json", "The list view document is empty.")); return (null, messages); }
        var views = (raw.Views ?? []).Select(v => new ViewDefinition(v.Key?.Trim() ?? "", v.Name?.Trim() ?? "", v.Description?.Trim() ?? "", (v.Columns ?? []).Select(c => c?.Trim() ?? "").ToList(),
            (v.Sort ?? []).Select(s => new SortClause(s.Field?.Trim() ?? "", s.Descending)).ToList(),
            (v.Filters ?? []).Select(f => new FilterClause(f.Field?.Trim() ?? "", f.Op?.Trim() ?? "", (f.Values ?? []).Select(x => x ?? "").ToList())).ToList(),
            v.PageSize ?? 25, v.StatusChips ?? true, v.DateFormat?.Trim() ?? "short", v.CurrencyFormat?.Trim() ?? "plain", string.IsNullOrWhiteSpace(v.GroupBy) ? null : v.GroupBy.Trim(), string.IsNullOrWhiteSpace(v.Text) ? null : v.Text.Trim())).ToList();
        return (new ViewSet(raw.SchemaVersion, views), messages);
    }

    public static string ToJson(ViewSet set) => JsonSerializer.Serialize(new
    {
        schemaVersion = set.SchemaVersion,
        views = set.Views.Select(v => new
        {
            key = v.Key, name = v.Name, description = v.Description, columns = v.Columns, sort = v.Sort.Select(s => new { field = s.Field, descending = s.Descending }),
            filters = v.Filters.Select(f => new { field = f.Field, op = f.Op, values = f.Values }), pageSize = v.PageSize, statusChips = v.StatusChips, dateFormat = v.DateFormat,
            currencyFormat = v.CurrencyFormat, groupBy = v.GroupBy, text = v.Text
        })
    }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    /// <summary>Checks one view against the fields the checking person may use. A view that names a field or a classification they cannot reach is refused, so nobody publishes a view that exposes more than they can see.</summary>
    public static List<ValidationMessage> ValidateView(ViewDefinition v, RegisterCatalogue catalogue, IReadOnlyCollection<string> readableClassifications, IReadOnlySet<string> knownSets, string path, bool requireKey = true)
    {
        var m = new List<ValidationMessage>();
        ValidationMessage E(string p, string code, string text) => new(ValidationSeverity.Error, p, code, text);
        if (requireKey && !KeyPattern().IsMatch(v.Key)) m.Add(E(path + ".key", "view_key", "A view key is lower-case letters, digits and hyphens, starting with a letter."));
        if (string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 100) m.Add(E(path + ".name", "view_name", "A view needs a name of up to 100 characters."));
        if (v.Description.Length > 300) m.Add(E(path + ".description", "view_description", "A description is limited to 300 characters."));
        if (v.Columns.Count is < 1 or > MaxColumns) m.Add(E(path + ".columns", "view_columns", $"A view shows between 1 and {MaxColumns} columns."));
        if (v.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != v.Columns.Count) m.Add(E(path + ".columns", "duplicate_column", "A column is listed twice."));
        foreach (var c in v.Columns)
        {
            var f = catalogue.Find(c);
            if (f is null) m.Add(E(path + ".columns", "column_unavailable", $"The column '{c}' is not a field you can use."));
            else if (!f.Displayable) m.Add(E(path + ".columns", "column_not_displayable", $"{f.Label} cannot be shown as a column."));
            else if (f.Retired) m.Add(new(ValidationSeverity.Warning, path + ".columns", "column_retired", $"{f.Label} is retired. Its stored values still show, but it takes no new ones."));
        }
        if (v.PageSize is < 10 or > RegisterQueryEngine.MaxPageSize) m.Add(E(path + ".pageSize", "page_size", $"The page size is between 10 and {RegisterQueryEngine.MaxPageSize}."));
        if (!ViewFormats.Dates.Contains(v.DateFormat)) m.Add(E(path + ".dateFormat", "date_format", $"The date format is one of: {string.Join(", ", ViewFormats.Dates)}."));
        if (!ViewFormats.Currencies.Contains(v.CurrencyFormat)) m.Add(E(path + ".currencyFormat", "currency_format", $"The currency format is one of: {string.Join(", ", ViewFormats.Currencies)}."));
        if (v.GroupBy is not null && !ViewFormats.Groupings.Contains(v.GroupBy)) m.Add(E(path + ".groupBy", "group_by", $"Grouping is by one of: {string.Join(", ", ViewFormats.Groupings)}."));
        if (v.Sort.Count > RegisterQueryEngine.MaxSorts) m.Add(E(path + ".sort", "too_many", $"At most {RegisterQueryEngine.MaxSorts} sort fields can be used."));
        foreach (var s in v.Sort)
            if (catalogue.Find(s.Field) is not { Sortable: true }) m.Add(E(path + ".sort", "sort_unavailable", $"'{s.Field}' cannot be used to sort."));
        if (v.Filters.Count > RegisterQueryEngine.MaxFilters) m.Add(E(path + ".filters", "too_many", $"At most {RegisterQueryEngine.MaxFilters} filters can be used."));
        foreach (var f in v.Filters)
        {
            if (f.Field == "set") { if (f.Op != FilterOps.Is || f.Values.Count is < 1 or > 2 || !knownSets.Contains(f.Values[0])) m.Add(E(path + ".filters", "set_unavailable", "That list is not available.")); continue; }
            var field = catalogue.Find(f.Field);
            if (field is null || !field.Filterable) { m.Add(E(path + ".filters", "filter_unavailable", $"'{f.Field}' is not a field you can filter on.")); continue; }
            if (!field.Operators.Contains(f.Op, StringComparer.Ordinal)) m.Add(E(path + ".filters", "filter_operator", $"{field.Label} cannot be filtered with '{f.Op}'."));
            if (field.Key == "classification" && f.Values.Any(x => !readableClassifications.Contains(x, StringComparer.OrdinalIgnoreCase)))
                m.Add(E(path + ".filters", "classification_unavailable", "A filter names a classification level you cannot read, so a view with it cannot be saved or published by you."));
        }
        if (v.Text is { Length: > RegisterQueryEngine.MaxText }) m.Add(E(path + ".text", "text_length", $"Search text is limited to {RegisterQueryEngine.MaxText} characters."));
        // The filters and sorts are also checked as a query, so a value that does not fit its field is caught here and not when somebody runs the view.
        return m;
    }
}

public enum ViewScope { System, Shared, Personal }

public sealed record ViewSummary(string Ref, string Name, string Description, ViewScope Scope, Guid? OrganisationId, bool CanEdit);
public sealed record PreparedView(ResolvedView View, ContractQuery Query, IReadOnlyList<string> Notes);
public sealed record ResolvedView(string Ref, ViewDefinition Definition, ViewScope Scope, Guid? OrganisationId, IReadOnlyList<string> Notes, bool CanEdit);

/// <summary>The register's views: those the platform ships, those an organisation has published (versioned configuration) and the signed-in person's own.</summary>
public interface IContractViews
{
    Task<IReadOnlyList<ViewSummary>> ListAsync(Guid? organisationId = null, CancellationToken ct = default);
    Task<ResolvedView> GetAsync(string viewRef, Guid? organisationId = null, CancellationToken ct = default);
    /// <summary>Runs a view for the signed-in person. Columns, filters or sorts they cannot use are left out (with a note), never an error that reveals why.</summary>
    Task<RegisterPage> RunAsync(string viewRef, Guid? organisationId = null, int page = 1, string? extraText = null, IReadOnlyList<FilterClause>? extraFilters = null, IReadOnlyList<SortClause>? sortOverride = null, CancellationToken ct = default);
    Task<IReadOnlyList<RegisterIssue>> ValidateAsync(ViewDefinition definition, Guid? organisationId = null, CancellationToken ct = default);
    /// <summary>Saves (or replaces) one of the person's own views. At most 50. Refused if it exposes anything they cannot see.</summary>
    Task<string> SaveMineAsync(string? existingRef, ViewDefinition definition, CancellationToken ct = default);
    Task DeleteMineAsync(string viewRef, CancellationToken ct = default);
    /// <summary>The query the register runs for a view as the signed-in person: columns, filters and sorts they cannot use are left out (with notes). A page runs it and an export uses the very same query.</summary>
    Task<PreparedView> PrepareAsync(string viewRef, Guid? organisationId = null, int page = 1, string? extraText = null, IReadOnlyList<FilterClause>? extraFilters = null, IReadOnlyList<SortClause>? sortOverride = null, CancellationToken ct = default);
    /// <summary>The query a view stands for, for callers that run or export it themselves.</summary>
    ContractQuery ToQuery(ViewDefinition definition, Guid? organisationId, int page, string? extraText = null, IReadOnlyList<FilterClause>? extraFilters = null);
}

public sealed class SavedView : ITenantOwned
{
    private SavedView() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public string Name { get; private set; } = "";
    public string DefinitionJson { get; private set; } = "";
    public DateTime CreatedUtc { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SavedView Create(Guid tenantId, Guid owner, string name, string json, DateTime now)
        => new() { Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OwnerUserId = Guard.NotEmpty(owner, nameof(owner)), Name = Guard.NotBlank(name, "Name", 100), DefinitionJson = Guard.NotBlank(json, "Definition", 20000), CreatedUtc = now, UpdatedUtc = now };

    public void Replace(string name, string json, DateTime now) { Name = Guard.NotBlank(name, "Name", 100); DefinitionJson = Guard.NotBlank(json, "Definition", 20000); UpdatedUtc = now; }
}

/// <summary>Publishes organisation-shared views through the configuration lifecycle (draft, validate, publish, version, roll back, audit).</summary>
internal sealed class ContractListViewKind(IServiceProvider services, IAuditWriter audit) : IConfigurationKind
{
    public const string KindName = "ContractListView";
    public string Kind => KindName;

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        var (set, messages) = ViewParser.Parse(contentJson);
        if (set is null) return new ValidationReport(messages);
        if (set.SchemaVersion != 1) messages.Add(new(ValidationSeverity.Error, "$.schemaVersion", "schema_version", "The schema version must be 1."));
        if (set.Views.Count > ViewParser.MaxViews) messages.Add(new(ValidationSeverity.Error, "$.views", "too_many", $"At most {ViewParser.MaxViews} shared views are allowed."));
        // A shared view is a definition, not data: it is checked against every field the organisation defines, by someone with the right to configure. Each person who opens it still sees only what their own access allows.
        var register = services.GetRequiredService<IContractRegister>();
        var catalogue = await register.DefinitionCatalogueAsync(organisationId, ct);
        var scale = await services.GetRequiredService<IAccessService>().ClassificationScaleAsync(ct);
        var readable = scale.Levels.ToList();
        var sets = services.GetServices<IContractSetProvider>().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var system = SystemViews.Load();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < set.Views.Count; i++)
        {
            var v = set.Views[i];
            if (!keys.Add(v.Key)) messages.Add(new(ValidationSeverity.Error, $"$.views[{i}].key", "duplicate_view", $"The view key '{v.Key}' is used twice."));
            if (system.Find(v.Key) is not null) messages.Add(new(ValidationSeverity.Error, $"$.views[{i}].key", "system_view", $"'{v.Key}' is the key of a view the platform ships. Choose another."));
            messages.AddRange(ViewParser.ValidateView(v, catalogue, readable, sets, $"$.views[{i}]"));
        }
        return new ValidationReport(messages);
    }

    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct)
    {
        var (now, _) = ViewParser.Parse(contentJson);
        var (before, _) = previousContentJson is null ? (null, null) : ViewParser.Parse(previousContentJson);
        var added = (now?.Views ?? []).Where(v => before?.Find(v.Key) is null).Select(v => v.Key).ToList();
        var removed = (before?.Views ?? []).Where(v => now?.Find(v.Key) is null).Select(v => v.Key).ToList();
        var changed = (now?.Views ?? []).Where(v => before?.Find(v.Key) is { } old && ViewParser.ToJson(new ViewSet(1, [old])) != ViewParser.ToJson(new ViewSet(1, [v]))).Select(v => v.Key).ToList();
        audit.Record(new AuditEntry("contracts.views.published", "ContractListView", releaseId.ToString(), OrganisationId: organisationId, Reason: $"{added.Count} added, {changed.Count} changed, {removed.Count} removed.",
            NewValuesJson: JsonSerializer.Serialize(new { release = releaseNumber, added, changed, removed })));
        return Task.CompletedTask;
    }
}

public static class SystemViews
{
    private static ViewSet? _cached;
    public static ViewSet Load()
    {
        if (_cached is not null) return _cached;
        using var stream = typeof(SystemViews).Assembly.GetManifestResourceStream("Ecp.Contracts.system-views.json") ?? throw new InvalidOperationException("The shipped views are missing from the build.");
        using var reader = new StreamReader(stream);
        var (set, messages) = ViewParser.Parse(reader.ReadToEnd());
        if (set is null || messages.Count > 0) throw new InvalidOperationException("The shipped views could not be read.");
        return _cached = set;
    }
}

internal sealed class ContractViewService(
    ContractsDbContext db, IContractRegister register, IConfigurationReleases releases, IAccessService access, ITenantContext tenant, IClock clock, IUnitOfWork uow, IAuditWriter audit,
    IEnumerable<IContractSetProvider> setProviders, Ecp.Tenancy.IOrganisationStructureService structure) : IContractViews
{
    private Guid Me => access.CurrentUserId ?? throw new ForbiddenException("A signed-in person is required.");
    private IReadOnlySet<string> SetKeys => setProviders.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

    private async Task<IReadOnlyList<Guid>> OrgsAsync(Guid? organisationId, CancellationToken ct)
    {
        var grants = await access.GrantsAsync("contracts.read", ct);
        var orgs = (await structure.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).Select(o => o.Id).ToList();
        return organisationId is { } one ? orgs.Where(o => o == one).ToList() : orgs;
    }

    public async Task<IReadOnlyList<ViewSummary>> ListAsync(Guid? organisationId = null, CancellationToken ct = default)
    {
        var list = SystemViews.Load().Views.Select(v => new ViewSummary("system:" + v.Key, v.Name, v.Description, ViewScope.System, null, false)).ToList();
        foreach (var org in await OrgsAsync(organisationId, ct))
            if (await releases.LatestAsync(ContractListViewKind.KindName, org, ct) is { } release && ViewParser.Parse(release.ContentJson).Set is { } shared)
                list.AddRange(shared.Views.Select(v => new ViewSummary($"shared:{org:N}:{v.Key}", v.Name, v.Description, ViewScope.Shared, org, false)));
        var me = Me;
        foreach (var mine in await db.Set<SavedView>().AsNoTracking().Where(v => v.OwnerUserId == me).OrderBy(v => v.Name).ToListAsync(ct))
            if (ViewParser.Parse(mine.DefinitionJson).Set?.Views.FirstOrDefault() is { } def) list.Add(new ViewSummary("personal:" + mine.Id.ToString("N"), mine.Name, def.Description, ViewScope.Personal, null, true));
        return list;
    }

    public async Task<ResolvedView> GetAsync(string viewRef, Guid? organisationId = null, CancellationToken ct = default)
    {
        var parts = viewRef.Split(':');
        switch (parts[0])
        {
            case "system" when parts.Length == 2 && SystemViews.Load().Find(parts[1]) is { } sys:
                return new ResolvedView(viewRef, sys, ViewScope.System, null, [], false);
            case "shared" when parts.Length == 3 && Guid.TryParseExact(parts[1], "N", out var org):
                {
                    if (!(await OrgsAsync(null, ct)).Contains(org)) throw new ResourceNotFoundException();
                    var release = await releases.LatestAsync(ContractListViewKind.KindName, org, ct);
                    var def = release is null ? null : ViewParser.Parse(release.ContentJson).Set?.Find(parts[2]);
                    return def is null ? throw new ResourceNotFoundException() : new ResolvedView(viewRef, def, ViewScope.Shared, org, [], false);
                }
            case "personal" when parts.Length == 2 && Guid.TryParseExact(parts[1], "N", out var id):
                {
                    var me = Me;
                    var row = await db.Set<SavedView>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.OwnerUserId == me, ct) ?? throw new ResourceNotFoundException();
                    var def = ViewParser.Parse(row.DefinitionJson).Set?.Views.FirstOrDefault() ?? throw new ResourceNotFoundException();
                    return new ResolvedView(viewRef, def with { Name = row.Name }, ViewScope.Personal, null, [], true);
                }
            default: throw new ResourceNotFoundException();
        }
    }

    public ContractQuery ToQuery(ViewDefinition v, Guid? organisationId, int page, string? extraText = null, IReadOnlyList<FilterClause>? extraFilters = null)
    {
        var sorts = new List<SortClause>();
        if (v.GroupBy is { } g) sorts.Add(new SortClause(g)); // rows of a group sit together; the view's own sort orders them within the group
        sorts.AddRange(v.Sort);
        var text = string.Join(' ', new[] { v.Text, extraText }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new ContractQuery(text.Length == 0 ? null : text, [.. v.Filters, .. extraFilters ?? []], sorts.Take(RegisterQueryEngine.MaxSorts).ToList(), page, v.PageSize, v.Columns, organisationId);
    }

    public async Task<PreparedView> PrepareAsync(string viewRef, Guid? organisationId = null, int page = 1, string? extraText = null, IReadOnlyList<FilterClause>? extraFilters = null, IReadOnlyList<SortClause>? sortOverride = null, CancellationToken ct = default)
    {
        var view = await GetAsync(viewRef, organisationId, ct);
        var org = organisationId ?? view.OrganisationId;
        var catalogue = await register.CatalogueAsync(org, ct);
        var notes = new List<string>(view.Notes);
        // Anything the person cannot use is dropped with a note; a shared view built by someone with wider access still runs, showing only what they may see.
        var columns = view.Definition.Columns.Where(c => catalogue.Find(c) is { Displayable: true }).ToList();
        foreach (var c in view.Definition.Columns.Except(columns, StringComparer.OrdinalIgnoreCase)) notes.Add($"The column '{c}' is not available to you and is not shown.");
        var sorts = (sortOverride is { Count: > 0 } ? sortOverride : view.Definition.Sort).Where(s => catalogue.Find(s.Field) is { Sortable: true }).ToList();
        var filters = new List<FilterClause>();
        foreach (var f in view.Definition.Filters)
        {
            if (f.Field == "set" || catalogue.Find(f.Field) is { Filterable: true }) filters.Add(f);
            else notes.Add($"The filter on '{f.Field}' uses a field that is not available to you, so it is not applied.");
        }
        var safe = view.Definition with { Columns = columns.Count > 0 ? columns : RegisterQueryEngine.DefaultColumns.ToList(), Sort = sorts, Filters = filters };
        return new PreparedView(view, ToQuery(safe, org, page, extraText, extraFilters), notes);
    }

    public async Task<RegisterPage> RunAsync(string viewRef, Guid? organisationId = null, int page = 1, string? extraText = null, IReadOnlyList<FilterClause>? extraFilters = null, IReadOnlyList<SortClause>? sortOverride = null, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(viewRef, organisationId, page, extraText, extraFilters, sortOverride, ct);
        var result = await register.SearchAsync(prepared.Query, ct);
        return result with { Notes = [.. prepared.Notes, .. result.Notes] };
    }

    public async Task<IReadOnlyList<RegisterIssue>> ValidateAsync(ViewDefinition definition, Guid? organisationId = null, CancellationToken ct = default)
    {
        var catalogue = await register.CatalogueAsync(organisationId, ct);
        var scale = await access.ClassificationScaleAsync(ct);
        var grants = await access.GrantsAsync("contracts.read", ct);
        var readable = scale.Levels.Where(l => grants.Any(g => g.AllowedClassifications.Contains(l))).ToList();
        var issues = ViewParser.ValidateView(definition, catalogue, readable, SetKeys, "view", requireKey: false).Where(m => m.Severity == ValidationSeverity.Error).Select(m => new RegisterIssue(m.Path, m.Code, m.Message)).ToList();
        if (issues.Count == 0)
            issues.AddRange(await register.ValidateAsync(ToQuery(definition, organisationId, 1), ct));
        return issues;
    }

    public async Task<string> SaveMineAsync(string? existingRef, ViewDefinition definition, CancellationToken ct = default)
    {
        var me = Me; var tenantId = tenant.RequireTenantId();
        var issues = await ValidateAsync(definition, null, ct);
        if (issues.Count > 0) throw new RegisterQueryException(issues);
        var json = ViewParser.ToJson(new ViewSet(1, [definition with { Key = "mine" }]));
        return await uow.ExecuteAsync(async c =>
        {
            SavedView row;
            if (existingRef is not null && existingRef.StartsWith("personal:", StringComparison.Ordinal) && Guid.TryParseExact(existingRef[9..], "N", out var id))
            {
                row = await db.Set<SavedView>().FirstOrDefaultAsync(v => v.Id == id && v.OwnerUserId == me, c) ?? throw new ResourceNotFoundException();
                row.Replace(definition.Name, json, clock.UtcNow);
            }
            else
            {
                if (await db.Set<SavedView>().CountAsync(v => v.OwnerUserId == me, c) >= 50) throw new DomainException("too_many_views", "You can keep at most 50 saved views. Delete one first.");
                row = SavedView.Create(tenantId, me, definition.Name, json, clock.UtcNow);
                db.Add(row);
            }
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("view_name_in_use", "You already have a view with that name."); }
            audit.Record(new AuditEntry("contracts.view.saved", nameof(SavedView), row.Id.ToString(), NewValuesJson: JsonSerializer.Serialize(new { row.Name })));
            return "personal:" + row.Id.ToString("N");
        }, ct);
    }

    public async Task DeleteMineAsync(string viewRef, CancellationToken ct = default)
    {
        var me = Me;
        if (!viewRef.StartsWith("personal:", StringComparison.Ordinal) || !Guid.TryParseExact(viewRef[9..], "N", out var id)) throw new ResourceNotFoundException();
        await uow.ExecuteAsync(async c =>
        {
            var row = await db.Set<SavedView>().FirstOrDefaultAsync(v => v.Id == id && v.OwnerUserId == me, c) ?? throw new ResourceNotFoundException();
            db.Remove(row);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("contracts.view.deleted", nameof(SavedView), id.ToString(), NewValuesJson: JsonSerializer.Serialize(new { row.Name })));
        }, ct);
    }
}
