using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

public sealed class ChoiceOption
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Hidden { get; set; }
}

public sealed class FieldRow
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public bool IsRuleField { get; set; }
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Help { get; set; }
    public int SectionId { get; set; }
    public string SectionKey { get; set; } = "";
    public int Position { get; set; }
    public bool IsRequired { get; set; }
    public bool IsHidden { get; set; }
    public bool ShowInList { get; set; }
    public bool Filterable { get; set; }
    public bool InExport { get; set; }
    public string AfterEffective { get; set; } = "Variation";
    public string? ChoicesJson { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public bool HasValues { get; set; }

    public List<ChoiceOption> Choices => string.IsNullOrEmpty(ChoicesJson) ? [] : JsonSerializer.Deserialize<List<ChoiceOption>>(ChoicesJson, Json) ?? [];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class SectionRow
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public int Position { get; set; }
    public bool IsHidden { get; set; }
}

/// <summary>Added contract fields: reading, checking, saving and showing their values. Built-in fields stay real columns on Contract.</summary>
public static partial class Fields
{
    public static readonly string[] AddedKinds = ["ShortText", "LongText", "WholeNumber", "Decimal", "Money", "Date", "YesNo", "Choice", "MultiChoice", "Person", "Supplier", "Link", "Email"];

    public static readonly Dictionary<string, string> KindNames = new()
    {
        ["ShortText"] = "Short text", ["LongText"] = "Long text", ["WholeNumber"] = "Whole number", ["Decimal"] = "Decimal number", ["Money"] = "Money (AUD)",
        ["Date"] = "Date", ["YesNo"] = "Yes/No", ["Choice"] = "Pick one from a list", ["MultiChoice"] = "Pick several from a list", ["Person"] = "Person",
        ["Supplier"] = "Supplier", ["Link"] = "Link", ["Email"] = "Email", ["BuiltIn"] = "Built in",
    };

    private static readonly CultureInfo AU = CultureInfo.GetCultureInfo("en-AU");

    public static async Task<List<FieldRow>> LoadAsync(SqlConnection c, SqlTransaction? tx = null) =>
        (await c.QueryAsync<FieldRow>("""
            SELECT f.*, s.[Key] AS SectionKey,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM contracts.ContractFieldValue v WHERE v.FieldId = f.Id) THEN 1 ELSE 0 END AS BIT) AS HasValues
            FROM contracts.FieldDefinition f JOIN contracts.FormSection s ON s.Id = f.SectionId
            ORDER BY s.Position, f.Position
            """, transaction: tx)).ToList();

    public static async Task<List<SectionRow>> LoadSectionsAsync(SqlConnection c, SqlTransaction? tx = null) =>
        (await c.QueryAsync<SectionRow>("SELECT * FROM contracts.FormSection ORDER BY Position", transaction: tx)).ToList();

    private sealed class ValueRow
    {
        public int ContractId { get; set; }
        public int FieldId { get; set; }
        public string? TextValue { get; set; }
        public decimal? NumberValue { get; set; }
        public DateTime? DateValue { get; set; }
        public bool? BoolValue { get; set; }
        public int? PersonId { get; set; }
        public int? SupplierId { get; set; }
        public string? PersonName { get; set; }
        public string? SupplierName { get; set; }
    }

    /// <summary>Values of added fields for some contracts: key to (value for the form, plain words for display).</summary>
    public static async Task<Dictionary<int, Dictionary<string, (object? Value, string Display)>>> ReadAsync(SqlConnection c, SqlTransaction? tx, IEnumerable<int> contractIds, List<FieldRow> fields, Func<FieldRow, bool>? which = null)
    {
        var ids = contractIds.Distinct().ToArray();
        var result = ids.ToDictionary(i => i, _ => new Dictionary<string, (object?, string)>());
        var wanted = fields.Where(f => !f.IsBuiltIn && (which?.Invoke(f) ?? true)).ToDictionary(f => f.Id);
        if (ids.Length == 0 || wanted.Count == 0) return result;
        var rows = await c.QueryAsync<ValueRow>("""
            SELECT v.*, p.Name AS PersonName, s.Name AS SupplierName FROM contracts.ContractFieldValue v
            LEFT JOIN contracts.Person p ON p.Id = v.PersonId LEFT JOIN contracts.Supplier s ON s.Id = v.SupplierId
            WHERE v.ContractId IN (SELECT value FROM OPENJSON(@ids))
            """, new { ids = JsonSerializer.Serialize(ids) }, tx);
        foreach (var r in rows)
        {
            if (!wanted.TryGetValue(r.FieldId, out var f)) continue;
            result[r.ContractId][f.Key] = Present(f, r);
        }
        return result;
    }

