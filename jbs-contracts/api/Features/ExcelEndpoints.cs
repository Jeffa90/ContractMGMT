using System.Globalization;
using System.Net.Mail;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Excel import and export for People, Lists, Suppliers, supplier contacts (D-103) and Contracts (decision D-44).
/// An export is the template for an import. An import is always previewed first; it adds new rows and updates existing ones,
/// never deletes or hides anything, and a blank cell leaves the existing value unchanged. Every row goes through the same
/// rules as the screens and is audited, plus one summary event per import. All in one transaction: all or nothing.
/// </summary>
public static class ExcelEndpoints
{
    public sealed class PlannedRow
    {
        public int Row { get; init; }
        public string Label { get; set; } = "";
        public string Action { get; set; } = "same"; // add, update, same, refused
        public List<string> Notes { get; } = [];
        public int Order { get; set; }
        public Func<Task>? Apply { get; set; }

        public PlannedRow Refuse(string why) { Action = "refused"; Notes.Add(why); Apply = null; return this; }
    }

    /// <param name="ExtraColumns">For an export: the added-field columns actually written, so each row's values line up with them.</param>
    private sealed record Ctx(SqlConnection C, SqlTransaction Tx, Person Me, AuditLog Audit, AppSettings Settings, IReadOnlyList<Excel.Column>? ExtraColumns = null);

    private sealed record Kind(
        string Name, string Label, string Sheet, Excel.Column[] Columns, string[] Required, string[] ExportOnly,
        Func<Person, bool> CanExport, Func<Person, bool> CanImport, string Refusal,
        Func<Ctx, Task<IEnumerable<object?[]>>> Export, Func<Ctx, List<Excel.Row>, Task<List<PlannedRow>>> Plan,
        Func<SqlConnection, SqlTransaction?, Task<List<Excel.Column>>>? ExtraColumns = null);

    private const string Via = " (from Excel)";

    private static readonly Dictionary<string, Kind> Kinds = new[] { People(), Lists(), Suppliers(), SupplierContacts(), Contracts() }.ToDictionary(k => k.Name);

