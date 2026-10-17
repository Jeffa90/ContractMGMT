using System.Collections.Concurrent;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecp.Configuration;

/// <summary>The regional proposals of a solution pack (the layer beneath an organisation's own profile).</summary>
public sealed record PackRegional(string PackKey, string PackVersion, RegionalProfileDocument Document);

public sealed record RegionalCacheStatistics(long Hits, long Misses, long Fallbacks, int Entries);

/// <summary>
/// Remembers resolved regional settings. The key carries the tenant, the organisation and a version (the organisation's release, the pack identity and the stored locale, zone and currency), so one tenant's or
/// organisation's settings can never be served to another and a publish, a rollback or a stored-setting change is seen at once. A failure of the cache is never an error: the caller resolves from persistence.
/// </summary>
public interface IRegionalCache
{
    bool TryGet(Guid tenantId, Guid organisationId, string version, out RegionalSettings? settings);
    void Store(Guid tenantId, Guid organisationId, string version, RegionalSettings settings);
    void Invalidate(Guid tenantId, Guid organisationId);
    bool TryGetPack(string packKey, string? version, out PackRegional? pack);
    void StorePack(string packKey, string? version, PackRegional? pack);
    RegionalCacheStatistics Statistics { get; }
    long Generation { get; }
    void CountFallback();
    void Clear();
}

public sealed class RegionalCache(TimeProvider? time = null, TimeSpan? timeToLive = null, int maxEntries = 2000) : IRegionalCache
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _ttl = timeToLive ?? TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Org, string Version), (RegionalSettings Settings, DateTimeOffset Stored)> _sets = new();
    private readonly ConcurrentDictionary<(string Key, string Version), (PackRegional? Pack, DateTimeOffset Stored)> _packs = new();
    private long _hits, _misses, _fallbacks, _generation;

    public long Generation => Interlocked.Read(ref _generation);
    public RegionalCacheStatistics Statistics => new(Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _fallbacks), _sets.Count);

    public bool TryGet(Guid tenantId, Guid organisationId, string version, out RegionalSettings? settings)
    {
        if (_sets.TryGetValue((tenantId, organisationId, version), out var e) && _time.GetUtcNow() - e.Stored < _ttl) { Interlocked.Increment(ref _hits); settings = e.Settings; return true; }
        Interlocked.Increment(ref _misses); settings = null; return false;
    }

    public void Store(Guid tenantId, Guid organisationId, string version, RegionalSettings settings)
    {
        if (_sets.Count >= maxEntries) _sets.Clear();
        _sets[(tenantId, organisationId, version)] = (settings, _time.GetUtcNow());
    }

    public void Invalidate(Guid tenantId, Guid organisationId)
    {
        foreach (var key in _sets.Keys.Where(k => k.Tenant == tenantId && k.Org == organisationId).ToList()) _sets.TryRemove(key, out _);
        Interlocked.Increment(ref _generation);
    }

    public bool TryGetPack(string packKey, string? version, out PackRegional? pack)
    {
        if (_packs.TryGetValue((packKey, version ?? "*"), out var e) && _time.GetUtcNow() - e.Stored < _ttl) { pack = e.Pack; return true; }
        pack = null; return false;
    }

    public void StorePack(string packKey, string? version, PackRegional? pack)
    {
        if (_packs.Count >= maxEntries) _packs.Clear();
        _packs[(packKey, version ?? "*")] = (pack, _time.GetUtcNow());
    }

    public void CountFallback() => Interlocked.Increment(ref _fallbacks);
    public void Clear() { _sets.Clear(); _packs.Clear(); Interlocked.Increment(ref _generation); }
}

/// <summary>
/// Combines the layers into one answer. Pure: no reads, so every order of precedence is testable. With a published profile the order is the organisation's own value, then the pack version the profile is pinned to, then the
/// organisation's stored setting (the compatibility layer that keeps an existing organisation exactly as it was); without a profile it is the stored setting, then the assigned pack. The installation's fall-back applies only
/// where no organisation exists. Nothing is assumed beyond that.
/// </summary>
public static class RegionalComposer
{
    public static RegionalSettings Compose(RegionalProfileDocument? profile, int? releaseNumber, PackRegional? pack, OrganisationSummary? record, LocalisationOptions? installation)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var packDoc = pack?.Document;
        var packLabel = pack is null ? "pack" : $"pack {pack.PackKey} {pack.PackVersion}";

