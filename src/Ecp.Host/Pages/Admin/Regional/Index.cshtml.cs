using Ecp.Configuration;
using Ecp.Host.Pages.Admin.Reviews;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Regional;

/// <summary>
/// An organisation's regional settings: country or region, locale, currency, time zone, time display, counterparty tax identifier types and address fields (docs/REGIONAL_SETTINGS.md). The pack proposes, the organisation decides.
/// The page is a form over the regional-profile configuration kind: saving only changes the draft; validating, previewing, publishing and rolling back are the ordinary lifecycle, with its permissions, versions and audit.
/// </summary>
[Authorize(Policy = "perm:config.draft|config.history.read")]
public sealed class IndexModel(IRegionalAdministration regional, IConfigurationService configuration, IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    public sealed class IdentifierRow
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Country { get; set; }
        public bool Required { get; set; }
        public bool Unique { get; set; }
        public string Rule { get; set; } = IdentifierRuleKinds.Free;
        public string? Length { get; set; }
        public string? Mask { get; set; }
        public string Checksum { get; set; } = IdentifierChecksums.None;
        public string Classification { get; set; } = "Internal";
    }

    public sealed class AddressRow
    {
        public string Key { get; set; } = "";
        public bool Used { get; set; } = true;
        public bool Required { get; set; }
    }

    public const int ExtraIdentifierRows = 2;

    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string? Country { get; set; }
    [BindProperty] public string? Locale { get; set; }
    [BindProperty] public string? SupportedLocales { get; set; }
    [BindProperty] public string? Currency { get; set; }
    [BindProperty] public string? SupportedCurrencies { get; set; }
    [BindProperty] public string? TimeZone { get; set; }
    [BindProperty] public string? TimeDisplay { get; set; }
    [BindProperty] public string? PhoneRegion { get; set; }
    [BindProperty] public List<IdentifierRow> Identifiers { get; set; } = [];
    [BindProperty] public List<AddressRow> AddressRows { get; set; } = [];
    [BindProperty] public bool OwnIdentifiers { get; set; }
    [BindProperty] public bool OwnAddress { get; set; }
    [BindProperty] public string? AddProfile { get; set; }
    [BindProperty] public string? Note { get; set; }
    [BindProperty] public int? ToRelease { get; set; }

    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public RegionalOverview? Overview { get; private set; }
    public RegionalPreview? Preview { get; private set; }
    public IReadOnlyList<ValidationMessage> Problems { get; private set; } = [];
    public IReadOnlyList<ReleaseDto> Releases { get; private set; } = [];
    public bool CanHistory { get; private set; }
    public GovernanceInfo? Governance { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct, fromPost: false); return Page(); });

    private async Task<Guid> ResolveAsync(CancellationToken ct)
    {
        // Only the organisations in which the person may draft or read the history of configuration; naming any other is a plain not-found.
        var grants = (await access.GrantsAsync("config.draft", ct)).Concat(await access.GrantsAsync("config.history.read", ct)).ToList();
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (OrganisationId is { } given && Organisations.All(o => o.Id != given)) throw new ResourceNotFoundException();
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        if (OrganisationId is not { } org) throw new ResourceNotFoundException();
        await Terms.UseOrganisationAsync(org, ct);
        await Format.UseAsync(org, ct);
        return org;
    }

    private async Task LoadAsync(CancellationToken ct, bool fromPost)
    {
        var org = await ResolveAsync(ct);
        Overview = await regional.OverviewAsync(org, ct);
        if (Overview.Draft?.Validation is { } report) Problems = report.Messages;
        Governance = await configuration.TryGovernanceAsync(Overview.Draft?.Id, ct);
        try { Releases = await configuration.ReleasesAsync(RegionalKinds.Name, org, ct); CanHistory = true; }
        catch (ForbiddenException) { CanHistory = false; }
        if (!fromPost)
        {
            var doc = await regional.WorkingDocumentAsync(org, ct);
            FromDocument(doc);
        }
        else Preview = await regional.PreviewAsync(org, ToDocument(await regional.WorkingDocumentAsync(org, ct)), ct);
        if (Preview is null) Preview = await regional.PreviewAsync(org, ToDocument(await regional.WorkingDocumentAsync(org, ct)), ct);
    }

    private void FromDocument(RegionalProfileDocument d)
    {
        Country = d.Country; Locale = d.Locale; Currency = d.Currency; TimeZone = d.TimeZone; PhoneRegion = d.PhoneRegion;
        SupportedLocales = d.SupportedLocales is null ? null : string.Join(", ", d.SupportedLocales);
        SupportedCurrencies = d.SupportedCurrencies is null ? null : string.Join(", ", d.SupportedCurrencies);
        TimeDisplay = d.TimeDisplay switch { Ecp.SharedKernel.Localisation.TimeDisplay.TwentyFourHour => "24h", Ecp.SharedKernel.Localisation.TimeDisplay.TwelveHour => "12h", Ecp.SharedKernel.Localisation.TimeDisplay.Locale => "locale", _ => "" };
        // Without its own choice the organisation inherits the pack's identifier types and address fields; the form then shows what is inherited, and ticking the box makes it the organisation's own.
        OwnIdentifiers = d.Identifiers is not null; OwnAddress = d.Address is not null;
        Identifiers = (d.Identifiers ?? Overview?.Effective.Identifiers ?? []).OrderBy(i => i.Order).Select(i => new IdentifierRow { Key = i.Key, Label = i.Label, Country = i.Country, Required = i.Required, Unique = i.Unique, Rule = i.Rule, Length = i.Length, Mask = i.Mask, Checksum = i.Checksum, Classification = i.Classification }).ToList();
        for (var n = 0; n < ExtraIdentifierRows; n++) Identifiers.Add(new IdentifierRow());
        var addressNow = d.Address ?? Overview?.Effective.Address ?? AddressProfile.Neutral;
        AddressRows = AddressProfile.FieldKeys.Select(k => { var s = addressNow.Spec(k); return new AddressRow { Key = k, Used = s.Used, Required = s.Required }; }).ToList();
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static IReadOnlyList<string>? ListOf(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The document the form describes. What the form leaves empty stays undecided in the profile, so it is inherited from the pack; nothing is invented.</summary>
    private RegionalProfileDocument ToDocument(RegionalProfileDocument current)
    {
        var rows = Identifiers.Where(r => !string.IsNullOrWhiteSpace(r.Key)).ToList();
        if (Blank(AddProfile) is { } add && rows.All(r => r.Key != add) && Overview is not null && Overview.OfferedProfiles.FirstOrDefault(p => p.Key == add) is { } offered)
            rows.Add(new IdentifierRow { Key = offered.Key, Label = offered.Label, Country = offered.Country, Required = offered.Required, Unique = offered.Unique, Rule = offered.Rule, Length = offered.Length, Mask = offered.Mask, Checksum = offered.Checksum, Classification = offered.Classification });
        var identifiers = !OwnIdentifiers ? null : rows.Select((r, i) => new IdentifierTypeProfile(r.Key!.Trim(), (r.Label ?? "").Trim(), Blank(r.Country), r.Required, r.Unique, r.Rule, Blank(r.Length), Blank(r.Mask), r.Checksum, r.Classification, i + 1)).ToList();
        TimeDisplay? display = Blank(TimeDisplay) switch { "24h" => Ecp.SharedKernel.Localisation.TimeDisplay.TwentyFourHour, "12h" => Ecp.SharedKernel.Localisation.TimeDisplay.TwelveHour, "locale" => Ecp.SharedKernel.Localisation.TimeDisplay.Locale, _ => null };
        AddressProfile? address = !OwnAddress ? null : new AddressProfile(AddressRows.Select(r => new AddressFieldSpec(r.Key, r.Used || r.Required, r.Required)).ToList());
        return current with
        {
            Country = Blank(Country)?.ToUpperInvariant(), Locale = Blank(Locale), SupportedLocales = ListOf(SupportedLocales), Currency = Blank(Currency)?.ToUpperInvariant(), SupportedCurrencies = ListOf(SupportedCurrencies)?.Select(c => c.ToUpperInvariant()).ToList(),
            TimeZone = Blank(TimeZone), TimeDisplay = display, PhoneRegion = Blank(PhoneRegion)?.ToUpperInvariant(), Identifiers = identifiers, Address = address
        };
    }

    private async Task<(Guid Org, DraftDto Draft)> SaveAsync(CancellationToken ct)
    {
        var org = await ResolveAsync(ct);
        Overview = await regional.OverviewAsync(org, ct);
        var current = await regional.WorkingDocumentAsync(org, ct);
        var saved = await regional.SaveDraftAsync(org, ToDocument(current), ct);
        return (org, saved);
    }

    private IActionResult Back(Guid org) => RedirectToPage(new { organisationId = org });
    private Task<IActionResult> Run(Func<Task<IActionResult>> body, CancellationToken ct) => RunAsync(body, () => LoadAsync(ct, fromPost: true));
    private Task<IActionResult> StepUp(Func<Task<IActionResult>> body, CancellationToken ct) => StepUpRunAsync(body, () => LoadAsync(ct, fromPost: true));

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => Run(async () =>
    {
        var (org, _) = await SaveAsync(ct);
        Flash("The draft is saved. Nothing is published. Validate it, check the preview, then publish.");
        return Back(org);
    }, ct);

    public Task<IActionResult> OnPostValidateAsync(CancellationToken ct) => Run(async () =>
    {
        var (org, draft) = await SaveAsync(ct);
        var checkedDraft = await configuration.ValidateAsync(draft.Id, ct);
        if (checkedDraft.Validation is { IsValid: false } report)
        {
            foreach (var m in report.Messages.Where(m => m.Severity == ValidationSeverity.Error)) ModelState.AddModelError(string.Empty, m.Message);
            await LoadAsync(ct, fromPost: true);
            return Page();
        }
        Flash("The draft is valid. Check the preview, then publish when you are satisfied.");
        return Back(org);
    }, ct);

    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) => StepUp(async () =>
    {
        var (org, draft) = await SaveAsync(ct);
        var checkedDraft = await configuration.ValidateAsync(draft.Id, ct);
        if (checkedDraft.Validation is { IsValid: false } report)
        {
            foreach (var m in report.Messages.Where(m => m.Severity == ValidationSeverity.Error)) ModelState.AddModelError(string.Empty, m.Message);
            await LoadAsync(ct, fromPost: true);
            return Page();
        }
        var outcome = await configuration.SubmitOrPublishAsync(draft.Id, string.IsNullOrWhiteSpace(Note) ? "Regional settings published" : Note!, ct);
        Flash(outcome.Message("Published. Dates, times, numbers and amounts are shown in the new formats from the next page. Stored timestamps stay in UTC and no amount changes."));
        return Back(org);
    }, ct);

    public Task<IActionResult> OnPostDiscardAsync(CancellationToken ct) => Run(async () =>
    {
        var org = await ResolveAsync(ct);
        if (await configuration.FindOpenDraftAsync(RegionalKinds.Name, org, ct) is { } draft) await configuration.DiscardDraftAsync(draft.Id, ct);
        Flash("The draft is discarded. The published settings are unchanged.");
        return Back(org);
    }, ct);

    public Task<IActionResult> OnPostRollbackAsync(CancellationToken ct) => StepUp(async () =>
    {
        var org = await ResolveAsync(ct);
        var outcome = await configuration.RequestRollbackAsync(RegionalKinds.Name, org, ToRelease ?? 0, string.IsNullOrWhiteSpace(Note) ? "Rolled back" : Note!, ct);
        Flash(outcome.Release is not null ? "The earlier release is published again as a new release. History is kept." : "Submitted for review. A second person has to review and publish the rollback; nothing changes until they do.");
        return Back(org);
    }, ct);
}
