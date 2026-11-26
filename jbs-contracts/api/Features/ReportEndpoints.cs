using System.Globalization;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Reports for admins and auditors (product-spec sections 11 and 13; D-82 to D-84): the register, the pipeline, approvals and the audit
/// trail, each on screen and as an Excel download. The register also prints cleanly to PDF from the browser (D-82), with each person's
/// choice of columns and the Contracts list's filters (D-86). Auditors only read.
/// </summary>
public static class ReportEndpoints
{
    private static readonly TimeZoneInfo Brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
    internal static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
    internal static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Brisbane);

    internal static async Task<(Person? Me, IResult? Refusal)> RequireAsync(CurrentUser current, Db db)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return (null, refusal);
        return me!.IsAdmin || me.IsAuditor || me.IsReports ? (me, null) : (null, ApiResults.NotAllowed("Reports are for admins, auditors and people with the Reports tick. Ask an admin."));
    }

    /// <summary>The audit trail and Download everything: admins and auditors only (the Reports tick doesn't include them, D-110).</summary>
    internal static async Task<(Person? Me, IResult? Refusal)> RequireAuditAsync(CurrentUser current, Db db)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return (null, refusal);
        return me!.IsAdmin || me.IsAuditor ? (me, null) : (null, ApiResults.NotAllowed("The audit trail is for admins and auditors."));
    }

    internal sealed class BySupplierRow
    {
        public int ContractId { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public string Stage { get; set; } = "";
        public DateTime? EndDate { get; set; }
        public bool IsEvergreen { get; set; }
        public string OwnerName { get; set; } = "";
        public int SupplierId { get; set; }
        public string SupplierName { get; set; } = "";
        public string? VendorNumber { get; set; }
        /// <summary>Covered through its group (a franchisee), rather than the supplier the contract is with.</summary>
        public bool ViaGroup { get; set; }
        /// <summary>The group, if any: the head supplier's name.</summary>
        public string? GroupName { get; set; }
    }

    private static async Task<List<BySupplierRow>> BySupplierAsync(SqlConnection c, Person me, string? scope, bool groupsOnly)
    {
        var p = ContractAccess.Params(me);
        return (await c.QueryAsync<BySupplierRow>($"""
            SELECT k.Id AS ContractId, k.Reference, k.Title, k.Stage, k.EndDate, k.IsEvergreen, o.Name AS OwnerName,
                   cov.Id AS SupplierId, cov.Name AS SupplierName, cov.VendorNumber, cov.ViaGroup,
                   CASE WHEN EXISTS (SELECT 1 FROM contracts.Supplier m WHERE m.ParentSupplierId = s.Id) THEN s.Name ELSE ps.Name END AS GroupName
            FROM contracts.Contract k
            JOIN contracts.Supplier s ON s.Id = k.SupplierId
            LEFT JOIN contracts.Supplier ps ON ps.Id = s.ParentSupplierId
            JOIN contracts.Person o ON o.Id = k.OwnerId
            CROSS APPLY (SELECT s.Id, s.Name, s.VendorNumber, CAST(0 AS BIT) AS ViaGroup
                         UNION ALL
                         SELECT m.Id, m.Name, m.VendorNumber, CAST(1 AS BIT) FROM contracts.Supplier m WHERE m.ParentSupplierId = s.Id) cov
            WHERE k.Stage IN ({StagesFor(scope)}) AND {ContractAccess.VisibleSql("k")}
              {(groupsOnly ? "AND (s.ParentSupplierId IS NOT NULL OR EXISTS (SELECT 1 FROM contracts.Supplier m WHERE m.ParentSupplierId = s.Id))" : "")}
            ORDER BY k.Reference, cov.ViaGroup, cov.Name
            """, p)).ToList();
    }

    internal static IResult File(byte[] bytes, string name, TimeProvider clock) =>
        Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"jbs-contracts-{name}-{TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Brisbane):yyyy-MM-dd}.xlsx");

    // ---------------------------------------------------------------- Register

    private static string StagesFor(string? scope) => scope switch
    {
        "ended" => "N'Effective', N'Ended'",
        "all" => "N'Effective', N'Ended', N'Archived'",
        "everything" => "N'Draft', N'LegalReview', N'Signing', N'Approval', N'Effective', N'Ended', N'Archived', N'Withdrawn'",
        _ => "N'Effective'",
    };

    internal sealed class RegisterRow
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public string Stage { get; set; } = "";
        public string? Description { get; set; }
        public string? SupplierName { get; set; }
        public string? SupplierVendorNumber { get; set; }
        public string? CategoryName { get; set; }
        public string? ParentCategoryName { get; set; }
        public string? AgreementTypeName { get; set; }
        public string? BusinessUnitName { get; set; }
        public string? SiteName { get; set; }
        public string? EntityName { get; set; }
        public string? FunctionName { get; set; }
        public string OwnerName { get; set; } = "";
        public string? BackupOwnerName { get; set; }
        public bool IsConfidential { get; set; }
        public bool NeedsLegalReview { get; set; }
        public decimal? Value { get; set; }
        public string CurrencyCode { get; set; } = "AUD";
        public string? SapOutlineAgreement { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EffectiveDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsEvergreen { get; set; }
        public bool AutoRenews { get; set; }
        public int? RenewalTermMonths { get; set; }
        public int? NoticePeriodDays { get; set; }
        public DateTime? ReviewDate { get; set; }
        public DateTime? RegisteredUtc { get; set; }
        public string? RegisteredByName { get; set; }
        public string? RenewalDecision { get; set; }
        public DateTime? EndedOn { get; set; }
        public string? EndedReason { get; set; }
        public string? LegalOutcome { get; set; }
        public string? LegacyReference { get; set; }

        public DateTime? NoticeDeadline => NoticePeriodDays is not { } n ? null : (IsEvergreen ? ReviewDate : EndDate) is { } a ? a.AddDays(-n) : null;
        public string? FamilyCategoryName { get; set; }
        public string? OtherCategories { get; set; }
        public string? Category => CategoryName is null ? null : string.Join(" > ", new[] { FamilyCategoryName, ParentCategoryName, CategoryName }.OfType<string>());
        /// <summary>The main category's family (D-115), for reporting by family.</summary>
        public string? Family => CategoryName is null ? null : FamilyCategoryName ?? ParentCategoryName ?? CategoryName;
    }

    /// <summary>
    /// A column the register can show (D-86): every built-in and added field as the admin has named and arranged them, then the register's
    /// own details. Type tells the screen how to show it: text, date, money, number or yesno.
    /// </summary>
    public sealed record RegisterColumn(string Key, string Label, string Group, string Type)
    {
        internal FieldRow? Added { get; init; }
    }

    private static readonly string[] DefaultColumns = ["title", "supplier", "owner", "value", "effectiveDate", "endDate", "noticeDeadline", "registered"];

    private static readonly Dictionary<string, string> BuiltInTypes = new()
    {
        ["title"] = "text", ["supplier"] = "text", ["category"] = "text", ["agreementType"] = "text", ["description"] = "text", ["owner"] = "text",
        ["backupOwner"] = "text", ["confidential"] = "yesno", ["businessUnit"] = "text", ["site"] = "text", ["entity"] = "text", ["function"] = "text", ["value"] = "money",
        ["sapOutlineAgreement"] = "text", ["startDate"] = "date", ["effectiveDate"] = "date", ["endDate"] = "date", ["evergreen"] = "yesno",
        ["autoRenews"] = "yesno", ["renewalTermMonths"] = "number", ["noticePeriodDays"] = "number", ["reviewDate"] = "date", ["needsLegalReview"] = "yesno",
    };

    internal static List<RegisterColumn> RegisterColumnsFor(List<FieldRow> fields, List<SectionRow> sections)
    {
        var titles = sections.ToDictionary(x => x.Key, x => x.Title);
        var hiddenSections = sections.Where(x => x.IsHidden).Select(x => x.Key).ToHashSet();
        var columns = new List<RegisterColumn>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Reference" };
        string Unique(string label, string key) => labels.Add(label) ? label : labels.Add($"{label} ({key})") ? $"{label} ({key})" : $"{label} ({key}) ";
        foreach (var f in fields.Where(f => !f.IsHidden && !hiddenSections.Contains(f.SectionKey)))
        {
            var group = titles.GetValueOrDefault(f.SectionKey, "Other");
            if (f.IsBuiltIn)
            {
                if (BuiltInTypes.TryGetValue(f.Key, out var type)) columns.Add(new(f.Key, Unique(f.Label, f.Key), group, type));
                // The main category's family and the other categories sit next to the category (D-115).
                if (f.Key == "category")
                {
                    columns.Add(new("family", Unique("Family", "family"), group, "text"));
                    columns.Add(new("otherCategories", Unique("Other categories", "otherCategories"), group, "text"));
                }
                // The supplier's SAP vendor number lives on the supplier; it sits next to the supplier (Jeff, 25 Nov).
                if (f.Key == "supplier") columns.Add(new("supplierVendorNumber", Unique("Supplier SAP vendor number", "supplierVendorNumber"), group, "text"));
            }
            else columns.Add(new(f.Key, Unique(f.Label, f.Key), group, "text") { Added = f });
        }
        foreach (var (key, label, type) in new[]
        {
            ("stage", "Stage", "text"), ("legacyReference", "Old reference", "text"), ("noticeDeadline", "Notice deadline", "date"), ("registered", "Registered", "date"), ("registeredBy", "Registered by", "text"),
            ("renewalDecision", "Renewal decision", "text"), ("endedOn", "Ended on", "date"), ("endedReason", "Why it ended", "text"), ("legalOutcome", "Legal outcome", "text"),
        })
            columns.Add(new(key, Unique(label, key), "Register", type));
        return columns;
    }

    internal static object? BuiltInValue(RegisterRow r, string key) => key switch
    {
        "title" => r.Title, "supplier" => r.SupplierName, "supplierVendorNumber" => r.SupplierVendorNumber, "category" => r.Category, "family" => r.Family, "otherCategories" => r.OtherCategories, "agreementType" => r.AgreementTypeName, "description" => r.Description,
        "owner" => r.OwnerName, "backupOwner" => r.BackupOwnerName, "confidential" => r.IsConfidential, "businessUnit" => r.BusinessUnitName, "site" => r.SiteName, "entity" => r.EntityName, "function" => r.FunctionName,
        "value" => r.Value, "sapOutlineAgreement" => r.SapOutlineAgreement, "startDate" => D(r.StartDate), "effectiveDate" => D(r.EffectiveDate),
        "endDate" => D(r.EndDate), "evergreen" => r.IsEvergreen, "autoRenews" => r.AutoRenews, "renewalTermMonths" => r.RenewalTermMonths, "noticePeriodDays" => r.NoticePeriodDays, "reviewDate" => D(r.ReviewDate),
        "needsLegalReview" => r.NeedsLegalReview, "stage" => ContractEndpoints.StageNames[r.Stage], "noticeDeadline" => D(r.NoticeDeadline),
        "registered" => r.RegisteredUtc is { } ru ? DateOnly.FromDateTime(Local(ru)) : null, "registeredBy" => r.RegisteredByName,
        "renewalDecision" => r.RenewalDecision is { } d ? RenewalEndpoints.DecisionNames[d] : null, "endedOn" => D(r.EndedOn), "endedReason" => r.EndedReason,
        "legalOutcome" => r.LegalOutcome, "legacyReference" => r.LegacyReference,
        _ => null,
    };

    internal sealed record Register(List<RegisterRow> Rows, List<RegisterColumn> Columns, Dictionary<int, Dictionary<string, (object? Value, string Display)>> Added, bool Filtered);

    private static Task<Register> RegisterAsync(SqlConnection c, Person me, string? scope, HttpRequest request) =>
        RegisterAsync(c, me, scope, request.Query.Where(x => x.Key.StartsWith("f.", StringComparison.Ordinal)).Select(x => KeyValuePair.Create(x.Key[2..], x.Value.ToString())));

    internal static async Task<Register> RegisterAsync(SqlConnection c, Person me, string? scope, IEnumerable<KeyValuePair<string, string>> filterValues)
    {
        var fields = await Fields.LoadAsync(c);
        var columns = RegisterColumnsFor(fields, await Fields.LoadSectionsAsync(c));
        var p = ContractAccess.Params(me);
        // The same filters as the Contracts list: f.{field key}={value}, for the fields an admin made filterable.
        var filters = Fields.FilterSql(fields, filterValues, p);
        var rows = (await c.QueryAsync<RegisterRow>($"""
            SELECT k.Id, k.Reference, k.Title, k.Stage, k.Description, s.Name AS SupplierName, s.VendorNumber AS SupplierVendorNumber, cat.Name AS CategoryName, parent.Name AS ParentCategoryName, family.Name AS FamilyCategoryName, occ.OtherCategories,
                   at.Value AS AgreementTypeName, lv.BusinessUnitName, lv.SiteName, lv.EntityName, lv.FunctionName, o.Name AS OwnerName,
                   b.Name AS BackupOwnerName, k.IsConfidential, k.NeedsLegalReview, k.Value, k.CurrencyCode, k.SapOutlineAgreement, k.StartDate, k.EffectiveDate, k.EndDate,
                   k.IsEvergreen, k.AutoRenews, k.RenewalTermMonths,
                   k.NoticePeriodDays, k.ReviewDate, k.RegisteredUtc, rb.Name AS RegisteredByName, k.RenewalDecision, k.EndedOn, k.EndedReason, k.LegalOutcome, k.LegacyReference
            FROM contracts.Contract k
            LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
            LEFT JOIN contracts.Category cat ON cat.Id = k.CategoryId LEFT JOIN contracts.Category parent ON parent.Id = cat.ParentId
            LEFT JOIN contracts.Category family ON family.Id = parent.ParentId
            OUTER APPLY (SELECT STRING_AGG(CAST(CONCAT_WS(N' > ', ocf.Name, ocp.Name, oc.Name) AS NVARCHAR(MAX)), N'; ') WITHIN GROUP (ORDER BY CONCAT_WS(N' > ', ocf.Name, ocp.Name, oc.Name)) AS OtherCategories
                FROM contracts.ContractCategory cc JOIN contracts.Category oc ON oc.Id = cc.CategoryId
                LEFT JOIN contracts.Category ocp ON ocp.Id = oc.ParentId LEFT JOIN contracts.Category ocf ON ocf.Id = ocp.ParentId WHERE cc.ContractId = k.Id) occ
            LEFT JOIN contracts.ListItem at ON at.Id = k.AgreementTypeId
            OUTER APPLY (SELECT
                    STRING_AGG(CASE WHEN li.ListName = N'BusinessUnit' THEN CAST(li.Value AS NVARCHAR(MAX)) END, N', ') WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS BusinessUnitName,
                    STRING_AGG(CASE WHEN li.ListName = N'Site' THEN CAST(li.Value AS NVARCHAR(MAX)) END, N', ') WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS SiteName,
                    STRING_AGG(CASE WHEN li.ListName = N'Entity' THEN CAST(li.Value AS NVARCHAR(MAX)) END, N', ') WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS EntityName,
                    STRING_AGG(CASE WHEN li.ListName = N'Function' THEN CAST(li.Value AS NVARCHAR(MAX)) END, N', ') WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS FunctionName
                FROM contracts.ContractListValue clv JOIN contracts.ListItem li ON li.Id = clv.ListItemId WHERE clv.ContractId = k.Id) lv
            JOIN contracts.Person o ON o.Id = k.OwnerId LEFT JOIN contracts.Person b ON b.Id = k.BackupOwnerId LEFT JOIN contracts.Person rb ON rb.Id = k.RegisteredById
            WHERE k.Stage IN ({StagesFor(scope)}) AND {ContractAccess.VisibleSql("k")}{string.Concat(filters.Select(f => $" AND {f}"))}
            ORDER BY k.Reference
            """, p)).ToList();
        var added = columns.Where(x => x.Added is not null).Select(x => x.Added!).ToList();
        var values = await Fields.ReadAsync(c, null, rows.Select(r => r.Id), added);
        return new(rows, columns, values, filters.Count > 0);
    }

    private static async Task<List<string>> ChosenAsync(SqlConnection c, Person me, List<RegisterColumn> columns)
    {
        var json = await c.ExecuteScalarAsync<string?>("SELECT ColumnsJson FROM contracts.ReportPreference WHERE PersonId = @id AND Report = N'register'", new { id = me.Id });
        var keys = columns.Select(x => x.Key).ToHashSet();
        var chosen = (json is null ? DefaultColumns : System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? DefaultColumns).Where(keys.Contains).Distinct().ToList();
        return chosen.Count > 0 ? chosen : DefaultColumns.Where(keys.Contains).ToList();
    }

    public sealed record ColumnsInput(List<string>? Columns);

    // ---------------------------------------------------------------- Endpoints

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports");

        group.MapGet("/register", async (string? scope, HttpRequest request, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var register = await RegisterAsync(c, me!, scope, request);
            return Results.Ok(new
            {
                columns = register.Columns.Select(x => new { x.Key, x.Label, x.Group, x.Type }),
                chosen = await ChosenAsync(c, me!, register.Columns),
                defaults = DefaultColumns,
                filtered = register.Filtered,
                rows = register.Rows.Select(r => new
                {
                    r.Id, r.Reference, r.IsConfidential, r.CurrencyCode,
                    Values = register.Columns.ToDictionary(x => x.Key, x => x.Added is { } f
                        ? (register.Added[r.Id].TryGetValue(f.Key, out var v) && v.Display != "" ? v.Display : null)
                        : BuiltInValue(r, x.Key)),
                }),
                totals = new
                {
                    count = register.Rows.Count,
                    value = register.Rows.Where(r => r.Value is not null).GroupBy(r => r.CurrencyCode).Select(g => new { currency = g.Key, total = g.Sum(r => r.Value!.Value) }),
                },
            });
        });

        // Each person's own choice of columns. Only their own view, so not audited (like Home's "Choose what I see").
        group.MapPut("/register/columns", async (ColumnsInput body, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var keys = RegisterColumnsFor(await Fields.LoadAsync(c), await Fields.LoadSectionsAsync(c)).Select(x => x.Key).ToHashSet();
            var chosen = (body.Columns ?? []).Distinct().ToList();
            if (chosen.Count == 0) return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid", "Choose at least one column.");
            if (chosen.FirstOrDefault(k => !keys.Contains(k)) is { } unknown) return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid", $"There's no column called \"{unknown}\". Refresh the page and try again.");
            await c.ExecuteAsync("""
                MERGE contracts.ReportPreference AS t USING (SELECT @id AS PersonId, N'register' AS Report) AS s ON t.PersonId = s.PersonId AND t.Report = s.Report
                WHEN MATCHED THEN UPDATE SET ColumnsJson = @json, UpdatedUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (PersonId, Report, ColumnsJson) VALUES (@id, N'register', @json);
                """, new { id = me!.Id, json = System.Text.Json.JsonSerializer.Serialize(chosen) });
            return Results.Ok(new { chosen });
        });

        // Excel has every column, whatever is chosen on screen, and follows the filters.
        group.MapGet("/register.xlsx", async (string? scope, HttpRequest request, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var register = await RegisterAsync(c, me!, scope, request);
            var people = (await c.QueryAsync<Person>("SELECT * FROM contracts.Person")).ToDictionary(p => p.Id);
            var columns = new List<Excel.Column> { new("Reference", null, 16) };
            foreach (var x in register.Columns)
            {
                columns.Add(new(x.Label, null, x.Type is "date" or "yesno" or "number" ? 14 : x.Key is "title" or "description" or "endedReason" or "legalOutcome" ? 36 : 22));
                if (x.Key == "value") columns.Add(new("Currency", null, 10));
            }
            var data = register.Rows.Select(r =>
            {
                var row = new List<object?> { r.Reference };
                foreach (var x in register.Columns)
                {
                    row.Add(x.Added is { } f
                        ? register.Added[r.Id].TryGetValue(f.Key, out var v) ? Fields.ExcelValue(f, v, id => people.GetValueOrDefault(id)?.WindowsAccount) : null
                        : BuiltInValue(r, x.Key));
                    if (x.Key == "value") row.Add(r.CurrencyCode);
                }
                return row.ToArray();
            });
            return File(Excel.Write("Register", columns, data), "register", clock);
        });

        group.MapGet("/pipeline", async (CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var now = clock.GetUtcNow().UtcDateTime;
            var open = (await c.QueryAsync<(int Id, string Reference, string Title, string Stage, DateTime StageSinceUtc, string? WhoseTurn, string? LegalPersonName, string OwnerName, string? SupplierName)>($"""
                SELECT k.Id, k.Reference, k.Title, k.Stage, k.StageSinceUtc, k.WhoseTurn, lp.Name AS LegalPersonName, o.Name AS OwnerName, s.Name AS SupplierName
                FROM contracts.Contract k JOIN contracts.Person o ON o.Id = k.OwnerId LEFT JOIN contracts.Person lp ON lp.Id = k.LegalPersonId
                LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
                WHERE k.Stage IN (N'Draft', N'LegalReview', N'Signing', N'Approval') AND {ContractAccess.VisibleSql("k")}
                """, ContractAccess.Params(me!))).ToList();
            double Days(DateTime since) => Math.Max(0, (now - since).TotalDays);
            string[] order = ["Draft", "LegalReview", "Signing", "Approval"];
            var counts = await c.QueryAsync<(string Stage, int Count)>($"SELECT k.Stage, COUNT(*) FROM contracts.Contract k WHERE {ContractAccess.VisibleSql("k")} GROUP BY k.Stage", ContractAccess.Params(me!));
            var variations = await c.QueryAsync<(string Stage, int Count)>("SELECT Stage, COUNT(*) FROM contracts.Variation WHERE Stage IN (N'Draft', N'LegalReview', N'Approval') GROUP BY Stage");
            return Results.Ok(new
            {
                stages = order.Select(st =>
                {
                    var inStage = open.Where(o => o.Stage == st).ToList();
                    return new
                    {
                        Stage = st, Name = ContractEndpoints.StageNames[st], Count = inStage.Count,
                        AverageDays = inStage.Count == 0 ? 0 : Math.Round(inStage.Average(o => Days(o.StageSinceUtc)), 1),
                        LongestDays = inStage.Count == 0 ? 0 : (int)inStage.Max(o => Days(o.StageSinceUtc)),
                    };
                }),
                legal = new
                {
                    legalsTurn = open.Count(o => o.Stage == "LegalReview" && o.WhoseTurn == "Legal"),
                    ownersTurn = open.Count(o => o.Stage == "LegalReview" && o.WhoseTurn == "Owner"),
                    notPickedUp = open.Count(o => o.Stage == "LegalReview" && o.WhoseTurn == "Legal" && o.LegalPersonName is null),
                },
                totals = counts.ToDictionary(x => x.Stage, x => x.Count),
                variations = variations.ToDictionary(x => x.Stage, x => x.Count),
                waitingLongest = open.OrderByDescending(o => Days(o.StageSinceUtc)).Take(15).Select(o => new
                {
                    o.Id, o.Reference, o.Title, o.Stage, StageName = ContractEndpoints.StageNames[o.Stage], Days = (int)Days(o.StageSinceUtc),
                    With = o.Stage == "LegalReview" ? (o.WhoseTurn == "Legal" ? o.LegalPersonName ?? "Legal (not picked up)" : o.OwnerName) : o.Stage == "Approval" ? "Approvers" : o.OwnerName,
                    o.SupplierName,
                }),
            });
        });

        // By supplier (D-119): one row per contract per supplier it covers, the supplier it's with and, when that supplier heads a
        // group (a franchise's head company), each supplier in the group, with their SAP vendor numbers.
        group.MapGet("/by-supplier", async (string? scope, bool? groupsOnly, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await BySupplierAsync(c, me!, scope, groupsOnly ?? true);
            return Results.Ok(new
            {
                rows = rows.Select(r => new
                {
                    r.ContractId, r.Reference, r.Title, StageName = ContractEndpoints.StageNames[r.Stage], EndDate = D(r.EndDate), r.IsEvergreen, r.OwnerName,
                    r.SupplierId, r.SupplierName, r.VendorNumber, r.ViaGroup, r.GroupName,
                }),
                contracts = rows.Select(r => r.ContractId).Distinct().Count(),
                suppliers = rows.Select(r => r.SupplierId).Distinct().Count(),
            });
        });

        group.MapGet("/by-supplier.xlsx", async (string? scope, bool? groupsOnly, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await BySupplierAsync(c, me!, scope, groupsOnly ?? true);
            Excel.Column[] columns = [new("Reference", null, 16), new("Contract", null, 34), new("Stage", null, 12), new("Supplier", null, 32), new("SAP vendor number", null, 18),
                new("Covered as", null, 14), new("Group", null, 30), new("End date", null, 13), new("Owner", null, 22)];
            return File(Excel.Write("By supplier", columns, rows.Select(r => new object?[]
            {
                r.Reference, r.Title, ContractEndpoints.StageNames[r.Stage], r.SupplierName, r.VendorNumber, r.ViaGroup ? "Group member" : "Signed it", r.GroupName,
                r.IsEvergreen ? "Evergreen" : D(r.EndDate), r.OwnerName,
            })), "by-supplier", clock);
        });

        group.MapGet("/approvals", async (int? days, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await ApprovalsAsync(c, me!, days, clock);
            var decided = rows.Where(r => r.TurnaroundDays is not null).ToList();
            return Results.Ok(new
            {
                days = Window(days),
                rows,
                summary = new
                {
                    approved = rows.Count(r => r.Decision == "Approved"), sentBack = rows.Count(r => r.Decision == "SentBack"),
                    averageDays = decided.Count == 0 ? (double?)null : Math.Round(decided.Average(r => r.TurnaroundDays!.Value), 1),
                    byApprover = rows.GroupBy(r => r.ByName).Select(g => new
                    {
                        Name = g.Key, Approved = g.Count(r => r.Decision == "Approved"), SentBack = g.Count(r => r.Decision == "SentBack"),
                        AverageDays = g.Any(r => r.TurnaroundDays is not null) ? Math.Round(g.Where(r => r.TurnaroundDays is not null).Average(r => r.TurnaroundDays!.Value), 1) : (double?)null,
                    }).OrderBy(x => x.Name),
                },
            });
        });

        group.MapGet("/approvals.xlsx", async (int? days, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await ApprovalsAsync(c, me!, days, clock);
            Excel.Column[] columns = [new("Decided", null, 18), new("Reference", null, 16), new("Contract", null, 34), new("What", null, 14), new("Decision", null, 12),
                new("By", null, 22), new("As backup for", null, 22), new("Days waiting", null, 12), new("Reason", null, 40)];
            return File(Excel.Write("Approvals", columns, rows.Select(r => new object?[]
            {
                Local(r.AtUtc), r.Reference, r.Title, r.Variation is { } v ? $"Variation {v}" : "Contract", r.Decision == "Approved" ? "Approved" : "Sent back",
                r.ByName, r.AsBackupForName, r.TurnaroundDays is { } t ? (decimal)t : null, r.Reason,
            })), "approvals", clock);
        });

        group.MapGet("/audit", async (int? days, string? q, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAuditAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await AuditAsync(c, days, q, clock, 500);
            var broken = await AuditLog.VerifyAsync(c);
            return Results.Ok(new
            {
                days = Window(days), intact = broken is null, brokenAt = broken,
                total = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent"),
                rows = rows.Select(r => new { r.Id, OccurredUtc = DateTime.SpecifyKind(r.OccurredUtc, DateTimeKind.Utc), r.ActorName, r.Action, r.Summary, r.ContractId, r.Reference }),
            });
        });

        group.MapGet("/audit.xlsx", async (int? days, string? q, CurrentUser current, Db db, AuditLog audit, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAuditAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await AuditAsync(c, days, q, clock, 100_000);
            Excel.Column[] columns = [new("Event", null, 10), new("When", null, 18), new("Who", null, 26), new("Action", null, 24), new("Contract", null, 16),
                new("What happened", null, 70), new("Details", "Old and new values, as recorded", 60), new("Hash", "Each event's hash includes the previous one, so a changed or removed event shows", 66)];
            // Exporting the audit is itself recorded.
            await using (var tx = await Db.BeginWriteAsync(c))
            {
                await audit.WriteAsync(c, tx, me!.AsActor(), "audit.exported", "Audit", null, $"{me.Name} exported {rows.Count:N0} audit events (last {Window(days)} days{(string.IsNullOrWhiteSpace(q) ? "" : $", matching \"{q.Trim()}\"")}).");
                await tx.CommitAsync();
            }
            return File(Excel.Write("Audit", columns, rows.Select(r => new object?[]
            {
                (int)r.Id, Local(r.OccurredUtc), r.ActorName, r.Action, r.Reference, r.Summary, r.DetailsJson, r.Hash,
            })), "audit", clock);
        });
    }

    private static int Window(int? days) => days is 7 or 30 or 90 or 365 or 3650 ? days.Value : 90;

    public sealed class ApprovalRow
    {
        public int ContractId { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public int? Variation { get; set; }
        public string Decision { get; set; } = "";
        public string ByName { get; set; } = "";
        public string? AsBackupForName { get; set; }
        private DateTime at;
        private DateTime? sent;
        public DateTime AtUtc { get => at; set => at = DateTime.SpecifyKind(value, DateTimeKind.Utc); }
        public DateTime? SentUtc { get => sent; set => sent = value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null; }
        public string? Reason { get; set; }
        public double? TurnaroundDays => SentUtc is { } s ? Math.Round(Math.Max(0, (AtUtc - s).TotalDays), 1) : null;
    }

    private static async Task<List<ApprovalRow>> ApprovalsAsync(SqlConnection c, Person me, int? days, TimeProvider clock)
    {
        var p = ContractAccess.Params(me, new { since = clock.GetUtcNow().UtcDateTime.AddDays(-Window(days)) });
        return (await c.QueryAsync<ApprovalRow>($"""
            SELECT a.ContractId, k.Reference, k.Title, v.Number AS Variation, a.Decision, by_.Name AS ByName, gh.Name AS AsBackupForName, a.AtUtc, a.Reason, sent.OccurredUtc AS SentUtc
            FROM contracts.Approval a JOIN contracts.Contract k ON k.Id = a.ContractId JOIN contracts.Person by_ ON by_.Id = a.ById
            LEFT JOIN contracts.Person gh ON gh.Id = a.AsBackupFor LEFT JOIN contracts.Variation v ON v.Id = a.VariationId
            -- When it was sent for approval (the most recent send before the decision): the turnaround.
            OUTER APPLY (SELECT TOP (1) e.OccurredUtc FROM contracts.AuditEvent e
                         WHERE e.ContractId = a.ContractId AND e.OccurredUtc < DATEADD(SECOND, 1, a.AtUtc) -- AtUtc is to the whole second, rounded
                           AND ((a.VariationId IS NULL AND e.Action = N'contract.sent-for-approval')
                             OR (a.VariationId IS NOT NULL AND e.Action = N'variation.sent-for-approval' AND e.SubjectId = a.VariationId))
                         ORDER BY e.Id DESC) sent
            WHERE a.AtUtc >= @since AND {ContractAccess.VisibleSql("k")}
              -- An approval undone by mistake (D-106) didn't happen, for this report: leave out the undo and the approval it undid.
              AND a.Decision <> N'Undone'
              AND NOT EXISTS (SELECT 1 FROM contracts.Approval u WHERE u.Decision = N'Undone' AND u.ContractId = a.ContractId AND a.VariationId IS NULL
                              AND u.Id = (SELECT MIN(n.Id) FROM contracts.Approval n WHERE n.ContractId = a.ContractId AND n.VariationId IS NULL AND n.Id > a.Id))
            ORDER BY a.AtUtc DESC
            """, p)).ToList();
    }

    private sealed class AuditRow
    {
        public long Id { get; set; }
        public DateTime OccurredUtc { get; set; }
        public string ActorName { get; set; } = "";
        public string Action { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? DetailsJson { get; set; }
        public string Hash { get; set; } = "";
        public int? ContractId { get; set; }
        public string? Reference { get; set; }
    }

    /// <summary>Audit events, newest first. Reports are for admins and auditors, who see every contract, Confidential ones included.</summary>
    private static async Task<List<AuditRow>> AuditAsync(SqlConnection c, int? days, string? q, TimeProvider clock, int top)
    {
        var text = (q ?? "").Trim();
        var like = $"%{text.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]")}%";
        return (await c.QueryAsync<AuditRow>("""
            SELECT TOP (@top) e.Id, e.OccurredUtc, e.ActorName, e.Action, e.Summary, e.DetailsJson, e.Hash, e.ContractId, k.Reference
            FROM contracts.AuditEvent e LEFT JOIN contracts.Contract k ON k.Id = e.ContractId
            WHERE e.OccurredUtc >= @since AND (@text = N'' OR e.Summary LIKE @like OR e.ActorName LIKE @like OR e.Action LIKE @like OR k.Reference LIKE @like)
            ORDER BY e.Id DESC
            """, new { top, since = clock.GetUtcNow().UtcDateTime.AddDays(-Window(days)), text, like })).ToList();
    }
}
