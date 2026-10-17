using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Register;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S10 (docs/REGIONAL_SETTINGS.md, docs/TAX_IDENTIFIERS.md). Organisations of different countries work side by side in one tenant: each resolves its own locale, currency, zone, identifier types and address
/// profile; a profile is drafted, validated, published, versioned, rolled back and audited; identifiers obey classification; business exports follow the organisation and machine exports do not. Both providers.
/// </summary>
internal static class RegionalSettingsScenarios
{
    private const string Harborline = "construction-subcontracts";
    private static readonly DateOnly Sample = new(2026, 10, 16);

    private sealed record RegOrg(Guid Org, string Admin);

    private static async Task<RegOrg> AddAsync(LifecycleWorld w, string name, string prefix, string zone, string locale, string currency, string? pack)
    {
        await using var s = w.W.AsScopeless();
        var org = await s.Get<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest(name, zone, locale, currency, prefix, pack));
        var key = "cfg" + prefix;
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync(key, key + "@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", key + "|" + w.W.TenantId, "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.BusinessConfigurationAdministrator, null, ScopeSet.ForOrganisations([org], "Internal"), null,
            EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        w.W.Users[key] = id;
        return new RegOrg(org, key);
    }

    private static Task<RegionalSettings> SettingsAsync(LifecycleWorld w, Guid org) => w.W.AsScopeless().Run(s => s.Get<IRegionalSettingsResolver>().GetAsync(org));
    private static Task<LocaleFormatter> FormatterAsync(LifecycleWorld w, Guid org) => w.W.AsScopeless().Run(s => s.Get<IRegionalSettingsResolver>().FormatterAsync(org));

    private static Task PublishAsync(LifecycleWorld w, Guid org, string json)
        => w.W.AsScopeless().Run(async s => { await s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(RegionalKinds.Name, org, json, "test", "test publish"); return true; });

    private static string Json(string? locale = null, string? currency = null, string? zone = null, string? identifiers = null, string? supportedCurrencies = null)
        => "{ \"schemaVersion\": 1, \"mode\": \"overrides\"" + (locale is null ? "" : $", \"locale\": \"{locale}\"") + (currency is null ? "" : $", \"currency\": \"{currency}\"") + (zone is null ? "" : $", \"timeZone\": \"{zone}\"")
            + (supportedCurrencies is null ? "" : $", \"supportedCurrencies\": {supportedCurrencies}") + (identifiers is null ? "" : $", \"identifiers\": {identifiers}") + " }";

    // ----- three regions, one tenant ---------------------------------------------------------------------------------------------------

