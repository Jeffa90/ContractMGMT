using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Importing old contracts (slice 9, D-91). Admins only. Step 1: a spreadsheet (Excel or CSV, for example a Medius export): match its columns
/// to JBS Contracts fields once (remembered), see a dry run row by row, then import. Current contracts load as Effective and locked with
/// their original dates; expired ones load as Ended. Unknown suppliers are added; unknown owners or categories are flagged. Each contract
/// keeps its old reference, so a second run skips it. Step 2: their files from ZIPs, matched to contracts by the old (or JBSAUS) reference
/// in each file's name or folder. Everything is audited.
/// </summary>
public static class LegacyImportEndpoints
{
    public sealed record Target(string Key, string Label, string Group, bool Required, string? Help, string[] Aliases);

    private static readonly Target[] BuiltIn =
    [
        new("legacyReference", "Old reference", "Needed", true, "The contract's number in the old system. Used to match its files, and so a second run skips it.",
            ["old reference", "agreement number", "agreement no", "agreement id", "contract number", "contract no", "contract id", "reference", "ref", "id", "medius id"]),
        new("title", "Title", "Needed", true, null, ["title", "contract title", "contract name", "agreement name", "name", "subject"]),
        new("supplier", "Supplier", "Needed", true, "A name that isn't in JBS Contracts yet adds the supplier.", ["supplier", "supplier name", "vendor", "vendor name", "counterparty", "party"]),
        new("category", "Category", "Needed", true, "A category, or Category > Subcategory. Blank uses the default you choose below.", ["category", "contract category", "spend category"]),
        new("owner", "Owner", "Needed", true, "Windows logon or full name of a contract owner. Blank uses the default you choose below.", ["owner", "contract owner", "responsible", "contract manager", "manager"]),
        new("effectiveDate", "Effective date", "Dates", false, "Blank uses the start date, then the registered date.", ["effective date", "effective from"]),
        new("startDate", "Start date", "Dates", false, null, ["start date", "start", "commencement date", "commencement", "valid from"]),
        new("endDate", "End date", "Dates", false, "Blank needs Evergreen to be Yes.", ["end date", "end", "expiry date", "expiry", "expires", "valid to", "termination date"]),
        new("evergreen", "Evergreen", "Dates", false, "Yes or No: it carries on until someone ends it.", ["evergreen", "auto renew", "auto renewal", "rolling", "perpetual"]),
        new("noticePeriodDays", "Notice period (days)", "Dates", false, "Without it there are no notice-deadline reminders.", ["notice period", "notice period days", "notice days", "notice"]),
        new("reviewDate", "Review date", "Dates", false, null, ["review date", "next review"]),
        new("registeredDate", "Registered date", "Dates", false, "When it was approved or registered. Blank uses the effective date.", ["registered date", "registered", "approved date", "approval date", "signed date", "date signed"]),
        new("status", "Status", "Other", false, "Active, current or live loads it as Effective; expired, ended, terminated or cancelled as Ended. Blank: worked out from the end date.",
            ["status", "contract status", "state"]),
        new("agreementType", "Agreement type", "Other", false, null, ["agreement type", "contract type", "type"]),
        new("businessUnit", "Business unit", "Other", false, null, ["business unit", "bu", "division"]),
        new("site", "Site", "Other", false, null, ["site", "location", "plant"]),
        new("backupOwner", "Backup owner", "Other", false, null, ["backup owner", "backup", "deputy"]),
        new("value", "Value", "Other", false, null, ["value", "contract value", "total value", "amount", "estimated value"]),
        new("currency", "Currency", "Other", false, "Three letters, for example AUD. Blank is AUD.", ["currency", "currency code"]),
        new("sapOutlineAgreement", "SAP outline agreement number", "Other", false, null, ["sap outline agreement number", "sap outline agreement", "outline agreement", "sap number", "sap"]),
        new("confidential", "Confidential", "Other", false, "Yes or No", ["confidential"]),
        new("description", "Description", "Other", false, null, ["description", "notes", "comments", "summary", "scope"]),
    ];

    private static readonly string[] EndedWords = ["expired", "ended", "terminated", "cancelled", "canceled", "inactive", "closed", "archived", "lapsed"];
    private static readonly string[] CurrentWords = ["active", "current", "live", "effective", "in force", "valid", "open", "renewed"];

    private static async Task<List<Target>> TargetsAsync(SqlConnection c, SqlTransaction? tx) =>
        [.. BuiltIn, .. (await Fields.LoadAsync(c, tx)).Where(f => !f.IsBuiltIn && !f.IsHidden)
            .Select(f => new Target($"field.{f.Key}", f.Label, "Added fields", f.IsRequired, Fields.ExcelNote(f), [f.Label]))];

