using System.Net;
using System.Text.RegularExpressions;
using Ecp.Identity;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// G-32: no rendered Setup, Onboarding, Pack Manager or Administration page may show a raw <c>[[term]]</c> token, whichever business the organisation is on (Blank, JBS Procurement, Harborline Construction) and
/// whoever is looking. The sweep signs in as several roles, starts from the pages that lead to those areas, follows their links within the area (never a download or an action) and looks at every page it can open.
/// </summary>
[Collection("Api")]
[Trait("Category", "TokenSweep")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class RenderedPageSweepTests
{
    private static readonly string[] Areas = ["/Admin", "/Onboarding", "/Setup", "/Migration"];
    private static readonly Regex Link = new("<a\\b[^>]*\\bhref=\"([^\"#]+)\"", RegexOptions.IgnoreCase);

    internal static string Visible(string html) => Regex.Replace(Regex.Replace(html, "<script.*?</script>|<style.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase), "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>Raw tokens in the visible text and in the attributes a person can read (titles, labels, placeholders). A token is the platform's own syntax, so a match is always a leak.</summary>
    internal static IEnumerable<string> RawTokens(string html)
    {
        foreach (Match m in Regex.Matches(Visible(html), "\\[\\[[^\\]\\r\\n]{0,80}\\]\\]")) yield return m.Value;
    }

    private static bool Followable(string href)
    {
        if (!href.StartsWith('/') || href.StartsWith("//")) return false;
        var path = href.Split('?')[0];
        if (!Areas.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase) || path.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase))) return false;
        var lower = href.ToLowerInvariant();
        return !lower.Contains("handler=") && !lower.Contains("download") && !lower.Contains("export") && !lower.Contains("signout") && !lower.Contains("/api/");
    }

    /// <summary>Visits the given pages and every page they link to within the areas. Returns the number of pages opened and every leak found.</summary>
    internal static async Task<(int Pages, List<string> Leaks)> CrawlAsync(HttpClient client, IEnumerable<string> start, int limit = 400)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(start);
        var leaks = new List<string>();
        var opened = 0;
        while (queue.Count > 0 && opened < limit)
        {
            var url = queue.Dequeue().Replace("&amp;", "&");
            if (!seen.Add(url)) continue;
            var response = await client.GetAsync(url);
            if (response.StatusCode != HttpStatusCode.OK) continue;
            if (response.Content.Headers.ContentType?.MediaType != "text/html") continue;
            var html = await response.Content.ReadAsStringAsync();
            opened++;
            foreach (var token in RawTokens(html).Distinct()) leaks.Add($"{url}: {token}");
            foreach (Match link in Link.Matches(html))
            {
                var href = System.Net.WebUtility.HtmlDecode(link.Groups[1].Value);
                if (Followable(href) && !seen.Contains(href)) queue.Enqueue(href);
            }
        }
        return (opened, leaks);
    }

    private static async Task<List<(string Name, Guid Id)>> AddBusinessesAsync(DemoApp app)
    {
        var (tenant, tenantAdminId) = await app.FindUserAsync("tenantadmin@a.example.test");
        return await app.WithTenantAsync(tenant, async sp =>
        {
            sp.GetRequiredService<CurrentUser>().Set(tenantAdminId);
            var provisioning = sp.GetRequiredService<ITenantProvisioningService>();
            var added = new List<(string, Guid)>();
            foreach (var (name, pack, locale, zone, currency, prefix) in new[]
            {
                ("Sweep Blank", "blank", "en-AU", "Australia/Brisbane", "AUD", "SWB"),
                ("Sweep JBS Procurement", "jbs-procurement", "en-AU", "Australia/Brisbane", "AUD", "SWJ"),
                ("Sweep Harborline Construction", "construction-subcontracts", "en-US", "America/Chicago", "USD", "SWH"),
            })
                added.Add((name, await provisioning.AddOrganisationAsync(new AddOrganisationRequest(name, zone, locale, currency, prefix, pack))));
            return added;
        });
    }

    [Fact]
    public async Task No_administration_onboarding_or_pack_page_shows_a_raw_term_token_for_any_business_or_role()
    {
        using var app = DemoApp.Start(services: s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteAddressFilter>());
        var orgs = await AddBusinessesAsync(app);
        var start = new List<string> { "/", "/Admin", "/Admin/Packs", "/Admin/Configuration", "/Admin/Operations", "/Admin/Terminology", "/Admin/Health", "/Onboarding", "/Admin/Organisations", "/Migration" };
        foreach (var (_, id) in orgs)
        {
            start.Add($"/Onboarding?org={id}");
            start.Add($"/Onboarding/Review?org={id}");
            foreach (var step in new[] { "pack", "structure", "categories", "viewers", "approvers", "terminology", "notifications", "review" }) start.Add($"/Onboarding/Step/{step}?org={id}");
            start.Add($"/Admin/Terminology?organisationId={id}");
            start.Add($"/Admin/Packs/Assign?organisationId={id}");
            start.Add($"/Admin/Capabilities?organisationId={id}");
            start.Add($"/Admin/Forms?organisationId={id}");
        }

        var total = 0;
        var leaks = new List<string>();
        var caller = 0;
        foreach (var who in new[] { "sysadmin@installation.example.test", "tenantadmin@a.example.test", "orgadmin@a.example.test", "bizconfig@a.example.test", "procadmin@a.example.test" })
        {
            using var client = await app.SignInAsync(who);
            client.DefaultRequestHeaders.Add("X-Test-Remote", $"10.7.0.{++caller}");   // each person has a separate request budget, as separate people would
            var (pages, found) = await CrawlAsync(client, start);
            total += pages;
            leaks.AddRange(found.Select(l => $"{who} {l}"));
        }
        Assert.True(total > 120, $"The sweep opened only {total} pages; it is not looking at enough of the application to mean anything.");
        Assert.True(leaks.Count == 0, "Raw term tokens were rendered:\n" + string.Join("\n", leaks.Distinct().Take(40)));
    }
}
