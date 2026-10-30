using System.Text;
using Ecp.Metadata;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>One kind of field as an administrator meets it: a plain name, what it is for, and which settings make sense for it.</summary>
public sealed record TypeGuide(string Code, string Label, string Description);

/// <summary>
/// The plain-language side of the guided Fields pages. Everything here is presentation: it turns the engine's closed vocabulary (type codes, keys, rules) into words an administrator uses, and generates the machine
/// names (the field key, a choice's stored code) so nobody has to type them. The rules themselves stay with the field engine and its publish-time validator.
/// </summary>
public static class FieldGuide
{
    public static readonly IReadOnlyList<TypeGuide> Types =
    [
        new(FieldTypes.Text, "Text", "One line of words, such as a name, a code or a short note."),
        new(FieldTypes.LongText, "Long text", "Several lines or paragraphs of notes."),
        new(FieldTypes.Integer, "Number", "A whole number, such as 30 or 12."),
        new(FieldTypes.Decimal, "Decimal", "A number with decimals, such as 2.75."),
        new(FieldTypes.Currency, "Money", "An amount of money, shown in the [[contract|lower]]'s currency."),
        new(FieldTypes.Percentage, "Percentage", "A percentage from 0 to 100."),
        new(FieldTypes.Date, "Date", "A calendar date, such as an expiry date."),
        new(FieldTypes.DateTime, "Date/time", "A date with a time of day (kept in UTC)."),
        new(FieldTypes.Boolean, "Yes/No", "A question with the answer Yes or No."),
        new(FieldTypes.SingleChoice, "Single choice", "Pick one option from a list that you write."),
        new(FieldTypes.MultiChoice, "Multiple choice", "Pick one or more options from a list that you write."),
        new(FieldTypes.Person, "Person", "Choose a colleague from the [[organisation|lower]]."),
        new(FieldTypes.Lookup, "Lookup", "Pick from a shared list that other fields use too."),
        new(FieldTypes.Email, "Email", "An email address."),
        new(FieldTypes.Url, "URL", "A web address."),
        new(FieldTypes.FileReference, "File reference", "Points to a document already attached to the [[contract|lower]]."),
        new(FieldTypes.Calculated, "Calculated", "Shown but never typed in: worked out from other fields, such as the days between two dates."),
    ];

    public static TypeGuide Of(string type) => Types.FirstOrDefault(t => t.Code == type) ?? new(type, FieldTypes.Label(type), "");
    public static string Label(string type) => Of(type).Label;

    // ---- which settings and rules belong to a type -----------------------------------------------------------------------------------------------------

    public static bool HasLength(string t) => t is FieldTypes.Text or FieldTypes.LongText;
    public static bool HasPattern(string t) => t == FieldTypes.Text;
    public static bool HasRange(string t) => FieldTypes.IsNumeric(t);
    public static bool HasDecimals(string t) => t is FieldTypes.Decimal or FieldTypes.Currency or FieldTypes.Percentage;
    public static bool HasCurrency(string t) => t == FieldTypes.Currency;
    public static bool HasDateRange(string t) => t is FieldTypes.Date or FieldTypes.DateTime;
    public static bool HasOptions(string t) => FieldTypes.IsChoice(t);
    public static bool HasItemCount(string t) => t == FieldTypes.MultiChoice;
    public static bool HasList(string t) => t == FieldTypes.Lookup;
    public static bool HasCalculation(string t) => t == FieldTypes.Calculated;
    public static bool HasPrefill(string t) => t is FieldTypes.Text or FieldTypes.Integer or FieldTypes.Decimal or FieldTypes.Currency or FieldTypes.Percentage or FieldTypes.Date or FieldTypes.Email or FieldTypes.Url;
    public static bool HasYesNoPrefill(string t) => t == FieldTypes.Boolean;
    public static bool NeedsNoMoreSettings(string t) => t is FieldTypes.Person or FieldTypes.Email or FieldTypes.Url or FieldTypes.FileReference;

    public static bool CanRequire(string t) => t != FieldTypes.Calculated;
    public static bool CanSearch(string t) => t is FieldTypes.Text or FieldTypes.LongText or FieldTypes.Email or FieldTypes.Url or FieldTypes.SingleChoice or FieldTypes.MultiChoice or FieldTypes.Lookup;
    public static bool CanFilter(string t) => t is not (FieldTypes.LongText or FieldTypes.Url or FieldTypes.Calculated);
    public static bool CanSort(string t) => t is not (FieldTypes.LongText or FieldTypes.Url or FieldTypes.MultiChoice or FieldTypes.Calculated);
    public static bool CanUseInApprovals(string t) => t is not (FieldTypes.MultiChoice or FieldTypes.LongText or FieldTypes.FileReference or FieldTypes.Url or FieldTypes.Calculated);