    private static async Task<IResult?> AdminAsync(CurrentUser current, Db db, Func<Person, Task<IResult>> then)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return refusal;
        if (!me!.IsAdmin) return ApiResults.NotAllowed("Only admins can import old contracts.");
        return await then(me);
    }

    /// <summary>The uploaded spreadsheet as an Excel workbook stream (a CSV is converted), or a plain problem.</summary>
    private static async Task<(Stream? Book, string FileName, string? Problem)> SheetAsync(IFormCollection form)
    {
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return (null, "", "Choose the spreadsheet to import.");
        if (file.Length > DocumentEndpoints.MaxBytes) return (null, "", "That file is over 25 MB. Split it into smaller files.");
        var name = Path.GetFileName(file.FileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".xlsx" or ".csv")) return (null, name, "Use an Excel workbook (.xlsx) or a CSV file. In Excel, Save As .xlsx first if it's an older .xls.");
        var memory = new MemoryStream();
        await file.CopyToAsync(memory);
        memory.Position = 0;
        return (ext == ".csv" ? Excel.FromCsv(memory) : memory, name, null);
    }

    // ---------------------------------------------------------------- The plan (shared by the dry run and the import)

    public sealed class PlanRow
    {
        public int Row { get; set; }
        public string? LegacyReference { get; set; }
        public string Label { get; set; } = "";
        public string Outcome { get; set; } = "";   // Effective, Ended, Already, Refused
        public List<string> Notes { get; } = [];
        public List<string> Problems { get; } = [];
        public string? ExistingReference { get; set; }
        internal SaveContract? Body { get; set; }
        internal string? NewSupplier { get; set; }
        internal DateOnly EffectiveOn { get; set; }
        internal DateOnly RegisteredOn { get; set; }
        internal DateOnly? EndedOn { get; set; }
    }

    private sealed record Options(Dictionary<string, string> Mapping, int? DefaultOwnerId, int? DefaultCategoryId);

    private static Options ReadOptions(IFormCollection form)
    {
        var mapping = new Dictionary<string, string>();
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(form["mapping"].ToString() is { Length: > 0 } m ? m : "{}") ?? [];
            foreach (var (column, key) in raw) if (!string.IsNullOrWhiteSpace(key)) mapping[column] = key!;
        }
        catch (JsonException) { }
        return new(mapping, int.TryParse(form["defaultOwnerId"], out var o) ? o : null, int.TryParse(form["defaultCategoryId"], out var cat) ? cat : null);
    }

    private static async Task<(List<PlanRow> Rows, Dictionary<string, string> NewSuppliers, string? Problem)> PlanAsync(
        SqlConnection c, SqlTransaction? tx, Person me, AppSettings settings, Stream book, Options o, DateOnly today)
    {
        var targets = await TargetsAsync(c, tx);
        // Source column for each field (the first column matched to it).
        var source = new Dictionary<string, string>();
        foreach (var (column, key) in o.Mapping) if (targets.Any(t => t.Key == key)) source.TryAdd(key, column);
        foreach (var t in targets.Where(t => t.Required && t.Key is not ("category" or "owner") && !t.Key.StartsWith("field.", StringComparison.Ordinal)))
            if (!source.ContainsKey(t.Key)) return ([], [], $"Match a column to {t.Label}: it's needed.");
        if (!source.ContainsKey("category") && o.DefaultCategoryId is null) return ([], [], "Match a column to Category, or choose a default category.");
        if (!source.ContainsKey("owner") && o.DefaultOwnerId is null) return ([], [], "Match a column to Owner, or choose a default owner.");

        var (rows, _, problem) = Excel.Read(book, source.Values.Distinct().ToList(), []);
        if (problem is not null) return ([], [], problem);

        static string K(string s) => s.Trim().ToUpperInvariant();
        var suppliers = (await c.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM contracts.Supplier", transaction: tx)).ToDictionary(s => K(s.Name), s => s.Id);
        var cats = (await c.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category", transaction: tx)).ToList();
        var items = (await c.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem", transaction: tx)).ToList();
        var owners = (await c.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsContractOwner = 1", transaction: tx)).ToList();
        var activePeople = (await c.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1", transaction: tx)).ToList();
        var already = (await c.QueryAsync<(string LegacyReference, string Reference)>("SELECT LegacyReference, Reference FROM contracts.Contract WHERE LegacyReference IS NOT NULL", transaction: tx))
            .ToDictionary(x => K(x.LegacyReference), x => x.Reference);
        var definitions = await Fields.LoadAsync(c, tx);
        var supplierIds = new Dictionary<string, int>(suppliers);
        var newSuppliers = new Dictionary<string, string>();
        var seen = new HashSet<string>();
        var plan = new List<PlanRow>();

        if (o.DefaultOwnerId is { } dOwner && owners.All(x => x.Id != dOwner)) return ([], [], "The default owner must be an active contract owner.");
        if (o.DefaultCategoryId is { } dCat && cats.All(x => x.Id != dCat)) return ([], [], "Choose a default category from the list.");

        string? Text(Excel.Row row, string key) => source.TryGetValue(key, out var col) ? Clean(row.Text(col)) : null;
        int? Person_(string text, string label, List<string> problems)
        {
            var byAccount = WindowsAccount.TryNormalise(text, settings.DefaultDomain, out var acc) ? owners.FirstOrDefault(x => WindowsAccount.Key(x.WindowsAccount) == WindowsAccount.Key(acc)) : null;
            var byName = owners.Where(x => K(x.Name) == K(text)).ToList();
            var match = byAccount ?? (byName.Count == 1 ? byName[0] : null);
            if (match is null) problems.Add($"{label}: \"{text}\" isn't an active contract owner. Add them on People (tick Contract owner), or match them to someone who is.");
            return match?.Id;
        }
        int? Item(string list, string? text, string label, List<string> problems)
        {
            if (text is null) return null;
            var match = items.FirstOrDefault(i => i.ListName == list && K(i.Value) == K(text));
            if (match is null) problems.Add($"{label}: there's no {label.ToLowerInvariant()} called \"{text}\". Add it on the Lists page first.");
            return match?.Id;
        }
        int? Category(string? text, List<string> problems)
        {
            if (text is null) return o.DefaultCategoryId;
            var parts = text.Split(['>', '›', '/'], 2, StringSplitOptions.TrimEntries);
            CategoryRow? match;
            if (parts.Length == 2)
            {
                var top = cats.FirstOrDefault(x => x.ParentId is null && K(x.Name) == K(parts[0]));
                match = top is null ? null : cats.FirstOrDefault(x => x.ParentId == top.Id && K(x.Name) == K(parts[1]));
            }
            else
            {
                match = cats.FirstOrDefault(x => x.ParentId is null && K(x.Name) == K(text));
                if (match is null)
                {
                    var subs = cats.Where(x => x.ParentId is not null && K(x.Name) == K(text)).ToList();
                    if (subs.Count > 1) { problems.Add($"Category: \"{text}\" is a subcategory under more than one category. Write it as Category > Subcategory."); return null; }
                    match = subs.SingleOrDefault();
                }
            }
            if (match is null) problems.Add($"Category: there's no category called \"{text}\". Add it on the Lists page first, or change the cell.");
            return match?.Id;
        }

        foreach (var row in rows)
        {
            var problems = new List<string>();
            var legacy = Text(row, "legacyReference");
            var title = Text(row, "title");
            var r = new PlanRow { Row = row.Number, LegacyReference = legacy, Label = title ?? legacy ?? "(no title)" };
            plan.Add(r);
            if (legacy is null) { r.Outcome = "Refused"; r.Problems.Add("Old reference: blank. Every row needs one."); continue; }
            if (legacy.Length > 100) { r.Outcome = "Refused"; r.Problems.Add("Old reference: keep it under 100 characters."); continue; }
            if (!seen.Add(K(legacy))) { r.Outcome = "Refused"; r.Problems.Add($"Old reference {legacy} is in the file more than once; only the first row is used."); continue; }
            if (already.TryGetValue(K(legacy), out var existing)) { r.Outcome = "Already"; r.ExistingReference = existing; r.Notes.Add($"Already imported as {existing}; skipped."); continue; }

            if (title is null) problems.Add("Title: blank.");
            else if (title.Length > 200) problems.Add("Title: keep it under 200 characters.");
            int? supplierId = null;
            var supplierName = Text(row, "supplier");
            if (supplierName is null) problems.Add("Supplier: blank.");
            else if (supplierIds.TryGetValue(K(supplierName), out var sid)) supplierId = sid;
            else if (supplierName.Length > 200) problems.Add("Supplier: keep the name under 200 characters.");
            else r.NewSupplier = newSuppliers.TryGetValue(K(supplierName), out var first) ? first : newSuppliers[K(supplierName)] = supplierName;
            var categoryId = Category(Text(row, "category"), problems);
            if (categoryId is null && Text(row, "category") is null) problems.Add("Category: blank, and there's no default category.");
            var ownerText = Text(row, "owner");
            var ownerId = ownerText is null ? o.DefaultOwnerId : Person_(ownerText, "Owner", problems);
            if (ownerId is null && ownerText is null) problems.Add("Owner: blank, and there's no default owner.");
            var backupId = Text(row, "backupOwner") is { } bo ? Person_(bo, "Backup owner", problems) : null;
            var agreementTypeId = Item("AgreementType", Text(row, "agreementType"), "Agreement type", problems);
            var businessUnitId = Item("BusinessUnit", Text(row, "businessUnit"), "Business unit", problems);
            var siteId = Item("Site", Text(row, "site"), "Site", problems);

            string? p = null;
            DateOnly? Date(string key) { if (!source.TryGetValue(key, out var col)) return null; var d = row.Date(col, out var pr); if (pr is not null) problems.Add(pr); return d; }
            bool? YesNo(string key) { if (!source.TryGetValue(key, out var col)) return null; var v = row.YesNo(col, out var pr); if (pr is not null) problems.Add(pr); return v; }
            var start = Date("startDate");
            var effective = Date("effectiveDate");
            var end = Date("endDate");
            var review = Date("reviewDate");
            var registered = Date("registeredDate");
            var evergreen = YesNo("evergreen") ?? false;
            var confidential = YesNo("confidential") ?? false;
            decimal? value = null;
            if (source.TryGetValue("value", out var vcol)) { value = row.Money(vcol, out p); if (p is not null) problems.Add(p); }
            int? notice = null;
            if (source.TryGetValue("noticePeriodDays", out var ncol)) { notice = row.WholeNumber(ncol, out p); if (p is not null) problems.Add(p); }
            var currency = Text(row, "currency")?.ToUpperInvariant() ?? "AUD";

            var effectiveOn = effective ?? start ?? registered;
            if (effectiveOn is null) problems.Add("Effective date: blank, and there's no start or registered date.");
            if (end is null && !evergreen) problems.Add("End date: blank. Fill it in, or set Evergreen to Yes.");
            if (evergreen) end = null;

            // Current or ended: the status column if there is one, otherwise the end date.
            var status = Text(row, "status")?.ToLowerInvariant();
            bool ended;
            if (status is not null && EndedWords.Any(w => status.Contains(w))) ended = true;
            else if (status is not null && CurrentWords.Any(w => status.Contains(w))) ended = false;
            else if (status is not null) { problems.Add($"Status: \"{Text(row, "status")}\" isn't a current or ended contract (only those are imported)."); ended = false; }
            else ended = end is { } e && e < today;

            // Added fields: a blank cell leaves it empty.
            var fieldValues = new Dictionary<string, JsonElement>();
            foreach (var (key, column) in source.Where(s => s.Key.StartsWith("field.", StringComparison.Ordinal)))
            {
                var field = definitions.FirstOrDefault(f => $"field.{f.Key}" == key);
                if (field is null) continue;
                var (json, fp) = Fields.FromExcel(field, row, column, activePeople, supplierIds, settings.DefaultDomain);
                if (fp is not null) problems.Add(fp);
                else if (json is { } j) fieldValues[field.Key] = j;
            }

            if (problems.Count > 0) { r.Outcome = "Refused"; r.Problems.AddRange(problems); continue; }

            r.Body = new SaveContract(title, Text(row, "description"), supplierId, categoryId, agreementTypeId, businessUnitId, siteId, ownerId, backupId, confidential,
                value, currency, Text(row, "sapOutlineAgreement"), start, effectiveOn, end, evergreen, notice, review, false, null, fieldValues);
            var errors = r.Body.SupplierId is null
                ? await ContractEndpoints.CheckAsync(c, tx!, r.Body with { SupplierId = suppliers.Values.FirstOrDefault() }, null) // a new supplier is added on import
                : await ContractEndpoints.CheckAsync(c, tx!, r.Body, null);
            var (_, fieldErrors) = await Fields.ParseAsync(c, tx!, fieldValues, definitions);
            var all = (errors ?? []).Concat(fieldErrors).Where(x => x.Key != "supplierId" || r.NewSupplier is null).ToList();
            if (all.Count > 0) { r.Outcome = "Refused"; r.Problems.AddRange(all.Select(x => $"{Label(targets, x.Key)}: {x.Value}")); continue; }

            r.Outcome = ended ? "Ended" : "Effective";
            r.EffectiveOn = effectiveOn!.Value;
            r.RegisteredOn = registered ?? effectiveOn.Value;
            r.EndedOn = ended ? end ?? today : null;
            if (r.NewSupplier is not null) r.Notes.Add($"Adds the supplier {r.NewSupplier}.");
            if (notice is null && !ended) r.Notes.Add("No notice period, so no notice-deadline reminders.");
            if (!ended && end is { } past && past < today) r.Notes.Add($"Its end date ({past:d MMM yyyy}) has passed but the status says it's current.");
        }
        return (plan, newSuppliers, null);
    }

    private static string Label(List<Target> targets, string errorKey)
    {
        var key = errorKey switch
        {
            "supplierId" => "supplier", "categoryId" => "category", "ownerId" => "owner", "backupOwnerId" => "backupOwner", "agreementTypeId" => "agreementType",
            "businessUnitId" => "businessUnit", "siteId" => "site", "currencyCode" => "currency", "isEvergreen" => "evergreen", _ => errorKey,
        };
        return targets.FirstOrDefault(t => t.Key == key || t.Key == $"field.{key}")?.Label ?? errorKey;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static object Summary(List<PlanRow> plan, Dictionary<string, string> newSuppliers) => new
    {
        effective = plan.Count(r => r.Outcome == "Effective"), ended = plan.Count(r => r.Outcome == "Ended"),
        already = plan.Count(r => r.Outcome == "Already"), refused = plan.Count(r => r.Outcome == "Refused"),
        newSuppliers = newSuppliers.Values.Order(),
        rows = plan.Select(r => new { r.Row, r.LegacyReference, r.Label, r.Outcome, r.Notes, r.Problems, r.ExistingReference }),
    };

    // ---------------------------------------------------------------- Endpoints

    public static void MapLegacyImportEndpoints(this IEndpointRouteBuilder app)
    {
        // Step 1a: the spreadsheet's columns, with examples, the fields they can go to, and a suggested match (remembered or by name).
        app.MapPost("/api/import/legacy/columns", async (HttpRequest request, CurrentUser current, Db db) => await AdminAsync(current, db, async me =>
        {
            if (!request.HasFormContentType) return ApiResults.Invalid("Choose the spreadsheet to import.", new() { ["file"] = "Choose the spreadsheet to import." });
            var (book, name, problem) = await SheetAsync(await request.ReadFormAsync());
            if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });
            var (columns, rowCount, readProblem) = Excel.Headers(book!);
            if (readProblem is not null) return ApiResults.Invalid(readProblem, new() { ["file"] = readProblem });
            await using var c = await db.OpenAsync();
            var targets = await TargetsAsync(c, null);
            var saved = await c.ExecuteScalarAsync<string?>("SELECT MappingJson FROM contracts.ImportMapping WHERE Name = N'legacy'") is { } json
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : [];
            var used = new HashSet<string>();
            string? Suggest(string header)
            {
                var n = Excel.Normalise(header);
                if (saved.TryGetValue(n, out var key) && targets.Any(t => t.Key == key) && used.Add(key)) return key;
                var match = targets.FirstOrDefault(t => !used.Contains(t.Key) && (Excel.Normalise(t.Label) == n || t.Aliases.Any(a => Excel.Normalise(a) == n)));
                if (match is null) return null;
                used.Add(match.Key);
                return match.Key;
            }
            return Results.Ok(new
            {
                fileName = name, rows = rowCount, remembered = saved.Count > 0,
                columns = columns.Select(col => new { header = col.Header, examples = col.Examples, suggested = Suggest(col.Header) }),
                targets = targets.Select(t => new { t.Key, t.Label, t.Group, t.Required, t.Help }),
            });
        }));

        // Step 1b: the dry run. Nothing changes.
        app.MapPost("/api/import/legacy/preview", async (HttpRequest request, CurrentUser current, Db db, AppSettings settings, TimeProvider clock) => await AdminAsync(current, db, async me =>
        {
            if (!request.HasFormContentType) return ApiResults.Invalid("Choose the spreadsheet to import.", new() { ["file"] = "Choose the spreadsheet to import." });
            var form = await request.ReadFormAsync();
            var (book, _, problem) = await SheetAsync(form);
            if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });
            await using var c = await db.OpenAsync();
            await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            var (plan, newSuppliers, planProblem) = await PlanAsync(c, (SqlTransaction)tx, me, settings, book!, ReadOptions(form), DateOnly.FromDateTime(HomeEndpoints.Today(clock)));
            await tx.RollbackAsync();
            if (planProblem is not null) return ApiResults.Invalid(planProblem, new() { ["mapping"] = planProblem });
            return Results.Ok(Summary(plan, newSuppliers));
        }));

        // Step 1c: the import. The rows that are ready go in, in one step; refused and already-imported rows are left out.
        app.MapPost("/api/import/legacy/import", async (HttpRequest request, CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock) => await AdminAsync(current, db, async me =>
        {
            if (!request.HasFormContentType) return ApiResults.Invalid("Choose the spreadsheet to import.", new() { ["file"] = "Choose the spreadsheet to import." });
            var form = await request.ReadFormAsync();
            var (book, fileName, problem) = await SheetAsync(form);
            if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });
            var options = ReadOptions(form);
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var (plan, newSuppliers, planProblem) = await PlanAsync(c, tx, me, settings, book!, options, DateOnly.FromDateTime(HomeEndpoints.Today(clock)));
            if (planProblem is not null) return ApiResults.Invalid(planProblem, new() { ["mapping"] = planProblem });
            var ready = plan.Where(r => r.Outcome is "Effective" or "Ended").ToList();
            if (ready.Count == 0) return ApiResults.Invalid("There's nothing ready to import. Check the dry run.", new() { ["file"] = "Nothing is ready to import." });

            // Remember the column matching for next time (by the column's name, ignoring spaces and case).
            var mapping = JsonSerializer.Serialize(options.Mapping.ToDictionary(m => Excel.Normalise(m.Key), m => m.Value));
            await c.ExecuteAsync("""
                MERGE contracts.ImportMapping AS t USING (SELECT N'legacy' AS Name) AS s ON t.Name = s.Name
                WHEN MATCHED THEN UPDATE SET MappingJson = @mapping, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                WHEN NOT MATCHED THEN INSERT (Name, MappingJson, UpdatedById) VALUES (N'legacy', @mapping, @me);
                """, new { mapping, me = me.Id }, tx);

            var via = $" (imported from {fileName})";
            var supplierIds = new Dictionary<string, int>();
            foreach (var name in newSuppliers.Values)
            {
                var id = await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Supplier WHERE NameKey = UPPER(LTRIM(RTRIM(@name)))", new { name }, tx)
                    ?? await c.ExecuteScalarAsync<int>("INSERT INTO contracts.Supplier (Name, CreatedById, UpdatedById) OUTPUT INSERTED.Id VALUES (@name, @me, @me)", new { name, me = me.Id }, tx);
                await audit.WriteAsync(c, tx, me.AsActor(), "supplier.added", "Supplier", id, $"{me.Name} added the supplier {name}{via}.");
                supplierIds[name.Trim().ToUpperInvariant()] = id;
            }
            var brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
            var created = new List<string>();
            foreach (var r in ready)
            {
                var body = r.NewSupplier is { } ns ? r.Body! with { SupplierId = supplierIds[ns.Trim().ToUpperInvariant()] } : r.Body!;
                var (id, reference, errors) = await ContractEndpoints.CreateDraftAsync(c, tx, audit, me, body, $"{via.TrimEnd(')')}, row {r.Row})");
                if (errors is not null) throw new InvalidOperationException($"Row {r.Row} failed on import after passing the dry run: {string.Join("; ", errors.Values)}");
                var registeredUtc = TimeZoneInfo.ConvertTimeToUtc(r.RegisteredOn.ToDateTime(new TimeOnly(12, 0)), brisbane); // midday Brisbane: the same date in UTC
                var p = new DynamicParameters(new { id, stage = r.Outcome, legacy = r.LegacyReference, file = fileName.Length > 260 ? fileName[..260] : fileName, me = me.Id, registeredUtc,
                    reason = $"Ended before it came into JBS Contracts (imported from {fileName})." });
                p.Add("endedOn", r.EndedOn?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
                await c.ExecuteAsync("""
                    UPDATE contracts.Contract SET Stage = @stage, StageSinceUtc = @registeredUtc, IsLocked = 1, RegisteredUtc = @registeredUtc, RegisteredById = @me,
                        LegacyReference = @legacy, ImportedUtc = SYSUTCDATETIME(), ImportedFrom = @file,
                        EndedOn = CASE WHEN @stage = N'Ended' THEN @endedOn END, EndedReason = CASE WHEN @stage = N'Ended' THEN @reason END,
                        UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, p, tx);
                await audit.WriteAsync(c, tx, me.AsActor(), "contract.imported", "Contract", id,
                    $"{me.Name} imported {reference} (old reference {r.LegacyReference}) from {fileName}, row {r.Row}, as {(r.Outcome == "Ended" ? $"Ended on {r.EndedOn:d MMM yyyy}" : "Effective")}; " +
                    $"effective {r.EffectiveOn:d MMM yyyy}, registered {r.RegisteredOn:d MMM yyyy}.",
                    new { legacyReference = r.LegacyReference, fileName, row = r.Row, stage = r.Outcome, effectiveOn = r.EffectiveOn, registeredOn = r.RegisteredOn, endedOn = r.EndedOn }, contractId: id);
                created.Add(reference);
            }
            await audit.WriteAsync(c, tx, me.AsActor(), "import.contracts", "Import", null,
                $"{me.Name} imported {created.Count:N0} old contracts from {fileName} ({plan.Count(x => x.Outcome == "Effective"):N0} Effective, {plan.Count(x => x.Outcome == "Ended"):N0} Ended; " +
                $"{plan.Count(x => x.Outcome == "Already"):N0} already there, {plan.Count(x => x.Outcome == "Refused"):N0} not imported; {newSuppliers.Count:N0} new suppliers).");
            await tx.CommitAsync();
            return Results.Ok(new { imported = created.Count, first = created.FirstOrDefault(), last = created.LastOrDefault(), summary = Summary(plan, newSuppliers) });
        }));

        // Step 2: files from a ZIP, matched to contracts by the old (or JBSAUS) reference in the file's name or folders.
        app.MapPost("/api/import/files/preview", async (HttpRequest request, CurrentUser current, Db db) => await AdminAsync(current, db, async me =>
        {
            var (zip, kind, problem) = await ZipAsync(request);
            if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });
            await using var c = await db.OpenAsync();
            using (zip)
            {
                var plan = await PlanFilesAsync(c, null, zip!);
                return Results.Ok(FilesSummary(plan));
            }
        }));

        app.MapPost("/api/import/files/import", async (HttpRequest request, CurrentUser current, Db db, AuditLog audit, AppSettings settings) => await AdminAsync(current, db, async me =>
        {
            if (settings.DocumentsDirectory is not { } root)
                return ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "no-storage", "Documents can't be saved yet because the server has no data folder set. Tell your admin.");
            var (zip, kind, problem) = await ZipAsync(request);
            if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });
            using (zip)
            {
                await using var c = await db.OpenAsync();
                await using var tx = await Db.BeginWriteAsync(c);
                var plan = await PlanFilesAsync(c, tx, zip!);
                var saved = new List<string>();
                try
                {
                    foreach (var f in plan.Where(f => f.Outcome == "Match"))
                    {
                        await using var stream = f.Entry!.Open();
                        var stored = await DocumentEndpoints.StoreStreamAsync(root, stream, f.FileName);
                        saved.Add(stored.Path);
                        var title = Path.GetFileNameWithoutExtension(f.FileName);
                        if (title.Length == 0) title = "Document";
                        if (title.Length > 200) title = title[..200];
                        var documentId = await c.ExecuteScalarAsync<int>("INSERT INTO contracts.Document (ContractId, Kind, Title, CreatedById) OUTPUT INSERTED.Id VALUES (@id, @kind, @title, @me)",
                            new { id = f.ContractId, kind, title, me = me.Id }, tx);
                        var versionId = await c.ExecuteScalarAsync<int>("""
                            INSERT INTO contracts.DocumentVersion (DocumentId, VersionNumber, FileName, ContentType, SizeBytes, Sha256, StoredName, UploadedById)
                            OUTPUT INSERTED.Id VALUES (@documentId, 1, @fileName, @type, @size, @sha, @stored, @me)
                            """, new { documentId, fileName = stored.FileName, type = stored.ContentType, size = stored.Size, sha = stored.Sha256, stored = stored.StoredName, me = me.Id }, tx);
                        await audit.WriteAsync(c, tx, me.AsActor(), "document.imported", "Document", documentId,
                            $"{me.Name} imported \"{title}\" ({DocumentEndpoints.Kinds[kind!]}) to {f.Reference} from {f.Path}.",
                            new { versionId, f.Path, stored.FileName, stored.Size, stored.Sha256 }, contractId: f.ContractId);
                    }
                    await audit.WriteAsync(c, tx, me.AsActor(), "import.files", "Import", null,
                        $"{me.Name} imported {saved.Count:N0} files from a ZIP ({plan.Count(f => f.Outcome == "Unmatched"):N0} not matched, {plan.Count(f => f.Outcome == "Already"):N0} already there, {plan.Count(f => f.Outcome == "Refused"):N0} not allowed).");
                    await tx.CommitAsync();
                }
                catch
                {
                    foreach (var path in saved) File.Delete(path);
                    throw;
                }
                return Results.Ok(new { imported = saved.Count, summary = FilesSummary(plan) });
            }
        }));
    }

    // ---------------------------------------------------------------- Files from ZIPs

    private const int MaxEntries = 2000;
    private const long MaxUnpacked = 1024L * 1024 * 1024; // 1 GB in one ZIP

    public sealed class FilePlan
    {
        public string Path { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        public string Outcome { get; set; } = ""; // Match, Already, Unmatched, Refused
        public string? Reference { get; set; }
        public string? Title { get; set; }
        public string? Reason { get; set; }
        internal int ContractId { get; set; }
        internal ZipArchiveEntry? Entry { get; set; }
    }

    private static async Task<(ZipArchive? Zip, string? Kind, string? Problem)> ZipAsync(HttpRequest request)
    {
        if (!request.HasFormContentType) return (null, null, "Choose a ZIP file.");
        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        var kind = form["kind"].ToString();
        if (!DocumentEndpoints.Kinds.ContainsKey(kind)) return (null, null, "Choose what kind of documents these are.");
        if (file is null || file.Length == 0) return (null, null, "Choose a ZIP file.");
        if (!System.IO.Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return (null, null, "Choose a .zip file.");
        if (file.Length > DocumentEndpoints.MaxBytes) return (null, null, "That ZIP is over 25 MB. Split the files into several ZIPs and import them one after the other.");
        var memory = new MemoryStream();
        await file.CopyToAsync(memory);
        memory.Position = 0;
        try { return (new ZipArchive(memory, ZipArchiveMode.Read), kind, null); }
        catch (InvalidDataException) { return (null, null, "That file isn't a ZIP that can be opened."); }
    }

    private static async Task<List<FilePlan>> PlanFilesAsync(SqlConnection c, SqlTransaction? tx, ZipArchive zip)
    {
        var contracts = (await c.QueryAsync<(int Id, string Reference, string? LegacyReference, string Title)>(
            "SELECT Id, Reference, LegacyReference, Title FROM contracts.Contract WHERE Stage <> N'Withdrawn'", transaction: tx)).ToList();
        // Every reference a file could carry, longest first so "MED-1001" beats "MED-100".
        var keys = contracts.SelectMany(k => new[] { (Key: k.Reference.ToUpperInvariant(), k.Id) }
                .Concat(k.LegacyReference is { } l ? [(Key: l.Trim().ToUpperInvariant(), k.Id)] : []))
            .Where(x => x.Key.Length >= 3).GroupBy(x => x.Key).Select(g => (g.Key, Ids: g.Select(x => x.Id).Distinct().ToList()))
            .OrderByDescending(x => x.Key.Length).ToList();
        var byId = contracts.ToDictionary(k => k.Id);
        var hashes = (await c.QueryAsync<(int ContractId, string Sha256)>(
            "SELECT d.ContractId, v.Sha256 FROM contracts.DocumentVersion v JOIN contracts.Document d ON d.Id = v.DocumentId", transaction: tx))
            .Select(x => (x.ContractId, x.Sha256)).ToHashSet();

        static bool Boundary(string s, int i) => i < 0 || i >= s.Length || !char.IsLetterOrDigit(s[i]);
        static bool Contains(string path, string key)
        {
            for (var at = path.IndexOf(key, StringComparison.Ordinal); at >= 0; at = path.IndexOf(key, at + 1, StringComparison.Ordinal))
                if (Boundary(path, at - 1) && Boundary(path, at + key.Length)) return true;
            return false;
        }

        var plan = new List<FilePlan>();
        long unpacked = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.Length == 0) continue;
            var name = System.IO.Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (name.StartsWith('.') || entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;
            var f = new FilePlan { Path = entry.FullName, FileName = name, Size = entry.Length, Entry = entry };
            plan.Add(f);
            if (plan.Count > MaxEntries) { f.Outcome = "Refused"; f.Reason = $"More than {MaxEntries:N0} files in one ZIP; split it."; continue; }
            unpacked += entry.Length;
            if (!DocumentEndpoints.AllowedType(name)) { f.Outcome = "Refused"; f.Reason = "That kind of file can't be added (PDF, Word, Excel, Outlook email, text, CSV, PNG or JPG only)."; continue; }
            if (entry.Length > DocumentEndpoints.MaxBytes) { f.Outcome = "Refused"; f.Reason = "Over 25 MB."; continue; }
            if (unpacked > MaxUnpacked) { f.Outcome = "Refused"; f.Reason = "The ZIP unpacks to more than 1 GB; split it."; continue; }

            var path = entry.FullName.ToUpperInvariant();
            var match = keys.FirstOrDefault(k => Contains(path, k.Key));
            if (match.Key is null) { f.Outcome = "Unmatched"; f.Reason = "No old or JBSAUS reference found in its name or folders."; continue; }
            if (match.Ids.Count > 1) { f.Outcome = "Unmatched"; f.Reason = $"{match.Key} matches more than one contract."; continue; }
            var k = byId[match.Ids[0]];
            f.ContractId = k.Id;
            f.Reference = k.Reference;
            f.Title = k.Title;
            // The same file already on that contract (by its fingerprint) isn't added again, so a ZIP can be imported twice safely.
            using (var s = entry.Open())
            {
                var sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(s));
                if (hashes.Contains((k.Id, sha))) { f.Outcome = "Already"; f.Reason = "This file is already on the contract."; continue; }
                hashes.Add((k.Id, sha)); // the same file twice in one ZIP goes in once
            }
            f.Outcome = "Match";
        }
        return plan;
    }

    private static object FilesSummary(List<FilePlan> plan) => new
    {
        matched = plan.Count(f => f.Outcome == "Match"), already = plan.Count(f => f.Outcome == "Already"),
        unmatched = plan.Count(f => f.Outcome == "Unmatched"), refused = plan.Count(f => f.Outcome == "Refused"),
        contracts = plan.Where(f => f.Outcome == "Match").Select(f => f.Reference).Distinct().Count(),
        files = plan.Select(f => new { f.Path, f.Size, f.Outcome, f.Reference, f.Title, f.Reason }),
    };
}