    public static async Task Organisations_of_three_regions_coexist_and_each_resolves_its_own_formats_identifiers_and_address_profile(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var jbs = await AddAsync(w, "Jbs Region", "RJ1", "Australia/Brisbane", "en-AU", "AUD", TestPacks.Reference);
        var harbor = await AddAsync(w, "Harbor Region", "RH1", "America/Chicago", "en-US", "USD", Harborline);
        var blank = await AddAsync(w, "Blank Region", "RB1", "Europe/Berlin", "de-DE", "EUR", TestPacks.Blank);

        var (j, h, b) = (await SettingsAsync(w, jbs.Org), await SettingsAsync(w, harbor.Org), await SettingsAsync(w, blank.Org));
        Assert.Equal(("en-AU", "AUD", "Australia/Brisbane"), (j.PrimaryLocale, j.DefaultCurrency, j.TimeZoneId));
        Assert.Equal(("en-US", "USD", "America/Chicago"), (h.PrimaryLocale, h.DefaultCurrency, h.TimeZoneId));
        Assert.Equal(("de-DE", "EUR", "Europe/Berlin"), (b.PrimaryLocale, b.DefaultCurrency, b.TimeZoneId));
        Assert.True(j.IsComplete && h.IsComplete && b.IsComplete);

        Assert.Equal("16/10/2026", (await FormatterAsync(w, jbs.Org)).Date(Sample));
        Assert.Equal("10/16/2026", (await FormatterAsync(w, harbor.Org)).Date(Sample));
        Assert.Equal("16.10.2026", (await FormatterAsync(w, blank.Org)).Date(Sample));

        // Identifier types: the JBS pack proposes an ABN, the Harborline pack a Federal Tax ID, and the Blank pack proposes none, so nothing defaults to an ABN.
        Assert.Equal("ABN", j.PrimaryIdentifier!.Label);
        Assert.Equal("au.abn", j.PrimaryIdentifier.Key);
        Assert.Equal("Federal Tax ID", h.PrimaryIdentifier!.Label);
        Assert.Equal("us.ein", h.PrimaryIdentifier.Key);
        Assert.Empty(b.Identifiers);
        Assert.Null(b.PrimaryIdentifier);

        // The same trusted writer applies each organisation's own rule; no rule leaks across organisations.
        await using var s = w.W.AsScopeless();
        var suppliers = s.Get<ISupplierService>();
        await suppliers.CreateAsync(new CreateSupplierRequest(jbs.Org, "Eleven Digits Pty", TaxIdentifier: "51 824 753 556"));
        var wrongForJbs = await Assert.ThrowsAsync<DomainException>(() => suppliers.CreateAsync(new CreateSupplierRequest(jbs.Org, "Mask Value Pty", TaxIdentifier: "12-3456789")));
        Assert.Equal("identifier_length", wrongForJbs.Code);
        await suppliers.CreateAsync(new CreateSupplierRequest(harbor.Org, "Mask Value Inc", TaxIdentifier: "12-3456789"));
        var wrongForHarbor = await Assert.ThrowsAsync<DomainException>(() => suppliers.CreateAsync(new CreateSupplierRequest(harbor.Org, "Eleven Digits Inc", TaxIdentifier: "51824753556")));
        Assert.Equal("identifier_length", wrongForHarbor.Code);
        var noType = await Assert.ThrowsAsync<DomainException>(() => suppliers.CreateAsync(new CreateSupplierRequest(blank.Org, "Anything GmbH", TaxIdentifier: "51824753556")));
        Assert.Equal("identifier_type_unknown", noType.Code);
        await suppliers.CreateAsync(new CreateSupplierRequest(blank.Org, "No Identifier GmbH"));         // a Blank organisation records no identifier unless it configures a type
    }

    // ----- lifecycle of a profile --------------------------------------------------------------------------------------------------------

    public static async Task A_profile_is_drafted_validated_published_versioned_rolled_back_and_audited_and_changes_only_its_own_organisation(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var a = await AddAsync(w, "Lifecycle A", "RA1", "Australia/Sydney", "en-AU", "AUD", TestPacks.Blank);
        var other = await AddAsync(w, "Lifecycle B", "RB2", "Australia/Sydney", "en-AU", "AUD", TestPacks.Blank);
        Assert.Equal("16/10/2026", (await FormatterAsync(w, a.Org)).Date(Sample));

        // Draft, validate, publish through the administration service and the ordinary configuration lifecycle.
        var releaseOne = await w.W.AsAsync(a.Admin, async s =>
        {
            var admin = s.Get<IRegionalAdministration>();
            var doc = await admin.WorkingDocumentAsync(a.Org);
            var draft = await admin.SaveDraftAsync(a.Org, doc with { Locale = "de-DE", Currency = "EUR", TimeZone = "Europe/Berlin" });
            var preview = await admin.PreviewAsync(a.Org, doc with { Locale = "de-DE", Currency = "EUR", TimeZone = "Europe/Berlin" });
            Assert.Equal("16.10.2026", preview.Samples.Single(x => x.Label == "Date").Value);
            var validated = await s.Get<IConfigurationService>().ValidateAsync(draft.Id);
            Assert.True(validated.Validation!.IsValid);
            return await s.Get<IConfigurationService>().PublishAsync(draft.Id, "German settings");
        });
        Assert.Equal(1, releaseOne.Number);

        // Effective at once for this organisation, and not for its neighbour. Stored timestamps are UTC and no amount changes.
        var after = await SettingsAsync(w, a.Org);
        Assert.Equal("de-DE", after.PrimaryLocale);
        Assert.Equal("profile", after.SourceOf("locale"));
        Assert.Equal("16.10.2026", (await FormatterAsync(w, a.Org)).Date(Sample));
        Assert.Equal("16/10/2026", (await FormatterAsync(w, other.Org)).Date(Sample));
        var record = await w.W.AsScopeless().Run(s => s.Get<IOrganisationStructureService>().GetOrganisationAsync(a.Org));
        Assert.Equal(("Europe/Berlin", "de-DE", "EUR"), (record!.TimeZoneId, record.CultureCode, record.DefaultCurrencyCode));

        // A second release, then a rollback to the first: history is kept and the earlier content is published again as a new release.
        await w.W.AsAsync(a.Admin, async s =>
        {
            var admin = s.Get<IRegionalAdministration>();
            var doc = await admin.WorkingDocumentAsync(a.Org);
            var draft = await admin.SaveDraftAsync(a.Org, doc with { Locale = "fr-FR" });
            await s.Get<IConfigurationService>().ValidateAsync(draft.Id);
            await s.Get<IConfigurationService>().PublishAsync(draft.Id, "French");
        });
        Assert.Equal("fr-FR", (await SettingsAsync(w, a.Org)).PrimaryLocale);
        var rolled = await w.W.AsAsync(a.Admin, s => s.Get<IConfigurationService>().RollbackAsync(RegionalKinds.Name, a.Org, 1, "back to German"));
        Assert.Equal(3, rolled.Number);
        Assert.Equal(1, rolled.RollbackOfNumber);
        Assert.Equal("de-DE", (await SettingsAsync(w, a.Org)).PrimaryLocale);
        var history = await w.W.AsAsync(a.Admin, s => s.Get<IConfigurationService>().ReleasesAsync(RegionalKinds.Name, a.Org));
        Assert.Equal([1, 2, 3], history.Select(r => r.Number).Order());

        // Audit: the publication and the change recorded on the organisation are in the trail.
        var actions = await w.W.AsScopeless().Run(async s => await s.Get<IAuditEventSource>().Events.Where(e => e.OrganisationId == a.Org).Select(e => e.Action).ToListAsync());
        Assert.Contains("organisation.regional.set", actions);
        Assert.Contains("config.draft.saved", actions);
        Assert.Contains("config.draft.validated", actions);
        Assert.Contains("config.published", actions);
        Assert.Contains("config.rolled_back", actions);
    }