        T? Pick<T>(string field, T? own, T? fromPack, T? stored, T? installed) where T : class
        {
            if (profile is not null)
            {
                if (own is not null) { sources[field] = "profile"; return own; }
                if (fromPack is not null) { sources[field] = packLabel; return fromPack; }
                if (stored is not null) { sources[field] = "record"; return stored; }
            }
            else
            {
                if (stored is not null) { sources[field] = "record"; return stored; }
                if (fromPack is not null) { sources[field] = packLabel; return fromPack; }
            }
            if (installed is not null) { sources[field] = "installation"; return installed; }
            return null;
        }
        static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

        var org = record is not null;
        var locale = Pick("locale", profile?.Locale, packDoc?.Locale, org ? Blank(record!.CultureCode) : null, org ? null : Blank(installation?.DefaultCulture));
        var currency = Pick("currency", profile?.Currency, packDoc?.Currency, org ? Blank(record!.DefaultCurrencyCode) : null, org ? null : Blank(installation?.DefaultCurrencyCode));
        var zone = Pick("timeZone", profile?.TimeZone, packDoc?.TimeZone, org ? Blank(record!.TimeZoneId) : null, org ? null : Blank(installation?.DefaultTimeZoneId));
        var country = Pick("country", profile?.Country, packDoc?.Country, null, null);
        var phone = Pick("phoneRegion", profile?.PhoneRegion, packDoc?.PhoneRegion, null, null);

        IReadOnlyList<string> locales = profile?.SupportedLocales ?? packDoc?.SupportedLocales ?? [];
        if (locale is not null && !locales.Contains(locale, StringComparer.OrdinalIgnoreCase)) locales = [locale, .. locales];
        IReadOnlyList<string> currencies = profile?.SupportedCurrencies ?? packDoc?.SupportedCurrencies ?? [];
        if (currency is not null && !currencies.Contains(currency, StringComparer.Ordinal)) currencies = [currency, .. currencies];

        var identifiers = profile?.Identifiers ?? packDoc?.Identifiers ?? [];
        sources["identifiers"] = profile?.Identifiers is not null ? "profile" : packDoc?.Identifiers is not null ? packLabel : "unset";
        var address = profile?.Address ?? packDoc?.Address ?? AddressProfile.Neutral;
        sources["address"] = profile?.Address is not null ? "profile" : packDoc?.Address is not null ? packLabel : "unset";
        var display = profile?.TimeDisplay ?? packDoc?.TimeDisplay ?? TimeDisplay.Locale;
        sources["timeDisplay"] = profile?.TimeDisplay is not null ? "profile" : packDoc?.TimeDisplay is not null ? packLabel : "unset";

        var source = profile is not null ? $"organisation profile, release {releaseNumber?.ToString() ?? "(draft)"}" + (profile.Source is { } s ? $", inheriting from {s.PackKey} {s.PackVersion}" : "")
            : org ? "organisation settings (stored when the organisation was set up)" + (pack is null ? "" : $", with {packLabel} for identifiers and address") : "installation fall-back";
        return new RegionalSettings(country, locale, locales, currency, currencies, zone, display, identifiers, address, phone, source, releaseNumber, pack?.PackKey, pack?.PackVersion, sources);
    }
}

/// <summary>
/// The one place anything asks for an organisation's locale, currency, time zone, identifier profiles or address profile (docs/REGIONAL_SETTINGS.md). Results are remembered for the life of a request and cached by tenant,
/// organisation and version. If the configuration cannot be read the answer is <see cref="RegionalSettings.Unset"/>-like and the failure is logged: a page then shows invariant formats rather than guessing a country.
/// </summary>
public interface IRegionalSettingsResolver
{
    Task<RegionalSettings> GetAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>What the organisation would have if <paramref name="draft"/> replaced its published profile. Nothing is cached.</summary>
    Task<RegionalSettings> PreviewAsync(Guid organisationId, RegionalProfileDocument draft, CancellationToken ct = default);
    /// <summary>A formatter for the organisation, built once per request.</summary>
    Task<LocaleFormatter> FormatterAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>What a pack proposes (the layer beneath the organisation's profile), for the pack the organisation works from or a named pack version.</summary>
    Task<PackRegional?> PackLayerAsync(Guid organisationId, string? packKey = null, string? packVersion = null, CancellationToken ct = default);
    /// <summary>The settings for output no organisation owns (a diagnostic, the first page of setup): the installation fall-back if one is configured, otherwise nothing is decided.</summary>
    RegionalSettings Installation();
}

internal sealed class RegionalResolver(IServiceProvider services, ConfigurationDbContext db, ITenantContext tenant, IRegionalCache cache, ConfigurationOptions options, LocalisationOptions installation, ILogger<RegionalResolver> logger)
    : IRegionalSettingsResolver
{
    // Resolved on first use: the configuration service builds every kind, and kinds in other modules depend on things that ask this resolver.
    private IConfigurationReleases releases => services.GetRequiredService<IConfigurationReleases>();
    private IOrganisationStructureService organisations => services.GetRequiredService<IOrganisationStructureService>();
    private IPackLoader packs => services.GetRequiredService<IPackLoader>();

    private readonly Dictionary<(Guid Tenant, Guid Organisation), RegionalSettings> _remembered = new();
    private readonly Dictionary<(Guid Tenant, Guid Organisation), LocaleFormatter> _formatters = new();
    private long _generation = -1;

    private void Refresh()
    {
        var generation = cache.Generation;
        if (generation == _generation) return;
        _remembered.Clear(); _formatters.Clear(); _generation = generation;
    }

    public RegionalSettings Installation() => RegionalComposer.Compose(null, null, null, null, installation);

    public async Task<LocaleFormatter> FormatterAsync(Guid organisationId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tenantId || organisationId == Guid.Empty) return Installation().IsComplete ? Installation().Formatter() : LocaleFormatter.Invariant;
        Refresh();
        if (_formatters.TryGetValue((tenantId, organisationId), out var known)) return known;
        var settings = await GetAsync(organisationId, ct);
        LocaleFormatter formatter;
        try { formatter = settings.IsComplete ? settings.Formatter() : new LocaleFormatter(settings.PrimaryLocale ?? "", settings.TimeZoneId ?? "UTC", settings.DefaultCurrency, settings.TimeDisplay); }
        catch (DomainException ex) { logger.LogWarning(ex, "An organisation's regional settings could not be turned into a formatter; invariant formats are used."); formatter = LocaleFormatter.Invariant; }
        return _formatters[(tenantId, organisationId)] = formatter;
    }

