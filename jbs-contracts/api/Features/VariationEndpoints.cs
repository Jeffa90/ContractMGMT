using System.Globalization;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using static JbsContracts.Api.Features.LegalEndpoints;

namespace JbsContracts.Api.Features;

public sealed record VariationAction(string? RowVersion, string? Reason = null, string? Note = null, bool? NeedsLegalReview = null,
    Dictionary<string, JsonElement>? Changes = null, int[]? DocumentVersionIds = null);

/// <summary>
/// Variations: the proper way to change an Effective contract (product-spec section 7; D-11, D-74 to D-78). The owner gives a reason and
/// changes the allowed fields (old and new side by side), optionally attaches the signed variation document, sends it to Legal (ticked by
/// default; the same back-and-forth), then for approval. When approved, the changes apply to the contract in one step, audited; the
/// registered date doesn't change. Sent back or withdrawn, nothing changes. One open Variation per contract.
/// </summary>
public static class VariationEndpoints
{
    public static readonly Dictionary<string, string> StageNames = new()
    {
        ["Draft"] = "Draft", ["LegalReview"] = "Legal review", ["Approval"] = "Approval", ["Approved"] = "Approved", ["Withdrawn"] = "Withdrawn",
    };

    private static readonly CultureInfo AU = CultureInfo.GetCultureInfo("en-AU");

    /// <summary>Built-in fields a Variation can change (spec section 7). Title, supplier, category and the like never change: start a new contract.</summary>
    private static readonly string[] BuiltIns = ["endDate", "evergreen", "autoRenews", "renewalTermMonths", "noticePeriodDays", "reviewDate", "value", "currencyCode", "businessUnit", "site", "entity", "function"];

    public sealed class VariationRow
    {
        public int Id { get; set; }
        public int ContractId { get; set; }
        public int Number { get; set; }
        public string Reason { get; set; } = "";
        public string ChangesJson { get; set; } = "{}";
        public bool NeedsLegalReview { get; set; }
        public string Stage { get; set; } = "Draft";
        public DateTime StageSinceUtc { get; set; }
        public string? WhoseTurn { get; set; }
        public int? LegalPersonId { get; set; }
        public string? LegalPersonName { get; set; }
        public DateTime? TurnSinceUtc { get; set; }
        public string? LegalOutcome { get; set; }
        public DateTime? LegalClearedUtc { get; set; }
        public string? LegalClearedByName { get; set; }
        public string? ClosedReason { get; set; }
        public string? AppliedChangesJson { get; set; }
        public DateTime CreatedUtc { get; set; }
        public int CreatedById { get; set; }
        public string CreatedByName { get; set; } = "";
        public byte[] RowVersion { get; set; } = [];