    public static async Task An_unavailable_zone_an_unknown_currency_or_a_default_outside_the_supported_list_blocks_publication_and_changes_nothing(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var o = await AddAsync(w, "Blocked Region", "RB3", "Australia/Sydney", "en-AU", "AUD", TestPacks.Blank);
        foreach (var json in new[]
        {
            Json(zone: "Mars/Olympus_Mons"), Json(currency: "ZZZ"), Json(locale: "english"),
            Json(currency: "CAD", supportedCurrencies: "[\"USD\"]"),
            Json(identifiers: "[ { \"key\": \"x.id\", \"label\": \"Id\", \"rule\": \"regex\", \"length\": \"(a+)+$\" } ]"),
            Json(identifiers: "[ { \"key\": \"x.id\", \"label\": \"<b>Id</b>\", \"rule\": \"free\" } ]")
        })
            await Assert.ThrowsAsync<DomainException>(() => PublishAsync(w, o.Org, json));
        Assert.Equal("en-AU", (await SettingsAsync(w, o.Org)).PrimaryLocale);
        Assert.Empty(await w.W.AsAsync(o.Admin, s => s.Get<IConfigurationService>().ReleasesAsync(RegionalKinds.Name, o.Org)));
    }

    public static async Task Cached_settings_are_isolated_by_organisation_and_follow_publication_and_rollback(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var a = await AddAsync(w, "Cache A", "RC1", "Australia/Sydney", "en-AU", "AUD", TestPacks.Blank);
        var b = await AddAsync(w, "Cache B", "RC2", "America/Chicago", "en-US", "USD", TestPacks.Blank);
        // Warm both organisations, then change one: the other must still answer with its own settings and the changed one must not serve a stale answer.
        Assert.Equal("en-AU", (await SettingsAsync(w, a.Org)).PrimaryLocale);
        Assert.Equal("en-US", (await SettingsAsync(w, b.Org)).PrimaryLocale);
        await PublishAsync(w, a.Org, Json(locale: "fr-CA", currency: "CAD", zone: "America/Toronto"));
        Assert.Equal("fr-CA", (await SettingsAsync(w, a.Org)).PrimaryLocale);
        Assert.Equal("en-US", (await SettingsAsync(w, b.Org)).PrimaryLocale);
        var cache = w.App.Services.GetRequiredService<IRegionalCache>();
        cache.Clear();                                   // a cold cache falls back to the stored configuration with the same answer
        Assert.Equal("fr-CA", (await SettingsAsync(w, a.Org)).PrimaryLocale);
        Assert.Equal("America/Toronto", (await SettingsAsync(w, a.Org)).TimeZoneId);
        Assert.Equal("America/Chicago", (await SettingsAsync(w, b.Org)).TimeZoneId);
    }