    private static (object? Value, string Display) Present(FieldRow f, ValueRow r)
    {
        switch (f.Kind)
        {
            case "WholeNumber": return (r.NumberValue is { } n ? (int)n : null, r.NumberValue?.ToString("N0", AU) ?? "");
            case "Decimal": return (r.NumberValue, r.NumberValue?.ToString("#,##0.####", AU) ?? "");
            case "Money": return (r.NumberValue, r.NumberValue is { } m ? $"${m.ToString("N2", AU)}" : "");
            case "Date": return (r.DateValue is { } d ? DateOnly.FromDateTime(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null, r.DateValue?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "");
            case "YesNo": return (r.BoolValue, r.BoolValue is null ? "" : r.BoolValue.Value ? "Yes" : "No");
            case "Choice":
                return (r.TextValue, f.Choices.FirstOrDefault(c => c.Key == r.TextValue)?.Label ?? r.TextValue ?? "");
            case "MultiChoice":
                var keys = string.IsNullOrEmpty(r.TextValue) ? [] : JsonSerializer.Deserialize<string[]>(r.TextValue) ?? [];
                return (keys, string.Join(", ", keys.Select(k => f.Choices.FirstOrDefault(c => c.Key == k)?.Label ?? k)));
            case "Person": return (r.PersonId, r.PersonName ?? "");
            case "Supplier": return (r.SupplierId, r.SupplierName ?? "");
            default: return (r.TextValue, r.TextValue ?? "");
        }
    }

    public sealed record Parsed(FieldRow Field, string? Text, decimal? Number, DateOnly? Date, bool? Bool, int? PersonId, int? SupplierId, bool IsEmpty);

    [GeneratedRegex(@"^https?://[^\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>
    /// Checks submitted values for added fields (key to JSON value). Unknown or removed fields are refused; each value must fit its field's kind
    /// and limits. Returns the parsed values, or errors keyed "field.{key}".
    /// </summary>
    public static async Task<(List<Parsed> Values, Dictionary<string, string> Errors)> ParseAsync(SqlConnection c, SqlTransaction tx, Dictionary<string, JsonElement>? submitted, List<FieldRow> fields)
    {
        var values = new List<Parsed>();
        var errors = new Dictionary<string, string>();
        if (submitted is null) return (values, errors);
        var byKey = fields.Where(f => !f.IsBuiltIn).ToDictionary(f => f.Key);
        foreach (var (key, json) in submitted)
        {
            var errKey = $"field.{key}";
            if (!byKey.TryGetValue(key, out var f)) { errors[errKey] = "That field doesn't exist any more. Reload the page."; continue; }
            var empty = json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (json.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(json.GetString()))
                || (json.ValueKind == JsonValueKind.Array && json.GetArrayLength() == 0);
            if (empty) { values.Add(new Parsed(f, null, null, null, null, null, null, true)); continue; }
            if (f.IsHidden) { errors[errKey] = $"{f.Label} has been removed by an admin and can't be filled in."; continue; }
            try
            {
                switch (f.Kind)
                {
                    case "ShortText" or "LongText":
                    {
                        var text = json.GetString()!.Trim();
                        var max = f.MaxLength ?? (f.Kind == "ShortText" ? 200 : 4000);
                        if (text.Length > max) { errors[errKey] = $"Keep it under {max:N0} characters."; continue; }
                        values.Add(new Parsed(f, text, null, null, null, null, null, false));
                        break;
                    }
                    case "Link":
                    {
                        var text = json.GetString()!.Trim();
                        if (!LinkPattern().IsMatch(text) || text.Length > 2000) { errors[errKey] = "Use a full web link starting with https://"; continue; }
                        values.Add(new Parsed(f, text, null, null, null, null, null, false));
                        break;
                    }
                    case "Email":
                    {
                        var text = json.GetString()!.Trim();
                        if (text.Length > 320 || !MailAddress.TryCreate(text, out var a) || a.Address != text || !text.Contains('.')) { errors[errKey] = "That doesn't look like an email address."; continue; }
                        values.Add(new Parsed(f, text, null, null, null, null, null, false));
                        break;
                    }
                    case "WholeNumber" or "Decimal" or "Money":
                    {
                        decimal n;
                        if (json.ValueKind == JsonValueKind.Number) n = json.GetDecimal();
                        else if (!decimal.TryParse(json.GetString()!.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out n)) { errors[errKey] = "Use numbers only."; continue; }
                        if (f.Kind == "WholeNumber" && n != decimal.Truncate(n)) { errors[errKey] = "Use a whole number."; continue; }
                        if (f.MinValue is { } min && n < min) { errors[errKey] = $"It can't be less than {min.ToString("#,##0.##", AU)}."; continue; }
                        if (f.MaxValue is { } maxV && n > maxV) { errors[errKey] = $"It can't be more than {maxV.ToString("#,##0.##", AU)}."; continue; }
                        if (Math.Abs(n) >= 1_000_000_000_000_000m) { errors[errKey] = "That number is too large."; continue; }
                        values.Add(new Parsed(f, null, n, null, null, null, null, false));
                        break;
                    }
                    case "Date":
                    {
                        if (!DateOnly.TryParseExact(json.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { errors[errKey] = "Choose a date."; continue; }
                        values.Add(new Parsed(f, null, null, d, null, null, null, false));
                        break;
                    }
                    case "YesNo":
                        values.Add(new Parsed(f, null, null, null, json.GetBoolean(), null, null, false));
                        break;
                    case "Choice":
                    {
                        var k = json.GetString()!;
                        var choice = f.Choices.FirstOrDefault(x => x.Key == k);
                        if (choice is null) { errors[errKey] = "Choose one of the options."; continue; }
                        values.Add(new Parsed(f, k, null, null, null, null, null, false));
                        break;
                    }
                    case "MultiChoice":
                    {
                        var keys = json.EnumerateArray().Select(x => x.GetString()!).Distinct().ToArray();
                        if (keys.Any(k => f.Choices.All(x => x.Key != k))) { errors[errKey] = "Choose from the options."; continue; }
                        values.Add(new Parsed(f, JsonSerializer.Serialize(keys), null, null, null, null, null, false));
                        break;
                    }
                    case "Person":
                    {
                        var id = json.GetInt32();
                        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person WHERE Id = @id", new { id }, tx) == 0) { errors[errKey] = "Choose someone from the list."; continue; }
                        values.Add(new Parsed(f, null, null, null, null, id, null, false));
                        break;
                    }
                    case "Supplier":
                    {
                        var id = json.GetInt32();
                        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Supplier WHERE Id = @id", new { id }, tx) == 0) { errors[errKey] = "Choose a supplier from the list."; continue; }
                        values.Add(new Parsed(f, null, null, null, null, null, id, false));
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
            {
                errors[errKey] = "That value doesn't fit this field.";
            }
        }
        return (values, errors);
    }

    /// <summary>Saves parsed values and returns the changes in plain words (label: old to new) for the audit record.</summary>
    public static async Task<List<string>> SaveAsync(SqlConnection c, SqlTransaction tx, int contractId, List<Parsed> values, List<FieldRow> fields)
    {
        if (values.Count == 0) return [];
        var before = (await ReadAsync(c, tx, [contractId], fields))[contractId];
        foreach (var v in values)
        {
            if (v.IsEmpty)
                await c.ExecuteAsync("DELETE FROM contracts.ContractFieldValue WHERE ContractId = @contractId AND FieldId = @id", new { contractId, v.Field.Id }, tx);
            else
            {
                var p = new DynamicParameters(new { contractId, fieldId = v.Field.Id, text = v.Text, number = v.Number, b = v.Bool, person = v.PersonId, supplier = v.SupplierId });
                p.Add("date", v.Date?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
                await c.ExecuteAsync("""
                    MERGE contracts.ContractFieldValue AS t
                    USING (SELECT @contractId AS ContractId, @fieldId AS FieldId) AS s ON t.ContractId = s.ContractId AND t.FieldId = s.FieldId
                    WHEN MATCHED THEN UPDATE SET TextValue = @text, NumberValue = @number, DateValue = @date, BoolValue = @b, PersonId = @person, SupplierId = @supplier, UpdatedUtc = SYSUTCDATETIME()
                    WHEN NOT MATCHED THEN INSERT (ContractId, FieldId, TextValue, NumberValue, DateValue, BoolValue, PersonId, SupplierId)
                        VALUES (@contractId, @fieldId, @text, @number, @date, @b, @person, @supplier);
                    """, p, tx);
            }
        }
        var after = (await ReadAsync(c, tx, [contractId], fields))[contractId];
        var changes = new List<string>();
        foreach (var v in values)
        {
            var old = before.TryGetValue(v.Field.Key, out var o) ? o.Display : "";
            var now = after.TryGetValue(v.Field.Key, out var n) ? n.Display : "";
            if (old == now) continue;
            changes.Add(now.Length == 0 ? $"cleared {v.Field.Label}" : old.Length == 0 ? $"set {v.Field.Label} to {now}" : $"{v.Field.Label} from {old} to {now}");
        }
        return changes;
    }

    // ---------------------------------------------------------------- Excel (decision D-44)

    public sealed record ExcelField(FieldRow Field, string Header);

    /// <summary>
    /// Added fields an admin offers in Excel, as columns headed by their label. A label that matches one of the sheet's own headings
    /// (or another field's, ignoring spaces and case) gets the field's key added, so every column is unambiguous.
    /// </summary>
    public static List<ExcelField> ExcelFields(List<FieldRow> fields, IEnumerable<string> fixedHeaders)
    {
        var used = fixedHeaders.Select(Excel.Normalise).ToHashSet();
        var result = new List<ExcelField>();
        foreach (var f in fields.Where(f => !f.IsBuiltIn && !f.IsHidden && f.InExport))
        {
            var header = f.Label;
            if (!used.Add(Excel.Normalise(header)))
            {
                header = $"{f.Label} ({f.Key})";
                if (!used.Add(Excel.Normalise(header))) continue;
            }
            result.Add(new ExcelField(f, header));
        }
        return result;
    }

    private static IEnumerable<string> OfferedLabels(FieldRow f) => f.Choices.Where(c => !c.Hidden).Select(c => c.Label);

    /// <summary>The note on the column heading: what to type.</summary>
    public static string? ExcelNote(FieldRow f) => f.Kind switch
    {
        "WholeNumber" => "Whole number",
        "Decimal" or "Money" => "Numbers only",
        "Date" => "Day/month/year",
        "YesNo" => "Yes or No",
        "Choice" => $"One of: {string.Join(", ", OfferedLabels(f))}",
        "MultiChoice" => $"Any of: {string.Join(", ", OfferedLabels(f))}. Separate them with commas.",
        "Person" => "Windows logon or full name of someone who uses JBS Contracts",
        "Supplier" => "The supplier's name, as it is under Suppliers",
        "Link" => "A full web link starting with https://",
        "Email" => "An email address",
        _ => f.Help,
    };

    /// <summary>A value as an Excel cell: numbers, dates and Yes/No keep their type; a person is their Windows logon (like Owner).</summary>
    public static object? ExcelValue(FieldRow f, (object? Value, string Display) v, Func<int, string?> windowsAccount) => f.Kind switch
    {
        "WholeNumber" => v.Value is int n ? n : null,
        "Decimal" or "Money" => v.Value is decimal m ? m : null,
        "Date" => v.Value is string d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        "YesNo" => v.Value is bool b ? b : null,
        "Person" => v.Value is int id ? windowsAccount(id) : null,
        _ => v.Display.Length == 0 ? null : v.Display,
    };

    /// <summary>
    /// Reads one added field's cell into the value the contract form would send. Null when the cell is blank (the value is left as it is).
    /// Options are matched by label, people by Windows logon or name, suppliers by name.
    /// </summary>
    public static (JsonElement? Value, string? Problem) FromExcel(FieldRow f, Excel.Row row, string header, List<Person> people, Dictionary<string, int> supplierIds, string? defaultDomain)
    {
        static string K(string s) => s.Trim().ToUpperInvariant();
        static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
        string? p;
        switch (f.Kind)
        {
            case "WholeNumber" or "Decimal" or "Money":
                var n = row.Money(header, out p);
                return (n is null ? null : J(n.Value), p);
            case "Date":
                var d = row.Date(header, out p);
                return (d is null ? null : J(d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), p);
            case "YesNo":
                var b = row.YesNo(header, out p);
                return (b is null ? null : J(b.Value), p);
        }
        var text = row.Text(header);
        if (text is null) return (null, null);
        switch (f.Kind)
        {
            case "Choice":
            {
                var match = f.Choices.FirstOrDefault(c => !c.Hidden && K(c.Label) == K(text));
                return match is null ? (null, $"{header}: \"{text}\" isn't one of the options ({string.Join(", ", OfferedLabels(f))}).") : (J(match.Key), null);
            }
            case "MultiChoice":
            {
                var keys = new List<string>();
                foreach (var part in text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = f.Choices.FirstOrDefault(c => !c.Hidden && K(c.Label) == K(part));
                    if (match is null) return (null, $"{header}: \"{part}\" isn't one of the options ({string.Join(", ", OfferedLabels(f))}).");
                    if (!keys.Contains(match.Key)) keys.Add(match.Key);
                }
                return (J(keys), null);
            }
            case "Person":
            {
                var byAccount = WindowsAccount.TryNormalise(text, defaultDomain, out var account)
                    ? people.FirstOrDefault(x => WindowsAccount.Key(x.WindowsAccount) == WindowsAccount.Key(account)) : null;
                var byName = people.Where(x => K(x.Name) == K(text)).ToList();
                var person = byAccount ?? (byName.Count == 1 ? byName[0] : null);
                return person is null ? (null, $"{header}: \"{text}\" isn't someone who uses JBS Contracts (use their Windows logon or full name).") : (J(person.Id), null);
            }
            case "Supplier":
                return supplierIds.TryGetValue(K(text), out var id) ? (J(id), null) : (null, $"{header}: there's no supplier called \"{text}\". Add it under Suppliers first.");
            default:
                return (J(text), null);
        }
    }

    /// <summary>The labels of added fields whose value these parsed values would change (for import previews).</summary>
    public static async Task<List<string>> ChangedAsync(SqlConnection c, SqlTransaction tx, int contractId, List<Parsed> values, List<FieldRow> fields)
    {
        if (values.Count == 0) return [];
        var before = (await ReadAsync(c, tx, [contractId], fields))[contractId];
        return values.Where(v => Canonical(v) != (before.TryGetValue(v.Field.Key, out var b) ? Canonical(b.Value) : null)).Select(v => v.Field.Label).ToList();
    }

    private static string? Canonical(Parsed v) => v.IsEmpty ? null
        : v.Text ?? (v.Number is { } n ? Canonical(n) : null) ?? v.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
          ?? (v.Bool is { } b ? Canonical(b) : null) ?? v.PersonId?.ToString(CultureInfo.InvariantCulture) ?? v.SupplierId?.ToString(CultureInfo.InvariantCulture);

    private static string? Canonical(object? value) => value switch
    {
        null => null,
        decimal d => d.ToString("0.############", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string[] keys => JsonSerializer.Serialize(keys),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    // ---------------------------------------------------------------- Filters on the Contracts list

    /// <summary>Built-in fields that can be picked from to filter the list. Text, numbers and dates are found with the search box instead.</summary>
    private static readonly HashSet<string> FilterableBuiltIns = ["supplier", "category", "agreementType", "owner", "backupOwner", "businessUnit", "site", "entity", "function", "confidential", "evergreen", "autoRenews", "needsLegalReview"];

    public static bool CanFilter(FieldRow f) => f.IsBuiltIn ? FilterableBuiltIns.Contains(f.Key) : f.Kind is "Choice" or "MultiChoice" or "YesNo" or "Person" or "Supplier";

    /// <summary>
    /// SQL conditions (for contract alias k) for the list's filters, given as field key to value ("f.site=12", "f.f-cover=public-liability", "f.confidential=yes").
    /// Only fields the admin made filterable, and that are still shown, are honoured; anything else is ignored. Values are always parameters.
    /// </summary>
    public static List<string> FilterSql(List<FieldRow> fields, IEnumerable<KeyValuePair<string, string>> filters, DynamicParameters p)
    {
        var sql = new List<string>();
        var byKey = fields.Where(f => f.Filterable && !f.IsHidden && CanFilter(f)).ToDictionary(f => f.Key);
        var n = 0;
        foreach (var (key, raw) in filters)
        {
            if (!byKey.TryGetValue(key, out var f) || string.IsNullOrWhiteSpace(raw)) continue;
            var value = raw.Trim();
            var name = $"filter{n++}";
            bool? yes = value.ToLowerInvariant() switch { "yes" => true, "no" => false, _ => null };
            int? id = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : null;
            if (f.IsBuiltIn)
            {
                string? column = f.Key switch
                {
                    "supplier" => "k.SupplierId", "agreementType" => "k.AgreementTypeId", "owner" => "k.OwnerId", "backupOwner" => "k.BackupOwnerId",
                    _ => null,
                };
                if (column is not null && id is not null) { sql.Add($"{column} = @{name}"); p.Add(name, id); }
                else if (f.Key is "businessUnit" or "site" or "entity" or "function" && id is not null)
                {
                    // Several values each (D-114): the contract has the one picked among them.
                    sql.Add($"EXISTS (SELECT 1 FROM contracts.ContractListValue lv WHERE lv.ContractId = k.Id AND lv.ListItemId = @{name})");
                    p.Add(name, id);
                }
                else if (f.Key == "category" && id is not null)
                {
                    // A category includes its subcategories.
                    sql.Add($"(k.CategoryId = @{name} OR k.CategoryId IN (SELECT Id FROM contracts.Category WHERE ParentId = @{name}))");
                    p.Add(name, id);
                }
                else if (yes is not null && f.Key is "confidential" or "evergreen" or "autoRenews" or "needsLegalReview")
                {
                    var bit = f.Key switch { "confidential" => "k.IsConfidential", "evergreen" => "k.IsEvergreen", "autoRenews" => "k.AutoRenews", _ => "k.NeedsLegalReview" };
                    sql.Add($"{bit} = @{name}");
                    p.Add(name, yes.Value);
                }
                continue;
            }
            var fieldId = $"{name}field";
            p.Add(fieldId, f.Id);
            const string Value = "contracts.ContractFieldValue v WHERE v.ContractId = k.Id AND v.FieldId = ";
            switch (f.Kind)
            {
                case "Choice":
                    sql.Add($"EXISTS (SELECT 1 FROM {Value}@{fieldId} AND v.TextValue = @{name})"); p.Add(name, value);
                    break;
                case "MultiChoice":
                    sql.Add($"EXISTS (SELECT 1 FROM {Value}@{fieldId} AND ISJSON(v.TextValue) = 1 AND EXISTS (SELECT 1 FROM OPENJSON(v.TextValue) j WHERE j.value = @{name}))"); p.Add(name, value);
                    break;
                case "YesNo" when yes == true:
                    sql.Add($"EXISTS (SELECT 1 FROM {Value}@{fieldId} AND v.BoolValue = 1)");
                    break;
                case "YesNo" when yes == false: // No includes contracts where it was never ticked
                    sql.Add($"NOT EXISTS (SELECT 1 FROM {Value}@{fieldId} AND v.BoolValue = 1)");
                    break;
                case "Person" when id is not null:
                    sql.Add($"EXISTS (SELECT 1 FROM {Value}@{fieldId} AND v.PersonId = @{name})"); p.Add(name, id);
                    break;
                case "Supplier" when id is not null:
                    sql.Add($"EXISTS (SELECT 1 FROM {Value}@{fieldId} AND v.SupplierId = @{name})"); p.Add(name, id);
                    break;
            }
        }
        return sql;
    }

    /// <summary>A parsed value in plain words, as the contract page would show it (for Variations: old and new side by side).</summary>
    public static async Task<string> DisplayAsync(SqlConnection c, SqlTransaction? tx, Parsed p)
    {
        if (p.IsEmpty) return "";
        var f = p.Field;
        return f.Kind switch
        {
            "WholeNumber" => p.Number?.ToString("N0", AU) ?? "",
            "Decimal" => p.Number?.ToString("#,##0.####", AU) ?? "",
            "Money" => p.Number is { } m ? $"${m.ToString("N2", AU)}" : "",
            "Date" => p.Date?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "",
            "YesNo" => p.Bool is null ? "" : p.Bool.Value ? "Yes" : "No",
            "Choice" => f.Choices.FirstOrDefault(x => x.Key == p.Text)?.Label ?? p.Text ?? "",
            "MultiChoice" => string.Join(", ", (JsonSerializer.Deserialize<string[]>(p.Text ?? "[]") ?? []).Select(k => f.Choices.FirstOrDefault(x => x.Key == k)?.Label ?? k)),
            "Person" => await c.ExecuteScalarAsync<string>("SELECT Name FROM contracts.Person WHERE Id = @id", new { id = p.PersonId }, tx) ?? "",
            "Supplier" => await c.ExecuteScalarAsync<string>("SELECT Name FROM contracts.Supplier WHERE Id = @id", new { id = p.SupplierId }, tx) ?? "",
            _ => p.Text ?? "",
        };
    }
}