    public async Task<RegionalSettings> GetAsync(Guid organisationId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tenantId || organisationId == Guid.Empty) return Installation();
        Refresh();
        if (_remembered.TryGetValue((tenantId, organisationId), out var remembered)) return remembered;
        var resolved = await ResolveAsync(tenantId, organisationId, ct);
        _remembered[(tenantId, organisationId)] = resolved;
        return resolved;
    }

    private async Task<RegionalSettings> ResolveAsync(Guid tenantId, Guid organisationId, CancellationToken ct)
    {
        try
        {
            var record = await organisations.GetOrganisationAsync(organisationId, ct);
            var release = await releases.LatestAsync(RegionalKinds.Name, organisationId, ct);
            var profile = release is null ? null : SafeParse(release.ContentJson);
            var pack = await PackLayerCoreAsync(record, profile?.Source, ct);
            var version = $"{release?.Id.ToString("N") ?? "-"}|{pack?.PackKey ?? "-"}@{pack?.PackVersion ?? "-"}|{record?.CultureCode}|{record?.TimeZoneId}|{record?.DefaultCurrencyCode}";
            try { if (cache.TryGet(tenantId, organisationId, version, out var hit) && hit is not null) return hit; }
            catch (Exception ex) { cache.CountFallback(); logger.LogWarning(ex, "The regional cache could not be read; resolving from persistence."); }
            var settings = RegionalComposer.Compose(profile, release?.Number, pack, record, null);
            try { cache.Store(tenantId, organisationId, version, settings); }
            catch (Exception ex) { cache.CountFallback(); logger.LogWarning(ex, "The regional cache could not be written; the settings were resolved from persistence."); }
            return settings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Regional settings could not be resolved; no country, locale, currency or zone is assumed.");
            return RegionalSettings.Unset;
        }
    }

    public async Task<RegionalSettings> PreviewAsync(Guid organisationId, RegionalProfileDocument draft, CancellationToken ct = default)
    {
        var record = await organisations.GetOrganisationAsync(organisationId, ct);
        var pack = await PackLayerCoreAsync(record, draft.Source, ct);
        return RegionalComposer.Compose(draft, null, pack, record, null);
    }

    public async Task<PackRegional?> PackLayerAsync(Guid organisationId, string? packKey = null, string? packVersion = null, CancellationToken ct = default)
    {
        var record = await organisations.GetOrganisationAsync(organisationId, ct);
        return await PackLayerCoreAsync(record, packKey is null ? null : new RegionalSource(packKey, packVersion ?? ""), ct);
    }

    private async Task<PackRegional?> PackLayerCoreAsync(OrganisationSummary? record, RegionalSource? pinned, CancellationToken ct)
    {
        try
        {
            string? packKey; string? version;
            if (pinned is { } p) { packKey = p.PackKey; version = string.IsNullOrWhiteSpace(p.PackVersion) ? null : p.PackVersion; }
            else
            {
                if (record is null) return null;
                packKey = record.SolutionPackKey;
                if (string.IsNullOrWhiteSpace(packKey)) packKey = options.DefaultPackId;
                if (string.IsNullOrWhiteSpace(packKey)) return null;
                version = await db.PackAssignments.AsNoTracking().Where(a => a.OrganisationId == record.Id && a.PackKey == packKey && a.Status == PackAssignment.Active).Select(a => a.Version).FirstOrDefaultAsync(ct);
            }
            if (cache.TryGetPack(packKey, version, out var known)) return known;
            var entry = await packs.FindAsync(packKey, version, usableOnly: version is null, ct);
            if (entry is null || entry.Unavailable || !entry.IsCompatible || !entry.Trust.IsAcceptable) { cache.StorePack(packKey, version, null); return null; }
            var json = await packs.GetDocumentAsync(entry.PackKey, entry.Version, RegionalKinds.Name, ct);
            var doc = json is null ? null : SafeParse(json);
            var layer = doc is null ? null : new PackRegional(entry.PackKey, entry.Version, doc);
            cache.StorePack(packKey, version, layer);
            return layer;
        }
        catch (Exception ex) when (ex is DomainException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "The solution pack's regional proposals could not be read; they are not applied.");
            return null;
        }
    }

    private static RegionalProfileDocument? SafeParse(string json) { try { return RegionalProfileDocument.Parse(json); } catch (DomainException) { return null; } }
}