    // ----- daylight saving through the whole stack -------------------------------------------------------------------------------------

    public static async Task The_organisations_zone_decides_the_local_time_of_the_same_utc_instant_through_daylight_saving(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var chicago = await AddAsync(w, "Dst Chicago", "RD1", "America/Chicago", "en-US", "USD", TestPacks.Blank);
        var brisbane = await AddAsync(w, "Dst Brisbane", "RD2", "Australia/Brisbane", "en-AU", "AUD", TestPacks.Blank);
        var january = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var july = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var c = await FormatterAsync(w, chicago.Org);
        var b = await FormatterAsync(w, brisbane.Org);
        Assert.NotEqual(c.DateTime(january).Split(' ')[^1], c.DateTime(july).Split(' ')[^1]);     // the offset changes with the clocks
        Assert.Equal(b.DateTime(january).Split(' ')[^1], b.DateTime(july).Split(' ')[^1]);        // a zone without daylight saving does not
        Assert.Contains("06:00", c.DateTime(january)); Assert.Contains("05:00", c.DateTime(july));
    }

    // ----- identifiers: access, duplicates, history -------------------------------------------------------------------------------------

    private const string RestrictedIdentifier = "[ { \"key\": \"xx.reg\", \"label\": \"Registration\", \"required\": false, \"unique\": true, \"rule\": \"digits\", \"length\": \"8\", \"checksum\": \"none\", \"classification\": \"Restricted\", \"order\": 1 }," +
                                                " { \"key\": \"xx.tax\", \"label\": \"Tax Number\", \"required\": false, \"unique\": false, \"rule\": \"alphanumeric\", \"length\": \"6-10\", \"checksum\": \"none\", \"classification\": \"Internal\", \"order\": 2 } ]";

    public static async Task A_classified_identifier_is_never_shown_counted_or_hinted_to_a_reader_without_the_classification(TestApp app)
    {
        var w = await AccessWorld.CreateAsync(app, "Idn");
        await using (var s = w.AsScopeless())
            await s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(RegionalKinds.Name, w.Org1, Json(identifiers: RestrictedIdentifier), "test", "restricted identifier type");

        // A trusted process records a Restricted value (a procurement administrator, whose ceiling is Confidential, could not record it themselves).
        Guid hiddenSupplier;
        await using (var s = w.AsScopeless())
        {
            hiddenSupplier = await s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(w.Org1, "Hidden Identifier Supplier"));
            await s.Get<ISupplierIdentifierWriter>().RecordTrustedAsync(w.Org1, hiddenSupplier, "xx.reg", "12345678", IdentifierSources.Manual, null, null);
            await s.Get<ISupplierIdentifierWriter>().RecordTrustedAsync(w.Org1, hiddenSupplier, "xx.tax", "TAX-0001", IdentifierSources.Manual, null, null);
        }

        // The administrator sees the Internal value but not the Restricted one, and the projected list value (the primary type is the Restricted one) is withheld.
        var adminItems = await w.AsAsync("procAdmin", s => s.Get<ISupplierIdentifiers>().ListAsync(hiddenSupplier));
        Assert.Equal(["xx.tax"], adminItems.Select(i => i.TypeKey));
        var listed = (await w.AsAsync("procAdmin", s => s.Get<ISecuredSupplierService>().ListAsync())).Single(x => x.Id == hiddenSupplier);
        Assert.Null(listed.TaxIdentifier);