    /// <summary>Cleans the rule flags so that a flag the type does not allow is never sent (a person changing the type never meets a refusal for a box they cannot see).</summary>
    public static (bool Required, bool Searchable, bool Filterable, bool Sortable, bool Approvals) Allowed(string t, bool required, bool searchable, bool filterable, bool sortable, bool approvals)
        => (required && CanRequire(t), searchable && CanSearch(t), filterable && CanFilter(t), sortable && CanSort(t), approvals && CanUseInApprovals(t));

    /// <summary>What a brand-new field of this type offers by default: findable, filterable, sortable where the type allows it, and part of exports.</summary>
    public static (bool Searchable, bool Filterable, bool Sortable, bool Reportable) Defaults(string t) => (CanSearch(t), CanFilter(t), CanSort(t), true);

    // ---- machine names, generated so nobody types them -------------------------------------------------------------------------------------------------

    /// <summary>A field key from its name: lower-case letters, digits and underscores, starting with a letter, never a core field's key, and different from every key already taken (retired fields included).</summary>
    public static string KeyFromName(string name, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var baseKey = sb.ToString().Trim('_');
        if (baseKey.Length == 0 || !char.IsAsciiLetter(baseKey[0])) baseKey = "field_" + baseKey;
        baseKey = baseKey.TrimEnd('_');
        if (baseKey.Length > 55) baseKey = baseKey[..55].TrimEnd('_');
        if (baseKey.Length < 2) baseKey = "field";
        var key = baseKey;
        for (var n = 2; CoreFields.IsCore(key) || used.Contains(key); n++) key = baseKey + "_" + n;
        return key;
    }

