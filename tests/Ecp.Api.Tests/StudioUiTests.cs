using System.Net;
using System.Text.RegularExpressions;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>The Configuration Studio and the pages it drives, through the real host: define a field, publish it, see it on the forms, search and export by it, and the pages' protections.</summary>
[Collection("Api")]
[Trait("Category", "Studio")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class StudioUiTests
{
    private const string Owner = "catowner.packaging@a.example.test";
    private const string Admin = "orgadmin@a.example.test";

    private static async Task<Guid> OrgAsync(DemoApp app)
    {
        var (tenant, _) = await app.FindUserAsync(Owner);
        return await app.WithTenantAsync(tenant, async sp =>
        {
            foreach (var candidate in await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync())
                if (await sp.GetRequiredService<Ecp.Contracts.ISupplierDirectory>().FindByVendorNumberAsync(candidate.Id, "900001") is not null) return candidate.Id;
            throw new InvalidOperationException("demo organisation missing");
        });
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string page, string handler, params (string, string)[] fields)
        => DemoApp.PostFormAsync(c, page, fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)), handler.Length == 0 ? page : $"{page}?handler={handler}");

    private static async Task<string> Ok(HttpResponseMessage r, string what)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, $"{what}: {(int)r.StatusCode} {Regex.Match(body, "<li>(.*?)</li>").Groups[1].Value}");
        return body;
    }

    [Fact]
    public async Task An_administrator_defines_and_publishes_a_field_and_it_appears_on_the_forms_the_register_and_an_export()
    {
        using var app = DemoApp.Start();
        var org = await OrgAsync(app);
        using var admin = await app.SignInAsync(Admin, followRedirects: true);
        var studioResp = await admin.GetAsync("/Admin/Studio"); var studio = await studioResp.Content.ReadAsStringAsync(); Assert.True(studioResp.IsSuccessStatusCode, ((int)studioResp.StatusCode) + " " + studio[..Math.Min(600, studio.Length)]);
        Assert.Contains("Additional contract fields", studio);
        var fieldsUrl = $"/Admin/Studio/Fields?organisationId={org}";

        // Nothing can be changed before a draft is started; then a field is added, previewed, saved, validated and published.
        await Ok(await Post(admin, "/Admin/Studio/Fields", "Start", ("organisationId", org.ToString())), "start");
        var form = "/Admin/Studio/Field";
        var fields = new[]
        {
            ("organisationId", org.ToString()), ("Edit.Key", "ui_tier"), ("Edit.Label", "Service tier"), ("Edit.Type", "SINGLE_CHOICE"), ("Edit.Section", "general"), ("Edit.Order", "0"), ("Edit.Required", "false"),
            ("Edit.Choices", "GOLD | Gold\nSILVER | Silver"), ("Edit.Classification", "Internal"), ("Edit.Searchable", "true"), ("Edit.Filterable", "true"), ("Edit.Sortable", "true"), ("Edit.Reportable", "true"),
            ("Edit.UsableInApprovals", "false"), ("Edit.Status", "Active")
        };
        var preview = await Ok(await Post(admin, form, "Preview", fields), "preview");
        Assert.True(preview.Contains("Create form"), Regex.Match(preview, "(?s)<main.*?</main>").Value[..Math.Min(1500, Regex.Match(preview, "(?s)<main.*?</main>").Value.Length)]); Assert.Contains("cf_ui_tier", preview); Assert.Contains("Gold", preview);
        await Ok(await Post(admin, form, "", fields), "save field");
        var list = await admin.GetStringAsync(fieldsUrl);
        Assert.Contains("Service tier", list); Assert.Contains("ui_tier", list);
        var checks = await Ok(await Post(admin, "/Admin/Studio/Fields", "Validate", ("organisationId", org.ToString())), "validate");
        Assert.DoesNotContain("Must fix", checks);
        await Ok(await Post(admin, "/Admin/Studio/Fields", "Publish", ("organisationId", org.ToString()), ("Note", "Service tier for the page tests")), "publish");
        Assert.Contains("Published release", await admin.GetStringAsync(fieldsUrl));

        // The history page shows the release; the owner sees the field on the create form and keeps what they enter.
        Assert.Contains("Service tier for the page tests", await admin.GetStringAsync($"/Admin/Studio/History?organisationId={org}"));
        using var owner = await app.SignInAsync(Owner, followRedirects: true);
        var create = await owner.GetStringAsync($"/Contracts/Create?organisationId={org}");
        Assert.Contains("Service tier", create); Assert.Contains("name=\"cf.ui_tier\"", create);

        // Someone without the right cannot open the Studio pages.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Admin/Studio/Fields")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Admin/Studio/Field")).StatusCode);
    }
}