        // A reader whose ceiling reaches Restricted sees both.
        var legalItems = await w.AsAsync("legal", s => s.Get<ISupplierIdentifiers>().ListAsync(hiddenSupplier));
        Assert.Equal(["xx.reg", "xx.tax"], legalItems.Select(i => i.TypeKey).Order());
        Assert.Equal("12345678", legalItems.Single(i => i.TypeKey == "xx.reg").Value);
        var legalListed = (await w.AsAsync("legal", s => s.Get<ISecuredSupplierService>().ListAsync())).Single(x => x.Id == hiddenSupplier);
        Assert.Equal("12345678", legalListed.TaxIdentifier);

        // Ending a hidden identifier answers exactly as for one that does not exist, and a duplicate is never reported across the classification line.
        var restrictedId = legalItems.Single(i => i.TypeKey == "xx.reg").Id;
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.AsAsync("procAdmin", s => s.Get<ISupplierIdentifiers>().EndAsync(restrictedId)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.AsAsync("procAdmin", s => s.Get<ISupplierIdentifiers>().EndAsync(Guid.NewGuid())));
        var another = await w.AsAsync("procAdmin", s => s.Get<ISecuredSupplierService>().CreateAsync(new CreateSupplierRequest(w.Org1, "Another Supplier")));
        var refused = await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.AsAsync("procAdmin", s => s.Get<ISupplierIdentifiers>().AddAsync(another, "xx.reg", "12345678")));   // the same answer as for something that is not there
        Assert.DoesNotContain("12345678", refused.Message);
        Assert.DoesNotContain("in use", refused.Message, StringComparison.OrdinalIgnoreCase);
        // The Internal type has no classification barrier, so the administrator can record and see it.
        var added = await w.AsAsync("procAdmin", s => s.Get<ISupplierIdentifiers>().AddAsync(another, "xx.tax", "ABC123"));
        Assert.Equal("FormatValid", added.Verification);                                        // structure checked only; never "Externally Verified"
        Assert.NotEqual(IdentifierVerification.ExternallyVerified, added.Verification);
    }

    public static async Task Duplicates_are_prevented_within_the_organisation_by_normalised_value_only_where_the_type_requires_it(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var us = await AddAsync(w, "Dup Harbor", "RU1", "America/Chicago", "en-US", "USD", Harborline);
        var other = await AddAsync(w, "Dup Other", "RU2", "America/Chicago", "en-US", "USD", Harborline);
        await using var s = w.W.AsScopeless();
        var suppliers = s.Get<ISupplierService>();
        await suppliers.CreateAsync(new CreateSupplierRequest(us.Org, "First Inc", TaxIdentifier: "12-3456789"));
        var dup = await Assert.ThrowsAsync<DomainException>(() => suppliers.CreateAsync(new CreateSupplierRequest(us.Org, "Second Inc", TaxIdentifier: "123456789")));   // same value, different separators
        Assert.Equal("identifier_in_use", dup.Code);
        await suppliers.CreateAsync(new CreateSupplierRequest(other.Org, "Third Inc", TaxIdentifier: "12-3456789"));                                                      // another organisation: not a duplicate
        // A type that does not require uniqueness allows two counterparties to share a value (a group of related companies).
        await PublishAsync(w, us.Org, Json(identifiers: "[ { \"key\": \"xx.grp\", \"label\": \"Group Number\", \"rule\": \"digits\", \"length\": \"6\", \"unique\": false, \"order\": 1 } ]"));
        await suppliers.CreateAsync(new CreateSupplierRequest(us.Org, "Group One", TaxIdentifier: "123456"));
        await suppliers.CreateAsync(new CreateSupplierRequest(us.Org, "Group Two", TaxIdentifier: "123456"));
    }

    public static async Task Existing_identifiers_stay_readable_and_searchable_when_an_organisation_changes_its_profile(TestApp app)
    {
        var w = await AccessWorld.CreateAsync(app, "Cmp");
        // The supplier created by the world has no identifier. Give one counterparty an ABN through the reference profile, then move the organisation to a different primary type.
        Guid supplier;
        await using (var s = w.AsScopeless())
            supplier = await s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(w.Org1, "Legacy Value Pty Ltd", TaxIdentifier: "51 824 753 556", VendorNumber: "V-7001"));
        await using (var s = w.AsScopeless())
            await s.Get<IContractService>().CreateDraftAsync(new CreateContractRequest(w.Org1, "CMP-LEGACY", "Legacy identifier contract", supplier, w.CatPackaging));
        var before = (await w.AsAsync("procAdmin", s => s.Get<ISecuredSupplierService>().ListAsync())).Single(x => x.Id == supplier);
        Assert.Equal("51824753556", before.TaxIdentifier);
        Assert.Equal("V-7001", before.VendorNumber);                                              // the vendor number is its own field, untouched

        await using (var s = w.AsScopeless())
            await s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(RegionalKinds.Name, w.Org1, Json(identifiers: "[ { \"key\": \"xx.new\", \"label\": \"New Type\", \"rule\": \"free\", \"order\": 1 } ]"), "test", "change type");

        var items = await w.AsAsync("procAdmin", s => s.Get<ISupplierIdentifiers>().ListAsync(supplier));
        var old = Assert.Single(items);
        Assert.Equal("au.abn", old.TypeKey);
        Assert.Equal("51 824 753 556", old.Value);                                                 // the value as entered, not rewritten
        // The register still finds the contract by that value, and an unrelated number finds nothing.
        var found = await w.AsAsync("procAdmin", s => s.Get<IContractRegister>().SearchAsync(new ContractQuery(Text: "51824753556", OrganisationId: w.Org1)));
        Assert.Contains(found.Rows, r => r.Cells.GetValueOrDefault("agreementReference") == "CMP-LEGACY");
        var none = await w.AsAsync("procAdmin", s => s.Get<IContractRegister>().SearchAsync(new ContractQuery(Text: "99999999999", OrganisationId: w.Org1)));
        Assert.Empty(none.Rows);
    }

    // ----- notifications ------------------------------------------------------------------------------------------------------------

    public static async Task A_reminder_writes_its_due_date_in_the_organisations_locale_and_never_as_an_iso_string(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var end = w.Today.AddDays(20);
        var contract = await w.ToEffectiveInOrderAsync(end, lead: 30);
        var result = await w.W.AsScopeless().Run(s => s.Get<Ecp.Notifications.IReminderScanner>().ScanAsync());
        Assert.True(result.RemindersSent >= 1);
        var fmt = await FormatterAsync(w, w.W.Org1);
        var reminder = (await w.InboxAsync("ownerPackaging")).First(n => n.Kind == "Reminder" && n.ReminderKind == "endDate" && n.ContractId == contract);
        Assert.Contains(fmt.Date(end), reminder.Body);                                          // the organisation's own format (en-AU here)
        Assert.DoesNotContain(end.ToString("yyyy-MM-dd"), reminder.Body);
        Assert.Equal("16/10/2026", fmt.Date(Sample));                                           // day first for this organisation
    }

    // ----- exports ---------------------------------------------------------------------------------------------------------------------

    public static async Task A_business_export_follows_the_organisations_formats_with_currency_codes_and_a_machine_export_stays_iso(TestApp app)
    {
        var w = await AccessWorld.CreateAsync(app, "Exp");
        await using (var s = w.AsScopeless())
        {
            var contracts = s.Get<IContractService>();
            await contracts.CreateDraftAsync(new CreateContractRequest(w.Org1, "EXP-DATED", "Dated export row", w.Sup1, w.CatPackaging, CurrencyCode: "CAD", EstimatedValue: 1234.5m, EndDate: new DateOnly(2027, 11, 25), ReviewDate: new DateOnly(2027, 10, 16)));
        }
        var query = new ContractQuery(OrganisationId: w.Org1, Text: "Dated export row", Columns: ["agreementReference", "endDate", "reviewDate", "estimatedValue"]);

        async Task<string> CsvAsync(ExportStyle style) => System.Text.Encoding.UTF8.GetString((await w.AsAsync("procAdmin", s => s.Get<IContractExports>().ExportAsync(new ExportRequest(query, ExportFormat.Csv, Style: style)))).Content);

        // en-AU (the organisation's stored settings): day first; a currency code accompanies the amount even though only the value column was asked for.
        var businessAu = await CsvAsync(ExportStyle.Business);
        Assert.Contains("25/11/2027", businessAu);
        Assert.DoesNotContain("2027-11-25", businessAu);
        Assert.Contains("Currency", businessAu);
        Assert.Contains("CAD", businessAu);

        // The organisation moves to a US locale: the same export is month first.
        await using (var s = w.AsScopeless()) await s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(RegionalKinds.Name, w.Org1, Json(locale: "en-US", currency: "USD", zone: "America/Chicago"), "test", "US formats");
        var businessUs = await CsvAsync(ExportStyle.Business);
        Assert.Contains("11/25/2027", businessUs);
        Assert.DoesNotContain("25/11/2027", businessUs);

        // A machine export has to be asked for and does not depend on any locale.
        var machineUs = await CsvAsync(ExportStyle.Machine);
        Assert.Contains("2027-11-25", machineUs);
        Assert.Contains("2027-10-16", machineUs);
        Assert.Contains("1234.5", machineUs);
        Assert.DoesNotContain("11/25/2027", machineUs);
        Assert.Equal(machineUs, await CsvAsync(ExportStyle.Machine));                                  // stable
    }
}

