using System.Globalization;
using System.Net.Mail;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Excel import and export for People, Lists, Suppliers and Contracts (decision D-44).
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

    private sealed record Ctx(SqlConnection C, SqlTransaction Tx, Person Me, AuditLog Audit, AppSettings Settings);

    private sealed record Kind(
        string Name, string Label, string Sheet, Excel.Column[] Columns, string[] Required, string[] ExportOnly,
        Func<Person, bool> CanExport, Func<Person, bool> CanImport, string Refusal,
        Func<Ctx, Task<IEnumerable<object?[]>>> Export, Func<Ctx, List<Excel.Row>, Task<List<PlannedRow>>> Plan);

    private const string Via = " (from Excel)";

    private static readonly Dictionary<string, Kind> Kinds = new[] { People(), Lists(), Suppliers(), Contracts() }.ToDictionary(k => k.Name);

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
            var rows = await k.Export(new Ctx(c, tx, me!, audit, settings));
            await tx.CommitAsync();
            var date = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Brisbane).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var bytes = Excel.Write(k.Sheet, k.Columns, rows);
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
        var known = k.Columns.Select(x => x.Header).Concat(k.ExportOnly).ToList();
        var (rows, ignored, problem) = Excel.Read(buffer, known, k.Required);
        if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });

        await using var c = await db.OpenAsync();
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
    ];

    private static Kind People() => new(
        "people", "people", "People",
        [
            new("Windows logon", "Required. The username (for example jsmith); the domain is added for you. Or type it in full, for example AMH\\jsmith.", 24),
            new("Name", "Required for someone new.", 26), new("Email", "Required for someone new. Reminders and approvals go here.", 32),
            new("Contract owner", "Yes or No", 15), new("Legal", "Yes or No", 10), new("Approver", "Yes or No", 11),
            new("Group Head", "Yes or No. Only one person can be Group Head; it implies Approver.", 12), new("Admin", "Yes or No", 10),
            new("Auditor", "Yes or No", 10), new("Active", "Yes or No. No switches off their access (nothing is deleted).", 10),
        ],
        ["Windows logon"], ["Last seen"],
        me => me.IsAdmin, me => me.IsAdmin, "Only admins can import or export people.",
        async x =>
        {
            var people = await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person ORDER BY Name", transaction: x.Tx);
            return people.Select(p => new object?[] { p.WindowsAccount, p.Name, p.Email, p.IsContractOwner, p.IsLegal, p.IsApprover, p.IsGroupHead, p.IsAdmin, p.IsAuditor, p.IsActive });
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
                    var args = new { acc, n, e, co = ro["Contract owner"], le = ro["Legal"], ap = ro["Approver"], gh = ro["Group Head"], ad = ro["Admin"], au = ro["Auditor"], act, me = x.Me.Id, id = b?.Id };
                    if (b is null)
                    {
                        var id = await x.C.ExecuteScalarAsync<int>("""
                            INSERT INTO contracts.Person (WindowsAccount, Name, Email, IsContractOwner, IsLegal, IsApprover, IsGroupHead, IsAdmin, IsAuditor, IsActive, CreatedById, UpdatedById)
                            OUTPUT INSERTED.Id VALUES (@acc, @n, @e, @co, @le, @ap, @gh, @ad, @au, @act, @me, @me)
                            """, args, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "person.added", "Person", id, $"{x.Me.Name} added {n} ({acc}) as {roleText}{Via}.", args);
                        await PeopleEndpoints.QueueWelcomeAsync(x.C, x.Tx, id, n, e, acc, roleText, x.Me.Name, x.Settings);
                    }
                    else
                    {
                        await x.C.ExecuteAsync("""
                            UPDATE contracts.Person SET Name = @n, Email = @e, IsContractOwner = @co, IsLegal = @le, IsApprover = @ap, IsGroupHead = @gh, IsAdmin = @ad,
                                IsAuditor = @au, IsActive = @act, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id
                            """, args, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "person.changed", "Person", b.Id, $"{x.Me.Name} changed {n}: {string.Join("; ", r.Notes).Replace("Change: ", "")}".TrimEnd('.') + $"{Via}.", args);
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
        ["site"] = ("Site", "Site"), ["currency"] = ("Currency", "Currency"),
    };

    private static Kind Lists() => new(
        "lists", "lists", "Lists",
        [
            new("List", "Required: Category, Agreement type, Business unit, Site or Currency.", 18),
            new("Name", "Required. For a currency, the three-letter code (AUD).", 30),
            new("Parent category", "Only for subcategories: the category it sits under.", 24),
            new("Active", "Yes or No. No hides it from the choices; contracts that use it keep it.", 10),
        ],
        ["List", "Name"], [],
        me => me.IsAdmin, me => me.IsAdmin, "Only admins can import or export the lists.",
        async x =>
        {
            var cats = (await x.C.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category", transaction: x.Tx)).ToList();
            var items = await x.C.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem ORDER BY ListName, SortOrder, Value", transaction: x.Tx);
            var labels = ListNames.Values.ToDictionary(v => v.ListName, v => v.Label);
            var catRows = cats.Where(c => c.ParentId is null).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .SelectMany(top => new[] { new object?[] { "Category", top.Name, null, top.IsActive } }
                    .Concat(cats.Where(s => s.ParentId == top.Id).OrderBy(s => s.SortOrder).ThenBy(s => s.Name).Select(s => new object?[] { "Category", s.Name, top.Name, s.IsActive })));
            return catRows.Concat(items.Select(i => new object?[] { labels[i.ListName], i.Value, null, i.IsActive }));
        },
        async (x, rows) =>
        {
            var cats = (await x.C.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category", transaction: x.Tx)).ToList();
            var items = (await x.C.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem", transaction: x.Tx)).ToList();
            static string K(string s) => s.Trim().ToUpperInvariant();
            // New top-level categories anywhere in the sheet, so a subcategory can come before its parent.
            var newTops = rows.Where(rw => Excel.Normalise(rw.Text("List") ?? "") == "category" && Clean(rw.Text("Parent category")) is null && Clean(rw.Text("Name")) is not null)
                .Select(rw => K(rw.Text("Name")!)).Where(n => !cats.Any(c => c.ParentId is null && K(c.Name) == n)).ToHashSet();
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
                if (!ListNames.TryGetValue(Excel.Normalise(listText), out var list)) { r.Refuse($"List: \"{listText}\" isn't one of Category, Agreement type, Business unit, Site or Currency."); continue; }
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
                if (!seen.Add($"{list.ListName}|{K(parent ?? "")}|{K(name)}")) { r.Refuse("This is in the sheet more than once; only the first row is used."); continue; }

                if (list.ListName == "Category")
                {
                    CategoryRow? parentRow = null;
                    if (parent is not null)
                    {
                        parentRow = cats.FirstOrDefault(c => c.ParentId is null && K(c.Name) == K(parent));
                        if (parentRow is null && !newTops.Contains(K(parent))) { r.Refuse($"Parent category: there's no category called \"{parent}\". Add it in an earlier row or under Admin, Lists."); continue; }
                    }
                    var match = parent is null
                        ? cats.FirstOrDefault(c => c.ParentId is null && K(c.Name) == K(name))
                        : parentRow is null ? null : cats.FirstOrDefault(c => c.ParentId == parentRow.Id && K(c.Name) == K(name));
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
                    r.Notes.Add(parent is null ? "New category." : $"New subcategory under {parent}.");
                    var (n, pName, act) = (name, parent, active ?? true);
                    r.Order = parent is null ? 0 : 1; // top-level categories first, so new subcategories find them
                    r.Apply = async () =>
                    {
                        int? parentId = pName is null ? null : await x.C.ExecuteScalarAsync<int>("SELECT Id FROM contracts.Category WHERE ParentId IS NULL AND NameKey = UPPER(LTRIM(RTRIM(@pName)))", new { pName }, x.Tx);
                        var order = await x.C.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(SortOrder), -1) + 1 FROM contracts.Category WHERE ParentKey = ISNULL(@parentId, 0)", new { parentId }, x.Tx);
                        var id = await x.C.ExecuteScalarAsync<int>("INSERT INTO contracts.Category (ParentId, Name, SortOrder, IsActive) OUTPUT INSERTED.Id VALUES (@parentId, @n, @order, @act)", new { parentId, n, order, act }, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "category.added", "Category", id,
                            pName is null ? $"{x.Me.Name} added the category {n}{Via}." : $"{x.Me.Name} added the subcategory {n} under {pName}{Via}.");
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
            new("Name", "Required. Rows are matched to existing suppliers by name.", 34), new("ABN", "11 digits; spaces are fine.", 16),
            new("SAP vendor number", null, 18), new("Notes", null, 40), new("Active", "Yes or No. Only admins can hide or show suppliers.", 10),
        ],
        ["Name"], ["Contracts"],
        me => true, me => me.IsContractOwner || me.IsAdmin, "Only contract owners and admins can import suppliers.",
        async x =>
        {
            var rows = await x.C.QueryAsync<SupplierRow>($"""
                SELECT s.*, (SELECT COUNT(*) FROM contracts.Contract k WHERE k.SupplierId = s.Id AND k.Stage <> N'Withdrawn' AND {ContractAccess.VisibleSql("k")}) AS ContractCount
                FROM contracts.Supplier s ORDER BY s.Name
                """, ContractAccess.Params(x.Me), x.Tx);
            var withCounts = x.Me is not null; // counts only include contracts this person can see
            return rows.Select(s => new object?[] { s.Name, s.Abn, s.VendorNumber, s.Notes, s.IsActive, withCounts ? s.ContractCount : null });
        },
        async (x, rows) =>
        {
            var existing = (await x.C.QueryAsync<SupplierRow>("SELECT *, 0 AS ContractCount FROM contracts.Supplier", transaction: x.Tx)).ToDictionary(s => s.Name.Trim().ToUpperInvariant());
            var seen = new HashSet<string>();
            var plan = new List<PlannedRow>();
            foreach (var row in rows)
            {
                var name = Clean(row.Text("Name"));
                var r = new PlannedRow { Row = row.Number, Label = name ?? "(no name)" };
                plan.Add(r);
                if (name is null) { r.Refuse("Name: add the supplier's name."); continue; }
                if (name.Length > 200) { r.Refuse("Name: keep it under 200 characters."); continue; }
                if (!seen.Add(name.ToUpperInvariant())) { r.Refuse("This supplier is in the sheet more than once; only the first row is used."); continue; }
                existing.TryGetValue(name.ToUpperInvariant(), out var before);
                var problems = new List<string>();
                var abn = Clean(row.Text("ABN")) is { } a ? new string(a.Where(ch => !char.IsWhiteSpace(ch)).ToArray()) : before?.Abn;
                if (abn is not null && abn != before?.Abn && !Abn.IsValid(abn)) problems.Add($"ABN: \"{row.Text("ABN")}\" doesn't check out. It has 11 digits.");
                var vendor = Clean(row.Text("SAP vendor number")) ?? before?.VendorNumber;
                if (vendor is { Length: > 50 }) problems.Add("SAP vendor number: keep it under 50 characters.");
                var notes = Clean(row.Text("Notes")) ?? before?.Notes;
                if (notes is { Length: > 2000 }) problems.Add("Notes: keep them under 2,000 characters.");
                var active = row.YesNo("Active", out var ap) ?? before?.IsActive ?? true;
                if (ap is not null) problems.Add(ap);
                if (before is not null && active != before.IsActive && !x.Me.IsAdmin) problems.Add("Active: only admins can hide or show suppliers.");
                if (problems.Count > 0) { r.Refuse(string.Join(" ", problems)); continue; }
                var args = new { name, abn, vendor, notes, active, me = x.Me.Id, id = before?.Id };
                if (before is null)
                {
                    r.Action = "add";
                    r.Notes.Add("New supplier.");
                    r.Apply = async () =>
                    {
                        var id = await x.C.ExecuteScalarAsync<int>("INSERT INTO contracts.Supplier (Name, Abn, VendorNumber, Notes, IsActive, CreatedById, UpdatedById) OUTPUT INSERTED.Id VALUES (@name, @abn, @vendor, @notes, @active, @me, @me)", args, x.Tx);
                        await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "supplier.added", "Supplier", id, $"{x.Me.Name} added the supplier {name}{Via}.", args);
                    };
                    continue;
                }
                var changes = new List<string>();
                if (before.Name != name) changes.Add($"name to {name}");
                if (before.Abn != abn) changes.Add("ABN");
                if (before.VendorNumber != vendor) changes.Add("SAP vendor number");
                if (before.Notes != notes) changes.Add("notes");
                if (before.IsActive != active) changes.Add(active ? "show again" : "hide");
                if (changes.Count == 0) { r.Action = "same"; continue; }
                r.Action = "update";
                r.Notes.Add($"Change: {string.Join("; ", changes)}.");
                r.Apply = async () =>
                {
                    await x.C.ExecuteAsync("UPDATE contracts.Supplier SET Name = @name, Abn = @abn, VendorNumber = @vendor, Notes = @notes, IsActive = @active, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id", args, x.Tx);
                    await x.Audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "supplier.changed", "Supplier", before.Id, $"{x.Me.Name} changed the supplier {name}: {string.Join("; ", changes)}{Via}.", args);
                };
            }
            return plan;
        });

    // ---------------------------------------------------------------- Contracts

    private static readonly Dictionary<string, string> FieldLabels = new()
    {
        ["title"] = "Title", ["description"] = "Description", ["supplierId"] = "Supplier", ["categoryId"] = "Category", ["agreementTypeId"] = "Agreement type",
        ["businessUnitId"] = "Business unit", ["siteId"] = "Site", ["ownerId"] = "Owner", ["backupOwnerId"] = "Backup owner", ["currencyCode"] = "Currency",
        ["value"] = "Value", ["sapOutlineAgreement"] = "SAP outline agreement number", ["endDate"] = "End date", ["noticePeriodDays"] = "Notice period (days)",
    };

    private static Kind Contracts() => new(
        "contracts", "contracts", "Contracts",
        [
            new("Reference", "Leave blank for a new contract (a JBSAUS reference is given). Fill in to update that contract; only drafts can be updated.", 16),
            new("Title", "Required for a new contract.", 34), new("Supplier", "The supplier's name. A new name adds the supplier.", 28),
            new("Category", "A category, or Category > Subcategory.", 26), new("Agreement type", null, 20),
            new("Owner", "Windows logon or name of an active contract owner. Blank for a new contract: you.", 20), new("Backup owner", null, 20),
            new("Confidential", "Yes or No", 13), new("Business unit", null, 18), new("Site", null, 16),
            new("Value", "Numbers only", 16), new("Currency", "Three letters, for example AUD", 10), new("SAP outline agreement number", null, 22),
            new("Start date", "Day/month/year", 13), new("Effective date", "Day/month/year", 13), new("End date", "Day/month/year. Blank for evergreen.", 13),
            new("Evergreen", "Yes or No", 11), new("Notice period (days)", "Whole number", 14), new("Review date", "Day/month/year", 13),
            new("Needs Legal review", "Yes or No (Yes if blank for a new contract)", 15), new("Description", null, 40),
        ],
        [], ["Stage", "Notice deadline", "Registered date"],
        me => true, me => me.IsContractOwner, "Only contract owners can import contracts.",
        async x =>
        {
            var rows = await ContractEndpoints.ListVisibleAsync(x.C, x.Tx, x.Me);
            var people = (await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person", transaction: x.Tx)).ToDictionary(p => p.Id);
            static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
            return rows.Where(r => r.Stage != "Withdrawn").Select(r => new object?[]
            {
                r.Reference, r.Title, r.SupplierName, r.CategoryDisplay, r.AgreementTypeName, people[r.OwnerId].WindowsAccount,
                r.BackupOwnerId is { } b ? people[b].WindowsAccount : null, r.IsConfidential, r.BusinessUnitName, r.SiteName, r.Value, r.CurrencyCode,
                r.SapOutlineAgreement, D(r.StartDate), D(r.EffectiveDate), D(r.EndDate), r.IsEvergreen, r.NoticePeriodDays, D(r.ReviewDate), r.NeedsLegalReview,
                r.Description, ContractEndpoints.StageNames[r.Stage], D(r.NoticeDeadline),
            });
        },
        async (x, rows) =>
        {
            static string K(string s) => s.Trim().ToUpperInvariant();
            var suppliers = (await x.C.QueryAsync<SupplierRow>("SELECT *, 0 AS ContractCount FROM contracts.Supplier", transaction: x.Tx)).ToDictionary(s => K(s.Name));
            var cats = (await x.C.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category", transaction: x.Tx)).ToList();
            var items = (await x.C.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem", transaction: x.Tx)).ToList();
            var owners = (await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsContractOwner = 1", transaction: x.Tx)).ToList();
            var newSuppliers = new Dictionary<string, string>();
            var seen = new HashSet<string>();
            var plan = new List<PlannedRow>();

            int? FindOwner(string text, List<string> problems, string header)
            {
                var byAccount = WindowsAccount.TryNormalise(text, x.Settings.DefaultDomain, out var acc) ? owners.FirstOrDefault(o => WindowsAccount.Key(o.WindowsAccount) == WindowsAccount.Key(acc)) : null;
                var match = byAccount ?? owners.SingleOrDefault(o => K(o.Name) == K(text));
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
            int? FindCategory(string? text, List<string> problems)
            {
                if (text is null) return null;
                var parts = text.Split(['>', '›'], 2, StringSplitOptions.TrimEntries);
                CategoryRow? match;
                if (parts.Length == 2)
                {
                    var top = cats.FirstOrDefault(c => c.ParentId is null && K(c.Name) == K(parts[0]));
                    match = top is null ? null : cats.FirstOrDefault(c => c.ParentId == top.Id && K(c.Name) == K(parts[1]));
                }
                else
                {
                    match = cats.FirstOrDefault(c => c.ParentId is null && K(c.Name) == K(text));
                    if (match is null)
                    {
                        var subs = cats.Where(c => c.ParentId is not null && K(c.Name) == K(text)).ToList();
                        if (subs.Count > 1) { problems.Add($"Category: \"{text}\" is a subcategory under more than one category. Write it as Category > Subcategory."); return null; }
                        match = subs.SingleOrDefault();
                    }
                }
                if (match is null) problems.Add($"Category: there's no category called \"{text}\". Add it under Admin, Lists first.");
                return match?.Id;
            }

            foreach (var row in rows)
            {
                var reference = Clean(row.Text("Reference"));
                var r = new PlannedRow { Row = row.Number, Label = reference ?? Clean(row.Text("Title")) ?? "(no title)" };
                plan.Add(r);
                if (reference is not null && !seen.Add(K(reference))) { r.Refuse("This reference is in the sheet more than once; only the first row is used."); continue; }

                ContractRow? before = null;
                if (reference is not null)
                {
                    before = await ContractEndpoints.LoadByReferenceAsync(x.C, x.Tx, reference);
                    if (before is null || !ContractAccess.CanSee(x.Me, before.IsConfidential, before.OwnerId, before.BackupOwnerId))
                    { r.Refuse($"Reference: there's no contract {reference}. Leave Reference blank to add a new contract."); continue; }
                    if (!ContractAccess.CanEdit(x.Me, before.Stage, before.IsLocked))
                    { r.Refuse($"{reference} is in {ContractEndpoints.StageNames[before.Stage]}; only drafts can be changed by import."); continue; }
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
                var agreementTypeId = FindItem("AgreementType", Clean(row.Text("Agreement type")), "Agreement type", problems) ?? before?.AgreementTypeId;
                var businessUnitId = FindItem("BusinessUnit", Clean(row.Text("Business unit")), "Business unit", problems) ?? before?.BusinessUnitId;
                var siteId = FindItem("Site", Clean(row.Text("Site")), "Site", problems) ?? before?.SiteId;
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
                var notice = row.WholeNumber("Notice period (days)", out p) ?? before?.NoticePeriodDays; if (p is not null) problems.Add(p);
                var review = row.Date("Review date", out p) ?? DO(before?.ReviewDate); if (p is not null) problems.Add(p);
                var legal = row.YesNo("Needs Legal review", out p) ?? before?.NeedsLegalReview ?? true; if (p is not null) problems.Add(p);
                var description = Clean(row.Text("Description")) ?? before?.Description;
                if (problems.Count > 0) { r.Refuse(string.Join(" ", problems)); continue; }

                var body = new SaveContract(title, description, supplierId, categoryId, agreementTypeId, businessUnitId, siteId, ownerId, backupId, confidential,
                    value, currency, sap, start, effective, end, evergreen, notice, review, legal, null);
                var errors = await ContractEndpoints.CheckAsync(x.C, x.Tx, body, before);
                if (errors is not null) { r.Refuse(string.Join(" ", errors.Select(e => $"{FieldLabels.GetValueOrDefault(e.Key, e.Key)}: {e.Value}"))); continue; }

                if (before is null)
                {
                    r.Action = "add";
                    r.Label = title!;
                    r.Notes.Add("New draft." + (addSupplier is not null ? $" Also adds the supplier {addSupplier}." : ""));
                }
                else
                {
                    var (changed, _) = await ContractEndpoints.PreviewChangesAsync(x.C, x.Tx, before, body);
                    if (addSupplier is not null && !changed.Contains("supplier")) changed.Add("supplier");
                    if (changed.Count == 0) { r.Action = "same"; continue; }
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
        });
}
