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
        new("legacyReference", "Old reference", "Needed", true, "The contract's number in the old system. It keeps this number in JBS Contracts. Also used to match its files, and so a second run skips it.",
            ["old reference", "agreement reference", "agreement number", "agreement no", "agreement id", "contract number", "contract no", "contract id", "reference", "ref", "id", "medius id"]),
        new("title", "Title", "Needed", true, null, ["title", "contract title", "contract name", "agreement name", "name", "subject"]),
        new("supplier", "Supplier", "Needed", true, "A name that isn't in JBS Contracts yet adds the supplier.", ["supplier", "supplier name", "vendor", "vendor name", "counterparty", "party"]),
        new("supplierVendorNumber", "Supplier's SAP vendor number", "Other", false, "Saved on a supplier that hasn't got one yet.",
            ["vendor number", "sap vendor number", "supplier number", "vendor no", "vendor code"]),
        new("category", "Category", "Needed", true, "A category, or Category > Subcategory. Blank uses the default you choose below.",
            ["category", "contract category", "spend category", "portfolio/category", "portfolio category", "portfolio"]),
        new("subcategory", "Subcategory", "Needed", false, "The subcategory under the category in its own column.", ["subcategory", "sub-category", "sub category"]),
        new("owner", "Owner", "Needed", true, "Windows logon or full name of a contract owner. Blank uses the default you choose below.", ["owner", "contract owner", "agreement owner", "responsible", "contract manager", "manager"]),
        new("effectiveDate", "Effective date", "Dates", false, "Blank uses the start date, then the registered date.", ["effective date", "effective from"]),
        new("startDate", "Start date", "Dates", false, null, ["start date", "start", "commencement date", "commencement", "valid from"]),
        new("endDate", "End date", "Dates", false, "Blank needs Evergreen to be Yes.", ["end date", "end", "expiry date", "expiry", "expires", "valid to", "termination date"]),
        new("evergreen", "Evergreen", "Dates", false, "Yes or No: it carries on until someone ends it, with no end date.", ["evergreen", "perpetual", "no end date"]),
        new("autoRenews", "Renews automatically", "Dates", false, "Yes or No (or Automatic, Manual): yes moves the end date on by the renewal term unless someone decides to let it end. Medius's Extension Type goes here: In Full keeps its end date and carries on after it until replaced (it doesn't renew by itself), Monthly renews a month at a time.",
            ["renews automatically", "auto renew", "auto renews", "auto renewal", "automatic renewal", "extension type", "renewal type", "rolling"]),
        new("renewalTermMonths", "Renewal term (months)", "Dates", false, "How long each automatic renewal lasts: 12, 12 months or 1 year.",
            ["renewal term", "renewal term months", "renewal period", "extension period", "extension term", "extension length", "renewal length"]),
        new("noticePeriodDays", "Notice period", "Dates", false, "Days, or for example 3 months (counted back from the end date). Without it there are no notice-deadline reminders.",
            ["notice period", "notice period days", "notice period months", "notice days", "notice"]),
        new("reviewDate", "Review date", "Dates", false, null, ["review date", "next review"]),
        new("registeredDate", "Registered date", "Dates", false, "When it was approved or registered. Blank uses the effective date.", ["registered date", "registered", "approved date", "approval date", "signed date", "date signed"]),
        new("status", "Status", "Other", false, "Active, current, effective or approved loads it as Effective; expired, ended, terminated or cancelled as Ended; archived is left out (if ticked below). Blank: worked out from the end date.",
            ["status", "contract status", "agreement status", "state"]),
        new("agreementType", "Agreement type", "Other", false, null, ["agreement type", "contract type", "type"]),
        new("businessUnit", "Business unit", "Other", false, null, ["business unit", "bu", "division"]),
        new("site", "Site", "Other", false, null, ["site", "location", "plant"]),
        new("entity", "Entity", "Other", false, "The JBS company that signs it.", ["entity", "entity name", "legal entity", "jbs entity", "contracting entity", "company"]),
        new("function", "Function", "Other", false, null, ["function", "business function", "department"]),
        new("backupOwner", "Backup owner", "Other", false, null, ["backup owner", "backup", "deputy"]),
        new("value", "Value", "Other", false, null, ["value", "contract value", "total value", "amount", "estimated value", "estimated agreement value", "estimated contract value"]),
        new("currency", "Currency", "Other", false, "Three letters, for example AUD. Blank is AUD.", ["currency", "currency code", "agreement currency"]),
        new("sapOutlineAgreement", "SAP outline agreement number", "Other", false, null, ["sap outline agreement number", "sap outline agreement", "outline agreement", "sap number", "sap"]),
        new("confidential", "Confidential", "Other", false, "Yes or No", ["confidential"]),
        new("description", "Description", "Other", false, null, ["description", "notes", "comments", "summary", "scope"]),
    ];

    private static readonly string[] EndedWords = ["expired", "ended", "terminated", "cancelled", "canceled", "inactive", "closed", "archived", "lapsed"];
    // "Approved": signed in the old system but starting later (Medius), so current.
    private static readonly string[] CurrentWords = ["active", "current", "live", "effective", "valid", "open", "renewed", "approved"];
    private static readonly string[] NotWords = ["not", "no", "non", "un", "never", "pending", "draft"];

    /// <summary>
    /// True for ended, false for current, null when the status isn't clear. Whole words only, so "Invalid" isn't read as "valid",
    /// and a negative ("Not renewed", "Not active") or anything with words for both is left for a person to decide.
    /// </summary>
    internal static bool? StatusSays(string status)
    {
        var words = System.Text.RegularExpressions.Regex.Split(status.ToLowerInvariant(), "[^a-z]+").Where(w => w.Length > 0).ToList();
        var text = string.Join(' ', words);
        if (words.Any(NotWords.Contains)) return null;
        var isEnded = words.Any(EndedWords.Contains);
        var isCurrent = words.Any(CurrentWords.Contains) || text.Contains("in force");
        return isEnded == isCurrent ? null : isEnded;
    }

    /// <summary>"90", "90 days", "3 months", "1 year", "6 wks": the number and its unit (day, week, month or year; a bare number is days).</summary>
    internal static (int? Number, string Unit) Period(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text.Trim().ToLowerInvariant(), @"^(\d{1,4})(?:\.0+)?\s*([a-z]*)\.?$");
        if (!m.Success) return (null, "");
        var unit = m.Groups[2].Value switch
        {
            "" or "d" or "day" or "days" => "day",
            "w" or "wk" or "wks" or "week" or "weeks" => "week",
            "m" or "mo" or "mth" or "mths" or "month" or "months" => "month",
            "y" or "yr" or "yrs" or "year" or "years" => "year",
            _ => null,
        };
        return unit is null ? (null, "") : (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), unit);
    }

    /// <summary>
    /// Whether an old system's renewal setting means it renews by itself: Yes, Automatic, Auto renewal (true); No, Manual, None, Fixed term,
    /// or any negative (false); anything else is left for a person (null).
    /// </summary>
    internal static bool? RenewsSays(string text)
    {
        var words = WordList(text);
        if (words.Count == 0) return null;
        if (words.Count == 1 && words[0] is "yes" or "y" or "true" or "1" or "x") return true;
        if (words.Any(w => w is "no" or "n" or "not" or "non" or "never" or "false" or "0" or "none" or "manual" or "manually" or "fixed")) return false;
        if (words.Any(w => w.StartsWith("auto", StringComparison.Ordinal) || w is "rolling" or "rollover")) return true;
        return null;
    }

    /// <summary>A cell's words, lower case, with joined-up words split ("AutoExtensionFull" reads as auto, extension, full).</summary>
    private static List<string> WordList(string text) =>
        System.Text.RegularExpressions.Regex.Split(System.Text.RegularExpressions.Regex.Replace(text, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant(), "[^a-z0-9]+")
            .Where(w => w.Length > 0).ToList();

    /// <summary>
    /// Medius's "Auto Extension - In Full" (AutoExtensionFull): after its end date it carries on under the same terms until a new
    /// agreement replaces it (Jeff, 27 Nov). It keeps its end date and doesn't renew by itself.
    /// </summary>
    internal static bool MeansCarriesOn(string text)
    {
        var words = WordList(text);
        return words.Any(w => w.StartsWith("auto", StringComparison.Ordinal)) && words.Contains("full");
    }

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
        /// <summary>Its number in JBS Contracts: the old reference (D-102), or null for the next JBSAUS number.</summary>
        internal string? Reference { get; set; }
    }

    private sealed record Options(Dictionary<string, string> Mapping, int? DefaultOwnerId, int? DefaultCategoryId, bool LeaveOutArchived = false,
        Translations? Translations = null);

    /// <summary>
    /// The drop-down fields whose old values can be translated (D-112): field key, its list (for the choices) and label. Category takes
    /// "Category > Subcategory" when the file has a subcategory column.
    /// </summary>
    internal static readonly (string Key, string List, string Label)[] TranslatedFields =
    [
        ("category", "Category", "Category"), ("agreementType", "AgreementType", "Agreement type"), ("businessUnit", "BusinessUnit", "Business unit"),
        ("site", "Site", "Site"), ("entity", "Entity", "Entity"), ("function", "Function", "Function"),
        // Owners: a past employee's contracts go to a current contract owner (D-113); blank means the default owner.
        ("owner", "Owner", "Owner"),
    ];

    /// <summary>
    /// Old values translated to JBS Contracts list values, by field and old value (any case). The new value is a list value's name
    /// ("Category > Subcategory" for categories), or empty for "leave it blank". Names, not ids, so a downloaded set works on Production.
    /// </summary>
    internal sealed class Translations
    {
        private readonly Dictionary<string, Dictionary<string, string>> byField = new(StringComparer.OrdinalIgnoreCase);
        public void Set(string field, string old, string target) =>
            (byField.TryGetValue(field, out var m) ? m : byField[field] = new(StringComparer.OrdinalIgnoreCase))[old.Trim()] = target.Trim();
        public bool TryGet(string field, string? old, out string target)
        {
            target = "";
            return old is not null && byField.TryGetValue(field, out var m) && m.TryGetValue(old.Trim(), out target!);
        }
        public Dictionary<string, Dictionary<string, string>> ToDictionary() => byField.ToDictionary(f => f.Key, f => new Dictionary<string, string>(f.Value));
        public static Translations From(Dictionary<string, Dictionary<string, string>>? raw)
        {
            var t = new Translations();
            foreach (var (field, values) in raw ?? [])
                if (TranslatedFields.Any(f => f.Key == field))
                    foreach (var (old, target) in values) if (!string.IsNullOrWhiteSpace(old)) t.Set(field, old, target ?? "");
            return t;
        }
    }

    /// <summary>
    /// What the plan found besides its rows: list items that aren't in JBS Contracts yet (as rows for the Lists import: list, name,
    /// parent category), and SAP vendor numbers to save on suppliers that haven't got one (by supplier name).
    /// </summary>
    private sealed class PlanExtras
    {
        public SortedSet<(string List, string Name, string Parent)> Missing { get; } = [];
        public Dictionary<string, string> VendorNumbers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, string> ListLabels = new()
    {
        ["AgreementType"] = "Agreement type", ["BusinessUnit"] = "Business unit", ["Site"] = "Site", ["Entity"] = "Entity", ["Function"] = "Function",
    };

    private static Options ReadOptions(IFormCollection form)
    {
        var mapping = new Dictionary<string, string>();
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(form["mapping"].ToString() is { Length: > 0 } m ? m : "{}") ?? [];
            foreach (var (column, key) in raw) if (!string.IsNullOrWhiteSpace(key)) mapping[column] = key!;
        }
        catch (JsonException) { }
        Translations? translations = null;
        try
        {
            if (form["translations"].ToString() is { Length: > 0 } t)
                translations = Translations.From(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(t));
        }
        catch (JsonException) { }
        return new(mapping, int.TryParse(form["defaultOwnerId"], out var o) ? o : null, int.TryParse(form["defaultCategoryId"], out var cat) ? cat : null,
            form["leaveOutArchived"] == "true", translations);
    }

    private static async Task<(List<PlanRow> Rows, Dictionary<string, string> NewSuppliers, string? Problem)> PlanAsync(
        SqlConnection c, SqlTransaction? tx, Person me, AppSettings settings, Stream book, Options o, DateOnly today, PlanExtras? extras = null)
    {
        extras ??= new PlanExtras();
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
        var supplierRows = (await c.QueryAsync<(int Id, string Name, string? VendorNumber)>("SELECT Id, Name, VendorNumber FROM contracts.Supplier", transaction: tx)).ToList();
        var suppliers = supplierRows.ToDictionary(s => K(s.Name), s => s.Id);
        var hasVendorNumber = supplierRows.Where(s => s.VendorNumber is not null).Select(s => K(s.Name)).ToHashSet();
        // A supplier is found by its SAP vendor number first (D-116), where only one supplier has it.
        var byVendor = supplierRows.Where(s => s.VendorNumber is not null).GroupBy(s => K(s.VendorNumber!)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var cats = (await c.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category", transaction: tx)).ToList();
        var items = (await c.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem", transaction: tx)).ToList();
        var owners = (await c.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsContractOwner = 1", transaction: tx)).ToList();
        var activePeople = (await c.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1", transaction: tx)).ToList();
        var already = (await c.QueryAsync<(string LegacyReference, string Reference)>("SELECT LegacyReference, Reference FROM contracts.Contract WHERE LegacyReference IS NOT NULL", transaction: tx))
            .ToDictionary(x => K(x.LegacyReference), x => x.Reference);
        var definitions = await Fields.LoadAsync(c, tx);
        var references = (await c.QueryAsync<string>("SELECT Reference FROM contracts.Contract", transaction: tx)).Select(K).ToHashSet();
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
            if (match is null) problems.Add($"{label}: \"{text}\" isn't an active contract owner. Choose who takes their contracts under Translate drop-down values, or add them on People (tick Contract owner).");
            return match?.Id;
        }
        int? Item(string list, string? text, string label, List<string> problems)
        {
            if (text is null) return null;
            var match = items.FirstOrDefault(i => i.ListName == list && K(i.Value) == K(text));
            if (match is null)
            {
                problems.Add($"{label}: there's no {label.ToLowerInvariant()} called \"{text}\". Add it on the Lists page first.");
                extras.Missing.Add((ListLabels[list], text, ""));
            }
            return match?.Id;
        }
        // Categories go Family > Category > Subcategory (D-115); a family alone isn't enough.
        var tree = new CategoryTree(cats);
        int? Category(string? text, string? sub, List<string> problems)
        {
            if (text is null && sub is null) return o.DefaultCategoryId;
            if (text is not null && sub is not null)
            {
                // Category and subcategory in their own columns (Medius): the subcategory sits directly under the category. Names
                // may contain "/" so they aren't split.
                var found = tree.All.Where(x => K(x.Name) == K(sub) && x.ParentId is { } p && K(tree.Get(p)!.Name) == K(text)).ToList();
                if (found.Count == 1) return found[0].Id;
                if (found.Count > 1) { problems.Add($"Category: \"{text} > {sub}\" is in more than one family. Translate it to the one you mean."); return null; }
                extras.Missing.Add(("Category", text, ""));
                extras.Missing.Add(("Category", sub, text));
                problems.Add($"Category: there's no \"{text} > {sub}\". Translate it to one of your categories, or add it on the Lists page first.");
                return null;
            }
            text ??= sub!;
            var match = tree.Resolve(text, out var problem);
            if (match is null)
            {
                problems.Add(problem!.Replace("Add it under Admin, Lists first.", "Translate it to one of your categories, or add it on the Lists page first."));
                var parts = CategoryTree.SplitPath(text);
                if (tree.FindPath(parts) is null)
                    for (var i = 0; i < parts.Length; i++) extras.Missing.Add(("Category", parts[i], string.Join(" > ", parts.Take(i))));
            }
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
            if (o.LeaveOutArchived && Text(row, "status") is { } st && System.Text.RegularExpressions.Regex.IsMatch(st, @"\barchived\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { r.Outcome = "LeftOut"; r.Notes.Add("Archived in the old system: left out. Untick \"Leave out archived contracts\" to bring it in as Ended."); continue; }

            if (title is null) problems.Add("Title: blank.");
            else if (title.Length > 200) problems.Add("Title: keep it under 200 characters.");
            int? supplierId = null;
            var supplierName = Text(row, "supplier");
            if (Text(row, "supplierVendorNumber") is { } number && byVendor.TryGetValue(K(number), out var numbered))
            {
                supplierId = numbered.Id;
                if (supplierName is not null && K(supplierName) != K(numbered.Name)) r.Notes.Add($"Supplier found by SAP vendor number {number}: {numbered.Name}.");
                supplierName = numbered.Name;
            }
            else if (supplierName is null) problems.Add("Supplier: blank.");
            else if (supplierIds.TryGetValue(K(supplierName), out var sid)) supplierId = sid;
            else if (supplierName.Length > 200) problems.Add("Supplier: keep the name under 200 characters.");
            else r.NewSupplier = newSuppliers.TryGetValue(K(supplierName), out var first) ? first : newSuppliers[K(supplierName)] = supplierName;
            if (supplierName is not null && Text(row, "supplierVendorNumber") is { } vendor && !hasVendorNumber.Contains(K(supplierName)))
            {
                if (vendor.Length > 50) problems.Add("Supplier's SAP vendor number: keep it under 50 characters.");
                else if (extras.VendorNumbers.TryAdd(supplierName.Trim(), vendor)) r.Notes.Add($"Saves SAP vendor number {vendor} on {supplierName.Trim()}.");
            }
            // Translated values (D-112): the old value's chosen JBS Contracts value, or blank.
            string? Tr(string field, string? text) => text is not null && o.Translations is { } tr && tr.TryGet(field, text, out var to) ? (to.Length == 0 ? null : to) : text;
            var (catText, subText) = (Text(row, "category"), Text(row, "subcategory"));
            // Translated to a path in your categories (D-115), or left blank.
            if (o.Translations is { } trs && (catText ?? subText) is not null && trs.TryGet("category", CategoryKey(catText, subText), out var catTo))
            {
                (catText, subText) = (catTo.Length == 0 ? null : catTo, null);
                if (catTo.Length == 0) r.Notes.Add("Category left blank (translation).");
            }
            var categoryId = Category(catText, subText, problems);
            if (categoryId is null && catText is null && subText is null) problems.Add("Category: blank, and there's no default category.");
            var ownerText = Tr("owner", Text(row, "owner"));
            var ownerId = ownerText is null ? o.DefaultOwnerId : Person_(ownerText, "Owner", problems);
            if (ownerId is null && ownerText is null) problems.Add("Owner: blank, and there's no default owner.");
            var backupId = Text(row, "backupOwner") is { } bo ? Person_(bo, "Backup owner", problems) : null;
            var agreementTypeId = Item("AgreementType", Tr("agreementType", Text(row, "agreementType")), "Agreement type", problems);
            // Several values in one cell (D-114): each is translated and found on its own.
            int[] Items(string list, string field, string label)
            {
                var ids = new List<int>();
                foreach (var part in SplitCell(field, list, Text(row, field), items, o.Translations))
                    if (Item(list, Tr(field, part), label, problems) is { } id && !ids.Contains(id)) ids.Add(id);
                return [.. ids];
            }
            var businessUnitIds = Items("BusinessUnit", "businessUnit", "Business unit");
            var siteIds = Items("Site", "site", "Site");
            var entityIds = Items("Entity", "entity", "Entity");
            var functionIds = Items("Function", "function", "Function");

            string? p = null;
            DateOnly? Date(string key) { if (!source.TryGetValue(key, out var col)) return null; var d = row.Date(col, out var pr); if (pr is not null) problems.Add(pr); return d; }
            bool? YesNo(string key) { if (!source.TryGetValue(key, out var col)) return null; var v = row.YesNo(col, out var pr); if (pr is not null) problems.Add(pr); return v; }
            var start = Date("startDate");
            var effective = Date("effectiveDate");
            var end = Date("endDate");
            var review = Date("reviewDate");
            var registered = Date("registeredDate");
            // Medius's Extension Type matched to Evergreen by mistake: say which field it belongs to.
            // Medius's Extension Type matched to Evergreen (it was, once): its values are the renewal setting, so read them as that.
            var evergreenText = Text(row, "evergreen");
            var renewalText = Text(row, "autoRenews");
            bool evergreen;
            if (evergreenText is not null && WordList(evergreenText).Contains("extension"))
            {
                evergreen = false;
                renewalText ??= evergreenText;
            }
            else evergreen = YesNo("evergreen") ?? false;
            var confidential = YesNo("confidential") ?? false;
            decimal? value = null;
            if (source.TryGetValue("value", out var vcol)) { value = row.Money(vcol, out p); if (p is not null) problems.Add(p); }
            if (evergreen) end = null;
            int? notice = null;
            if (Text(row, "noticePeriodDays") is { } noticeText)
            {
                var (n, unit) = Period(noticeText);
                if (n is not { } count || count > 3650) problems.Add($"Notice period: \"{noticeText}\" isn't a number of days, weeks, months or years.");
                else if (unit is "month" or "year")
                {
                    // Months back from the end date, so 3 months before 30 June is 31 March.
                    var months = unit == "year" ? count * 12 : count;
                    notice = end is { } anchor ? anchor.DayNumber - anchor.AddMonths(-months).DayNumber : months * 30;
                    if (end is null) r.Notes.Add($"Notice period {noticeText} is counted as {notice} days (there's no end date to count back from).");
                }
                else notice = unit == "week" ? count * 7 : count;
            }
            var autoRenews = false;
            if (renewalText is { } renewsText)
            {
                if (RenewsSays(renewsText) is { } says) autoRenews = says;
                else problems.Add($"Renews automatically: \"{renewsText}\" isn't clearly yes or no. Change it to, for example, Yes or No.");
            }
            int? term = null;
            if (Text(row, "renewalTermMonths") is { } termText)
            {
                var (n, unit) = Period(termText);
                if (n is not { } count || unit is "day" or "week") problems.Add($"Renewal term: \"{termText}\" isn't a number of months or years.");
                else term = unit == "year" ? count * 12 : count;
            }
            // Medius's "Auto Extension - Monthly" with no extension period renews a month at a time.
            if (autoRenews && term is null && renewalText is { } monthly && monthly.Contains("month", StringComparison.OrdinalIgnoreCase))
            {
                term = 1;
                r.Notes.Add($"\"{monthly}\": renews a month at a time.");
            }
            // "In Full": after its end date it carries on under the same terms until a new agreement replaces it (Jeff, 27 Nov). It keeps
            // its end date and doesn't move it on; past the end date it stays Effective and asks for a renewal decision.
            var continues = false;
            if (renewalText is { } full && MeansCarriesOn(full))
            {
                autoRenews = false;
                term = null;
                continues = !evergreen;
                r.Notes.Add($"\"{full}\": keeps its end date and continues until replaced: after its end date it carries on under the same terms, shown as Continuing until replaced, not overdue.");
            }
            if (autoRenews && evergreen) problems.Add("Renews automatically: it's also Evergreen. An evergreen contract has no end date to renew; change one of them.");
            if (autoRenews && term is null) problems.Add("Renewal term: blank, but it renews automatically. Fill in how many months each renewal lasts.");
            if (!autoRenews) term = null;
            var currency = Text(row, "currency")?.ToUpperInvariant() ?? "AUD";

            var effectiveOn = effective ?? start ?? registered;
            if (effectiveOn is null) problems.Add("Effective date: blank, and there's no start or registered date.");
            if (end is null && !evergreen) problems.Add("End date: blank. Fill it in, or set Evergreen to Yes.");

            // Current or ended: the status column if there is one, otherwise the end date.
            var status = Text(row, "status");
            bool ended;
            var said = status is null ? null : StatusSays(status);
            if (said is { } s) ended = s;
            else if (status is not null) { problems.Add($"Status: \"{status}\" isn't clearly a current or ended contract (only those are imported). Change it to, for example, Active or Expired."); ended = false; }
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

            r.Body = new SaveContract(title, Text(row, "description"), supplierId, categoryId, agreementTypeId, businessUnitIds, siteIds, ownerId, backupId, confidential,
                value, currency, Text(row, "sapOutlineAgreement"), start, effectiveOn, end, evergreen, notice, review, false, null, fieldValues,
                entityIds, functionIds, autoRenews, term, ContinuesUntilReplaced: continues);
            var errors = r.Body.SupplierId is null
                ? await ContractEndpoints.CheckAsync(c, tx!, r.Body with { SupplierId = suppliers.Values.FirstOrDefault() }, null) // a new supplier is added on import
                : await ContractEndpoints.CheckAsync(c, tx!, r.Body, null);
            var (_, fieldErrors) = await Fields.ParseAsync(c, tx!, fieldValues, definitions);
            var all = (errors ?? []).Concat(fieldErrors).Where(x => x.Key != "supplierId" || r.NewSupplier is null).ToList();
            if (all.Count > 0) { r.Outcome = "Refused"; r.Problems.AddRange(all.Select(x => $"{Label(targets, x.Key)}: {x.Value}")); continue; }

            r.Outcome = ended ? "Ended" : "Effective";
            // It keeps its old number (D-102), unless that's too long or another contract already has it.
            if (legacy.Length <= 30 && references.Add(K(legacy))) r.Reference = legacy;
            else r.Notes.Add(legacy.Length > 30 ? "Its old reference is too long to keep as its number, so it gets the next JBSAUS number."
                : $"{legacy} is already another contract's number, so it gets the next JBSAUS number.");
            r.EffectiveOn = effectiveOn!.Value;
            r.RegisteredOn = registered ?? effectiveOn.Value;
            // It ended on its end date, unless that's still to come (for example terminated early): then it ended by today.
            r.EndedOn = ended ? (end is { } ce && ce < today ? ce : today) : null;
            if (r.NewSupplier is not null) r.Notes.Add($"Adds the supplier {r.NewSupplier}.");
            if (notice is null && !ended) r.Notes.Add("No notice period, so no notice-deadline reminders.");
            if (!ended && end is { } past && past < today) r.Notes.Add($"Its end date ({past:d MMM yyyy}) has passed but the status says it's current.");
        }
        return (plan, newSuppliers, null);
    }

    private const string LeaveBlank = "(leave blank)";

    /// <summary>Letters and digits only, lower case: "1220 - DINMORE PROCESSING" and "1220 Dinmore Processing" read the same.</summary>
    private static string Plain(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A code at the start of a value, when it has a digit in it ("1220" in "1220 - DINMORE PROCESSING").</summary>
    private static string? LeadingCode(string s)
    {
        var first = new string(s.TrimStart().TakeWhile(char.IsLetterOrDigit).ToArray());
        return first.Any(char.IsDigit) ? first.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The list value an old value most likely is (D-118): the same once case, spaces and punctuation are ignored; else the only one
    /// starting with the same code. Null when there's no clear match, so a person chooses.
    /// </summary>
    internal static string? Similar(string old, IReadOnlyList<string> choices)
    {
        var plain = Plain(old);
        if (choices.FirstOrDefault(ch => Plain(ch) == plain) is { } same) return same;
        if (LeadingCode(old) is not { } code) return null;
        var byCode = choices.Where(ch => LeadingCode(ch) == code).ToList();
        return byCode.Count == 1 ? byCode[0] : null;
    }

    /// <summary>The fields whose cells can hold several values (D-114).</summary>
    private static readonly HashSet<string> SeveralFields = ["businessUnit", "site", "entity", "function"];

    /// <summary>
    /// A cell's values: several for business unit, site, entity and function (D-114), else the cell as it is. A value with a comma
    /// in it stays whole when it's already a list value or has a translation.
    /// </summary>
    private static List<string> SplitCell(string field, string list, string? cell, IEnumerable<ListItemRow> items, Translations? translations)
    {
        if (cell is null) return [];
        if (!SeveralFields.Contains(field)) return [cell];
        static string K(string s) => s.Trim().ToUpperInvariant();
        return ListCells.Split(cell, t => items.Any(i => i.ListName == list && K(i.Value) == K(t)) || (translations?.TryGet(field, t, out _) ?? false));
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>?> SavedTranslationsAsync(SqlConnection c, SqlTransaction? tx) =>
        await c.ExecuteScalarAsync<string?>("SELECT MappingJson FROM contracts.ImportMapping WHERE Name = N'legacy-values'", transaction: tx) is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) : null;

    /// <summary>How a row's category reads for translating: "Category > Subcategory" when there's a subcategory.</summary>
    internal static string CategoryKey(string? category, string? sub) =>
        category is not null && sub is not null ? $"{category} > {sub}" : (category ?? sub ?? "");

    private static string Label(List<Target> targets, string errorKey)
    {
        var key = errorKey switch
        {
            "supplierId" => "supplier", "categoryId" => "category", "ownerId" => "owner", "backupOwnerId" => "backupOwner", "agreementTypeId" => "agreementType",
            "businessUnitIds" => "businessUnit", "siteIds" => "site", "entityIds" => "entity", "functionIds" => "function", "currencyCode" => "currency",
            "isEvergreen" => "evergreen", _ => errorKey,
        };
        return targets.FirstOrDefault(t => t.Key == key || t.Key == $"field.{key}")?.Label ?? errorKey;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static object Summary(List<PlanRow> plan, Dictionary<string, string> newSuppliers, PlanExtras extras) => new
    {
        effective = plan.Count(r => r.Outcome == "Effective"), ended = plan.Count(r => r.Outcome == "Ended"),
        already = plan.Count(r => r.Outcome == "Already"), refused = plan.Count(r => r.Outcome == "Refused"), leftOut = plan.Count(r => r.Outcome == "LeftOut"),
        newSuppliers = newSuppliers.Values.Order(),
        // What to add under Admin, Lists first, grouped by list (the Lists sheet download has the same).
        missing = extras.Missing.GroupBy(m => m.List).Select(g => new { list = g.Key, names = g.Select(m => m.Parent.Length > 0 ? $"{m.Parent} > {m.Name}" : m.Name) }),
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
                // A remembered match of Medius's Extension Type to Evergreen was a mistake (D-120): suggest Renews automatically instead.
                if (saved.TryGetValue(n, out var key) && !(key == "evergreen" && n.Contains("extension")) && targets.Any(t => t.Key == key) && used.Add(key)) return key;
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

        // Step 1a: the old values in each drop-down column, to translate to JBS Contracts list values (D-112). Nothing changes.
        // With download=translations it gives the translations as a sheet (to use again on Production); a translations sheet sent
        // as translationsFile is read back in.
        app.MapPost("/api/import/legacy/values", async (HttpRequest request, CurrentUser current, Db db) => await AdminAsync(current, db, async me =>
        {
            if (!request.HasFormContentType) return ApiResults.Invalid("Choose the spreadsheet to import.", new() { ["file"] = "Choose the spreadsheet to import." });
            var form = await request.ReadFormAsync();
            var options = ReadOptions(form);
            await using var c = await db.OpenAsync();
            var saved = Translations.From(await SavedTranslationsAsync(c, null));
            // What's on screen wins over what's remembered.
            foreach (var (field, values) in options.Translations?.ToDictionary() ?? [])
                foreach (var (old, to) in values) saved.Set(field, old, to);
            // A translations sheet (Download translations, edited) wins over both: the person chose to use it.
            if (form.Files.GetFile("translationsFile") is { Length: > 0 } tf)
            {
                await using var ts = tf.OpenReadStream();
                var mem = new MemoryStream();
                await ts.CopyToAsync(mem);
                mem.Position = 0;
                var (trRows, _, trProblem) = Excel.Read(mem, ["Field", "Old value", "JBS Contracts value"], ["Field", "Old value"]);
                if (trProblem is not null) return ApiResults.Invalid(trProblem, new() { ["translationsFile"] = trProblem });
                foreach (var row in trRows)
                {
                    var field = TranslatedFields.FirstOrDefault(f => string.Equals(f.Label, Clean(row.Text("Field")), StringComparison.OrdinalIgnoreCase));
                    if (field.Key is null || Clean(row.Text("Old value")) is not { } old) continue;
                    var to = Clean(row.Text("JBS Contracts value")) ?? "";
                    saved.Set(field.Key, old, string.Equals(to, LeaveBlank, StringComparison.OrdinalIgnoreCase) ? "" : to);
                }
            }
            if (form["download"] == "translations")
            {
                var rowsOut = saved.ToDictionary().SelectMany(f => f.Value.Select(v => new object?[]
                    { TranslatedFields.First(t => t.Key == f.Key).Label, v.Key, v.Value.Length == 0 ? LeaveBlank : v.Value }))
                    .OrderBy(r => (string)r[0]!).ThenBy(r => (string)r[1]!);
                var bytes = Excel.Write("Translations", [new("Field", null, 18), new("Old value", null, 40), new("JBS Contracts value", "A list value's name, Category > Subcategory, or (leave blank).", 40)], rowsOut);
                return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "jbs-contracts-import-translations.xlsx");
            }

            var (book, _, problem) = await SheetAsync(form);
            if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });
            string? Column(string key) => options.Mapping.FirstOrDefault(m => m.Value == key).Key;
            var wanted = TranslatedFields.Select(f => f.Key).Append("subcategory").Append("status").Select(k => (k, col: Column(k))).Where(x => x.col is not null).ToDictionary(x => x.k, x => x.col!);
            var (rows, _, readProblem) = Excel.Read(book!, wanted.Values.Distinct().ToList(), []);
            if (readProblem is not null) return ApiResults.Invalid(readProblem, new() { ["file"] = readProblem });
            string? Cell(Excel.Row row, string key) => wanted.TryGetValue(key, out var col) ? Clean(row.Text(col)) : null;

            var cats = (await c.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category WHERE IsActive = 1 ORDER BY SortOrder, Name")).ToList();
            var items = (await c.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem WHERE IsActive = 1 ORDER BY SortOrder, Value")).ToList();
            var choices = new Dictionary<string, List<string>>
            {
                // Every category and subcategory, as Family > Category (> Subcategory), D-115.
                ["Category"] = new CategoryTree(cats).Pickable().Where(p => p.IsActive).Select(p => p.Path).ToList(),
            };
            foreach (var (_, list, _) in TranslatedFields.Where(f => f.List is not ("Category" or "Owner")))
                choices[list] = items.Where(i => i.ListName == list).Select(i => i.Value).ToList();
            choices["Owner"] = (await c.QueryAsync<string>("SELECT Name FROM contracts.Person WHERE IsActive = 1 AND IsContractOwner = 1 ORDER BY Name")).ToList();

            var counts = TranslatedFields.ToDictionary(f => f.Key, _ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            foreach (var row in rows)
            {
                if (options.LeaveOutArchived && Cell(row, "status") is { } st && System.Text.RegularExpressions.Regex.IsMatch(st, @"\barchived\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                foreach (var (key, list, _) in TranslatedFields)
                {
                    var value = key == "category" ? (Cell(row, "category") ?? Cell(row, "subcategory")) is null ? null : CategoryKey(Cell(row, "category"), Cell(row, "subcategory")) : Cell(row, key);
                    foreach (var part in SplitCell(key, list, value, items, saved)) counts[key][part] = counts[key].GetValueOrDefault(part) + 1;
                }
            }
            return Results.Ok(new
            {
                fields = TranslatedFields.Where(f => wanted.ContainsKey(f.Key)).Select(f => new
                {
                    key = f.Key, label = f.Key == "category" && wanted.ContainsKey("subcategory") ? "Category > Subcategory" : f.Label, list = f.List,
                    values = counts[f.Key].OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v =>
                    {
                        // Remembered (or on screen), else the same name already in the list (D-118: ignoring case, spaces and
                        // punctuation, or by the same code at the start, as with plants), else not chosen yet.
                        string? to = saved.TryGet(f.Key, v.Key, out var t) ? t : Similar(v.Key, choices[f.List]);
                        return new { value = v.Key, count = v.Value, to, remembered = saved.TryGet(f.Key, v.Key, out _) };
                    }),
                }),
                choices,
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
            var extras = new PlanExtras();
            var (plan, newSuppliers, planProblem) = await PlanAsync(c, (SqlTransaction)tx, me, settings, book!, ReadOptions(form), DateOnly.FromDateTime(HomeEndpoints.Today(clock)), extras);
            await tx.RollbackAsync();
            if (planProblem is not null) return ApiResults.Invalid(planProblem, new() { ["mapping"] = planProblem });
            if (form["download"] == "missing")
            {
                // The missing list items as a sheet for Admin, Lists, Import from Excel: parents first.
                var sheet = Excel.Write("Lists", [new("List", null, 22), new("Name", null, 34), new("Parent category", null, 28)],
                    extras.Missing.OrderBy(m => m.List).ThenBy(m => m.Parent.Length > 0).ThenBy(m => m.Parent).ThenBy(m => m.Name)
                        .Select(m => new object?[] { m.List, m.Name, m.Parent.Length > 0 ? m.Parent : null }));
                return Results.File(sheet, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "jbs-contracts-lists-to-add.xlsx");
            }
            return Results.Ok(Summary(plan, newSuppliers, extras));
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
            var extras = new PlanExtras();
            var (plan, newSuppliers, planProblem) = await PlanAsync(c, tx, me, settings, book!, options, DateOnly.FromDateTime(HomeEndpoints.Today(clock)), extras);
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
            // And the translations (D-112), added to what was remembered.
            if (options.Translations is { } chosen)
            {
                var all = Translations.From(await SavedTranslationsAsync(c, tx));
                foreach (var (field, values) in chosen.ToDictionary()) foreach (var (old, to) in values) all.Set(field, old, to);
                await c.ExecuteAsync("""
                    MERGE contracts.ImportMapping AS t USING (SELECT N'legacy-values' AS Name) AS s ON t.Name = s.Name
                    WHEN MATCHED THEN UPDATE SET MappingJson = @json, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHEN NOT MATCHED THEN INSERT (Name, MappingJson, UpdatedById) VALUES (N'legacy-values', @json, @me);
                    """, new { json = JsonSerializer.Serialize(all.ToDictionary()), me = me.Id }, tx);
            }

            var via = $" (imported from {fileName})";
            var supplierIds = new Dictionary<string, int>();
            foreach (var name in newSuppliers.Values)
            {
                var vendor = extras.VendorNumbers.GetValueOrDefault(name.Trim());
                var id = await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Supplier WHERE NameKey = UPPER(LTRIM(RTRIM(@name)))", new { name }, tx)
                    ?? await c.ExecuteScalarAsync<int>("INSERT INTO contracts.Supplier (Name, VendorNumber, CreatedById, UpdatedById) OUTPUT INSERTED.Id VALUES (@name, @vendor, @me, @me)",
                        new { name, vendor, me = me.Id }, tx);
                await audit.WriteAsync(c, tx, me.AsActor(), "supplier.added", "Supplier", id,
                    $"{me.Name} added the supplier {name}{(vendor is null ? "" : $" (SAP vendor number {vendor})")}{via}.");
                supplierIds[name.Trim().ToUpperInvariant()] = id;
            }
            // Existing suppliers without an SAP vendor number get the one from the file (D-109).
            foreach (var (name, vendor) in extras.VendorNumbers.Where(v => !newSuppliers.ContainsKey(v.Key.ToUpperInvariant())))
            {
                var sid = await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Supplier WHERE NameKey = UPPER(LTRIM(RTRIM(@name))) AND VendorNumber IS NULL", new { name }, tx);
                if (sid is null) continue;
                await c.ExecuteAsync("UPDATE contracts.Supplier SET VendorNumber = @vendor, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @sid", new { vendor, me = me.Id, sid }, tx);
                await audit.WriteAsync(c, tx, me.AsActor(), "supplier.changed", "Supplier", sid, $"{me.Name} set {name}'s SAP vendor number to {vendor}{via}.");
            }
            var brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
            var created = new List<string>();
            foreach (var r in ready)
            {
                var body = r.NewSupplier is { } ns ? r.Body! with { SupplierId = supplierIds[ns.Trim().ToUpperInvariant()] } : r.Body!;
                var (id, reference, errors) = await ContractEndpoints.CreateDraftAsync(c, tx, audit, me, body, $"{via.TrimEnd(')')}, row {r.Row})", r.Reference);
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
                    $"{me.Name} imported {reference}{(reference == r.LegacyReference ? "" : $" (old reference {r.LegacyReference})")} from {fileName}, row {r.Row}, as {(r.Outcome == "Ended" ? $"Ended on {r.EndedOn:d MMM yyyy}" : "Effective")}; " +
                    $"effective {r.EffectiveOn:d MMM yyyy}, registered {r.RegisteredOn:d MMM yyyy}.",
                    new { legacyReference = r.LegacyReference, fileName, row = r.Row, stage = r.Outcome, effectiveOn = r.EffectiveOn, registeredOn = r.RegisteredOn, endedOn = r.EndedOn }, contractId: id);
                created.Add(reference);
            }
            await ContractNumbers.CatchUpAsync(c, tx); // new contracts carry on after the highest number
            await audit.WriteAsync(c, tx, me.AsActor(), "import.contracts", "Import", null,
                $"{me.Name} imported {created.Count:N0} old contracts from {fileName} ({plan.Count(x => x.Outcome == "Effective"):N0} Effective, {plan.Count(x => x.Outcome == "Ended"):N0} Ended; " +
                $"{plan.Count(x => x.Outcome == "Already"):N0} already there, {plan.Count(x => x.Outcome == "Refused"):N0} not imported; {newSuppliers.Count:N0} new suppliers).");
            await tx.CommitAsync();
            return Results.Ok(new { imported = created.Count, first = created.FirstOrDefault(), last = created.LastOrDefault(), summary = Summary(plan, newSuppliers, extras) });
        }));

        // Step 2: files from a ZIP, matched to contracts by the old (or JBSAUS) reference in the file's name or folders.
        app.MapPost("/api/import/files/preview", async (HttpRequest request, CurrentUser current, Db db) => await AdminAsync(current, db, async me =>
        {
            var (zip, problem) = await ZipAsync(request);
            if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });
            await using var c = await db.OpenAsync();
            using (zip)
            {
                if (await KindsProblemAsync(c, null, zip!) is { } kindProblem) return ApiResults.Invalid(kindProblem, new() { ["kind"] = kindProblem });
                var plan = await PlanFilesAsync(c, null, zip!);
                return Results.Ok(FilesSummary(plan));
            }
        }));

        app.MapPost("/api/import/files/import", async (HttpRequest request, CurrentUser current, Db db, AuditLog audit, AppSettings settings) => await AdminAsync(current, db, async me =>
        {
            if (settings.DocumentsDirectory is not { } root)
                return ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "no-storage", "Documents can't be saved yet because the server has no data folder set. Tell your admin.");
            var (zip, problem) = await ZipAsync(request);
            if (problem is not null) return ApiResults.Invalid(problem, new() { ["file"] = problem });
            using (zip)
            {
                await using var c = await db.OpenAsync();
                await using var tx = await Db.BeginWriteAsync(c);
                if (await KindsProblemAsync(c, tx, zip!) is { } kindProblem) return ApiResults.Invalid(kindProblem, new() { ["kind"] = kindProblem });
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
                            new { id = f.ContractId, kind = f.Kind, title, me = me.Id }, tx);
                        var versionId = await c.ExecuteScalarAsync<int>("""
                            INSERT INTO contracts.DocumentVersion (DocumentId, VersionNumber, FileName, ContentType, SizeBytes, Sha256, StoredName, UploadedById)
                            OUTPUT INSERTED.Id VALUES (@documentId, 1, @fileName, @type, @size, @sha, @stored, @me)
                            """, new { documentId, fileName = stored.FileName, type = stored.ContentType, size = stored.Size, sha = stored.Sha256, stored = stored.StoredName, me = me.Id }, tx);
                        await audit.WriteAsync(c, tx, me.AsActor(), "document.imported", "Document", documentId,
                            $"{me.Name} imported \"{title}\" ({f.KindName}) to {f.Reference} from {f.Path}.",
                            new { versionId, f.Path, stored.FileName, stored.Size, stored.Sha256, f.Kind, kindFrom = f.KindWhy }, contractId: f.ContractId);
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
        public string Kind { get; set; } = "";
        public string KindName { get; set; } = "";
        public string? KindWhy { get; set; }
        internal int ContractId { get; set; }
        internal ZipArchiveEntry? Entry { get; set; }
    }

    /// <summary>
    /// The ZIP, the kind for files whose kind can't be worked out, the admin's own choices (file path to kind), and the ZIP's own name
    /// (Medius exports one ZIP per contract, named with its number, D-123).
    /// </summary>
    private sealed record ZipUpload(ZipArchive Zip, string DefaultKind, Dictionary<string, string> Chosen, string ZipName = "") : IDisposable
    {
        public void Dispose() => Zip.Dispose();
    }

    private static async Task<(ZipUpload? Upload, string? Problem)> ZipAsync(HttpRequest request)
    {
        if (!request.HasFormContentType) return (null, "Choose a ZIP file.");
        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        var kind = form["kind"].ToString();
        if (kind.Length == 0) return (null, "Choose what kind of document a file is when its name doesn't say.");
        Dictionary<string, string> chosen = [];
        if (form["kinds"].ToString() is { Length: > 0 } json)
        {
            try { chosen = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
            catch (JsonException) { return (null, "The kinds chosen for the files couldn't be read. Check the files again."); }

        }
        if (file is null || file.Length == 0) return (null, "Choose a ZIP file.");
        if (!System.IO.Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return (null, "Choose a .zip file.");
        if (file.Length > DocumentEndpoints.MaxBytes) return (null, "That ZIP is over 25 MB. Split the files into several ZIPs and import them one after the other.");
        var memory = new MemoryStream();
        await file.CopyToAsync(memory);
        memory.Position = 0;
        try { return (new ZipUpload(new ZipArchive(memory, ZipArchiveMode.Read), kind, chosen, System.IO.Path.GetFileNameWithoutExtension(file.FileName)), null); }
        catch (InvalidDataException) { return (null, "That file isn't a ZIP that can be opened."); }
    }

    /// <summary>The default and chosen kinds must be built-in, or added by an admin and not hidden (D-98).</summary>
    private static async Task<string?> KindsProblemAsync(SqlConnection c, SqlTransaction? tx, ZipUpload upload)
    {
        if (!await DocumentEndpoints.IsChoosableKindAsync(c, tx, upload.DefaultKind)) return "Choose what kind of document a file is when its name doesn't say.";
        foreach (var kind in upload.Chosen.Values.Distinct())
            if (!await DocumentEndpoints.IsChoosableKindAsync(c, tx, kind)) return "One of the kinds chosen isn't a document kind any more. Check the files again.";
        return null;
    }

    // What a file is, from words in its folders or name (D-95). Checked in this order, so a "signed variation" is a Variation and a
    // "signed off" cover page is an internal sign-off, not a signed copy. Whole words only ("unsigned" isn't "signed").
    private static readonly (string Kind, string[] Words)[] KindWords =
    [
        ("InternalSignOff", ["cover page", "cover sheet", "coversheet", "sign off", "signoff", "signed off", "internal approval", "approval form", "sign off form"]),
        ("Variation", ["variation", "variations", "amendment", "amendments", "addendum", "deed of variation"]),
        ("Draft", ["draft", "drafts"]),
        ("LegalMarkup", ["markup", "mark up", "marked up", "redline", "redlined", "tracked changes"]),
        ("SignedCopy", ["signed", "executed", "fully executed", "countersigned", "counter signed"]),
        ("Schedule", ["schedule", "schedules", "annexure", "annexures", "appendix", "attachment"]),
        ("Correspondence", ["correspondence", "email", "emails", "letter", "letters"]),
        ("Supporting", ["supporting", "support", "quote", "quotes", "invoice", "invoices", "certificate"]),
    ];

    /// <summary>Normalises "Deed_of-Variation(signed)" or "SignedAgreement" to " deed of variation signed agreement " for whole-word matching.</summary>
    private static string Words(string text)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(text, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
        return " " + System.Text.RegularExpressions.Regex.Replace(spaced, "[^a-z0-9]+", " ").Trim() + " ";
    }

    private static string? KindFrom(string text)
    {
        var words = Words(text);
        foreach (var (kind, list) in KindWords)
            if (list.Any(w => words.Contains($" {w} ", StringComparison.Ordinal))) return kind;
        return null;
    }

    /// <summary>The kind for one file: the admin's choice, else its folders (nearest first), else its name, else the default.</summary>
    internal static (string Kind, string Why) KindFor(string path, string defaultKind, IReadOnlyDictionary<string, string> chosen)
    {
        if (chosen.TryGetValue(path, out var mine)) return (mine, "you chose");
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 2; i >= 0; i--)
            if (KindFrom(parts[i]) is { } fromFolder) return (fromFolder, $"folder \"{parts[i]}\"");
        var name = parts.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(parts[^1]) : path;
        if (KindFrom(name) is { } fromName) return (fromName, "its name");
        if (System.IO.Path.GetExtension(path).Equals(".msg", StringComparison.OrdinalIgnoreCase)) return ("Correspondence", "an Outlook email");
        return (defaultKind, "your default");
    }

    private static async Task<List<FilePlan>> PlanFilesAsync(SqlConnection c, SqlTransaction? tx, ZipUpload upload)
    {
        var zip = upload.Zip;
        var kindNames = await DocumentEndpoints.KindNamesAsync(c, tx);
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
            var (kind, why) = KindFor(entry.FullName, upload.DefaultKind, upload.Chosen);
            var f = new FilePlan { Path = entry.FullName, FileName = name, Size = entry.Length, Entry = entry, Kind = kind, KindName = kindNames.GetValueOrDefault(kind, kind), KindWhy = why };
            plan.Add(f);
            if (plan.Count > MaxEntries) { f.Outcome = "Refused"; f.Reason = $"More than {MaxEntries:N0} files in one ZIP; split it."; continue; }
            unpacked += entry.Length;
            if (!DocumentEndpoints.AllowedType(name)) { f.Outcome = "Refused"; f.Reason = "That kind of file can't be added (PDF, Word, Excel, Outlook email, text, CSV, PNG or JPG only)."; continue; }
            if (entry.Length > DocumentEndpoints.MaxBytes) { f.Outcome = "Refused"; f.Reason = "Over 25 MB."; continue; }
            if (unpacked > MaxUnpacked) { f.Outcome = "Refused"; f.Reason = "The ZIP unpacks to more than 1 GB; split it."; continue; }

            // A reference in the file's own name or folders, else in the ZIP's name (Medius: one ZIP per contract, D-123).
            var path = entry.FullName.ToUpperInvariant();
            var zipName = upload.ZipName.ToUpperInvariant();
            var match = keys.FirstOrDefault(k => Contains(path, k.Key));
            if (match.Key is null && zipName.Length > 0) match = keys.FirstOrDefault(k => Contains(zipName, k.Key));
            if (match.Key is null) { f.Outcome = "Unmatched"; f.Reason = "No old or JBSAUS reference found in its name, its folders or the ZIP's name."; continue; }
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
        files = plan.Select(f => new { f.Path, f.Size, f.Outcome, f.Reference, f.Title, f.Reason, f.Kind, f.KindName, f.KindWhy }),
    };
}