    public static void MapExcelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/excel/{kind}", async (string kind, CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!Kinds.TryGetValue(kind, out var k)) return ApiResults.NotFound("That isn't something that can be exported.");
            if (!k.CanExport(me!)) return ApiResults.NotAllowed(k.Refusal);
            await using var c = await db.OpenAsync();
            await using var tx = c.BeginTransaction();
            // The added-field columns are read once, and each row is filled to match them (an admin may change fields meanwhile).
            var extra = k.ExtraColumns is null ? [] : await k.ExtraColumns(c, tx);
            var rows = await k.Export(new Ctx(c, tx, me!, audit, settings, extra));
            // Columns to fill in, then any added contract fields, then columns that are for information only (not imported).
            var columns = k.Columns.Concat(extra)
                .Concat(k.ExportOnly.Select(h => new Excel.Column(h, "For information only. Changes here aren't imported.", 16))).ToList();
            await tx.CommitAsync();
            var date = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Brisbane).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var bytes = Excel.Write(k.Sheet, columns, rows);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"jbs-contracts-{k.Name}-{date}.xlsx");
        });

        app.MapPost("/api/excel/{kind}/preview", (string kind, HttpRequest request, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            RunAsync(kind, request, current, db, audit, settings, apply: false)).DisableAntiforgery();

        app.MapPost("/api/excel/{kind}/import", (string kind, HttpRequest request, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            RunAsync(kind, request, current, db, audit, settings, apply: true)).DisableAntiforgery();
    }

    private static readonly TimeZoneInfo Brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");

    private static async Task<IResult> RunAsync(string kind, HttpRequest request, CurrentUser current, Db db, AuditLog audit, AppSettings settings, bool apply)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return refusal;
        if (!Kinds.TryGetValue(kind, out var k)) return ApiResults.NotFound("That isn't something that can be imported.");
        if (!k.CanImport(me!)) return ApiResults.NotAllowed(k.Refusal);
        if (!request.HasFormContentType) return ApiResults.Invalid("Choose an Excel file.", new() { ["file"] = "Choose an Excel file (.xlsx)." });
        var file = (await request.ReadFormAsync()).Files.GetFile("file");
        if (file is null || file.Length == 0) return ApiResults.Invalid("Choose an Excel file.", new() { ["file"] = "Choose an Excel file (.xlsx)." });
        if (file.Length > Excel.MaxBytes) return ApiResults.Invalid("That file is too big.", new() { ["file"] = "Excel files can be up to 5 MB. Split it into smaller files." });
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return ApiResults.Invalid("Use an .xlsx file.", new() { ["file"] = "Save the sheet as an Excel workbook (.xlsx) and try again." });

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        buffer.Position = 0;
        await using var c = await db.OpenAsync();
        var extra = k.ExtraColumns is null ? [] : await k.ExtraColumns(c, null);
        var known = k.Columns.Concat(extra).Select(x => x.Header).Concat(k.ExportOnly).ToList();
        var (rows, ignored, problem) = Excel.Read(buffer, known, k.Required);
        if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });

        await using var tx = await Db.BeginWriteAsync(c);
        var ctx = new Ctx(c, tx, me!, audit, settings);
        var plan = await k.Plan(ctx, rows);
        var counts = new
        {
            added = plan.Count(p => p.Action == "add"),
            updated = plan.Count(p => p.Action == "update"),
            unchanged = plan.Count(p => p.Action == "same"),
            refused = plan.Count(p => p.Action == "refused"),
        };
        if (apply)
        {
            foreach (var row in plan.Where(p => p.Apply is not null).OrderBy(p => p.Order).ThenBy(p => p.Row))
                await row.Apply!();
            await audit.WriteAsync(c, tx, me!.AsActor(), $"{k.Name}.imported", "Import", null,
                $"{me.Name} imported {k.Label} from Excel ({file.FileName}): {counts.added} added, {counts.updated} changed, {counts.unchanged} unchanged, {counts.refused} not imported.",
                new { file = file.FileName, counts });
            await tx.CommitAsync();
        }
        return Results.Ok(new
        {
            applied = apply,
            counts,
            ignoredColumns = ignored,
            rows = plan.OrderBy(p => p.Row).Select(p => new { p.Row, p.Label, p.Action, p.Notes }),
        });
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------------------------------------------------------------- People

    private static readonly (string Header, Func<Person, bool> Get, string Label)[] RoleColumns =
    [
        ("Contract owner", p => p.IsContractOwner, "Contract owner"), ("Legal", p => p.IsLegal, "Legal"), ("Approver", p => p.IsApprover, "Approver"),
        ("Group Head", p => p.IsGroupHead, "Group Head"), ("Admin", p => p.IsAdmin, "Admin"), ("Auditor", p => p.IsAuditor, "Auditor"),
        ("Procurement", p => p.IsProcurement, "Procurement"), ("Reports", p => p.IsReports, "Reports"),
    ];

    private static Kind People() => new(
        "people", "people", "People",
        [
            new("Windows logon", "Required. The username (for example jsmith); the domain is added for you. Or type it in full, for example AMH\\jsmith.", 24),
            new("Name", "Required for someone new.", 26), new("Email", "Required for someone new. Reminders and approvals go here.", 32),
            new("Contract owner", "Yes or No", 15), new("Legal", "Yes or No", 10), new("Approver", "Yes or No", 11),
            new("Group Head", "Yes or No. Only one person can be Group Head; it implies Approver.", 12), new("Admin", "Yes or No", 10),
            new("Auditor", "Yes or No", 10), new("Procurement", "Yes or No. Adds and looks after supplier documents, details and reviews.", 13),
            new("Reports", "Yes or No. Sees Reports for the contracts they may see (not the audit trail).", 10), new("Active", "Yes or No. No switches off their access (nothing is deleted).", 10),
        ],
        ["Windows logon"], ["Last seen"],
        me => me.IsAdmin, me => me.IsAdmin, "Only admins can import or export people.",
        async x =>
        {
            var people = await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person ORDER BY Name", transaction: x.Tx);
            return people.Select(p => new object?[] { p.WindowsAccount, p.Name, p.Email, p.IsContractOwner, p.IsLegal, p.IsApprover, p.IsGroupHead, p.IsAdmin, p.IsAuditor, p.IsProcurement, p.IsReports, p.IsActive });
        },
        async (x, rows) =>
        {
            var existing = (await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person", transaction: x.Tx)).ToDictionary(p => WindowsAccount.Key(p.WindowsAccount));
            var seen = new HashSet<string>();
            var plan = new List<PlannedRow>();
            var groupHeadRows = 0;
            foreach (var row in rows)
            {
                var r = new PlannedRow { Row = row.Number, Label = row.Text("Windows logon") ?? "(no logon)" };
                plan.Add(r);
                if (!WindowsAccount.TryNormalise(row.Text("Windows logon"), x.Settings.DefaultDomain, out var account))
                { r.Refuse("Windows logon: type the username (for example jsmith) or DOMAIN\\username."); continue; }
                r.Label = account;
                if (!seen.Add(WindowsAccount.Key(account))) { r.Refuse("This logon is in the sheet more than once; only the first row is used."); continue; }
                existing.TryGetValue(WindowsAccount.Key(account), out var before);

                var problems = new List<string>();
                var roles = new Dictionary<string, bool>();
                foreach (var (header, get, _) in RoleColumns)
                {
                    var v = row.YesNo(header, out var p);
                    if (p is not null) problems.Add(p);
                    roles[header] = v ?? (before is not null && get(before));
                }
                if (roles["Group Head"]) roles["Approver"] = true;
                var active = row.YesNo("Active", out var ap) ?? before?.IsActive ?? true;
                if (ap is not null) problems.Add(ap);
                var name = Clean(row.Text("Name")) ?? before?.Name;
                var email = Clean(row.Text("Email")) ?? before?.Email;
                if (name is null) problems.Add("Name: add their name.");
                else if (name.Length > 200) problems.Add("Name: keep it under 200 characters.");
                if (email is null) problems.Add("Email: add their work email.");
                else if (email.Length > 320 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !email.Contains('.'))
                    problems.Add($"Email: \"{email}\" doesn't look like an email address.");
                if (before?.Id == x.Me.Id && (!roles["Admin"] || !active)) problems.Add("You can't remove your own admin access or switch yourself off. Ask another admin.");
                if (roles["Group Head"] && ++groupHeadRows > 1) problems.Add("Only one person can be Group Head. Tick it on one row only.");
                if (problems.Count > 0) { r.Refuse(string.Join(" ", problems)); continue; }

                var roleText = string.Join(", ", RoleColumns.Where(rc => roles[rc.Header]).Select(rc => rc.Label));
                if (roleText.Length == 0) roleText = "no roles (can look only)";
                if (before is null)
                {
                    r.Action = "add";
                    r.Label = $"{name} ({account})";
                    r.Notes.Add($"New: {roleText}{(active ? "" : ", switched off")}.");
                }
                else
                {
                    var changes = new List<string>();
                    if (before.Name != name) changes.Add($"name to {name}");
                    if (before.Email != email) changes.Add("email");
                    foreach (var (header, get, label) in RoleColumns)
                        if (get(before) != roles[header]) changes.Add(roles[header] ? $"add {label}" : $"remove {label}");
                    if (before.IsActive != active) changes.Add(active ? "switch access back on" : "switch access off");
                    r.Label = $"{name} ({account})";
                    if (changes.Count == 0) { r.Action = "same"; continue; }
                    r.Action = "update";
                    r.Notes.Add($"Change: {string.Join("; ", changes)}.");
                }
                var (b, n, e, ro, act, acc) = (before, name!, email!, roles, active, account);
                r.Apply = async () =>
                {
                    if (ro["Group Head"])
                    {
                        var previous = await x.C.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WITH (UPDLOCK, HOLDLOCK) WHERE IsGroupHead = 1 AND Id <> ISNULL(@id, 0)", new { id = b?.Id }, x.Tx);
                        if (previous is not null)
                        {
                            await x.C.ExecuteAsync("UPDATE contracts.Person SET IsGroupHead = 0, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @id", new { id = previous.Id }, x.Tx);
                            await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "person.group-head-moved", "Person", previous.Id, $"{x.Me.Name} made {n} the Group Head in place of {previous.Name}, who stays an approver (backup){Via}.");
                        }
                    }
                    var args = new { acc, n, e, co = ro["Contract owner"], le = ro["Legal"], ap = ro["Approver"], gh = ro["Group Head"], ad = ro["Admin"], au = ro["Auditor"], pr = ro["Procurement"], rp = ro["Reports"], act, me = x.Me.Id, id = b?.Id };
                    if (b is null)
                    {
                        var id = await x.C.ExecuteScalarAsync<int>("""
                            INSERT INTO contracts.Person (WindowsAccount, Name, Email, IsContractOwner, IsLegal, IsApprover, IsGroupHead, IsAdmin, IsAuditor, IsProcurement, IsReports, IsActive, CreatedById, UpdatedById)
                            OUTPUT INSERTED.Id VALUES (@acc, @n, @e, @co, @le, @ap, @gh, @ad, @au, @pr, @rp, @act, @me, @me)
                            """, args, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "person.added", "Person", id, $"{x.Me.Name} added {n} ({acc}) as {roleText}{Via}.", args);
                        await PeopleEndpoints.QueueWelcomeAsync(x.C, x.Tx, id, n, e, acc, roleText, x.Me.Name, x.Settings);
                    }
                    else
                    {
                        await x.C.ExecuteAsync("""
                            UPDATE contracts.Person SET Name = @n, Email = @e, IsContractOwner = @co, IsLegal = @le, IsApprover = @ap, IsGroupHead = @gh, IsAdmin = @ad,
                                IsAuditor = @au, IsProcurement = @pr, IsReports = @rp, IsActive = @act, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id
                            """, args, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "person.changed", "Person", b.Id, $"{x.Me.Name} changed {n}: {string.Join("; ", r.Notes).Replace("Change: ", "")}".TrimEnd('.') + $"{Via}.", args);
                        if (b.IsLegal && b.IsActive && !(ro["Legal"] && act))
                            await LegalEndpoints.ReleaseReviewsAsync(x.C, x.Tx, x.Audit, x.Me.AsActor(), b.Id, n);
                    }
                };
            }
            // There must always be an active admin. The importer is one and can't remove themselves, so this always holds.
            return plan;
        });

    // ---------------------------------------------------------------- Lists

    private static readonly Dictionary<string, (string ListName, string Label)> ListNames = new()
    {
        ["category"] = ("Category", "Category"), ["agreementtype"] = ("AgreementType", "Agreement type"), ["businessunit"] = ("BusinessUnit", "Business unit"),
        ["site"] = ("Site", "Site"), ["entity"] = ("Entity", "Entity"), ["function"] = ("Function", "Function"), ["currency"] = ("Currency", "Currency"),
        ["supplierdocumenttype"] = ("SupplierDocumentType", "Supplier document type"), ["suppliertier"] = ("SupplierTier", "Supplier tier"),
        ["contractdocumentkind"] = ("DocumentKind", "Contract document kind"), ["supplierrisklevel"] = ("SupplierRisk", "Supplier risk level"),
    };

    private static Kind Lists() => new(
        "lists", "lists", "Lists",
        [
            new("List", "Required: Category, Agreement type, Business unit, Site, Entity, Function, Currency, Supplier document type or Supplier tier.", 22),
            new("Name", "Required. For a currency, the three-letter code (AUD).", 30),
            new("Parent category", "Categories only. Blank for a family; for a category, its family; for a subcategory, Family > Category.", 34),
            new("Active", "Yes or No. No hides it from the choices; contracts that use it keep it.", 10),
        ],
        ["List", "Name"], [],
        me => me.IsAdmin, me => me.IsAdmin, "Only admins can import or export the lists.",
        async x =>
        {
            var cats = (await x.C.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category", transaction: x.Tx)).ToList();
            var items = await x.C.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem ORDER BY ListName, SortOrder, Value", transaction: x.Tx);
            var labels = ListNames.Values.ToDictionary(v => v.ListName, v => v.Label);
            // Families, each followed by its categories and their subcategories; the parent as a path (D-115).
            var tree = new CategoryTree(cats);
            IEnumerable<object?[]> Under(int? parentId) => tree.Children(parentId).SelectMany(n =>
                new[] { new object?[] { "Category", n.Name, n.ParentId is { } p ? tree.Path(p) : null, n.IsActive } }.Concat(Under(n.Id)));
            var catRows = Under(null);
            return catRows.Concat(items.Select(i => new object?[] { labels[i.ListName], i.Value, null, i.IsActive }));
        },
        async (x, rows) =>
        {
            var cats = (await x.C.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category", transaction: x.Tx)).ToList();
            var items = (await x.C.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem", transaction: x.Tx)).ToList();
            static string K(string s) => s.Trim().ToUpperInvariant();
            // Every category path in the sheet, so a row can sit under a family or category added in another row (D-115).
            var tree = new CategoryTree(cats);
            static string PathKey(IEnumerable<string> parts) => string.Join(">", parts.Select(K));
            var sheetPaths = rows.Where(rw => Excel.Normalise(rw.Text("List") ?? "") == "category" && Clean(rw.Text("Name")) is not null)
                .Select(rw => PathKey(CategoryTree.SplitPath(Clean(rw.Text("Parent category")) ?? "").Append(rw.Text("Name")!.Trim()))).ToHashSet();
            var seen = new HashSet<string>();
            var plan = new List<PlannedRow>();
            foreach (var row in rows)
            {
                var r = new PlannedRow { Row = row.Number };
                plan.Add(r);
                var listText = row.Text("List") ?? "";
                var name = Clean(row.Text("Name"));
                var parent = Clean(row.Text("Parent category"));
                r.Label = parent is null ? $"{listText}: {name}" : $"{listText}: {parent} > {name}";
                if (!ListNames.TryGetValue(Excel.Normalise(listText), out var list)) { r.Refuse($"List: \"{listText}\" isn't one of Category, Agreement type, Business unit, Site, Entity, Function, Currency, Supplier document type or Supplier tier."); continue; }
                if (name is null) { r.Refuse("Name: add a name."); continue; }
                if (name.Length > 100) { r.Refuse("Name: keep it under 100 characters."); continue; }
                var active = row.YesNo("Active", out var ap);
                if (ap is not null) { r.Refuse(ap); continue; }
                if (list.ListName == "Currency")
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z]{3}$")) { r.Refuse("Name: a currency is a three-letter code, for example AUD."); continue; }
                    name = name.ToUpperInvariant();
                }
                if (parent is not null && list.ListName != "Category") { r.Refuse("Parent category: only categories have subcategories."); continue; }
                var parentParts = parent is null ? [] : CategoryTree.SplitPath(parent);
                if (!seen.Add($"{list.ListName}|{PathKey(parentParts)}|{K(name)}")) { r.Refuse("This is in the sheet more than once; only the first row is used."); continue; }

                if (list.ListName == "Category")
                {
                    if (parentParts.Length >= CategoryTree.MaxDepth) { r.Refuse("Parent category: subcategories are the last level. Use a family, or Family > Category."); continue; }
                    CategoryRow? parentRow = null;
                    if (parentParts.Length > 0)
                    {
                        parentRow = tree.FindPath(parentParts);
                        if (parentRow is null && !sheetPaths.Contains(PathKey(parentParts)))
                        { r.Refuse($"Parent category: there's no \"{parent}\". Write its full path (Family > Category), and add it in another row or under Admin, Lists."); continue; }
                    }
                    var match = parentParts.Length == 0
                        ? tree.Children(null).FirstOrDefault(c => K(c.Name) == K(name))
                        : parentRow is null ? null : tree.Children(parentRow.Id).FirstOrDefault(c => K(c.Name) == K(name));
                    if (match is not null)
                    {
                        if (active is null || active == match.IsActive) { r.Action = "same"; continue; }
                        r.Action = "update";
                        r.Notes.Add(active.Value ? "Show it again." : "Hide it (contracts that use it keep it).");
                        var (id, show, label) = (match.Id, active.Value, name);
                        r.Apply = async () =>
                        {
                            await x.C.ExecuteAsync("UPDATE contracts.Category SET IsActive = @show WHERE Id = @id", new { show, id }, x.Tx);
                            await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "category.changed", "Category", id, $"{x.Me.Name} {(show ? "showed" : "hid")} the category {label}{Via}.");
                        };
                        continue;
                    }
                    r.Action = "add";
                    var level = CategoryTree.LevelNames[parentParts.Length];
                    var pName = parentParts.Length == 0 ? null : string.Join(" > ", parentParts);
                    r.Notes.Add(pName is null ? "New family." : $"New {level} in {pName}.");
                    var (n, pParts, act) = (name, parentParts, active ?? true);
                    r.Order = parentParts.Length; // families first, then categories, then subcategories, so each finds its parent
                    r.Apply = async () =>
                    {
                        int? parentId = null;
                        foreach (var part in pParts)
                            parentId = await x.C.ExecuteScalarAsync<int>("SELECT Id FROM contracts.Category WHERE ParentKey = ISNULL(@parentId, 0) AND NameKey = UPPER(LTRIM(RTRIM(@part)))", new { parentId, part }, x.Tx);
                        var order = await x.C.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(SortOrder), -1) + 1 FROM contracts.Category WHERE ParentKey = ISNULL(@parentId, 0)", new { parentId }, x.Tx);
                        var id = await x.C.ExecuteScalarAsync<int>("INSERT INTO contracts.Category (ParentId, Name, SortOrder, IsActive) OUTPUT INSERTED.Id VALUES (@parentId, @n, @order, @act)", new { parentId, n, order, act }, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "category.added", "Category", id,
                            pName is null ? $"{x.Me.Name} added the family {n}{Via}." : $"{x.Me.Name} added the {level} {n} under {pName}{Via}.");
                    };
                }
                else
                {
                    var match = items.FirstOrDefault(i => i.ListName == list.ListName && K(i.Value) == K(name));
                    var lower = list.Label.ToLowerInvariant();
                    if (match is not null)
                    {
                        if (active is null || active == match.IsActive) { r.Action = "same"; continue; }
                        r.Action = "update";
                        r.Notes.Add(active.Value ? "Show it again." : "Hide it (contracts that use it keep it).");
                        var (id, show, label) = (match.Id, active.Value, name);
                        r.Apply = async () =>
                        {
                            await x.C.ExecuteAsync("UPDATE contracts.ListItem SET IsActive = @show WHERE Id = @id", new { show, id }, x.Tx);
                            await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "list.changed", "ListItem", id, $"{x.Me.Name} {(show ? "showed" : "hid")} the {lower} {label}{Via}.");
                        };
                        continue;
                    }
                    r.Action = "add";
                    r.Notes.Add($"New {lower}.");
                    var (ln, n, act) = (list.ListName, name, active ?? true);
                    r.Apply = async () =>
                    {
                        var order = await x.C.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(SortOrder), -1) + 1 FROM contracts.ListItem WHERE ListName = @ln", new { ln }, x.Tx);
                        var id = await x.C.ExecuteScalarAsync<int>("INSERT INTO contracts.ListItem (ListName, Value, SortOrder, IsActive) OUTPUT INSERTED.Id VALUES (@ln, @n, @order, @act)", new { ln, n, order, act }, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "list.added", "ListItem", id, $"{x.Me.Name} added the {lower} {n}{Via}.");
                    };
                }
            }
            return plan;
        });

    // ---------------------------------------------------------------- Suppliers

    private static Kind Suppliers() => new(
        "suppliers", "suppliers", "Suppliers",
        [
            new("Name", "Required. Rows are matched to suppliers by SAP vendor number, then by name. A supplier found by its number keeps its name here.", 34),
            new("ABN", "11 digits; spaces are fine.", 16),
            new("SAP vendor number", null, 18),
            new("Part of", "The group it's in, for example a franchise's head company: its name or SAP vendor number. Blank leaves it as it is.", 30),
            new("Notes", null, 40), new("Active", "Yes or No. Only admins can hide or show suppliers.", 10),
        ],
        ["Name"], ["Contracts"],
        me => true, me => me.IsAdmin, "Only admins can import suppliers. You can still export them.",
        async x =>
        {
            var rows = await x.C.QueryAsync<SupplierRow>($"""
                SELECT s.*, ps.Name AS ParentSupplierName,
                       (SELECT COUNT(*) FROM contracts.Contract k WHERE (k.SupplierId = s.Id OR k.SupplierId = s.ParentSupplierId) AND k.Stage <> N'Withdrawn' AND {ContractAccess.VisibleSql("k")}) AS ContractCount
                FROM contracts.Supplier s LEFT JOIN contracts.Supplier ps ON ps.Id = s.ParentSupplierId ORDER BY s.Name
                """, ContractAccess.Params(x.Me), x.Tx);
            var withCounts = x.Me is not null; // counts only include contracts this person can see
            return rows.Select(s => new object?[] { s.Name, s.Abn, s.VendorNumber, s.ParentSupplierName, s.Notes, s.IsActive, withCounts ? s.ContractCount : null });
        },
        async (x, rows) =>
        {
            static string K(string s) => s.Trim().ToUpperInvariant();
            var all = (await x.C.QueryAsync<SupplierRow>("SELECT *, 0 AS ContractCount FROM contracts.Supplier", transaction: x.Tx)).ToList();
            var byName = all.ToDictionary(s => K(s.Name));
            var byVendor = all.Where(s => s.VendorNumber is not null).ToLookup(s => K(s.VendorNumber!));
            var hasMembers = all.Where(s => s.ParentSupplierId is not null).Select(s => s.ParentSupplierId!.Value).ToHashSet();

            // First, which supplier each row is (D-116): by SAP vendor number, then by name; or a new one.
            var rowsInfo = rows.Select(row => (Row: row, Name: Clean(row.Text("Name")), Vendor: Clean(row.Text("SAP vendor number")), PartOf: Clean(row.Text("Part of")))).ToList();
            SupplierRow? Find(string? name, string? vendor, out string? clash)
            {
                clash = null;
                SupplierRow? byNumber = null;
                if (vendor is not null)
                {
                    var found = byVendor[K(vendor)].ToList();
                    if (found.Count > 1) { clash = $"SAP vendor number: {found.Count} suppliers have {vendor} ({string.Join(", ", found.Select(f => f.Name))}). Fix them on their pages first."; return null; }
                    byNumber = found.SingleOrDefault();
                }
                var named = name is null ? null : byName.GetValueOrDefault(K(name));
                if (byNumber is not null && named is not null && byNumber.Id != named.Id)
                { clash = $"SAP vendor number {vendor} is {byNumber.Name}'s, but the name is {named.Name}'s. Check which supplier this row is."; return null; }
                return byNumber ?? named;
            }
            // Names and numbers of the suppliers this sheet adds, so a row can be "Part of" a group added in another row.
            var adding = new Dictionary<string, string>(); // key (name or number) to the new supplier's name
            foreach (var (_, name, vendor, _) in rowsInfo)
            {
                if (name is null || Find(name, vendor, out var c) is not null || c is not null) continue;
                adding.TryAdd(K(name), name);
                if (vendor is not null) adding.TryAdd(K(vendor), name);
            }
            // Rows that put themselves in a group, by name and number: a group in this sheet can't itself be in one.
            var inGroupInSheet = rowsInfo.Where(i => i.PartOf is not null && i.Name is not null)
                .SelectMany(i => new[] { K(i.Name!), i.Vendor is null ? null : K(i.Vendor) }).OfType<string>().ToHashSet();
            var newRows = new Dictionary<string, PlannedRow>(); // a new supplier's row, by its name
            var waiting = new List<(PlannedRow Row, string Parent)>(); // rows in a group this sheet adds

            var seen = new HashSet<int>();
            var seenNew = new HashSet<string>();
            var plan = new List<PlannedRow>();
            foreach (var (row, sheetName, sheetVendor, partOf) in rowsInfo)
            {
                var r = new PlannedRow { Row = row.Number, Label = sheetName ?? sheetVendor ?? "(no name)" };
                plan.Add(r);
                if (sheetName is null) { r.Refuse("Name: add the supplier's name."); continue; }
                if (sheetName.Length > 200) { r.Refuse("Name: keep it under 200 characters."); continue; }
                var before = Find(sheetName, sheetVendor, out var clash);
                if (clash is not null) { r.Refuse(clash); continue; }
                if (before is not null ? !seen.Add(before.Id) : !seenNew.Add(K(sheetName))) { r.Refuse("This supplier is in the sheet more than once; only the first row is used."); continue; }
                // Found by its number under another name: it keeps the name it has here (Jeff, 26 Nov).
                var name = before?.Name ?? sheetName;
                r.Label = name;
                var problems = new List<string>();
                if (before is not null && K(before.Name) != K(sheetName)) r.Notes.Add($"Found by SAP vendor number {sheetVendor}; the sheet calls it \"{sheetName}\" and it keeps the name {before.Name}.");
                var abn = Clean(row.Text("ABN")) is { } a ? new string(a.Where(ch => !char.IsWhiteSpace(ch)).ToArray()) : before?.Abn;
                if (abn is not null && abn != before?.Abn && !Abn.IsValid(abn)) problems.Add($"ABN: \"{row.Text("ABN")}\" doesn't check out. It has 11 digits.");
                var vendor = sheetVendor ?? before?.VendorNumber;
                if (vendor is { Length: > 50 }) problems.Add("SAP vendor number: keep it under 50 characters.");
                var notes = Clean(row.Text("Notes")) ?? before?.Notes;
                if (notes is { Length: > 2000 }) problems.Add("Notes: keep them under 2,000 characters.");
                var active = row.YesNo("Active", out var ap) ?? before?.IsActive ?? true;
                if (ap is not null) problems.Add(ap);
                if (before is not null && active != before.IsActive && !x.Me.IsAdmin) problems.Add("Active: only admins can hide or show suppliers.");

                // Part of a group: an existing supplier or one this sheet adds; one level only.
                int? parentId = before?.ParentSupplierId;
                string? parentNew = null, parentName = null;
                if (partOf is not null)
                {
                    var parent = Find(partOf, partOf, out _) ?? byVendor[K(partOf)].FirstOrDefault();
                    if (parent is not null)
                    {
                        if (parent.Id == before?.Id) problems.Add("Part of: a supplier can't be in its own group.");
                        else if (parent.ParentSupplierId is not null) problems.Add($"Part of: {parent.Name} is itself in a group. Groups go one level, so use the group's head supplier.");
                        else { parentId = parent.Id; parentName = parent.Name; }
                    }
                    else if (adding.TryGetValue(K(partOf), out var newParent))
                    {
                        if (K(newParent) == K(name)) problems.Add("Part of: a supplier can't be in its own group.");
                        else if (inGroupInSheet.Contains(K(partOf)) || inGroupInSheet.Contains(K(newParent)))
                            problems.Add($"Part of: {newParent} is itself put in a group in this sheet. Groups go one level, so use the group's head supplier.");
                        else { parentNew = newParent; parentName = newParent; parentId = null; }
                    }
                    else problems.Add($"Part of: there's no supplier \"{partOf}\" (by name or SAP vendor number). Add it in another row or on the Suppliers page.");
                    if (before is not null && hasMembers.Contains(before.Id)) problems.Add($"Part of: {name} has its own group members, so it can't go into another group.");
                }
                if (problems.Count > 0) { r.Refuse(string.Join(" ", problems)); continue; }

                var args = new { name, abn, vendor, notes, active, me = x.Me.Id, id = before?.Id };
                async Task<int?> ParentIdAsync() => parentNew is null ? parentId
                    : await x.C.ExecuteScalarAsync<int>("SELECT Id FROM contracts.Supplier WHERE NameKey = UPPER(LTRIM(RTRIM(@parentNew)))", new { parentNew }, x.Tx);
                r.Order = partOf is null ? 0 : 1; // groups first, so their members find them
                if (parentNew is not null) waiting.Add((r, parentNew));
                if (before is null)
                {
                    newRows[K(name)] = r;
                    r.Action = "add";
                    r.Notes.Add(parentName is null ? "New supplier." : $"New supplier in the group {parentName}.");
                    r.Apply = async () =>
                    {
                        var parent = await ParentIdAsync();
                        var id = await x.C.ExecuteScalarAsync<int>("""
                            INSERT INTO contracts.Supplier (Name, Abn, VendorNumber, Notes, IsActive, ParentSupplierId, CreatedById, UpdatedById) OUTPUT INSERTED.Id
                            VALUES (@name, @abn, @vendor, @notes, @active, @parent, @me, @me)
                            """, new { args.name, args.abn, args.vendor, args.notes, args.active, parent, args.me }, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "supplier.added", "Supplier", id,
                            $"{x.Me.Name} added the supplier {name}{(parentName is null ? "" : $" in the group {parentName}")}{Via}.", args);
                    };
                    continue;
                }
                var changes = new List<string>();
                if (before.Abn != abn) changes.Add("ABN");
                if (before.VendorNumber != vendor) changes.Add("SAP vendor number");
                if (before.Notes != notes) changes.Add("notes");
                if (before.IsActive != active) changes.Add(active ? "show again" : "hide");
                var groupChanges = parentNew is not null || parentId != before.ParentSupplierId;
                if (groupChanges) changes.Add($"into the group {parentName}");
                if (changes.Count == 0) { r.Action = "same"; continue; }
                r.Action = "update";
                r.Notes.Add($"Change: {string.Join("; ", changes)}.");
                r.Apply = async () =>
                {
                    var parent = await ParentIdAsync();
                    await x.C.ExecuteAsync("""
                        UPDATE contracts.Supplier SET Abn = @abn, VendorNumber = @vendor, Notes = @notes, IsActive = @active, ParentSupplierId = @parent,
                            UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id
                        """, new { args.abn, args.vendor, args.notes, args.active, parent, args.me, args.id }, x.Tx);
                    await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "supplier.changed", "Supplier", before.Id, $"{x.Me.Name} changed the supplier {name}: {string.Join("; ", changes)}{Via}.", args);
                };
            }
            // A row in a group this sheet adds waits for that group's row: if it isn't added, neither is this.
            foreach (var (r, parent) in waiting)
                if (!newRows.TryGetValue(K(parent), out var parentRow) || parentRow.Action == "refused")
                    r.Refuse($"Part of: {parent} isn't being added (see its row), so this can't go in its group.");
            return plan;
        });

    // ---------------------------------------------------------------- Supplier contacts (D-103)

    private sealed class ContactRow
    {
        public int Id { get; set; }
        public int SupplierId { get; set; }
        public string Supplier { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Role { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
    }

    private const string SelectContacts = """
        SELECT k.Id, k.SupplierId, s.Name AS Supplier, k.Name, k.Role, k.Email, k.Phone FROM contracts.SupplierContact k
        JOIN contracts.Supplier s ON s.Id = k.SupplierId WHERE k.IsRemoved = 0
        """;

    /// <summary>
    /// One row per contact, several rows for several contacts at a supplier. A row matches the supplier's contact with the same email
    /// (or, without one, the same name) and updates it; otherwise it adds one. Contacts aren't removed by import (that's on the supplier's page).
    /// </summary>
    private static Kind SupplierContacts() => new(
        "contacts", "supplier contacts", "Contacts",
        [
            new("Supplier", "Required. The supplier's name as in JBS Contracts, or its SAP vendor number.", 32), new("Name", "Required. The contact's full name.", 26),
            new("Role", "For example Account manager", 24), new("Email", null, 32), new("Phone", null, 18),
        ],
        ["Supplier", "Name"], [],
        me => true, me => me.IsAdmin, "Only admins can import supplier contacts. You can still export them.",
        async x => (await x.C.QueryAsync<ContactRow>($"{SelectContacts} ORDER BY s.Name, k.Name", transaction: x.Tx))
            .Select(k => new object?[] { k.Supplier, k.Name, k.Role, k.Email, k.Phone }),
        async (x, rows) =>
        {
            static string K(string s) => s.Trim().ToUpperInvariant();
            var supplierRows = (await x.C.QueryAsync<(int Id, string Name, string? VendorNumber)>("SELECT Id, Name, VendorNumber FROM contracts.Supplier", transaction: x.Tx)).ToList();
            var suppliers = supplierRows.ToDictionary(s => K(s.Name), s => (s.Id, s.Name));
            // Or by SAP vendor number (D-116), where only one supplier has it.
            foreach (var g in supplierRows.Where(s => s.VendorNumber is not null).GroupBy(s => K(s.VendorNumber!)).Where(g => g.Count() == 1))
                suppliers.TryAdd(g.Key, (g.Single().Id, g.Single().Name));
            var contacts = (await x.C.QueryAsync<ContactRow>(SelectContacts, transaction: x.Tx)).ToList();
            var seen = new HashSet<string>();
            var plan = new List<PlannedRow>();
            foreach (var row in rows)
            {
                var supplierName = Clean(row.Text("Supplier"));
                var name = Clean(row.Text("Name"));
                var r = new PlannedRow { Row = row.Number, Label = $"{name ?? "(no name)"}, {supplierName ?? "(no supplier)"}" };
                plan.Add(r);
                if (supplierName is null) { r.Refuse("Supplier: add the supplier's name."); continue; }
                if (!suppliers.TryGetValue(K(supplierName), out var supplier))
                { r.Refuse($"Supplier: there's no supplier \"{supplierName}\" (by name or SAP vendor number). Add the supplier first (the suppliers sheet, or Add supplier)."); continue; }
                r.Label = $"{name ?? "(no name)"}, {supplier.Name}";
                var problems = new List<string>();
                if (name is null) problems.Add("Name: add the contact's name.");
                else if (name.Length > 200) problems.Add("Name: keep it under 200 characters.");
                var role = Clean(row.Text("Role"));
                if (role is { Length: > 200 }) problems.Add("Role: keep it under 200 characters.");
                var email = Clean(row.Text("Email"));
                if (email is not null && (email.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")))
                    problems.Add($"Email: \"{email}\" doesn't look like an email address.");
                var phone = Clean(row.Text("Phone"));
                if (phone is { Length: > 50 }) problems.Add("Phone: keep it under 50 characters.");
                if (problems.Count > 0) { r.Refuse(string.Join(" ", problems)); continue; }

                var key = $"{supplier.Id}|{(email is not null ? "E:" + K(email) : "N:" + K(name!))}";
                if (!seen.Add(key)) { r.Refuse("This contact is in the sheet more than once; only the first row is used."); continue; }
                var before = contacts.FirstOrDefault(k => k.SupplierId == supplier.Id && (email is not null
                    ? k.Email is not null && K(k.Email) == K(email)
                    : k.Email is null && K(k.Name) == K(name!)));
                // A blank cell leaves what's there.
                role ??= before?.Role;
                phone ??= before?.Phone;
                var args = new { supplierId = supplier.Id, id = before?.Id, name, role, email = email ?? before?.Email, phone, me = x.Me.Id };
                if (before is null)
                {
                    r.Action = "add";
                    r.Notes.Add("New contact.");
                    r.Apply = async () =>
                    {
                        await x.C.ExecuteAsync("""
                            INSERT INTO contracts.SupplierContact (SupplierId, Name, Role, Email, Phone, CreatedById) VALUES (@supplierId, @name, @role, @email, @phone, @me)
                            """, args, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "supplier.contact-added", "Supplier", supplier.Id,
                            $"{x.Me.Name} added {name} as a contact for {supplier.Name}{Via}.", args);
                    };
                    continue;
                }
                var changes = new List<string>();
                if (before.Name != name) changes.Add("name");
                if (before.Role != role) changes.Add("role");
                if (before.Phone != phone) changes.Add("phone");
                if (changes.Count == 0) { r.Action = "same"; continue; }
                r.Action = "update";
                r.Notes.Add($"Change: {string.Join(", ", changes)}.");
                r.Apply = async () =>
                {
                    await x.C.ExecuteAsync("UPDATE contracts.SupplierContact SET Name = @name, Role = @role, Phone = @phone, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @id", args, x.Tx);
                    await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "supplier.contact-changed", "Supplier", supplier.Id,
                        $"{x.Me.Name} changed {supplier.Name}'s contact {name}: {string.Join(", ", changes)}{Via}.", args);
                };
            }
            return plan;
        });

    // ---------------------------------------------------------------- Contracts

    private static readonly Dictionary<string, string> FieldLabels = new()
    {
        ["title"] = "Title", ["description"] = "Description", ["supplierId"] = "Supplier", ["categoryId"] = "Category", ["otherCategoryIds"] = "Other categories", ["agreementTypeId"] = "Agreement type",
        ["businessUnitIds"] = "Business unit", ["siteIds"] = "Site", ["entityIds"] = "Entity", ["functionIds"] = "Function", ["ownerId"] = "Owner", ["backupOwnerId"] = "Backup owner", ["currencyCode"] = "Currency",
        ["value"] = "Value", ["sapOutlineAgreement"] = "SAP outline agreement number", ["endDate"] = "End date", ["noticePeriodDays"] = "Notice period (days)",
        ["autoRenews"] = "Renews automatically", ["continuesUntilReplaced"] = "Continues until replaced", ["renewalTermMonths"] = "Renewal term (months)", ["reviewDate"] = "Review date",
    };

    /// <summary>The contract sheet's own headings (added fields whose label matches one get their key added, so no two columns share a heading).</summary>
    private static IEnumerable<string> ContractHeaders() => Kinds["contracts"].Columns.Select(c => c.Header).Concat(Kinds["contracts"].ExportOnly);

    private static Kind Contracts() => new(
        "contracts", "contracts", "Contracts",
        [
            new("Reference", "Leave blank for a new contract (it's given the next JBSAUS number). Fill in to update that contract; only drafts can be updated.", 16),
            new("Title", "Required for a new contract.", 34), new("Supplier", "The supplier's name. A new name adds the supplier.", 28),
            new("Category", "The main category (main spend): Family > Category, or Family > Category > Subcategory.", 30),
            new("Other categories", "Any others, separated by ;", 30), new("Agreement type", null, 20),
            new("Owner", "Windows logon or name of an active contract owner. Blank for a new contract: you.", 20), new("Backup owner", null, 20),
            new("Confidential", "Yes or No", 13), new("Business unit", "One or more. Separate several with ;", 18), new("Site", "One or more. Separate several with ;", 16),
            new("Entity", "The JBS company that signs it. Separate several with ;", 24), new("Function", "One or more. Separate several with ;", 18),
            new("Value", "Numbers only", 16), new("Currency", "Three letters, for example AUD", 10), new("SAP outline agreement number", null, 22),
            new("Start date", "Day/month/year", 13), new("Effective date", "Day/month/year", 13), new("End date", "Day/month/year. Blank for evergreen.", 13),
            new("Evergreen", "Yes or No", 11),
            new("Renews automatically", "Yes or No. Yes moves the end date on by the renewal term unless someone decides to let it end.", 14),
            new("Renewal term (months)", "Whole number, needed when it renews automatically", 14),
            new("Continues until replaced", "Yes or No. Yes: after its end date it carries on under the same terms until replaced, and isn't shown as overdue.", 14),
            new("Notice period (days)", "Whole number", 14), new("Review date", "Day/month/year", 13),
            new("Review months before end", "1, 3, 6 or 12: the review date is worked out from the end date and moves with it. Blank with a review date: that date.", 14),
            new("Needs Legal review", "Yes or No (Yes if blank for a new contract)", 15), new("Description", null, 40),
        ],
        [], ["Stage", "Notice deadline"],
        me => true, me => me.IsAdmin, "Only admins can import contracts. You can still export them.",
        async x =>
        {
            var rows = (await ContractEndpoints.ListVisibleAsync(x.C, x.Tx, x.Me)).Where(r => r.Stage != "Withdrawn").ToList();
            var people = (await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person", transaction: x.Tx)).ToDictionary(p => p.Id);
            var byHeader = Fields.ExcelFields(await Fields.LoadAsync(x.C, x.Tx), ContractHeaders()).ToDictionary(a => a.Header);
            var added = (x.ExtraColumns ?? []).Select(col => byHeader.GetValueOrDefault(col.Header)).ToList(); // null: that field changed meanwhile (blank cells)
            var values = await Fields.ReadAsync(x.C, x.Tx, rows.Select(r => r.Id), added.OfType<Fields.ExcelField>().Select(a => a.Field).ToList());
            static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
            static string? Several(string[] names) => names.Length == 0 ? null : string.Join("; ", names); // read back by ListCells
            return rows.Select(r => new object?[]
            {
                r.Reference, r.Title, r.SupplierName, r.CategoryDisplay, r.OtherCategories.Length == 0 ? null : string.Join("; ", r.OtherCategories), r.AgreementTypeName, people[r.OwnerId].WindowsAccount,
                r.BackupOwnerId is { } b ? people[b].WindowsAccount : null, r.IsConfidential, Several(r.BusinessUnitNames), Several(r.SiteNames), Several(r.EntityNames), Several(r.FunctionNames),
                r.Value, r.CurrencyCode, r.SapOutlineAgreement, D(r.StartDate), D(r.EffectiveDate), D(r.EndDate), r.IsEvergreen, r.AutoRenews, r.RenewalTermMonths, r.ContinuesUntilReplaced, r.NoticePeriodDays, D(r.ReviewDate), r.ReviewMonthsBeforeEnd, r.NeedsLegalReview,
                r.Description,
            }
            .Concat(added.Select(a => a is not null && values[r.Id].TryGetValue(a.Field.Key, out var v) ? Fields.ExcelValue(a.Field, v, id => people.GetValueOrDefault(id)?.WindowsAccount) : null))
            .Concat([ContractEndpoints.StageNames[r.Stage], D(r.NoticeDeadline)]).ToArray());
        },
        async (x, rows) =>
        {
            static string K(string s) => s.Trim().ToUpperInvariant();
            var suppliers = (await x.C.QueryAsync<SupplierRow>("SELECT *, 0 AS ContractCount FROM contracts.Supplier", transaction: x.Tx)).ToDictionary(s => K(s.Name));
            var cats = (await x.C.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category", transaction: x.Tx)).ToList();
            var items = (await x.C.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem", transaction: x.Tx)).ToList();
            var owners = (await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsContractOwner = 1", transaction: x.Tx)).ToList();
            var activePeople = (await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1", transaction: x.Tx)).ToList();
            var definitions = await Fields.LoadAsync(x.C, x.Tx);
            var added = Fields.ExcelFields(definitions, ContractHeaders());
            var supplierIds = suppliers.Values.ToDictionary(s => K(s.Name), s => s.Id);
            var newSuppliers = new Dictionary<string, string>();
            var seen = new HashSet<string>();
            var plan = new List<PlannedRow>();

            int? FindOwner(string text, List<string> problems, string header)
            {
                var byAccount = WindowsAccount.TryNormalise(text, x.Settings.DefaultDomain, out var acc) ? owners.FirstOrDefault(o => WindowsAccount.Key(o.WindowsAccount) == WindowsAccount.Key(acc)) : null;
                var byName = owners.Where(o => K(o.Name) == K(text)).ToList();
                var match = byAccount ?? (byName.Count == 1 ? byName[0] : null);
                if (match is null) problems.Add($"{header}: \"{text}\" isn't an active contract owner (use their Windows logon or full name).");
                return match?.Id;
            }
            int? FindItem(string list, string? text, string header, List<string> problems)
            {
                if (text is null) return null;
                var match = items.FirstOrDefault(i => i.ListName == list && K(i.Value) == K(text));
                if (match is null) problems.Add($"{header}: there's no {header.ToLowerInvariant()} called \"{text}\". Add it under Admin, Lists first.");
                return match?.Id;
            }
            // Several values in one cell (D-114). Null for a blank cell (the contract keeps what it has).
            int[]? FindItems(string list, string? text, string header, List<string> problems)
            {
                if (text is null) return null;
                var ids = new List<int>();
                foreach (var part in ListCells.Split(text, t => items.Any(i => i.ListName == list && K(i.Value) == K(t))))
                    if (FindItem(list, part, header, problems) is { } id) ids.Add(id);
                return [.. ids];
            }
            // A path, or a name found once below the families (D-115).
            var tree = new CategoryTree(cats);
            int? FindCategory(string? text, List<string> problems)
            {
                if (text is null) return null;
                var match = tree.Resolve(text, out var problem);
                if (problem is not null) problems.Add(problem);
                return match?.Id;
            }
            // Other categories: several, separated by semicolons. Null for a blank cell (the contract keeps what it has).
            int[]? FindOtherCategories(string? text, List<string> problems)
            {
                if (text is null) return null;
                var ids = new List<int>();
                foreach (var part in text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = tree.Resolve(part, out var problem);
                    if (problem is not null) problems.Add("Other categories: " + problem["Category: ".Length..]);
                    else if (!ids.Contains(match!.Id)) ids.Add(match.Id);
                }
                return [.. ids];
            }

            foreach (var row in rows)
            {
                var reference = Clean(row.Text("Reference"));
                var r = new PlannedRow { Row = row.Number, Label = reference ?? Clean(row.Text("Title")) ?? "(no title)" };
                plan.Add(r);
                if (reference is not null && !seen.Add(K(reference))) { r.Refuse("This reference is in the sheet more than once; only the first row is used."); continue; }

                ContractRow? before = null;
                string? onlyDrafts = null; // set when the contract has moved past Draft: an unchanged row is fine, a change is refused
                if (reference is not null)
                {
                    before = await ContractEndpoints.LoadByReferenceAsync(x.C, x.Tx, reference);
                    if (before is null || !ContractAccess.CanSee(x.Me, before.IsConfidential, before.OwnerId, before.BackupOwnerId))
                    { r.Refuse($"Reference: there's no contract {reference}. Leave Reference blank to add a new contract."); continue; }
                    if (!ContractAccess.CanEdit(x.Me, before))
                        onlyDrafts = $"{before.Reference} is in {ContractEndpoints.StageNames[before.Stage]}, so it can't be changed by import now.";
                    r.Label = $"{before.Reference} {before.Title}";
                }

                var problems = new List<string>();
                var title = Clean(row.Text("Title")) ?? before?.Title;
                if (title is null) problems.Add("Title: a new contract needs a title.");
                var supplierName = Clean(row.Text("Supplier"));
                int? supplierId = before?.SupplierId;
                string? addSupplier = null;
                if (supplierName is not null)
                {
                    if (suppliers.TryGetValue(K(supplierName), out var s)) supplierId = s.Id;
                    else if (supplierName.Length > 200) problems.Add("Supplier: keep the name under 200 characters.");
                    else { supplierId = null; addSupplier = newSuppliers.TryGetValue(K(supplierName), out var first) ? first : newSuppliers[K(supplierName)] = supplierName; }
                }
                var categoryId = FindCategory(Clean(row.Text("Category")), problems) ?? before?.CategoryId;
                var otherCategoryIds = FindOtherCategories(Clean(row.Text("Other categories")), problems) ?? before?.OtherCategoryIds;
                var agreementTypeId = FindItem("AgreementType", Clean(row.Text("Agreement type")), "Agreement type", problems) ?? before?.AgreementTypeId;
                var businessUnitIds = FindItems("BusinessUnit", Clean(row.Text("Business unit")), "Business unit", problems) ?? before?.BusinessUnitIds;
                var siteIds = FindItems("Site", Clean(row.Text("Site")), "Site", problems) ?? before?.SiteIds;
                var entityIds = FindItems("Entity", Clean(row.Text("Entity")), "Entity", problems) ?? before?.EntityIds;
                var functionIds = FindItems("Function", Clean(row.Text("Function")), "Function", problems) ?? before?.FunctionIds;
                var ownerId = Clean(row.Text("Owner")) is { } o ? FindOwner(o, problems, "Owner") : before?.OwnerId ?? x.Me.Id;
                var backupId = Clean(row.Text("Backup owner")) is { } bo ? FindOwner(bo, problems, "Backup owner") : before?.BackupOwnerId;
                string? p;
                var confidential = row.YesNo("Confidential", out p) ?? before?.IsConfidential ?? false; if (p is not null) problems.Add(p);
                var value = row.Money("Value", out p) ?? before?.Value; if (p is not null) problems.Add(p);
                var currency = Clean(row.Text("Currency"))?.ToUpperInvariant() ?? before?.CurrencyCode ?? "AUD";
                var sap = Clean(row.Text("SAP outline agreement number")) ?? before?.SapOutlineAgreement;
                static DateOnly? DO(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
                var start = row.Date("Start date", out p) ?? DO(before?.StartDate); if (p is not null) problems.Add(p);
                var effective = row.Date("Effective date", out p) ?? DO(before?.EffectiveDate); if (p is not null) problems.Add(p);
                var end = row.Date("End date", out p) ?? DO(before?.EndDate); if (p is not null) problems.Add(p);
                var evergreen = row.YesNo("Evergreen", out p) ?? before?.IsEvergreen ?? false; if (p is not null) problems.Add(p);
                if (evergreen && row.Date("End date", out _) is null) end = null; // ticking Evergreen clears an old end date
                var said = row.YesNo("Renews automatically", out p); if (p is not null) problems.Add(p);
                var autoRenews = said ?? (!evergreen && (before?.AutoRenews ?? false)); // Evergreen without a Yes here stops it renewing
                var continuesSaid = row.YesNo("Continues until replaced", out p); if (p is not null) problems.Add(p);
                var continues = continuesSaid ?? (!evergreen && !autoRenews && (before?.ContinuesUntilReplaced ?? false)); // Evergreen or renewing stops it
                var term = row.WholeNumber("Renewal term (months)", out p) ?? before?.RenewalTermMonths; if (p is not null) problems.Add(p);
                var notice = row.WholeNumber("Notice period (days)", out p) ?? before?.NoticePeriodDays; if (p is not null) problems.Add(p);
                var reviewGiven = row.Date("Review date", out p); if (p is not null) problems.Add(p);
                var review = reviewGiven ?? DO(before?.ReviewDate);
                var reviewMonths = row.WholeNumber("Review months before end", out p); if (p is not null) problems.Add(p);
                reviewMonths ??= reviewGiven is null ? before?.ReviewMonthsBeforeEnd : null; // a review date given replaces a period
                var legal = row.YesNo("Needs Legal review", out p) ?? before?.NeedsLegalReview ?? true; if (p is not null) problems.Add(p);
                var description = Clean(row.Text("Description")) ?? before?.Description;
                // Added fields: a blank cell leaves the value as it is.
                var fieldValues = new Dictionary<string, System.Text.Json.JsonElement>();
                foreach (var a in added.Where(a => row.Has(a.Header)))
                {
                    var (json, problem) = Fields.FromExcel(a.Field, row, a.Header, activePeople, supplierIds, x.Settings.DefaultDomain);
                    if (problem is not null) problems.Add(problem);
                    else if (json is { } j) fieldValues[a.Field.Key] = j;
                }
                if (problems.Count > 0) { r.Refuse(string.Join(" ", problems)); continue; }

                var body = new SaveContract(title, description, supplierId, categoryId, agreementTypeId, businessUnitIds, siteIds, ownerId, backupId, confidential,
                    value, currency, sap, start, effective, end, evergreen, notice, review, legal, null, fieldValues, entityIds, functionIds, autoRenews, term, reviewMonths, otherCategoryIds, continues);
                var errors = await ContractEndpoints.CheckAsync(x.C, x.Tx, body, before);
                var (parsed, fieldErrors) = await Fields.ParseAsync(x.C, x.Tx, fieldValues, definitions);
                var labels = added.ToDictionary(a => $"field.{a.Field.Key}", a => a.Header);
                var allErrors = (errors ?? []).Concat(fieldErrors).ToList();
                if (allErrors.Count > 0 && onlyDrafts is not null) { r.Refuse(onlyDrafts); continue; }
                if (allErrors.Count > 0)
                { r.Refuse(string.Join(" ", allErrors.Select(e => $"{FieldLabels.GetValueOrDefault(e.Key) ?? labels.GetValueOrDefault(e.Key, e.Key)}: {e.Value}"))); continue; }

                if (before is null)
                {
                    r.Action = "add";
                    r.Label = title!;
                    r.Notes.Add("New draft." + (addSupplier is not null ? $" Also adds the supplier {addSupplier}." : ""));
                }
                else
                {
                    var (changed, _) = await ContractEndpoints.PreviewChangesAsync(x.C, x.Tx, before, body);
                    changed.AddRange(await Fields.ChangedAsync(x.C, x.Tx, before.Id, parsed, definitions));
                    if (addSupplier is not null && !changed.Contains("supplier")) changed.Add("supplier");
                    if (changed.Count == 0) { r.Action = "same"; continue; }
                    if (onlyDrafts is not null) { r.Refuse($"{onlyDrafts} This row would change: {string.Join(", ", changed)}."); continue; }
                    r.Action = "update";
                    r.Notes.Add($"Change: {string.Join(", ", changed)}." + (addSupplier is not null ? $" Also adds the supplier {addSupplier}." : ""));
                }
                var (b, bodyCopy, supplierToAdd) = (before, body, addSupplier);
                r.Apply = async () =>
                {
                    var final = bodyCopy;
                    if (supplierToAdd is not null)
                    {
                        var existingId = await x.C.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Supplier WHERE NameKey = UPPER(LTRIM(RTRIM(@supplierToAdd)))", new { supplierToAdd }, x.Tx);
                        if (existingId is null)
                        {
                            existingId = await x.C.ExecuteScalarAsync<int>("INSERT INTO contracts.Supplier (Name, CreatedById, UpdatedById) OUTPUT INSERTED.Id VALUES (@supplierToAdd, @me, @me)", new { supplierToAdd, me = x.Me.Id }, x.Tx);
                            await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "supplier.added", "Supplier", existingId, $"{x.Me.Name} added the supplier {supplierToAdd}{Via}.");
                        }
                        final = final with { SupplierId = existingId };
                    }
                    if (b is null)
                    {
                        var (_, _, errs) = await ContractEndpoints.CreateDraftAsync(x.C, x.Tx, x.Audit, x.Me, final, Via);
                        if (errs is not null) throw new InvalidOperationException($"Row {r.Row} failed on import after passing the preview: {string.Join("; ", errs.Values)}");
                    }
                    else
                    {
                        var current = await ContractEndpoints.LoadForUpdateAsync(x.C, x.Tx, b.Id);
                        var (errs, _) = await ContractEndpoints.UpdateDraftAsync(x.C, x.Tx, x.Audit, x.Me, current!, final, Via);
                        if (errs is not null) throw new InvalidOperationException($"Row {r.Row} failed on import after passing the preview: {string.Join("; ", errs.Values)}");
                    }
                };
            }
            return plan;
        },
        async (c, tx) => Fields.ExcelFields(await Fields.LoadAsync(c, tx), ContractHeaders())
            .Select(a => new Excel.Column(a.Header, Fields.ExcelNote(a.Field), a.Field.Kind == "LongText" ? 40 : 20)).ToList());
}