public sealed class RegionalSettingsScenarioTests_Sqlite
{
    [Fact] public Task Coexistence() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.Organisations_of_three_regions_coexist_and_each_resolves_its_own_formats_identifiers_and_address_profile);
    [Fact] public Task Profile_lifecycle() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.A_profile_is_drafted_validated_published_versioned_rolled_back_and_audited_and_changes_only_its_own_organisation);
    [Fact] public Task Blocked() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.An_unavailable_zone_an_unknown_currency_or_a_default_outside_the_supported_list_blocks_publication_and_changes_nothing);
    [Fact] public Task Cache() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.Cached_settings_are_isolated_by_organisation_and_follow_publication_and_rollback);
    [Fact] public Task Daylight_saving() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.The_organisations_zone_decides_the_local_time_of_the_same_utc_instant_through_daylight_saving);
    [Fact] public Task Classified_identifier() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.A_classified_identifier_is_never_shown_counted_or_hinted_to_a_reader_without_the_classification);
    [Fact] public Task Duplicates() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.Duplicates_are_prevented_within_the_organisation_by_normalised_value_only_where_the_type_requires_it);
    [Fact] public Task Existing_values() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.Existing_identifiers_stay_readable_and_searchable_when_an_organisation_changes_its_profile);
    [Fact] public Task Exports() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.A_business_export_follows_the_organisations_formats_with_currency_codes_and_a_machine_export_stays_iso);
    [Fact] public Task Reminder_dates() => ProviderRunner.SqliteAsync(RegionalSettingsScenarios.A_reminder_writes_its_due_date_in_the_organisations_locale_and_never_as_an_iso_string, LifecycleScenarios.Scanner);
}