        public Dictionary<string, JsonElement> Changes => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ChangesJson) ?? [];
        public bool IsOpen => Stage is "Draft" or "LegalReview" or "Approval";
    }

    private const string SelectVariation = """
        SELECT v.*, lp.Name AS LegalPersonName, lc.Name AS LegalClearedByName, cb.Name AS CreatedByName
        FROM contracts.Variation v LEFT JOIN contracts.Person lp ON lp.Id = v.LegalPersonId LEFT JOIN contracts.Person lc ON lc.Id = v.LegalClearedById
        JOIN contracts.Person cb ON cb.Id = v.CreatedById
        """;

    private static string Ref(ContractRow c, VariationRow v) => $"{c.Reference} Variation {v.Number}";
    private static string Name(VariationRow v) => $"Variation {v.Number}";

    public static async Task<IEnumerable<object>> ListAsync(SqlConnection c, int contractId) =>
        (await c.QueryAsync<VariationRow>($"{SelectVariation} WHERE v.ContractId = @contractId ORDER BY v.Number DESC", new { contractId }))
        .Select(v => (object)new
        {
            v.Id, v.Number, v.Reason, v.Stage, StageName = StageNames[v.Stage], StageSinceUtc = DateTime.SpecifyKind(v.StageSinceUtc, DateTimeKind.Utc), v.CreatedByName,
            Applied = v.AppliedChangesJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(v.AppliedChangesJson),
        });

    // ---------------------------------------------------------------- Fields a Variation can change

    public sealed record Allowed(string Key, string Label, string Kind, List<ChoiceOption> Choices, FieldRow? Added);

    private static async Task<List<Allowed>> AllowedAsync(SqlConnection c, SqlTransaction? tx, List<FieldRow> defs)
    {
        var items = (await c.QueryAsync<(int Id, string ListName, string Value, bool IsActive)>("SELECT Id, ListName, Value, IsActive FROM contracts.ListItem ORDER BY SortOrder, Value", transaction: tx)).ToList();
        List<ChoiceOption> Options(string list, bool byValue = false) =>
            items.Where(i => i.ListName == list).Select(i => new ChoiceOption { Key = byValue ? i.Value : i.Id.ToString(CultureInfo.InvariantCulture), Label = i.Value, Hidden = !i.IsActive }).ToList();
        var byKey = defs.ToDictionary(f => f.Key);
        string Label(string key, string fallback) => byKey.TryGetValue(key, out var f) ? f.Label : fallback;
        bool Shown(string key) => !byKey.TryGetValue(key, out var f) || !f.IsHidden;
        var result = new List<Allowed>();
        void Add(string key, string kind, string fallback, List<ChoiceOption>? choices = null, string? shownWith = null)
        {
            if (Shown(shownWith ?? key)) result.Add(new Allowed(key, Label(key, fallback), kind, choices ?? [], null));
        }
        Add("endDate", "Date", "End date");
        Add("evergreen", "YesNo", "Evergreen (no end date)");
        Add("autoRenews", "YesNo", "Renews automatically");
        Add("renewalTermMonths", "WholeNumber", "Renewal term (months)");
        Add("noticePeriodDays", "WholeNumber", "Notice period (days)");
        Add("reviewDate", "Date", "Review date");
        Add("value", "Money", "Value");
        Add("currencyCode", "Choice", "Currency", Options("Currency", byValue: true), shownWith: "value");
        Add("businessUnit", "Choice", "Business unit", Options("BusinessUnit"));
        Add("site", "Choice", "Site", Options("Site"));
        Add("entity", "Choice", "Entity", Options("Entity"));
        Add("function", "Choice", "Function", Options("Function"));
        result.AddRange(defs.Where(f => !f.IsBuiltIn && !f.IsHidden && f.AfterEffective == "Variation").Select(f => new Allowed(f.Key, f.Label, f.Kind, f.Choices, f)));
        return result;
    }

    /// <summary>The contract's current value of a built-in field, in the form's shape.</summary>
    private static object? Current(ContractRow c, string key) => key switch
    {
        "endDate" => c.EndDate is { } d ? DateOnly.FromDateTime(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        "evergreen" => c.IsEvergreen,
        "autoRenews" => c.AutoRenews,
        "renewalTermMonths" => c.RenewalTermMonths,
        "noticePeriodDays" => c.NoticePeriodDays,
        "reviewDate" => c.ReviewDate is { } r ? DateOnly.FromDateTime(r).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        "value" => c.Value,
        "currencyCode" => c.CurrencyCode,
        "businessUnit" => c.BusinessUnitId?.ToString(CultureInfo.InvariantCulture),
        "site" => c.SiteId?.ToString(CultureInfo.InvariantCulture),
        "entity" => c.EntityId?.ToString(CultureInfo.InvariantCulture),
        "function" => c.FunctionId?.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Canon(object? v) => v switch
    {
        null => "",
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
        JsonElement { ValueKind: JsonValueKind.String } j => j.GetString() ?? "",
        JsonElement { ValueKind: JsonValueKind.Number } j => j.GetDecimal().ToString("0.############", CultureInfo.InvariantCulture),
        JsonElement j => j.GetRawText(),
        decimal m => m.ToString("0.############", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string[] keys => JsonSerializer.Serialize(keys),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private sealed record Plan(SaveContract Body, List<(string Key, string Label, string Old, string New)> Changes, Dictionary<string, string> Errors, Dictionary<string, JsonElement> Kept);

    /// <summary>
    /// Works out what the proposed changes mean: drops anything unchanged, checks every value as the contract form would, and describes
    /// each change in plain words (old and new). The body is the whole contract with the changes in, ready to apply.
    /// </summary>
    private static async Task<Plan> PlanAsync(SqlConnection c, SqlTransaction? tx, ContractRow k, Dictionary<string, JsonElement> proposed)
    {
        var defs = await Fields.LoadAsync(c, tx);
        var allowed = (await AllowedAsync(c, tx, defs)).ToDictionary(a => a.Key);
        var errors = new Dictionary<string, string>();
        var kept = new Dictionary<string, JsonElement>();
        var addedValues = (await Fields.ReadAsync(c, tx, [k.Id], defs))[k.Id];
        foreach (var (key, json) in proposed)
        {
            if (!allowed.TryGetValue(key, out var a)) { errors[$"change.{key}"] = "This can't change through a Variation."; continue; }
            var current = a.Added is null ? Current(k, key) : addedValues.TryGetValue(key, out var cur) ? cur.Value : null;
            if (Canon(json) != Canon(current)) kept[key] = json;
        }

        static DateOnly? DateOf(JsonElement j) => j.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(j.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        T? Get<T>(string key, Func<JsonElement, T?> read, T? fallback)
        {
            if (!kept.TryGetValue(key, out var j)) return fallback;
            if (j.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (j.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(j.GetString()))) return default;
            try
            {
                var v = read(j);
                if (v is null) errors[$"change.{key}"] = "That value doesn't fit this field.";
                return v;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { errors[$"change.{key}"] = "That value doesn't fit this field."; return default; }
        }
        static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
        var end = kept.ContainsKey("endDate") ? Get<DateOnly?>("endDate", DateOf, null) : D(k.EndDate);
        var review = kept.ContainsKey("reviewDate") ? Get<DateOnly?>("reviewDate", DateOf, null) : D(k.ReviewDate);
        var evergreen = kept.ContainsKey("evergreen") ? Get<bool?>("evergreen", j => j.GetBoolean(), null) ?? false : k.IsEvergreen;
        var notice = kept.ContainsKey("noticePeriodDays") ? Get<int?>("noticePeriodDays", j => j.ValueKind == JsonValueKind.Number ? j.GetInt32() : int.Parse(j.GetString()!, CultureInfo.InvariantCulture), null) : k.NoticePeriodDays;
        var value = kept.ContainsKey("value") ? Get<decimal?>("value", j => j.ValueKind == JsonValueKind.Number ? j.GetDecimal() : decimal.Parse(j.GetString()!.Replace("$", "").Replace(",", ""), CultureInfo.InvariantCulture), null) : k.Value;
        var currency = kept.ContainsKey("currencyCode") ? Get<string>("currencyCode", j => j.GetString(), null) ?? k.CurrencyCode : k.CurrencyCode;
        var businessUnit = kept.ContainsKey("businessUnit") ? Get<int?>("businessUnit", j => int.Parse(j.GetString()!, CultureInfo.InvariantCulture), null) : k.BusinessUnitId;
        var site = kept.ContainsKey("site") ? Get<int?>("site", j => int.Parse(j.GetString()!, CultureInfo.InvariantCulture), null) : k.SiteId;
        var entity = kept.ContainsKey("entity") ? Get<int?>("entity", j => int.Parse(j.GetString()!, CultureInfo.InvariantCulture), null) : k.EntityId;
        var function = kept.ContainsKey("function") ? Get<int?>("function", j => int.Parse(j.GetString()!, CultureInfo.InvariantCulture), null) : k.FunctionId;
        var autoRenews = kept.ContainsKey("autoRenews") ? Get<bool?>("autoRenews", j => j.GetBoolean(), null) ?? false : k.AutoRenews;
        var term = kept.ContainsKey("renewalTermMonths") ? Get<int?>("renewalTermMonths", j => j.ValueKind == JsonValueKind.Number ? j.GetInt32() : int.Parse(j.GetString()!, CultureInfo.InvariantCulture), null) : k.RenewalTermMonths;
        var addedChanges = kept.Where(p => !BuiltIns.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);

        var body = new SaveContract(k.Title, k.Description, k.SupplierId, k.CategoryId, k.AgreementTypeId, businessUnit, site, k.OwnerId, k.BackupOwnerId, k.IsConfidential,
            value, currency, k.SapOutlineAgreement, D(k.StartDate), D(k.EffectiveDate), end, evergreen, notice, review, k.NeedsLegalReview, null, addedChanges,
            entity, function, autoRenews, term,
            kept.ContainsKey("reviewDate") ? null : k.ReviewMonthsBeforeEnd); // a review date chosen here replaces a period; a period follows a new end date
        if (errors.Count == 0)
        {
            var map = new Dictionary<string, string> { ["businessUnitId"] = "businessUnit", ["siteId"] = "site", ["entityId"] = "entity", ["functionId"] = "function" };
            foreach (var (key, message) in await ContractEndpoints.CheckAsync(c, tx!, body, k) ?? [])
                errors[$"change.{map.GetValueOrDefault(key, key)}"] = message;
        }
        var (parsed, fieldErrors) = await Fields.ParseAsync(c, tx!, addedChanges, defs);
        foreach (var (key, message) in fieldErrors) errors[$"change.{key[6..]}"] = message;

        // Plain words, old and new.
        var changes = new List<(string, string, string, string)>();
        var names = (await c.QueryAsync<(int Id, string Value)>("SELECT Id, Value FROM contracts.ListItem", transaction: tx)).ToDictionary(i => i.Id, i => i.Value);
        string Day(DateOnly? d) => d?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "none";
        string Money(decimal? m, string cur) => m is { } v ? $"${v.ToString("N2", AU)} {cur}" : "none";
        foreach (var key in kept.Keys)
        {
            var a = allowed[key];
            var (old, now) = key switch
            {
                "endDate" => (Day(D(k.EndDate)), Day(end)),
                "reviewDate" => (Day(D(k.ReviewDate)), Day(review)),
                "evergreen" => (k.IsEvergreen ? "Yes" : "No", evergreen ? "Yes" : "No"),
                "autoRenews" => (k.AutoRenews ? "Yes" : "No", autoRenews ? "Yes" : "No"),
                "renewalTermMonths" => (k.RenewalTermMonths is { } ot ? ContractEndpoints.Months(ot) : "none", term is { } nt ? ContractEndpoints.Months(nt) : "none"),
                "noticePeriodDays" => (k.NoticePeriodDays is { } o ? $"{o} days" : "none", notice is { } n ? $"{n} days" : "none"),
                "value" => (Money(k.Value, k.CurrencyCode), Money(value, currency)),
                "currencyCode" => (k.CurrencyCode, currency),
                "businessUnit" => (k.BusinessUnitId is { } b ? names.GetValueOrDefault(b, "") : "none", businessUnit is { } nb ? names.GetValueOrDefault(nb, "") : "none"),
                "site" => (k.SiteId is { } s ? names.GetValueOrDefault(s, "") : "none", site is { } ns ? names.GetValueOrDefault(ns, "") : "none"),
                "entity" => (k.EntityId is { } oe ? names.GetValueOrDefault(oe, "") : "none", entity is { } ne ? names.GetValueOrDefault(ne, "") : "none"),
                "function" => (k.FunctionId is { } of ? names.GetValueOrDefault(of, "") : "none", function is { } nf ? names.GetValueOrDefault(nf, "") : "none"),
                _ => (addedValues.TryGetValue(key, out var ov) && ov.Display.Length > 0 ? ov.Display : "none",
                      parsed.FirstOrDefault(p => p.Field.Key == key) is { } pv && !pv.IsEmpty ? await Fields.DisplayAsync(c, tx, pv) : "none"),
            };
            changes.Add((key, a.Label, old, now));
        }
        return new Plan(body, changes, errors, kept);
    }

    private static string Describe(IEnumerable<(string Key, string Label, string Old, string New)> changes) =>
        string.Join("; ", changes.Select(ch => $"{ch.Label} from {ch.Old} to {ch.New}"));

    // ---------------------------------------------------------------- Who can do what

    private static bool Involved(Person me, ContractRow c, VariationRow v) => v.CreatedById == me.Id || c.OwnerId == me.Id || c.BackupOwnerId == me.Id;

    private static List<string> ActionsFor(Person me, ContractRow c, VariationRow v)
    {
        var a = new List<string>();
        var ownersTurn = v.Stage == "LegalReview" && v.WhoseTurn == "Owner";
        var cleared = v.LegalClearedUtc is not null;
        if (me.IsContractOwner && ((v.Stage == "Draft" && !cleared) || ownersTurn)) a.Add("edit");
        if (me.IsContractOwner && ((v.Stage == "Draft" && (v.NeedsLegalReview || cleared)) || ownersTurn)) a.Add("sendToLegal");
        if (me.IsLegal && v.Stage == "LegalReview" && v.WhoseTurn == "Legal")
        {
            if (v.LegalPersonId != me.Id) a.Add("take");
            a.AddRange(["sendToOwner", "clear"]);
        }
        if (me.IsContractOwner && v.Stage == "Draft" && (!v.NeedsLegalReview || cleared)) a.Add("sendForApproval");
        if (me.IsApprover && v.Stage == "Approval" && !Involved(me, c, v)) a.AddRange(["approve", "sendBack"]);
        if (me.IsContractOwner && v.IsOpen) a.Add("withdraw");
        return a;
    }

    // ---------------------------------------------------------------- Endpoints

    private sealed record VCtx(SqlConnection C, SqlTransaction Tx, Person Me, ContractRow Contract, VariationRow Variation, AuditLog Audit)
    {
        public Task Log(string action, string summary, object? details) =>
            Audit.WriteAsync(C, Tx, Me.AsActor(), action, "Variation", Variation.Id, summary, details, contractId: Contract.Id);
    }

    /// <summary>Loads and locks the contract and the Variation, checks access and that the page isn't stale, runs the action, commits.</summary>
    private static async Task<IResult> ActAsync(int id, int vid, string? rowVersion, CurrentUser current, Db db, AuditLog audit, Func<VCtx, Task<IResult?>> action)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return refusal;
        await using var c = await db.OpenAsync();
        await using var tx = await Db.BeginWriteAsync(c);
        var contract = await ContractEndpoints.LoadForUpdateAsync(c, tx, id);
        if (contract is null || !ContractAccess.CanSee(me!, contract.IsConfidential, contract.OwnerId, contract.BackupOwnerId))
            return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
        await c.ExecuteAsync("SELECT Id FROM contracts.Variation WITH (UPDLOCK, ROWLOCK) WHERE Id = @vid", new { vid }, tx);
        var v = await c.QuerySingleOrDefaultAsync<VariationRow>($"{SelectVariation} WHERE v.Id = @vid AND v.ContractId = @id", new { vid, id }, tx);
        if (v is null) return ApiResults.NotFound("We couldn't find that Variation.");
        if (Convert.ToBase64String(v.RowVersion) != rowVersion)
            return ApiResults.Changed("Someone else changed this Variation a moment ago, so nothing was done. Reload it to see the latest, then try again.");
        var problem = await action(new VCtx(c, tx, me!, contract, v, audit));
        if (problem is not null) return problem;
        await tx.CommitAsync();
        var after = await c.ExecuteScalarAsync<byte[]>("SELECT RowVersion FROM contracts.Variation WHERE Id = @vid", new { vid });
        return Results.Ok(new { id = vid, rowVersion = Convert.ToBase64String(after!) });
    }

    public static void MapVariationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/{id:int}/variations");

        // Start a Variation: the owner gives the reason. One open Variation per contract.
        group.MapPost("/", (int id, VariationAction body, CurrentUser current, Db db, AuditLog audit) =>
            LegalEndpoints.ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can start a Variation.");
                if (c.Stage != "Effective") return ApiResults.NotAllowed("Only Effective contracts change through a Variation.");
                if (c.OpenVariationId is not null) return ApiResults.NotAllowed("This contract already has an open Variation. Finish or withdraw it first.");
                var reason = Clean(body.Reason);
                if (reason is null) return ApiResults.Invalid("Say why it's changing.", new() { ["reason"] = "Say why, for example \"Supplier agreed a 12-month extension\"." });
                if (reason.Length > 2000) return TooLong("reason", 2000);
                var number = await x.C.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(Number), 0) + 1 FROM contracts.Variation WHERE ContractId = @id", new { id }, x.Tx);
                var vid = await x.C.ExecuteScalarAsync<int>("""
                    INSERT INTO contracts.Variation (ContractId, Number, Reason, NeedsLegalReview, CreatedById) OUTPUT INSERTED.Id VALUES (@id, @number, @reason, @legal, @me)
                    """, new { id, number, reason, legal = body.NeedsLegalReview ?? true, me = x.Me.Id }, x.Tx);
                await x.Audit("variation.started", $"{x.Me.Name} started Variation {number} of {c.Reference}" + Quote(reason) + Stop(reason), new { variation = vid, reason });
                x.Result = new { variationId = vid };
                return null;
            }, audit));

        group.MapGet("/{vid:int}", async (int id, int vid, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var contract = await c.QuerySingleOrDefaultAsync<ContractRow>("SELECT k.*, o.Name AS OwnerName, b.Name AS BackupOwnerName, s.Name AS SupplierName FROM contracts.Contract k JOIN contracts.Person o ON o.Id = k.OwnerId LEFT JOIN contracts.Person b ON b.Id = k.BackupOwnerId LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId WHERE k.Id = @id", new { id });
            if (contract is null || !ContractAccess.CanSee(me!, contract.IsConfidential, contract.OwnerId, contract.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            var v = await c.QuerySingleOrDefaultAsync<VariationRow>($"{SelectVariation} WHERE v.Id = @vid AND v.ContractId = @id", new { vid, id });
            if (v is null) return ApiResults.NotFound("We couldn't find that Variation.");
            SqlTransaction? tx = null; // a read: no transaction
            var defs = await Fields.LoadAsync(c, tx);
            var allowed = await AllowedAsync(c, tx, defs);
            var added = (await Fields.ReadAsync(c, tx, [id], defs))[id];
            var plan = await PlanAsync(c, tx, contract, v.Changes);
            var builtInDisplay = await c.QueryAsync<(int Id, string Value)>("SELECT Id, Value FROM contracts.ListItem", transaction: tx);
            var names = builtInDisplay.ToDictionary(i => i.Id.ToString(CultureInfo.InvariantCulture), i => i.Value);
            string Display(Allowed a)
            {
                if (a.Added is not null) return added.TryGetValue(a.Key, out var x) ? x.Display : "";
                var cur = Current(contract, a.Key);
                return a.Key switch
                {
                    "endDate" or "reviewDate" => cur is string s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "",
                    "evergreen" or "autoRenews" => cur is true ? "Yes" : "No",
                    "renewalTermMonths" => cur is int t ? ContractEndpoints.Months(t) : "",
                    "noticePeriodDays" => cur is int n ? $"{n} days" : "",
                    "value" => cur is decimal m ? $"${m.ToString("N2", AU)}" : "",
                    "businessUnit" or "site" or "entity" or "function" => cur is string k ? names.GetValueOrDefault(k, "") : "",
                    _ => Convert.ToString(cur, CultureInfo.InvariantCulture) ?? "",
                };
            }
            var conversation = await ConversationAsync(c, id, vid);
            var documents = await DocumentEndpoints.ListAsync(c, id, vid);
            var approvals = await ApprovalEndpoints.HistoryAsync(c, id, vid);
            return Results.Ok(new
            {
                variation = new
                {
                    v.Id, v.Number, Reference = Ref(contract, v), v.Reason, v.Stage, StageName = StageNames[v.Stage], StageSinceUtc = DateTime.SpecifyKind(v.StageSinceUtc, DateTimeKind.Utc),
                    v.NeedsLegalReview, v.WhoseTurn, v.LegalPersonName, TurnSinceUtc = ContractEndpoints.Utc(v.TurnSinceUtc), v.LegalOutcome, v.LegalClearedByName,
                    LegalClearedUtc = ContractEndpoints.Utc(v.LegalClearedUtc), v.ClosedReason, v.CreatedByName, CreatedUtc = DateTime.SpecifyKind(v.CreatedUtc, DateTimeKind.Utc),
                    RowVersion = Convert.ToBase64String(v.RowVersion),
                },
                contract = new { contract.Id, contract.Reference, contract.Title, contract.Stage, contract.OwnerName, contract.BackupOwnerName, contract.SupplierName, contract.IsConfidential },
                fields = allowed.Select(a => new
                {
                    a.Key, a.Label, a.Kind, Choices = a.Choices, a.Added?.Help, a.Added?.MinValue, a.Added?.MaxValue, a.Added?.MaxLength,
                    Current = a.Added is null ? Current(contract, a.Key) : added.TryGetValue(a.Key, out var x) ? x.Value : null,
                    CurrentDisplay = Display(a),
                    Proposed = plan.Kept.TryGetValue(a.Key, out var p) ? (object?)p : null, Changed = plan.Kept.ContainsKey(a.Key),
                }),
                changes = plan.Changes.Select(ch => new { ch.Key, ch.Label, ch.Old, ch.New }),
                problems = plan.Errors,
                conversation, documents, approvals,
                actions = ActionsFor(me!, contract, v),
                cannotApproveReason = v.Stage == "Approval" && me!.IsApprover && Involved(me, contract, v)
                    ? "You can't approve this one because you raised the Variation, own the contract or are its backup. Another approver can." : null,
            });
        });

        // Owner: reason, Legal tick and the changes (while it's a draft that Legal hasn't cleared, or on the owner's turn).
        group.MapPut("/{vid:int}", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var v = x.Variation;
                if (!ActionsFor(x.Me, x.Contract, v).Contains("edit"))
                    return ApiResults.NotAllowed(v.LegalClearedUtc is not null && v.Stage == "Draft" ? "Legal has cleared it, so it can't change. Send it back to Legal to change it." : "You can't change this Variation now.");
                var reason = Clean(body.Reason) ?? v.Reason;
                if (reason.Length > 2000) return TooLong("reason", 2000);
                var plan = await PlanAsync(x.C, x.Tx, x.Contract, body.Changes ?? v.Changes);
                if (plan.Errors.Count > 0) return ApiResults.Invalid("Check the highlighted changes.", plan.Errors);
                var legal = v.Stage == "Draft" ? body.NeedsLegalReview ?? v.NeedsLegalReview : v.NeedsLegalReview;
                await x.C.ExecuteAsync("UPDATE contracts.Variation SET Reason = @reason, NeedsLegalReview = @legal, ChangesJson = @changes, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @vid",
                    new { reason, legal, changes = JsonSerializer.Serialize(plan.Kept), vid }, x.Tx);
                var before = await PlanAsync(x.C, x.Tx, x.Contract, v.Changes);
                if (Describe(before.Changes) != Describe(plan.Changes) || reason != v.Reason || legal != v.NeedsLegalReview)
                    await x.Log("variation.changed", $"{x.Me.Name} updated {Name(v)} of {x.Contract.Reference}: " + (plan.Changes.Count == 0 ? "no changes yet." : Describe(plan.Changes) + "."),
                        new { reason, needsLegalReview = legal, changes = plan.Changes.Select(ch => new { ch.Key, ch.Old, ch.New }) });
                return null;
            }));

        // Owner: to Legal (first time, or back on the owner's turn, or after clearance when something must change).
        group.MapPost("/{vid:int}/send-to-legal", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var (c, v) = (x.Contract, x.Variation);
                if (!ActionsFor(x.Me, c, v).Contains("sendToLegal")) return ApiResults.NotAllowed("You can't send this Variation to Legal now.");
                if (v.Changes.Count == 0) return ApiResults.Invalid("Add at least one change first.", new() { ["changes"] = "Add at least one change first." });
                var note = Clean(body.Note);
                if (note is { Length: > MaxNote }) return TooLong("note", MaxNote);
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Variation SET Stage = N'LegalReview', StageSinceUtc = CASE WHEN Stage = N'LegalReview' THEN StageSinceUtc ELSE SYSUTCDATETIME() END,
                        WhoseTurn = N'Legal', TurnSinceUtc = SYSUTCDATETIME(), LegalOutcome = NULL, LegalClearedUtc = NULL, LegalClearedById = NULL, UpdatedUtc = SYSUTCDATETIME()
                    WHERE Id = @vid
                    """, new { vid }, x.Tx);
                var (docs, docProblem) = await LegalEndpoints.CheckDocumentsAsync(x.C, x.Tx, id, vid, body.DocumentVersionIds);
                if (docProblem is not null) return docProblem;
                var number = await AddEntryAsync(x.C, x.Tx, x.Me, id, vid, "SentToLegal", "Legal", v.LegalPersonId, v.LegalPersonId is null ? "NotStarted" : "Reviewing", note, docs);
                await x.Log("variation.sent-to-legal", $"{x.Me.Name} sent {Name(v)} of {c.Reference} to Legal" + Quote(note) + Stop(note), new { note });
                var recipients = await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsLegal = 1 AND (@lp IS NULL OR Id = @lp) AND Id <> @me",
                    new { lp = v.LegalPersonId, me = x.Me.Id }, x.Tx);
                foreach (var p in recipients)
                    await EmailOutbox.QueueAsync(x.C, x.Tx, "legal-sent", p.Email, p.Name,
                        await EmailWording.RenderAsync(x.C, x.Tx, v.LegalPersonId is null ? "LegalNew" : "LegalReturned",
                            With(With(With(EmailWording.For(p.Name, c), "Reference", Ref(c, v)), "Sent by", x.Me.Name), "Note", note), VariationLink(settings, id, vid)),
                        personId: p.Id, contractId: id, dedupeKey: $"variation-legal:{vid}:{number}:{p.Id}");
                return null;
            }));

        group.MapPost("/{vid:int}/legal/take", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var v = x.Variation;
                if (!ActionsFor(x.Me, x.Contract, v).Contains("take")) return ApiResults.NotAllowed("You can't take this Variation now.");
                await x.C.ExecuteAsync("UPDATE contracts.Variation SET LegalPersonId = @me, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @vid", new { me = x.Me.Id, vid }, x.Tx);
                await AddEntryAsync(x.C, x.Tx, x.Me, id, vid, "Taken", "Legal", x.Me.Id, "Reviewing", null, []);
                await x.Log("variation.legal-taken", $"{x.Me.Name} took the Legal review of {Name(v)} of {x.Contract.Reference}.", null);
                return null;
            }));

        group.MapPost("/{vid:int}/legal/send-to-owner", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var (c, v) = (x.Contract, x.Variation);
                if (!ActionsFor(x.Me, c, v).Contains("sendToOwner")) return ApiResults.NotAllowed("You can't send this Variation to the owner now.");
                var note = Clean(body.Note);
                if (note is null) return ApiResults.Invalid("Say what the owner needs to do.", new() { ["note"] = "Say what's needed." });
                if (note.Length > MaxNote) return TooLong("note", MaxNote);
                await x.C.ExecuteAsync("UPDATE contracts.Variation SET WhoseTurn = N'Owner', TurnSinceUtc = SYSUTCDATETIME(), LegalPersonId = ISNULL(LegalPersonId, @me), UpdatedUtc = SYSUTCDATETIME() WHERE Id = @vid",
                    new { me = x.Me.Id, vid }, x.Tx);
                var (docs, docProblem) = await LegalEndpoints.CheckDocumentsAsync(x.C, x.Tx, id, vid, body.DocumentVersionIds);
                if (docProblem is not null) return docProblem;
                var number = await AddEntryAsync(x.C, x.Tx, x.Me, id, vid, "SentToOwner", "Owner", v.LegalPersonId ?? x.Me.Id, "WaitingOnOwner", note, docs);
                await x.Log("variation.sent-to-owner", $"{x.Me.Name} sent {Name(v)} of {c.Reference} back to the owner" + Quote(note) + Stop(note), new { note });
                await EmailOwnersAsync(new Ctx(x.C, x.Tx, x.Me, c), $"variation-legal:{vid}:{number}", "legal-back", p => EmailWording.RenderAsync(x.C, x.Tx, "BackFromLegal",
                    With(With(With(EmailWording.For(p.Name, c), "Reference", Ref(c, v)), "Legal person", x.Me.Name), "Note", note), VariationLink(settings, id, vid)));
                return null;
            }));

        group.MapPost("/{vid:int}/legal/clear", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var (c, v) = (x.Contract, x.Variation);
                if (!ActionsFor(x.Me, c, v).Contains("clear")) return ApiResults.NotAllowed("You can't clear this Variation now.");
                var note = Clean(body.Note);
                if (note is null) return ApiResults.Invalid("Add a short outcome.", new() { ["note"] = "Add a short outcome. The approver sees it." });
                if (note.Length > MaxNote) return TooLong("note", MaxNote);
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Variation SET Stage = N'Draft', StageSinceUtc = SYSUTCDATETIME(), WhoseTurn = NULL, TurnSinceUtc = NULL, LegalPersonId = ISNULL(LegalPersonId, @me),
                        LegalOutcome = @note, LegalClearedUtc = SYSUTCDATETIME(), LegalClearedById = @me, UpdatedUtc = SYSUTCDATETIME()
                    WHERE Id = @vid
                    """, new { me = x.Me.Id, note, vid }, x.Tx);
                var number = await AddEntryAsync(x.C, x.Tx, x.Me, id, vid, "Cleared", null, v.LegalPersonId ?? x.Me.Id, null, note, []);
                await x.Log("variation.legal-cleared", $"{x.Me.Name} cleared {Name(v)} of {c.Reference} (Legal)" + Quote(note) + Stop(note), new { note });
                await EmailOwnersAsync(new Ctx(x.C, x.Tx, x.Me, c), $"variation-legal:{vid}:{number}", "variation-cleared", p => EmailWording.RenderAsync(x.C, x.Tx, "VariationCleared",
                    With(With(With(EmailWording.For(p.Name, c), "Variation", Name(v)), "Legal person", x.Me.Name), "Outcome", note), VariationLink(settings, id, vid)));
                return null;
            }));

        // Owner: for approval. The Group Head only is emailed (D-60).
        group.MapPost("/{vid:int}/send-for-approval", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var (c, v) = (x.Contract, x.Variation);
                if (!ActionsFor(x.Me, c, v).Contains("sendForApproval"))
                    return ApiResults.NotAllowed(v.NeedsLegalReview && v.LegalClearedUtc is null ? "Legal needs to clear it first (or untick Needs Legal review if Legal has agreed it isn't needed)." : "You can't send this Variation for approval now.");
                var plan = await PlanAsync(x.C, x.Tx, c, v.Changes);
                if (plan.Changes.Count == 0) return ApiResults.Invalid("Add at least one change first.", new() { ["changes"] = "Add at least one change first." });
                if (plan.Errors.Count > 0) return ApiResults.Invalid("Some changes no longer fit. Check them and save again.", plan.Errors);
                await x.C.ExecuteAsync("UPDATE contracts.Variation SET Stage = N'Approval', StageSinceUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME() WHERE Id = @vid", new { vid }, x.Tx);
                await x.Log("variation.sent-for-approval", $"{x.Me.Name} sent {Name(v)} of {c.Reference} for approval: {Describe(plan.Changes)}.", null);
                var approvers = (await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsApprover = 1", transaction: x.Tx))
                    .Where(a => !Involved(a, c, v) && a.Id != x.Me.Id).ToList();
                var head = approvers.Where(a => a.IsGroupHead).ToList();
                var stamp = Convert.ToBase64String(v.RowVersion);
                foreach (var a in head.Count > 0 ? head : approvers)
                    await EmailOutbox.QueueAsync(x.C, x.Tx, "variation-waiting", a.Email, a.Name,
                        await EmailWording.RenderAsync(x.C, x.Tx, "VariationWaiting",
                            With(With(With(With(EmailWording.For(a.Name, c), "Variation", Name(v)), "Sent by", x.Me.Name), "Reason", v.Reason), "Changes", Describe(plan.Changes)),
                            VariationLink(settings, id, vid)),
                        personId: a.Id, contractId: id, dedupeKey: $"variation-waiting:{vid}:{stamp}:{a.Id}");
                return null;
            }));

        // Approver: the changes apply to the contract in one step, audited; the registered date doesn't change.
        group.MapPost("/{vid:int}/approve", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var (c, v) = (x.Contract, x.Variation);
                if (!ActionsFor(x.Me, c, v).Contains("approve"))
                    return ApiResults.NotAllowed(v.Stage == "Approval" && x.Me.IsApprover ? "You can't approve this one because you raised the Variation, own the contract or are its backup. Another approver can." : "You can't approve this Variation.");
                if (c.Stage != "Effective") return ApiResults.NotAllowed("The contract is no longer Effective, so the Variation can't apply.");
                var plan = await PlanAsync(x.C, x.Tx, c, v.Changes);
                if (plan.Errors.Count > 0) return ApiResults.Invalid("Some changes no longer fit, so nothing was applied. Send it back to the owner to fix them.", plan.Errors);
                var (errors, _) = await ContractEndpoints.UpdateDraftAsync(x.C, x.Tx, x.Audit, x.Me, c, plan.Body, $" (through {Name(v)}, approved)");
                if (errors is not null) return ApiResults.Invalid("Some changes no longer fit, so nothing was applied.", errors);
                var groupHead = x.Me.IsGroupHead ? null : await x.C.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE IsGroupHead = 1 AND IsActive = 1", transaction: x.Tx);
                var applied = JsonSerializer.Serialize(plan.Changes.Select(ch => new { ch.Key, ch.Label, ch.Old, ch.New }));
                await x.C.ExecuteAsync("UPDATE contracts.Variation SET Stage = N'Approved', StageSinceUtc = SYSUTCDATETIME(), AppliedChangesJson = @applied, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @vid",
                    new { applied, vid }, x.Tx);
                var approvalId = await x.C.ExecuteScalarAsync<int>(
                    "INSERT INTO contracts.Approval (ContractId, VariationId, Decision, ById, AsBackupFor) OUTPUT INSERTED.Id VALUES (@id, @vid, N'Approved', @me, @gh)",
                    new { id, vid, me = x.Me.Id, gh = groupHead?.Id }, x.Tx);
                await x.Log("variation.approved", $"{x.Me.Name} approved {Name(v)} of {c.Reference}" + (groupHead is not null ? $" (as backup for {groupHead.Name})" : "") + $": {Describe(plan.Changes)}.", null);
                await EmailOwnersAsync(new Ctx(x.C, x.Tx, x.Me, c), $"variation-approved:{vid}:{approvalId}", "variation-approved", p => EmailWording.RenderAsync(x.C, x.Tx, "VariationApproved",
                    With(With(With(EmailWording.For(p.Name, c), "Variation", Name(v)), "Approver", x.Me.Name), "Changes", Describe(plan.Changes)), VariationLink(settings, id, vid)));
                return null;
            }));

        group.MapPost("/{vid:int}/send-back", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var (c, v) = (x.Contract, x.Variation);
                if (!ActionsFor(x.Me, c, v).Contains("sendBack")) return ApiResults.NotAllowed("You can't send this Variation back.");
                var reason = Clean(body.Note);
                if (reason is null) return ApiResults.Invalid("Say what needs fixing.", new() { ["note"] = "Say what needs fixing." });
                if (reason.Length > MaxNote) return TooLong("note", MaxNote);
                var groupHead = x.Me.IsGroupHead ? null : await x.C.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE IsGroupHead = 1 AND IsActive = 1", transaction: x.Tx);
                await x.C.ExecuteAsync("UPDATE contracts.Variation SET Stage = N'Draft', StageSinceUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME() WHERE Id = @vid", new { vid }, x.Tx);
                var approvalId = await x.C.ExecuteScalarAsync<int>(
                    "INSERT INTO contracts.Approval (ContractId, VariationId, Decision, ById, AsBackupFor, Reason) OUTPUT INSERTED.Id VALUES (@id, @vid, N'SentBack', @me, @gh, @reason)",
                    new { id, vid, me = x.Me.Id, gh = groupHead?.Id, reason }, x.Tx);
                await x.Log("variation.sent-back", $"{x.Me.Name} sent {Name(v)} of {c.Reference} back" + Quote(reason) + Stop(reason), new { reason });
                await EmailOwnersAsync(new Ctx(x.C, x.Tx, x.Me, c), $"variation-back:{vid}:{approvalId}", "variation-sent-back", p => EmailWording.RenderAsync(x.C, x.Tx, "VariationSentBack",
                    With(With(With(EmailWording.For(p.Name, c), "Variation", Name(v)), "Approver", x.Me.Name), "Reason", reason), VariationLink(settings, id, vid)));
                return null;
            }));

        group.MapPost("/{vid:int}/withdraw", (int id, int vid, VariationAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, vid, body.RowVersion, current, db, audit, async x =>
            {
                var v = x.Variation;
                if (!ActionsFor(x.Me, x.Contract, v).Contains("withdraw")) return ApiResults.NotAllowed("You can't withdraw this Variation.");
                var reason = Clean(body.Note);
                if (reason is null) return ApiResults.Invalid("Say why.", new() { ["note"] = "Say why it's being withdrawn." });
                if (reason.Length > MaxNote) return TooLong("note", MaxNote);
                await x.C.ExecuteAsync("UPDATE contracts.Variation SET Stage = N'Withdrawn', StageSinceUtc = SYSUTCDATETIME(), WhoseTurn = NULL, ClosedReason = @reason, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @vid",
                    new { reason, vid }, x.Tx);
                await x.Log("variation.withdrawn", $"{x.Me.Name} withdrew {Name(v)} of {x.Contract.Reference}" + Quote(reason) + Stop(reason) + " Nothing changed on the contract.", new { reason });
                return null;
            }));
    }

    private static string? VariationLink(AppSettings s, int id, int vid) => s.SiteUrl is null ? null : $"{s.SiteUrl}/contracts/{id}/variations/{vid}";
}
