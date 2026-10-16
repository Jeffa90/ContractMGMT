using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Register;
using Ecp.Formats;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecp.Integration.Tests.CustomFieldWorld;

namespace Ecp.Integration.Tests;

/// <summary>The contract register: typed search, filters, sorting and paging, what each person can and cannot find, list views, personal and shared views, and exports.</summary>
internal static class RegisterScenarios
{
    private static int _n;

    internal static async Task<Guid> Make(LifecycleWorld w, string title, decimal value = 1000m, string? type = null, string? risk = null, string classification = "Internal", Guid? category = null, Guid? bu = null, Guid? site = null,
        DateOnly? end = null, string? extension = null, bool critical = false, Dictionary<string, string?>? custom = null, string user = "ownerPackaging", Guid? supplier = null, string? currency = "AUD", DateOnly? review = null)
    {
        var n = Interlocked.Increment(ref _n);
        return await w.As(user, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(w.W.Org1, $"RG-{w.W.TenantId.ToString("N")[..4].ToUpperInvariant()}-{n:0000}", title, supplier ?? w.W.Sup1, category ?? w.W.CatPackaging,
            Classification: classification, CurrencyCode: currency, EstimatedValue: currency is null ? null : value, ContractType: type, RiskLevel: risk, BusinessUnitId: bu, SiteId: site, EndDate: end, ExtensionType: extension,
            CriticalNotice: critical, NoticePeriodValue: critical ? 3 : null, NoticePeriodUnit: critical ? "Months" : null, ReviewDate: review, CustomValues: custom)));
    }

    internal static Task<RegisterPage> Search(LifecycleWorld w, string user, ContractQuery q) => w.As(user, s => s.Get<IContractRegister>().SearchAsync(q));
    private static FilterClause F(string field, string op, params string[] values) => new(field, op, values);
    private static string[] Titles(RegisterPage p) => p.Rows.Select(r => r.Title).ToArray();

    public static async Task Typed_search_filters_sorts_and_pages_correctly_over_core_fields(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var today = w.Today;
        // A supplier of its own keeps the standard world's other contracts out of every count below.
        var sup = await w.W.AsScopeless().Run(s => s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(w.W.Org1, "Xyzsup Trading Pty Ltd")));
        Task<RegisterPage> Search(LifecycleWorld wd, string user, ContractQuery q) => RegisterScenarios.Search(wd, user, q with { Filters = [F("supplier", "Contains", "xyzsup"), .. q.Filters ?? []] });
        async Task<Guid> Make(LifecycleWorld wd, string title, decimal value = 1000m, string? type = null, string? risk = null, string classification = "Internal", Guid? category = null, Guid? bu = null, Guid? site = null,
            DateOnly? end = null, string? extension = null, bool critical = false, string? currency = "AUD", DateOnly? review = null)
            => await RegisterScenarios.Make(wd, title, value, type, risk, classification, category, bu, site, end, extension, critical, null, "ownerPackaging", sup, currency, review);
        await Make(w, "Alpha cartons", 500m, "Goods", "Low", end: today.AddDays(20));
        await Make(w, "Alpha films", 1500m, "Goods", "High", end: today.AddDays(200), critical: true);
        await Make(w, "Beta services", 2500m, "Services", "High", bu: w.W.Bu1, site: w.W.Site1, end: today.AddDays(45));
        await Make(w, "Beta consulting", 4500m, "Services", "Medium", category: w.W.CatFlexible, bu: w.W.Bu1, end: today.AddDays(400));
        await Make(w, "Gamma licence", 9000m, "Licence", "Low", extension: "Evergreen", review: today.AddDays(30));
        await Make(w, "Delta pallet", 100m, "Goods", null, currency: null);
        await Make(w, "Epsilon wraps", 7000m, "Goods", "Low", classification: "Confidential", site: w.W.Site2);
        for (var i = 0; i < 5; i++) await Make(w, $"Filler {i:00}", 10m + i, "Goods", "Low");
        const string owner = "ownerPackaging";

        var all = await Search(w, owner, new ContractQuery(PageSize: 5));
        if (all.Total != 12) Assert.Fail("Found: " + string.Join(", ", (await Search(w, owner, new ContractQuery(PageSize: 200))).Rows.Select(r => r.Title)));
        Assert.Equal(12, all.Total); Assert.Equal(5, all.Rows.Count); Assert.Equal(3, all.Pages);
        var seen = new HashSet<Guid>();
        for (var p = 1; p <= 3; p++) foreach (var r in (await Search(w, owner, new ContractQuery(Page: p, PageSize: 5))).Rows) Assert.True(seen.Add(r.Id), "a row appeared on two pages");
        Assert.Equal(12, seen.Count);
        Assert.Equal(200, (await Search(w, owner, new ContractQuery(PageSize: 100000))).PageSize);        // a page can never be larger than the safe maximum

        // Text: partial, case-insensitive, every word must match; the reference matches exactly or partly.
        Assert.Equal(["Alpha cartons", "Alpha films"], Titles(await Search(w, owner, new ContractQuery(Text: "alpha"))).Order().ToArray());
        Assert.Equal(["Beta consulting"], Titles(await Search(w, owner, new ContractQuery(Text: "beta CONSULT"))));
        Assert.Equal(["Xyzsup Trading Pty Ltd"], (await Search(w, owner, new ContractQuery(Text: "trading pty", Columns: ["supplier"]))).Rows.Select(r => r.Cells["supplier"]!).Distinct().ToArray());
        Assert.Single((await Search(w, owner, new ContractQuery(Text: (await Search(w, owner, new ContractQuery(Text: "Gamma"))).Rows[0].AgreementReference))).Rows);
        Assert.Empty((await Search(w, owner, new ContractQuery(Text: "alpha zebra"))).Rows);
        Assert.Equal(0, (await Search(w, owner, new ContractQuery(Text: "100%"))).Total);              // % is text, not a wildcard

        // Typed filters over core fields.
        Assert.Equal(["Alpha films", "Beta services"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("riskLevel", "Eq", "High")]))).Order().ToArray());
        Assert.Equal(["Beta consulting", "Beta services"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("contractType", "In", "Services")]))).Order().ToArray());
        Assert.Equal(["Alpha films", "Beta services"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("estimatedValue", "Between", "1000", "3000")]))).Order().ToArray());
        Assert.Equal(["Alpha cartons", "Beta services"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("endDate", "Between", "@today", "@today+60")]))).Order().ToArray());
        Assert.Equal(["Alpha films"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("criticalNotice", "Is", "true")]))));
        Assert.Equal(["Gamma licence"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("evergreen", "Is", "true")]))));
        Assert.Equal(11, (await Search(w, owner, new ContractQuery(Filters: [F("evergreen", "Is", "false")]))).Total);
        Assert.Equal(["Beta consulting"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("category", "In", w.W.CatFlexible.ToString())]))));
        Assert.Equal(12, (await Search(w, owner, new ContractQuery(Filters: [F("category", "Eq", w.W.CatPackaging.ToString())]))).Total);   // the parent category includes its child
        Assert.Equal(["Beta consulting", "Beta services"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("businessUnit", "Eq", w.W.Bu1.ToString())]))).Order().ToArray());
        Assert.Equal(["Epsilon wraps"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("site", "Eq", w.W.Site2.ToString()), F("classification", "Eq", "Confidential")]))));
        Assert.Equal(["Delta pallet"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("currencyCode", "IsEmpty")]))));
        Assert.Equal(["Gamma licence"], Titles(await Search(w, owner, new ContractQuery(Filters: [F("reviewDate", "Lte", "@today+31")]))));
        Assert.Equal(12, (await Search(w, owner, new ContractQuery(Filters: [F("owner", "Eq", "@me")]))).Total);
        Assert.Equal(0, (await Search(w, owner, new ContractQuery(Filters: [F("owner", "Eq", w.W.Users["ownerEnergy"].ToString())]))).Total);
        Assert.Single((await Search(w, owner, new ContractQuery(Columns: ["status"]))).Rows.Select(r => r.Cells["status"]).Distinct());

        // Sorting: by value descending then reference; null values sort together and ties are broken by reference.
        var byValue = await Search(w, owner, new ContractQuery(Sorts: [new SortClause("estimatedValue", true)], PageSize: 3));
        Assert.Equal(["Gamma licence", "Epsilon wraps", "Beta consulting"], Titles(byValue));
        var byType = await Search(w, owner, new ContractQuery(Sorts: [new SortClause("contractType"), new SortClause("estimatedValue", true)], Columns: ["contractType", "estimatedValue"]));
        Assert.Equal("Goods", byType.Rows[0].Cells["contractType"]);
        Assert.Equal("7000", byType.Rows[0].Cells["estimatedValue"]);

        // The grammar refuses what it does not know, and says why.
        async Task<RegisterQueryException> Bad(ContractQuery q) => await Assert.ThrowsAsync<RegisterQueryException>(() => Search(w, owner, q));
        Assert.Equal("field", (await Bad(new ContractQuery(Filters: [F("password", "Eq", "x")]))).Issues[0].Code);
        Assert.Equal("operator", (await Bad(new ContractQuery(Filters: [F("estimatedValue", "Contains", "x")]))).Issues[0].Code);
        Assert.Equal("value", (await Bad(new ContractQuery(Filters: [F("endDate", "Eq", "tomorrow")]))).Issues[0].Code);
        Assert.Equal("value", (await Bad(new ContractQuery(Filters: [F("estimatedValue", "Eq", "1; DROP TABLE Contract")]))).Issues[0].Code);
        Assert.Equal("sort", (await Bad(new ContractQuery(Sorts: [new SortClause("description")]))).Issues[0].Code);
        Assert.Equal("too_many", (await Bad(new ContractQuery(Filters: Enumerable.Range(0, 21).Select(_ => F("riskLevel", "Eq", "Low")).ToList()))).Issues[0].Code);
        Assert.Equal("set", (await Bad(new ContractQuery(Filters: [F("set", "Is", "nothingLikeThis")]))).Issues[0].Code);
    }

    public static async Task The_register_only_ever_returns_what_the_person_may_read_and_hidden_fields_cannot_be_used_to_find_anything(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(string.Join(",",
            Field("plain", "TEXT", "\"searchable\": true, \"filterable\": true, \"sortable\": true, \"reportable\": true"),
            Field("secret", "TEXT", "\"classification\": \"Restricted\", \"searchable\": true, \"filterable\": true, \"sortable\": true, \"reportable\": true"),
            Field("legal_only", "TEXT", "\"visibleToRoles\": [\"Legal\"], \"filterable\": true, \"searchable\": true"))));
        var a = await Make(w, "Open contract", custom: new() { ["plain"] = "common" });
        var b = await Make(w, "Holds a secret", custom: new() { ["plain"] = "common" });
        var c = await Make(w, "Confidential contract");
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractCustomFieldWriter>().ApplyAsync(b, new Dictionary<string, string?> { ["secret"] = "needle-in-haystack", ["legal_only"] = "legal-needle" }, "test", "Migration", Guid.NewGuid(), null); return 0; });
        _ = a; _ = c;

        // Core search: each person gets exactly the contracts they may read, and the total matches what is listed.
        var mine = new[] { a, b, c };
        var owner = await Search(w, "ownerPackaging", new ContractQuery(PageSize: 200));
        Assert.Equal(owner.Total, owner.Rows.Count);
        Assert.Empty(mine.Except(owner.Rows.Select(r => r.Id)));
        var energyViewer = await Search(w, "viewerRestricted", new ContractQuery(PageSize: 200));      // Internal ceiling, Site A, Energy category only
        Assert.Equal(energyViewer.Total, energyViewer.Rows.Count);
        Assert.Empty(mine.Intersect(energyViewer.Rows.Select(r => r.Id)));
        var nobody = await Search(w, "otherApprover", new ContractQuery());                           // an approver sees only what waits on their group
        Assert.Equal(0, nobody.Total);

        // A field above the person's level is not offered to them at all: it cannot be used to find a contract, to count it, or to order the others, and the refusal does not say that the field exists.
        var needle = F("secret", "Eq", "needle-in-haystack");
        Assert.Equal("field", (await Assert.ThrowsAsync<RegisterQueryException>(() => Search(w, "ownerPackaging", new ContractQuery(Filters: [needle])))).Issues[0].Code);
        Assert.Equal(0, (await Search(w, "ownerPackaging", new ContractQuery(Text: "needle-in-haystack"))).Total);               // free text never reaches it
        await Assert.ThrowsAsync<RegisterQueryException>(() => Search(w, "ownerPackaging", new ContractQuery(Filters: [F("secret", "IsNotEmpty")])));
        await Assert.ThrowsAsync<RegisterQueryException>(() => Search(w, "ownerPackaging", new ContractQuery(Sorts: [new SortClause("secret", true)])));
        Assert.DoesNotContain("secret", (await Search(w, "ownerPackaging", new ContractQuery(Columns: ["contractTitle", "secret"]))).Columns.Select(c => c.Key));
        Assert.DoesNotContain("secret", (await w.As("ownerPackaging", s => s.Get<IContractRegister>().CatalogueAsync(w.W.Org1))).Fields.Select(f => f.Key));
        var withSecret = await Search(w, "legalA", new ContractQuery(Filters: [needle], Columns: ["contractTitle", "secret"]));
        Assert.Equal(["Holds a secret"], Titles(withSecret));
        Assert.Equal("needle-in-haystack", withSecret.Rows[0].Cells["secret"]);
        var ownerColumn = await Search(w, "ownerPackaging", new ContractQuery(Columns: ["contractTitle", "secret", "plain"]));
        Assert.Equal(["contractTitle", "plain"], ownerColumn.Columns.Select(c => c.Key).ToArray());             // the column is left out with a note, not an error
        Assert.Contains(ownerColumn.Notes, n => n.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(ownerColumn.Rows, r => r.Cells["plain"] == "common");

        // A field hidden by role is not in the owner's catalogue at all: filtering on it is refused as unknown, the same answer as for a field that does not exist.
        var refused = await Assert.ThrowsAsync<RegisterQueryException>(() => Search(w, "ownerPackaging", new ContractQuery(Filters: [F("legal_only", "Eq", "legal-needle")])));
        var missing = await Assert.ThrowsAsync<RegisterQueryException>(() => Search(w, "ownerPackaging", new ContractQuery(Filters: [F("no_such_field", "Eq", "legal-needle")])));
        Assert.Equal(missing.Issues[0].Code, refused.Issues[0].Code);
        Assert.Equal(0, (await Search(w, "ownerPackaging", new ContractQuery(Text: "legal-needle"))).Total);
        Assert.Equal(1, (await Search(w, "legalA", new ContractQuery(Text: "legal-needle"))).Total);
        Assert.DoesNotContain(Titles(await Search(w, "ownerPackaging", new ContractQuery(Text: "common"))), t => t == "Confidential contract");

        // Another tenant's world finds none of this.
        var other = await LifecycleWorld.CreateAsync(app);
        Assert.Equal(0, (await Search(other, "ownerPackaging", new ContractQuery(Text: "Open contract"))).Total);
        Assert.Empty(mine.Intersect((await Search(other, "ownerPackaging", new ContractQuery(PageSize: 200))).Rows.Select(r => r.Id)));
    }
}
