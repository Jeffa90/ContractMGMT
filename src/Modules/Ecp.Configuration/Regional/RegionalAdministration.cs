using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;

namespace Ecp.Configuration;

/// <summary>One line of a preview: what a person would read for a sample value under the settings being previewed.</summary>
public sealed record RegionalSample(string Label, string Value);

/// <summary>What an organisation's regional settings are, where each value comes from, and what a draft would change.</summary>
public sealed record RegionalOverview(Guid OrganisationId, string OrganisationName, RegionalSettings Effective, PackRegional? Pack, ReleaseDto? Latest, DraftDto? Draft,
    IReadOnlyList<IdentifierTypeProfile> OfferedProfiles, IReadOnlyList<RegionalSample> Samples);

public sealed record RegionalPreview(RegionalSettings Settings, IReadOnlyList<RegionalSample> Samples, IReadOnlyList<ValidationMessage> Messages);

/// <summary>
/// The form-based administration of regional settings (docs/REGIONAL_SETTINGS.md), as a service: the overview, the working document, saving a draft and previewing one. Drafting, validating, publishing and rolling back go
/// through the ordinary configuration lifecycle, so the permissions (<c>config.draft</c>, <c>config.publish</c>, <c>config.rollback</c>, <c>config.history.read</c>), second-person publication, versioning and audit apply.
/// </summary>
public interface IRegionalAdministration
{
    Task<RegionalOverview> OverviewAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>The draft if one is open, otherwise the published profile, otherwise an empty profile that inherits from the organisation's pack.</summary>
    Task<RegionalProfileDocument> WorkingDocumentAsync(Guid organisationId, CancellationToken ct = default);
    Task<DraftDto> SaveDraftAsync(Guid organisationId, RegionalProfileDocument document, CancellationToken ct = default);
    /// <summary>The settings the document would give, with sample values written in them. Nothing is saved or cached.</summary>
    Task<RegionalPreview> PreviewAsync(Guid organisationId, RegionalProfileDocument document, CancellationToken ct = default);
    /// <summary>The identifier profiles the installed packs carry, by key. An organisation chooses from them; nothing is chosen for it.</summary>
    Task<IReadOnlyList<IdentifierTypeProfile>> OfferedProfilesAsync(CancellationToken ct = default);
}

