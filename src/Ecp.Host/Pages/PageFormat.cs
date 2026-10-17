using System.Globalization;
using Ecp.Configuration;
using Ecp.SharedKernel.Localisation;

namespace Ecp.Host.Pages;

/// <summary>
/// How a page shows dates, times, numbers and amounts for the organisation it is about (docs/REGIONAL_SETTINGS.md). Loaded once per request from the one regional resolver: the signed-in person's home organisation
/// first, then the organisation of the record a page calls <see cref="UseAsync"/> for. Stored instants are UTC and are converted here for display only. With no organisation the formats are the invariant ones
/// (ISO dates, UTC); nothing assumes a country.
/// </summary>
public sealed class PageFormat(IRegionalSettingsResolver regional, ILogger<PageFormat> logger)
{
    private LocaleFormatter _formatter = LocaleFormatter.Invariant;
    private RegionalSettings _settings = RegionalSettings.Unset;

    public LocaleFormatter Formatter => _formatter;
    public RegionalSettings Settings => _settings;
    public Guid? OrganisationId { get; private set; }

    public async Task UseAsync(Guid? organisationId, CancellationToken ct = default)
    {
        if (organisationId is not { } id || id == Guid.Empty) return;
        if (OrganisationId == id) return;
        try
        {
            _settings = await regional.GetAsync(id, ct);
            _formatter = await regional.FormatterAsync(id, ct);
            OrganisationId = id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Regional settings could not be loaded for a page; invariant formats are used.");
        }
    }

    public string Date(DateOnly date) => _formatter.Date(date);
    public string Date(DateOnly? date, string empty = "-") => date is { } d ? _formatter.Date(d) : empty;
    /// <summary>The calendar date of a UTC instant in the organisation's time zone.</summary>
    public string Date(DateTime utc) => _formatter.Date(utc);
    public string Date(DateTime? utc, string empty = "-") => utc is { } d ? _formatter.Date(d) : empty;
    /// <summary>A UTC instant as local date and time in the organisation's zone with its offset.</summary>
    public string DateTime(DateTime utc) => _formatter.DateTime(utc);
    public string DateTime(DateTime? utc, string empty = "-") => utc is { } d ? _formatter.DateTime(d) : empty;
    public string Number(decimal value, int decimals = 2) => _formatter.Number(value, decimals);
    /// <summary>An amount with its own currency. With neither a currency nor an organisation default, the amount is shown with a question mark rather than a guessed currency.</summary>
    public string Money(decimal amount, string? currency)
    {
        try { return _formatter.Money(amount, string.IsNullOrWhiteSpace(currency) ? null : currency); }
        catch (Ecp.SharedKernel.DomainException) { return amount.ToString("N2", CultureInfo.InvariantCulture) + " (currency not set)"; }
    }
    public string Money(decimal? amount, string? currency, string empty = "-") => amount is { } a ? Money(a, currency) : empty;
}
