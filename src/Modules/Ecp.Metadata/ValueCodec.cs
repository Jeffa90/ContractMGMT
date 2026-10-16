using System.Globalization;
using System.Text.RegularExpressions;

namespace Ecp.Metadata;

/// <summary>One stored value of a field, in the typed slot its type uses. A multiple-choice field has one per chosen item.</summary>
public sealed record TypedValue(string? Text = null, decimal? Number = null, DateOnly? Date = null, DateTime? DateTimeUtc = null, bool? Bool = null, Guid? Ref = null);

public sealed record ParseResult(IReadOnlyList<TypedValue> Values, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public bool IsEmpty => Values.Count == 0;
    public static ParseResult Empty { get; } = new([], []);
    public static ParseResult Fail(string error) => new([], [error]);
}

/// <summary>
/// Turns what a person (or a source file) typed into the typed value of a field, applying the type's rules and the field's predefined checks, and turns stored values back into text.
/// Pure and culture-free on the way in (invariant text); the caller formats for display. Nothing here evaluates anything the definition's author wrote except a regular expression, which
/// runs in the non-backtracking engine with a time limit.
/// </summary>
public static class ValueCodec
{
    public const int MaxText = 400, MaxLongText = 4000, MaxUrl = 2000;
    private static readonly TimeSpan PatternLimit = TimeSpan.FromMilliseconds(100);
    private static readonly Regex EmailPattern = new(@"^[^@\s]{1,64}@[^@\s]{1,255}\.[^@\s.]{2,}$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the raw strings entered for a field. Blank input is "no value" (valid; whether a value is required is the caller's rule). A retired or inactive choice already stored on the contract
    /// is accepted again as it is (<paramref name="keep"/>) so editing other fields does not fail on history.
    /// </summary>
    public static ParseResult Parse(FieldDefinition d, CustomFieldSet set, IReadOnlyList<string?> raw, IReadOnlySet<string>? keep = null)
    {
        if (d.IsCalculated) return ParseResult.Fail($"{d.Label} is calculated and cannot be set.");
        var inputs = raw.Select(r => r ?? "").Select(r => d.Type == FieldTypes.LongText ? r : r.Trim()).Where(r => r.Trim().Length > 0).ToList();
        if (inputs.Count == 0) return ParseResult.Empty;
        var v = d.Validation;
        var errors = new List<string>();
        var values = new List<TypedValue>();
        if (!FieldTypes.IsMulti(d.Type) && inputs.Count > 1) return ParseResult.Fail($"{d.Label} takes one value.");

        foreach (var text in inputs)
        {
            switch (d.Type)
            {
                case FieldTypes.Text:
                    if (text.Any(char.IsControl)) errors.Add($"{d.Label} must not contain control characters or line breaks.");
                    else if (text.Length > Math.Min(v.MaxLength ?? MaxText, MaxText)) errors.Add($"{d.Label} must be at most {Math.Min(v.MaxLength ?? MaxText, MaxText)} characters.");
                    else if (v.MinLength is { } min && text.Length < min) errors.Add($"{d.Label} must be at least {min} characters.");
                    else if (v.Pattern is not null && !Matches(v.Pattern, text)) errors.Add(v.PatternMessage ?? $"{d.Label} is not in the expected format.");
                    else values.Add(new TypedValue(Text: text));
                    break;
                case FieldTypes.LongText:
                    var normal = text.Replace("\r\n", "\n").Replace('\r', '\n');
                    if (normal.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) errors.Add($"{d.Label} must not contain control characters.");
                    else if (normal.Length > Math.Min(v.MaxLength ?? MaxLongText, MaxLongText)) errors.Add($"{d.Label} must be at most {Math.Min(v.MaxLength ?? MaxLongText, MaxLongText)} characters.");
                    else if (v.MinLength is { } lmin && normal.Trim().Length < lmin) errors.Add($"{d.Label} must be at least {lmin} characters.");
                    else values.Add(new TypedValue(Text: normal.Trim()));
                    break;
                case FieldTypes.Integer:
                    if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) || whole != decimal.Truncate(whole) || Math.Abs(whole) >= 1e12m) errors.Add($"{d.Label} must be a whole number.");
                    else AddNumber(d, whole, errors, values);
                    break;
                case FieldTypes.Decimal:
                case FieldTypes.Currency:
                case FieldTypes.Percentage:
                    if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number) || Math.Abs(number) >= 1e15m) errors.Add($"{d.Label} must be a number.");
                    else if (DecimalPlaces(number) > (v.Decimals ?? DefaultDecimals(d.Type))) errors.Add($"{d.Label} allows at most {v.Decimals ?? DefaultDecimals(d.Type)} decimal places.");
                    else AddNumber(d, number, errors, values);
                    break;
                case FieldTypes.Date:
                    if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) errors.Add($"{d.Label} must be a date (yyyy-mm-dd).");
                    else if (v.MinDate is { } dmin && date < dmin) errors.Add($"{d.Label} must not be before {dmin:yyyy-MM-dd}.");
                    else if (v.MaxDate is { } dmax && date > dmax) errors.Add($"{d.Label} must not be after {dmax:yyyy-MM-dd}.");
                    else values.Add(new TypedValue(Date: date));
                    break;
                case FieldTypes.DateTime:
                    if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)) errors.Add($"{d.Label} must be a date and time.");
                    else if (v.MinDate is { } tmin && DateOnly.FromDateTime(when.UtcDateTime) < tmin) errors.Add($"{d.Label} must not be before {tmin:yyyy-MM-dd}.");
                    else if (v.MaxDate is { } tmax && DateOnly.FromDateTime(when.UtcDateTime) > tmax) errors.Add($"{d.Label} must not be after {tmax:yyyy-MM-dd}.");
                    else values.Add(new TypedValue(DateTimeUtc: DateTime.SpecifyKind(new DateTime(when.UtcDateTime.Ticks - when.UtcDateTime.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc)));
                    break;
                case FieldTypes.Boolean:
                    switch (text.ToLowerInvariant())
                    {
                        case "true" or "yes" or "y" or "on" or "1": values.Add(new TypedValue(Bool: true)); break;
                        case "false" or "no" or "n" or "off" or "0": values.Add(new TypedValue(Bool: false)); break;
                        default: errors.Add($"{d.Label} must be yes or no."); break;
                    }
                    break;
                case FieldTypes.SingleChoice:
                case FieldTypes.MultiChoice:
                case FieldTypes.Lookup:
                    var choice = set.FindChoice(d, text);
                    if (choice is null) errors.Add($"'{Shorten(text)}' is not one of the choices for {d.Label}.");
                    else if (!choice.Active && keep?.Contains(choice.Code) != true) errors.Add($"'{choice.Label}' is no longer available for {d.Label}.");
                    else if (values.Any(x => string.Equals(x.Text, choice.Code, StringComparison.Ordinal))) { /* the same item twice is one item */ }
                    else values.Add(new TypedValue(Text: choice.Code));
                    break;
                case FieldTypes.Person:
                case FieldTypes.FileReference:
                    if (!Guid.TryParse(text, out var id) || id == Guid.Empty) errors.Add($"{d.Label} must be chosen from the list.");
                    else values.Add(new TypedValue(Ref: id));
                    break;
                case FieldTypes.Email:
                    if (text.Length > 254 || text.Any(char.IsControl) || !EmailPattern.IsMatch(text)) errors.Add($"{d.Label} must be an email address.");
                    else values.Add(new TypedValue(Text: text));
                    break;
                case FieldTypes.Url:
                    if (!TryUrl(text, out var url)) errors.Add($"{d.Label} must be a web address starting with https:// or http://.");
                    else values.Add(new TypedValue(Text: url));
                    break;
                default:
                    errors.Add($"{d.Label} has a type that cannot hold a value.");
                    break;
            }
        }
        if (errors.Count > 0) return new ParseResult([], errors);
        if (FieldTypes.IsMulti(d.Type))
        {
            if (v.MinItems is { } mi && values.Count < mi) return ParseResult.Fail($"{d.Label} needs at least {mi} choice(s).");
            if (v.MaxItems is { } ma && values.Count > ma) return ParseResult.Fail($"{d.Label} allows at most {ma} choice(s).");
        }
        return new ParseResult(values, []);
    }

    private static void AddNumber(FieldDefinition d, decimal n, List<string> errors, List<TypedValue> values)
    {
        var v = d.Validation;
        var (lo, hi) = d.Type == FieldTypes.Percentage ? (v.Min ?? 0m, v.Max ?? 100m) : (v.Min, v.Max);
        if (lo is { } min && n < min) errors.Add($"{d.Label} must be at least {min.ToString(CultureInfo.InvariantCulture)}.");
        else if (hi is { } max && n > max) errors.Add($"{d.Label} must be at most {max.ToString(CultureInfo.InvariantCulture)}.");
        else values.Add(new TypedValue(Number: n));
    }

    public static int DefaultDecimals(string type) => type switch { FieldTypes.Currency => 2, FieldTypes.Percentage => 2, FieldTypes.Integer => 0, _ => 4 };
    private static int DecimalPlaces(decimal n) { var t = n / 1.000000000000000000000000000000000m; return (decimal.GetBits(t)[3] >> 16) & 0xFF; }
    /// <summary>A number without trailing zeros, so 1234.50 and 1234.50000000 (as the database returns it) are the same text.</summary>
    public static string Plain(decimal n) => (n / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "...";

    private static bool Matches(string pattern, string text)
    {
        try { return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternLimit).IsMatch(text); }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException) { return false; }
    }

    /// <summary>True when the pattern can be used: compiled by the non-backtracking engine, short, with no features that engine refuses.</summary>
    public static bool IsAcceptablePattern(string pattern, out string? reason)
    {
        reason = null;
        if (pattern.Length is 0 or > 200) { reason = "A pattern must be between 1 and 200 characters."; return false; }
        try { _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternLimit); return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { reason = "The pattern is not supported (back-references, look-around and similar features are not allowed): " + ex.Message.Split('\n')[0]; return false; }
    }

    private static bool TryUrl(string text, out string url)
    {
        url = "";
        if (text.Length > MaxUrl || text.Any(char.IsControl) || text.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    // ---- going back out ---------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The canonical text of each stored value: what a form posts back, an audit entry records and a reporting projection carries.</summary>
    public static IReadOnlyList<string> ToRaw(FieldDefinition d, IReadOnlyList<TypedValue> values)
        => values.Select(v => d.Type switch
        {
            FieldTypes.Integer or FieldTypes.Decimal or FieldTypes.Currency or FieldTypes.Percentage => Plain(v.Number ?? 0),
            FieldTypes.Date => v.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            FieldTypes.DateTime => v.DateTimeUtc?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "",
            FieldTypes.Boolean => v.Bool == true ? "true" : "false",
            FieldTypes.Person or FieldTypes.FileReference => v.Ref?.ToString("D") ?? "",
            _ => v.Text ?? ""
        }).ToList();

    /// <summary>Text for people: choice labels instead of codes, dates in the culture and time zone given, amounts with their currency. A stored code that is no longer defined shows as it is.</summary>
    public static string Format(FieldDefinition d, CustomFieldSet set, IReadOnlyList<TypedValue> values, CultureInfo culture, TimeZoneInfo? zone = null, string? fallbackCurrency = null)
    {
        string One(TypedValue v) => d.Type switch
        {
            FieldTypes.Integer => (v.Number ?? 0).ToString("N0", culture),
            FieldTypes.Decimal => (v.Number ?? 0).ToString("#,0.####", culture),
            FieldTypes.Currency => ((d.Validation.Currency ?? fallbackCurrency) is { } cur ? cur + " " : "") + (v.Number ?? 0).ToString("#,0.00", culture),
            FieldTypes.Percentage => (v.Number ?? 0).ToString("0.##", culture) + "%",
            FieldTypes.Date => v.Date?.ToString("d", culture) ?? "",
            FieldTypes.DateTime => v.DateTimeUtc is { } dt ? TimeZoneInfo.ConvertTimeFromUtc(dt, zone ?? TimeZoneInfo.Utc).ToString("g", culture) : "",
            FieldTypes.Boolean => v.Bool == true ? "Yes" : "No",
            FieldTypes.SingleChoice or FieldTypes.MultiChoice or FieldTypes.Lookup => set.ChoicesOf(d).FirstOrDefault(c => string.Equals(c.Code, v.Text, StringComparison.Ordinal))?.Label ?? v.Text ?? "",
            FieldTypes.Person or FieldTypes.FileReference => v.Ref?.ToString("D") ?? "",
            _ => v.Text ?? ""
        };
        return string.Join("; ", values.Select(One));
    }
}