/// <summary>Sample values written in an organisation's formats, shared by the administration page, the onboarding preview and the setup preview.</summary>
public static class RegionalSamples
{
    // Fixed instants chosen so that day-first and month-first orders differ, and so that a zone with daylight saving shows both of its offsets.
    private static readonly DateOnly SampleDate = new(2026, 10, 16);
    private static readonly DateTime WinterUtc = new(2026, 1, 15, 14, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime SummerUtc = new(2026, 7, 15, 14, 30, 0, DateTimeKind.Utc);

    public static IReadOnlyList<RegionalSample> Build(RegionalSettings s)
    {
        // A value the host cannot use (an unavailable zone, an unknown locale) must not break the page that is there to show the problem.
        try { return BuildCore(s); }
        catch (DomainException ex) { return [new("Preview", "Not available until the problems listed are corrected (" + ex.Message + ").")]; }
    }

    private static List<RegionalSample> BuildCore(RegionalSettings s)
    {
        var samples = new List<RegionalSample>();
        var fmt = s.Formatter();
        var undecided = "(not decided: shown in the invariant form)";
        samples.Add(new("Date", s.PrimaryLocale is null ? SampleDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " " + undecided : fmt.Date(SampleDate)));
        samples.Add(new("Date and time, 15 January (UTC 14:30)", s.TimeZoneId is null ? undecided : fmt.DateTime(WinterUtc)));
        samples.Add(new("Date and time, 15 July (UTC 14:30)", s.TimeZoneId is null ? undecided : fmt.DateTime(SummerUtc)));
        if (s.TimeZoneId is not null && TimeZones.TryFind(s.TimeZoneId, out var zone))
            samples.Add(new("Offset from UTC in January and July", $"{Offset(zone.GetUtcOffset(WinterUtc))} and {Offset(zone.GetUtcOffset(SummerUtc))}" + (zone.SupportsDaylightSavingTime ? " (the zone changes its clocks)" : " (no clock change)")));
        samples.Add(new("Number", s.PrimaryLocale is null ? undecided : fmt.Number(1234567.891m)));
        if (s.DefaultCurrency is { } currency)
        {
            samples.Add(new("Amount, as a person reads it", fmt.Money(1234.5m, currency)));
            samples.Add(new("Amount with its currency code (exports and messages)", fmt.MoneyWithCode(1234.5m, currency)));
        }
        else samples.Add(new("Amount", "(no default currency decided: every amount must say its own currency)"));
        samples.Add(new("Address fields used", string.Join(", ", AddressProfile.FieldKeys.Where(k => s.Address.Spec(k).Used).Select(k => k + (s.Address.Spec(k).Required ? " (required)" : "")))));
        samples.Add(new("Tax identifier types", s.Identifiers.Count == 0 ? "(none set up: no identifier is recorded or asked for)" : string.Join(", ", s.Identifiers.OrderBy(i => i.Order).Select(i => i.LabelOr("Tax Identifier") + (i.Country is null ? "" : " [" + i.Country + "]")))));
        return samples;
    }

    private static string Offset(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm");
}

internal sealed class RegionalAdministration(IConfigurationService configuration, IRegionalSettingsResolver resolver, IOrganisationStructureService organisations, IPackLoader packs) : IRegionalAdministration
{
    public async Task<RegionalOverview> OverviewAsync(Guid organisationId, CancellationToken ct = default)
    {
        var org = await organisations.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();
        var effective = await resolver.GetAsync(organisationId, ct);
        var pack = await resolver.PackLayerAsync(organisationId, null, null, ct);
        var latest = await configuration.LatestReleaseAsync(RegionalKinds.Name, organisationId, ct);
        var draft = await configuration.FindOpenDraftAsync(RegionalKinds.Name, organisationId, ct);
        return new RegionalOverview(organisationId, org.Name, effective, pack, latest, draft, await OfferedProfilesAsync(ct), RegionalSamples.Build(effective));
    }

    public async Task<RegionalProfileDocument> WorkingDocumentAsync(Guid organisationId, CancellationToken ct = default)
    {
        var draft = await configuration.FindOpenDraftAsync(RegionalKinds.Name, organisationId, ct);
        if (draft is not null) return RegionalProfileDocument.Parse(draft.ContentJson);
        var latest = await configuration.LatestReleaseAsync(RegionalKinds.Name, organisationId, ct);
        if (latest is not null) return RegionalProfileDocument.Parse(latest.ContentJson);
        var pack = await resolver.PackLayerAsync(organisationId, null, null, ct);
        return pack is null ? RegionalProfileDocument.Empty : RegionalProfileDocument.Empty.InheritingFrom(pack.PackKey, pack.PackVersion);
    }

    public async Task<DraftDto> SaveDraftAsync(Guid organisationId, RegionalProfileDocument document, CancellationToken ct = default)
    {
        var json = document.ToJson();
        var draft = await configuration.GetOrCreateDraftAsync(RegionalKinds.Name, organisationId, json, ct);
        return await configuration.SaveDraftAsync(draft.Id, json, ct);
    }

    public async Task<RegionalPreview> PreviewAsync(Guid organisationId, RegionalProfileDocument document, CancellationToken ct = default)
    {
        var messages = document.Check().ToList();
        var settings = await resolver.PreviewAsync(organisationId, document, ct);
        if (!settings.IsComplete) messages.Add(new(ValidationSeverity.Warning, "$", "regional_incomplete", "The locale, default currency or time zone is not decided yet. Production readiness reports this organisation until all three are set."));
        return new RegionalPreview(settings, RegionalSamples.Build(settings), messages);
    }

    public async Task<IReadOnlyList<IdentifierTypeProfile>> OfferedProfilesAsync(CancellationToken ct = default)
    {
        var found = new Dictionary<string, IdentifierTypeProfile>(StringComparer.Ordinal);
        foreach (var entry in await packs.ListAsync(ct))
        {
            if (entry.Unavailable || !entry.IsCompatible || !entry.Trust.IsAcceptable) continue;
            var json = await packs.GetDocumentAsync(entry.PackKey, entry.Version, RegionalKinds.Name, ct);
            if (json is null) continue;
            RegionalProfileDocument doc;
            try { doc = RegionalProfileDocument.Parse(json); } catch (DomainException) { continue; }
            foreach (var p in doc.Identifiers ?? []) found.TryAdd(p.Key, p);
        }
        return found.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
    }
}
