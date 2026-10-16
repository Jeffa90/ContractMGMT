using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Ecp.Contracts;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.Identity;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>Drives the migration capability through the real pages, as a person would in a browser: nothing here calls a service directly except to check the outcome.</summary>
[Collection("Api")]
[Trait("Category", "Migration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class MigrationUiTests
{
    private static Guid Guid1(string text, string pattern) => Guid.Parse(Regex.Match(text, pattern).Groups[1].Value);
    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string pageUrl, string? handler, params (string Key, string Value)[] fields)
        => await DemoApp.PostFormAsync(c, pageUrl, fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)), handler is null ? pageUrl : $"{pageUrl}?handler={handler}");

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, string pageUrl, string handler, string fileName, byte[] content, params (string Key, string Value)[] fields)
    {
        var token = await DemoApp.TokenAsync(c, pageUrl);
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await c.PostAsync($"{pageUrl}?handler={handler}", form);
    }

    private static async Task<string> GetAsync(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"GET {url} returned {(int)r.StatusCode}: {body[..Math.Min(1500, body.Length)]}");
        return body;
    }

    private static string Location(HttpResponseMessage r) => r.Headers.Location?.ToString() ?? throw new InvalidOperationException($"Expected a redirect but got {(int)r.StatusCode}.");

    private static async Task Ok(HttpResponseMessage r, string what)
    {
        if (r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK) return;
        throw new InvalidOperationException($"{what} failed with {(int)r.StatusCode}: {(await r.Content.ReadAsStringAsync())[..Math.Min(600, (await r.Content.ReadAsStringAsync()).Length)]}");
    }

    [Fact]
    public async Task The_sample_files_load_end_to_end_through_the_pages_and_appear_in_the_application()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync("procadmin@a.example.test");
        using var second = await app.SignInAsync("procadmin2@a.example.test");

        // The migration area is offered to people who hold the permissions, with a plain explanation.
        var home = await GetAsync(admin, "/Migration");
        Assert.Contains("Data migration", home);
        var orgId = Guid1(home, $"<option value=\"({GuidPattern})\"");

        // 1. Project.
        var created = await PostAsync(admin, "/Migration", null, ("OrganisationId", orgId.ToString()), ("Name", "Sample register load"), ("SourceSystem", "Medius"), ("SourceSystemVersion", "2026.1"),
            ("DataOwner", "Data Owner"), ("ExtractionDate", DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var projectUrl = Location(created);
        var projectId = Guid1(projectUrl, $"({GuidPattern})");

        // 2. Mapping profile from the repository samples.
        var profile = await PostAsync(admin, "/Migration/Profiles", null, ("OrganisationId", orgId.ToString()), ("Name", "Sample register profile"), ("SourceSystem", "Medius"), ("SourceSystemVersion", "2026.1"));
        var profileUrl = Location(profile);
        var profilePage = await GetAsync(admin, profileUrl);
        var versionId = Guid1(profilePage, $"name=\"VersionId\" value=\"({GuidPattern})\"");
        await Ok(await UploadAsync(admin, profileUrl, "Fields", "field-mapping-profile-complete.csv", MigrationSamples.FieldMappings, ("VersionId", versionId.ToString())), "field mappings");
        await Ok(await UploadAsync(admin, profileUrl, "Lookups", "status-lookup-map.csv", MigrationSamples.StatusLookups, ("VersionId", versionId.ToString())), "lookups");
        Assert.Contains("18", await GetAsync(admin, $"{profileUrl}?versionId={versionId}")); // eighteen mappings
        await Ok(await PostAsync(admin, profileUrl, "Options", ("VersionId", versionId.ToString()), ("OpenEndedTypes", "Evergreen")), "options");
        var published = await PostAsync(admin, profileUrl, "Publish", ("VersionId", versionId.ToString()), ("PublishNote", "Sample profile"));
        Assert.Equal(HttpStatusCode.Redirect, published.StatusCode);
        Assert.Contains("published, unchangeable", await GetAsync(admin, $"{profileUrl}?versionId={versionId}"));

        // 3. Legacy-load policy, then a legacy final batch.
        await Ok(await PostAsync(admin, projectUrl, "Legacy", ("AllowedStatuses", "Effective, Ended"), ("AllowRegisteredDate", "true"), ("ExpiresDate", DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd")), ("AllowOverwrite", "false")), "policy");
        var batch = await PostAsync(admin, projectUrl, "CreateBatch", ("BatchName", "Sample final load"), ("Kind", "Final"), ("Strategy", "SkipExisting"), ("DocumentOption", "AddVersion"),
            ("IsLegacyLoad", "true"), ("ProfileVersionId", versionId.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, batch.StatusCode);
        var batchUrl = Location(batch);
        var batchId = Guid1(batchUrl, $"({GuidPattern})");

        // 4. Source files: contracts CSV and the ZIP (whose root holds attachment-manifest.csv).
        await Ok(await UploadAsync(admin, batchUrl, "UploadContracts", "contracts-import.csv", MigrationSamples.ContractsCsv), "contracts upload");
        await Ok(await UploadAsync(admin, batchUrl, "UploadArchive", "attachments.zip", MigrationSamples.AttachmentZip(includeManifest: true)), "archive upload");
        var afterUpload = await GetAsync(admin, batchUrl);
        Assert.Contains("contracts-import.csv", afterUpload);
        Assert.Contains("attachments.zip", afterUpload);
        Assert.Contains("attachment-manifest.csv", afterUpload); // read from the archive

        // 5. Dry run: nothing is written to the target.
        await using (var before = app.Factory.Services.CreateAsyncScope()) { }
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, batchUrl, "Validate")).StatusCode);
        var validated = await GetAsync(admin, batchUrl);
        Assert.Contains("would load", validated);
        Assert.Contains("Validated", validated);

        // 6. A legacy load needs a second person. The committer cannot be the confirmer.
        var tooEarly = await PostAsync(admin, batchUrl, "Commit");
        Assert.Contains("second person", await (tooEarly.StatusCode == HttpStatusCode.Redirect ? GetAsync(admin, batchUrl) : tooEarly.Content.ReadAsStringAsync()));
        await Ok(await PostAsync(second, batchUrl, "Confirm"), "confirm");
        await Ok(await PostAsync(admin, batchUrl, "Commit"), "commit");
        var committed = await GetAsync(admin, batchUrl);
        Assert.Contains("Committed", committed);

        // 7. Reconcile and accept.
        await Ok(await PostAsync(admin, batchUrl, "Reconcile"), "reconcile");
        var reconciliation = await GetAsync(admin, $"{batchUrl}/Reconciliation");
        Assert.Contains("Balanced", reconciliation);
        await Ok(await PostAsync(admin, batchUrl, "Accept", ("Note", "Checked both contracts and three documents against the source."), ("Acknowledge", "true")), "accept");
        Assert.Contains("Accepted", await GetAsync(admin, batchUrl));

        // 8. What the application now shows: contracts, suppliers, documents, provenance, audit.
        var register = await GetAsync(admin, "/Contracts?q=DEMO-AGR");
        Assert.Contains("DEMO-AGR-0001", register); Assert.Contains("DEMO-AGR-0002", register);
        var contractId = await ContractIdAsync(app, "DEMO-AGR-0001");
        var details = await GetAsync(admin, $"/Contracts/Details/{contractId}");
        Assert.Contains("Migration provenance", details);
        Assert.Contains("MED-1001", details);
        Assert.Contains("DEMO-AGR-0001_executed.pdf", details);
        Assert.Contains("DEMO-AGR-0001_schedule.docx", details);
        Assert.Contains("the source system", details); // Registered Date came from the source, labelled as such
        var documentLink = Regex.Match(details, $"href=\"(/Documents/Download/{GuidPattern})\"").Groups[1].Value;
        Assert.NotEmpty(documentLink);
        var download = await admin.GetAsync(documentLink);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await download.Content.ReadAsByteArrayAsync())[..4]));
        Assert.Contains("Southern Cross Demo Supplies", await GetAsync(admin, "/Suppliers"));

        var provenance = await GetAsync(admin, $"{batchUrl}/Provenance");
        Assert.Contains("MED-1001", provenance); Assert.Contains("Document", provenance);

        using var auditor = await app.SignInAsync("auditor@a.example.test");
        var audit = await GetAsync(auditor, $"/Admin/Audit?batch={batchId}");
        Assert.Contains("migration.contract.legacy_loaded", audit);
        Assert.Contains("migration.document.imported", audit);

        // Downloads work and the error report is a real workbook.
        var errors = await admin.GetAsync($"{batchUrl}?handler=ErrorsXlsx");
        Assert.Equal(HttpStatusCode.OK, errors.StatusCode);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString((await errors.Content.ReadAsByteArrayAsync())[..2]));
    }

    private static async Task<Guid> ContractIdAsync(DemoApp app, string reference)
    {
        var (tenant, _) = await app.FindUserAsync("procadmin@a.example.test");
        return await app.WithTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<Ecp.Contracts.Persistence.ContractsDbContext>();
            return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Contracts).Where(c => c.AgreementReference == reference).Select(c => c.Id));
        });
    }
}