    /// <summary>The stored code of a new choice, taken from what the person typed so the stored value reads naturally ("30 days"). Unique within the list.</summary>
    public static string CodeFromLabel(string label, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var ch in label.Trim())
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '_' or '.' or '/' or '&' or '-' ? ch : '_');
        var code = sb.ToString().Trim(' ', '_', '.', '/', '&', '-');
        if (code.Length == 0) code = "Option";
        if (code.Length > 44) code = code[..44].TrimEnd();
        var candidate = code;
        for (var n = 2; used.Contains(candidate); n++) candidate = code + " " + n;
        return candidate;
    }

    // ---- plain-English descriptions --------------------------------------------------------------------------------------------------------------------

    /// <summary>Where a field shows up, in words. Tokens like [[contract]] are expanded by the caller with the organisation's own terminology.</summary>
    public static IReadOnlyList<string> UsedOn(FieldDefinition d)
    {
        var places = new List<string>();
        if (d.IsRetired) places.Add("Not offered any more (existing values still shown on [[contract|plural|lower]])");
        else if (d.IsCalculated) places.Add("[[contract]] details");
        else { places.Add("New [[contract]]"); places.Add("Edit [[contract]]"); places.Add("[[contract]] details"); }
        if (d.Searchable) places.Add("Search");
        if (d.Filterable) places.Add("Filter");
        if (d.Sortable) places.Add("Sort");
        if (d.Reportable) places.Add("Export");
        return places;
    }

    /// <summary>A workflow state code read as words, splitting at each capital letter. Only the label changes; the stored value stays the state's own code.</summary>
    public static string StageName(string code) => System.Text.RegularExpressions.Regex.Replace(code, "(?<=[a-z])(?=[A-Z])", " ", System.Text.RegularExpressions.RegexOptions.NonBacktracking);

    public static string Sentence(IEnumerable<string> parts)
    {
        var list = parts.Where(p => p.Length > 0).ToList();
        return list.Count switch { 0 => "", 1 => list[0], _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1] };
    }

    /// <summary>
    /// The plain-English statement of what publishing this field does. <paramref name="roleName"/> turns a role code into the words the organisation uses for it and <paramref name="date"/> writes a date in the organisation's format. Every line is a consequence the platform will really
    /// enforce; nothing is promised that the engine does not do.
    /// </summary>
    public static IReadOnlyList<string> Review(FieldDefinition d, string sectionLabel, CustomFieldSet set, Func<string, string> roleName, Func<DateOnly, string> date, bool published)
    {
        var lines = new List<string>();
        var where = d.IsRetired
            ? $"{d.Label} will no longer be offered on New [[contract]] or Edit [[contract]]. Values already entered are kept and still shown on the [[contract|plural|lower]] that hold them."
            : d.IsCalculated
                ? $"{d.Label} will appear under {sectionLabel} on [[contract]] details. It is worked out from other fields and is never typed in."
                : $"{d.Label} will appear under {sectionLabel} on New [[contract]], Edit [[contract]] and [[contract]] details.";
        lines.Add(where);

        if (!d.IsRetired)
        {
            var traits = new List<string> { d.Required ? "required" : "optional" };
            if (d.Searchable) traits.Add("searchable");
            if (d.Filterable) traits.Add("filterable");
            if (d.Sortable) traits.Add("sortable");
            if (d.Reportable) traits.Add("included in exports and reports");
            lines.Add($"It will be {Sentence(traits)}.");
            if (!d.Searchable && !d.Filterable && !d.Reportable) lines.Add("It will not be available in search, filters or exports.");
        }

        var options = set.ChoicesOf(d).Where(c => c.Active).Select(c => c.Label).ToList();
        if (d.Type == FieldTypes.SingleChoice) lines.Add($"People will choose one of: {string.Join(", ", options)}.");
        else if (d.Type == FieldTypes.MultiChoice) lines.Add($"People will choose one or more of: {string.Join(", ", options)}.");
        else if (d.Type == FieldTypes.Lookup && set.FindList(d.Lookup) is { } list) lines.Add($"People will choose from the shared list \"{list.Label}\".");
        var inactive = d.Choices.Where(c => !c.Active).Select(c => c.Label).ToList();
        if (inactive.Count > 0) lines.Add($"No longer offered, but kept on the records that hold them: {string.Join(", ", inactive)}.");

        if (d.VisibleToRoles.Count > 0) lines.Add($"Only people with these roles will see it: {string.Join(", ", d.VisibleToRoles.Select(roleName))}.");
        if (d.EditableByRoles.Count > 0 && !d.IsCalculated) lines.Add($"Only people with these roles can change it: {string.Join(", ", d.EditableByRoles.Select(roleName))}.");
        if (d.VisibleInStates.Count > 0) lines.Add($"It is shown only while the [[contract|lower]] is: {string.Join(", ", d.VisibleInStates)}.");
        if (d.EditableInStates.Count > 0 && !d.IsCalculated) lines.Add($"It can be changed only while the [[contract|lower]] is: {string.Join(", ", d.EditableInStates)}.");
        if (d.ContractTypes.Count > 0) lines.Add($"It applies only to these [[contract.type|plural|lower]]: {string.Join(", ", d.ContractTypes)}.");
        lines.Add($"Its values are held at the {d.Classification} level (or the [[contract|lower]]'s own level when that is more sensitive). People without access at that level will not see them, in the register, in an export or in an [[approval|lower]].");
        if (d.EffectiveFrom is { } from) lines.Add($"It starts on {date(from)}.");
        if (d.EffectiveTo is { } to) lines.Add($"It stops being offered after {date(to)}.");
        lines.Add(published
            ? "Changes apply from the moment they are published. Values already entered are kept; nothing is changed in bulk."
            : "Nothing changes for anyone until it is published. When it is, it is added without changing the database or any existing record.");
        return lines;
    }

    /// <summary>Whether the draft's definition differs from the published one (an addition counts as different).</summary>
    public static bool Differs(FieldDefinition? published, FieldDefinition working)
        => published is null || System.Text.Json.JsonSerializer.Serialize(published) != System.Text.Json.JsonSerializer.Serialize(working);

    /// <summary>The status words of the landing page, from what is published and what the open draft says.</summary>
    public static (string Key, string Text) StatusOf(FieldDefinition working, FieldDefinition? published)
    {
        if (published is null) return ("new", working.IsRetired ? "New and retired, not published yet" : "New, not published yet");
        if (!Differs(published, working)) return (working.IsRetired ? "retired" : "published", working.IsRetired ? "Retired" : "Published");
        if (working.IsRetired && !published.IsRetired) return ("retiring", "Retiring, not published yet");
        if (!working.IsRetired && published.IsRetired) return ("reinstating", "Reinstated, not published yet");
        return ("changed", "Changed, not published yet");
    }
}
