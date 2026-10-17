using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>The Pack Manager pages through the real host: who can open them, the upload, check, install flow, the step-up before changes, and the protections on every page.</summary>
[Collection("Api")]
[Trait("Category", "Packs")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class PackManagerUiTests
{
    private const string TenantAdmin = "tenantadmin@a.example.test";
    private const string OrgAdmin = "orgadmin@a.example.test";
    private const string BizConfig = "bizconfig@a.example.test";
    private const string Viewer = "viewer.all@a.example.test";
    private const string Auditor = "auditor@a.example.test";

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] package, string name = "pack.ecpkg", bool withToken = true)
    {
        var token = await DemoApp.TokenAsync(client, "/Admin/Packs/Upload");
        using var form = new MultipartFormDataContent();
        if (withToken) form.Add(new StringContent(token), "__RequestVerificationToken");
        var file = new ByteArrayContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        return await client.PostAsync("/Admin/Packs/Upload", form);
    }

    [Fact]
    public async Task Only_people_who_hold_the_pack_permissions_can_open_each_page()
    {
        using var app = DemoApp.Start();
        using var anonymous = app.NewClient();
        var r = await anonymous.GetAsync("/Admin/Packs");
        Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);

        using var viewer = await app.SignInAsync(Viewer);
        foreach (var page in new[] { "/Admin/Packs", "/Admin/Packs/Upload", "/Admin/Packs/Assign", "/Admin/Packs/TrustKeys", "/Admin/Packs/Audit", "/Admin/Packs/Checks" })
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(page)).StatusCode);

        // The organisation administrator can read, check and assign, but cannot upload, manage trust keys or read the history.
        using var org = await app.SignInAsync(OrgAdmin, followRedirects: true);
        Assert.Equal(HttpStatusCode.OK, (await org.GetAsync("/Admin/Packs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await org.GetAsync("/Admin/Packs/Assign")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await org.GetAsync("/Admin/Packs/Checks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await org.GetAsync("/Admin/Packs/Upload")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await org.GetAsync("/Admin/Packs/TrustKeys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await org.GetAsync("/Admin/Packs/Audit")).StatusCode);

        // The business configuration administrator can upload but does not manage trust.
        using var biz = await app.SignInAsync(BizConfig, followRedirects: true);
        Assert.Equal(HttpStatusCode.OK, (await biz.GetAsync("/Admin/Packs/Upload")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await biz.GetAsync("/Admin/Packs/TrustKeys")).StatusCode);

        // The tenant administrator manages trust and reads the history; the auditor reads the history only.
        using var tenant = await app.SignInAsync(TenantAdmin, followRedirects: true);
        Assert.Equal(HttpStatusCode.OK, (await tenant.GetAsync("/Admin/Packs/TrustKeys")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.GetAsync("/Admin/Packs/Audit")).StatusCode);
        using var auditor = await app.SignInAsync(Auditor, followRedirects: true);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/Admin/Packs/Audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/Admin/Packs/Upload")).StatusCode);
    }

    [Fact]
    public async Task The_catalogue_lists_the_shipped_packs_from_their_package_files_and_marks_unsigned_ones_plainly()
    {
        using var app = DemoApp.Start();
        using var org = await app.SignInAsync(OrgAdmin, followRedirects: true);
        var html = await org.GetStringAsync("/Admin/Packs");
        Assert.Contains("Blank (no business defaults)", html);
        Assert.Contains("NOT SIGNED", html);
        Assert.Contains("Shipped", html);
        Assert.Contains("/Admin/Packs/Details?key=blank", html);
        var details = await org.GetStringAsync("/Admin/Packs/Details?key=blank&version=1.2.0");
        Assert.Contains("This pack is NOT signed.", details);
        Assert.Contains("Package checksum (SHA-256)", details);
        Assert.Equal(HttpStatusCode.NotFound, (await org.GetAsync("/Admin/Packs/Details?key=nope&version=1.0.0")).StatusCode);
        // A shipped pack is read-only: there is no retire control for it.
        Assert.DoesNotContain("Retire this version", details);
    }

    [Fact]
    public async Task Upload_check_and_install_needs_an_explicit_confirmation_and_shows_everything_in_plain_language()
    {
        using var app = DemoApp.Start();
        using var biz = await app.SignInAsync(BizConfig, followRedirects: false);
        var key = "ui-" + Guid.NewGuid().ToString("N")[..8];
        var package = PackTestKit.Build(key, "1.0.0");

        // The upload form refuses a post without the antiforgery token.
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(biz, package, withToken: false)).StatusCode);

        var up = await UploadAsync(biz, package);
        Assert.Equal(HttpStatusCode.Redirect, up.StatusCode);
        var review = up.Headers.Location!.OriginalString;
        Assert.StartsWith("/Admin/Packs/Review?token=", review);
        var page = await biz.GetStringAsync(review);
        Assert.Contains("NOT signed", page);
        Assert.Contains(key, page);
        Assert.Contains("Download this report (CSV)", page);
        Assert.Contains("will not publish any configuration", page);
        Assert.DoesNotContain("Exception", page);
        Assert.DoesNotContain("   at ", page);

        // The downloadable report is a CSV.
        var csv = await biz.GetAsync(review + "&handler=Report");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Severity,Code", await csv.Content.ReadAsStringAsync());

        // Without the confirmation box nothing is installed.
        var token = Regex.Match(review, "token=([0-9a-f-]+)").Groups[1].Value;
        var refused = await DemoApp.PostFormAsync(biz, review, new Dictionary<string, string> { ["Token"] = token }, $"/Admin/Packs/Review?handler=Install&token={token}");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Tick the box", await refused.Content.ReadAsStringAsync());
        Assert.DoesNotContain(key, await (await biz.GetAsync("/Admin/Packs?show=installed")).Content.ReadAsStringAsync());

        // With it, the pack is installed and the person lands on its details.
        var install = await DemoApp.PostFormAsync(biz, review, new Dictionary<string, string> { ["Token"] = token, ["Confirmed"] = "true" }, $"/Admin/Packs/Review?handler=Install&token={token}");
        Assert.Equal(HttpStatusCode.Redirect, install.StatusCode);
        Assert.StartsWith("/Admin/Packs/Details", install.Headers.Location!.OriginalString);
        Assert.Contains(key, await biz.GetStringAsync("/Admin/Packs?show=installed"));

        // The upload is spent: the review page now says so rather than failing.
        Assert.Equal(HttpStatusCode.NotFound, (await biz.GetAsync(review)).StatusCode);
    }

    [Fact]
    public async Task Another_person_cannot_open_an_upload_and_installing_after_ten_minutes_asks_for_a_fresh_sign_in()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        using var app = DemoApp.Start(services: s => s.AddSingleton<IClock>(clock));
        using var biz = await app.SignInAsync(BizConfig);
        using var tenant = await app.SignInAsync(TenantAdmin, followRedirects: true);
        var package = PackTestKit.Build("ui-" + Guid.NewGuid().ToString("N")[..8], "1.0.0");
        var up = await UploadAsync(biz, package);
        var review = up.Headers.Location!.OriginalString;
        var token = Regex.Match(review, "token=([0-9a-f-]+)").Groups[1].Value;

        // Someone else, even with the right permission, cannot see or install it.
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync(review)).StatusCode);

        // Eleven minutes later the installer must confirm it is them before the change.
        clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await DemoApp.PostFormAsync(biz, review, new Dictionary<string, string> { ["Token"] = token, ["Confirmed"] = "true" }, $"/Admin/Packs/Review?handler=Install&token={token}");
        Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.StartsWith("/Reauth", stale.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Text_from_a_pack_is_always_encoded_and_never_becomes_markup()
    {
        using var app = DemoApp.Start();
        using var biz = await app.SignInAsync(BizConfig);
        var key = "xss-" + Guid.NewGuid().ToString("N")[..8];
        const string hostile = "\"><b id=injected>bold</b>&amp;";
        var package = PackTestKit.Build(key, "1.0.0", displayName: hostile, description: "<u id=desc>d</u>", publisherName: "<i id=pub>Pub</i>");
        var up = await UploadAsync(biz, package);
        var review = up.Headers.Location!.OriginalString;
        var page = await biz.GetStringAsync(review);
        Assert.DoesNotContain("<b id=injected>", page);
        Assert.DoesNotContain("<i id=pub>", page);
        Assert.DoesNotContain("<u id=desc>", page);
        Assert.Contains("&lt;b id=injected&gt;", page);
    }

    [Fact]
    public async Task Trust_keys_reject_private_keys_and_never_echo_key_material()
    {
        using var app = DemoApp.Start();
        using var tenant = await app.SignInAsync(TenantAdmin, followRedirects: true);
        var (privatePem, publicSpki) = Ecp.Packs.PackKeys.Generate();
        var rejected = await DemoApp.PostFormAsync(tenant, "/Admin/Packs/TrustKeys", new Dictionary<string, string> { ["KeyId"] = "oops", ["Label"] = "Private key pasted by mistake", ["PublicKeyPem"] = privatePem }, "/Admin/Packs/TrustKeys?handler=Register");
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PRIVATE KEY", body.Replace("Never paste a private key; it would be refused.", ""));
        Assert.DoesNotContain(privatePem.Split('\n')[1].Trim(), body);

        var ok = await DemoApp.PostFormAsync(tenant, "/Admin/Packs/TrustKeys", new Dictionary<string, string> { ["KeyId"] = "ui-key", ["Label"] = "UI test publisher", ["PublicKeyPem"] = Ecp.Packs.PackKeys.ToPublicPem(publicSpki) }, "/Admin/Packs/TrustKeys?handler=Register");
        Assert.True(ok.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK);
        var list = await tenant.GetStringAsync("/Admin/Packs/TrustKeys");
        Assert.Contains("ui-key", list);
        Assert.Contains("UI test publisher", list);
        Assert.DoesNotContain("PRIVATE KEY", list);
    }

    private static async Task<(HttpStatusCode Status, string Status2, string Message)> PackReadinessAsync(DemoApp app)
    {
        using var client = app.NewClient();
        var response = await client.GetAsync("/health/ready");
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var check = doc.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "solution-packs");
        return (response.StatusCode, check.GetProperty("status").GetString()!, check.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Readiness_reports_the_shipped_packs_and_fails_when_the_pack_folder_is_missing()
    {
        using (var app = DemoApp.Start())
        {
            var (status, check, message) = await PackReadinessAsync(app);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("Healthy", check);
            Assert.Contains("shipped solution pack", message);
        }
        using (var missing = DemoApp.Start(s => { s["Ecp:DemoSeed"] = "false"; s["Ecp:Packs:ShippedDirectory"] = Path.Combine(Path.GetTempPath(), "no-such-packs-" + Guid.NewGuid().ToString("N")); }))
        {
            var (status, check, message) = await PackReadinessAsync(missing);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Equal("Unhealthy", check);
            Assert.DoesNotContain(Path.GetTempPath(), message);                      // no server path
        }
        using (var unsigned = DemoApp.Start(s => { s["Ecp:DemoSeed"] = "false"; s["Ecp:Packs:AllowUnsignedSyntheticPacks"] = "false"; }))
        {
            var (_, check, message) = await PackReadinessAsync(unsigned);              // unsigned shipped packs are not usable when the setting is off: degraded, with what to do
            Assert.Equal("Degraded", check);
            Assert.Contains("usable", message);
        }
    }
}