/// <summary>
/// The regional-profile configuration kind: versioned, validated against the catalogues, previewed, published, rolled back and audited by the ordinary lifecycle. Publishing makes the profile effective by recording the
/// effective locale, time zone and default currency on the organisation (the compatibility layer every older reader still uses) and dropping its cached settings. Timestamps already stored stay UTC and no amount changes.
/// </summary>
internal sealed class RegionalProfileKind(IRegionalCache cache, ITenantContext tenant, IServiceProvider services) : IConfigurationKind
{
    public string Kind => RegionalKinds.Name;

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        RegionalProfileDocument doc;
        try { doc = RegionalProfileDocument.Parse(contentJson); }
        catch (DomainException ex) { return new ValidationReport([new ValidationMessage(ValidationSeverity.Error, "$", ex.Code, ex.Message)]); }
        var messages = doc.Check().ToList();
        if (messages.Any(m => m.Severity == ValidationSeverity.Error)) return new ValidationReport(messages);
        // A pack's document (validated with no organisation) is data for many organisations; nothing about any one organisation is looked up.
        if (organisationId == Guid.Empty) return new ValidationReport(messages);

        if (doc.Mode != RegionalProfileDocument.Overrides) { messages.Add(new(ValidationSeverity.Error, "$.mode", "regional_mode", "An organisation's regional profile has the mode 'overrides'.")); return new ValidationReport(messages); }
        var resolver = services.GetRequiredService<IRegionalSettingsResolver>();
        if (doc.Source is { } src && await resolver.PackLayerAsync(organisationId, src.PackKey, src.PackVersion, ct) is null)
            messages.Add(new(ValidationSeverity.Warning, "$.source", "regional_source_missing", $"The pack {src.PackKey} {src.PackVersion} has no regional proposals here, so nothing is inherited from it."));
        var effective = await resolver.PreviewAsync(organisationId, doc, ct);
        if (!effective.IsComplete)
            messages.Add(new(ValidationSeverity.Warning, "$", "regional_incomplete", "The locale, default currency or time zone is not decided. Dates, times and amounts are shown in an invariant form, and Production readiness reports this organisation, until all three are set."));
        if (effective.DefaultCurrency is { } cur && effective.SupportedCurrencies.Count > 0 && !effective.SupportedCurrencies.Contains(cur, StringComparer.Ordinal))
            messages.Add(new(ValidationSeverity.Error, "$.supportedCurrencies", "regional_default_supported", "The supported currencies must include the default currency."));
        return new ValidationReport(messages);
    }

    public async Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct)
    {
        var resolver = services.GetRequiredService<IRegionalSettingsResolver>();
        var organisations = services.GetRequiredService<IOrganisationStructureService>();
        var doc = RegionalProfileDocument.Parse(contentJson);
        var effective = await resolver.PreviewAsync(organisationId, doc, ct);
        // The stored settings keep any value the profile leaves undecided, so an organisation is never left without a locale, zone or currency by a partial profile.
        var record = await organisations.GetOrganisationAsync(organisationId, ct);
        if (record is not null && effective.PrimaryLocale is { } locale && effective.TimeZoneId is { } zone && effective.DefaultCurrency is { } currency)
            await organisations.SetRegionalAsync(organisationId, zone, locale, currency, ct);
        if (tenant.TenantId is { } t) { try { cache.Invalidate(t, organisationId); } catch { /* the cache key carries the release, so a failed invalidation is harmless */ } }
    }
}