[Trait("Category", "SqlServer")]
public sealed class RegionalSettingsScenarioTests_SqlServer
{
    [SqlServerFact] public Task Coexistence() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.Organisations_of_three_regions_coexist_and_each_resolves_its_own_formats_identifiers_and_address_profile);
    [SqlServerFact] public Task Profile_lifecycle() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.A_profile_is_drafted_validated_published_versioned_rolled_back_and_audited_and_changes_only_its_own_organisation);
    [SqlServerFact] public Task Blocked() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.An_unavailable_zone_an_unknown_currency_or_a_default_outside_the_supported_list_blocks_publication_and_changes_nothing);
    [SqlServerFact] public Task Cache() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.Cached_settings_are_isolated_by_organisation_and_follow_publication_and_rollback);
    [SqlServerFact] public Task Daylight_saving() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.The_organisations_zone_decides_the_local_time_of_the_same_utc_instant_through_daylight_saving);
    [SqlServerFact] public Task Classified_identifier() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.A_classified_identifier_is_never_shown_counted_or_hinted_to_a_reader_without_the_classification);
    [SqlServerFact] public Task Duplicates() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.Duplicates_are_prevented_within_the_organisation_by_normalised_value_only_where_the_type_requires_it);
    [SqlServerFact] public Task Existing_values() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.Existing_identifiers_stay_readable_and_searchable_when_an_organisation_changes_its_profile);
    [SqlServerFact] public Task Exports() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.A_business_export_follows_the_organisations_formats_with_currency_codes_and_a_machine_export_stays_iso);
    [SqlServerFact] public Task Reminder_dates() => ProviderRunner.SqlServerAsync(RegionalSettingsScenarios.A_reminder_writes_its_due_date_in_the_organisations_locale_and_never_as_an_iso_string, LifecycleScenarios.Scanner);
}
