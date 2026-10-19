using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The company implementation evidence pack over real HTTP: only the installation administrator can open or download it; every section carries one of Verified, Warning, Failed or Not Verified and only Verified counts as Passed;
/// the files are hashed; it states facts and never declares readiness; and it holds no secret, no contract or document content and no organisation names; a cell a spreadsheet could run is neutralised.
/// </summary>
internal static class EvidencePackScenarios
{
    private const string SysAdmin = "sysadmin@installation.example.test";
    public static Action<Dictionary<string, string?>> Settings => s => { s["Ecp:Mode"] = "Test"; s["Ecp:Governance:SecondPerson"] = "Enforced"; s["Documents:Scanner:Provider"] = "mock"; };

    public static async Task The_pack_is_for_the_installation_administrator_and_states_facts_not_readiness(DemoApp app)
    {
        foreach (var email in new[] { "tenantadmin@a.example.test", "orgadmin@a.example.test", "auditor@a.example.test", "procadmin@a.example.test" })
        {
            using var other = await app.SignInAsync(email);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/Admin/Operations/EvidencePack")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/Admin/Operations/EvidencePack?handler=Download")).StatusCode);
        }
        using var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/Operations/EvidencePack")).StatusCode);

        using var sys = await app.SignInAsync(SysAdmin);
        // A neutral acceptance whose reason looks like a spreadsheet formula, and an email setting with secret references: neither may leak or run.
        var accepted = await DemoApp.PostFormAsync(sys, "/Admin/Operations/UpgradeReadiness", new Dictionary<string, string> { ["Code"] = "UPF-040", ["Reason"] = "=HYPERLINK(\"http://x.test\",\"click\") accepted for the rehearsal", ["ValidDays"] = "30" }, "/Admin/Operations/UpgradeReadiness?handler=Accept");
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await DemoApp.PostFormAsync(sys, "/Admin/Operations/Email", new Dictionary<string, string> { ["Provider"] = "smtp", ["From"] = "p@x.test", ["Host"] = "127.0.0.1", ["Port"] = "2525", ["UseTls"] = "false", ["UserRef"] = "env:ECP_EVIDENCE_USER", ["PasswordRef"] = "env:ECP_EVIDENCE_PASSWORD" }, "/Admin/Operations/Email?handler=Save");

        var page = await sys.GetStringAsync("/Admin/Operations/EvidencePack");
        Accessibility.AssertBasics(page, "/Admin/Operations/EvidencePack");
        Assert.Contains("never says the environment is ready for Production", page);
        Assert.Contains("Not Verified", page);

        var response = await sys.GetAsync("/Admin/Operations/EvidencePack?handler=Download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), Assert.Single(response.Headers.GetValues("X-Evidence-Pack-Sha256")));

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(["SHA256SUMS", "evidence-pack.csv", "evidence-pack.json", "evidence-pack.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8); return r.ReadToEnd(); }
        // The hash manifest matches the files.
        foreach (var line in Read("SHA256SUMS").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split("  ");
            using var s = zip.GetEntry(parts[1])!.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            Assert.Equal(parts[0], Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())));
        }

        var json = Read("evidence-pack.json");
        var csv = Read("evidence-pack.csv");
        var md = Read("evidence-pack.md");
        using var doc = JsonDocument.Parse(json);
        var sections = doc.RootElement.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Range(1, 13).Select(i => $"EVP-{i:00}"), sections.Select(s => s.GetProperty("code").GetString()!));
        var allowed = new[] { "Verified", "Warning", "Failed", "Not Verified" };
        foreach (var s in sections)
        {
            var status = s.GetProperty("status").GetString()!;
            Assert.Contains(status, allowed);
            // Only Verified counts as Passed. Warning, Failed and Not Verified never do.
            Assert.Equal(status == "Verified" ? "Passed" : status == "Failed" ? "Failed" : status == "Not Verified" ? "Not Verified" : "Warning (not accepted)", s.GetProperty("countsAs").GetString());
        }
        Assert.Contains(sections, s => s.GetProperty("status").GetString() == "Not Verified");                     // identity and UAT are never claimed
        Assert.Equal(5, doc.RootElement.GetProperty("signOff").GetArrayLength());
        Assert.All(doc.RootElement.GetProperty("signOff").EnumerateArray(), l => Assert.Equal("", l.GetProperty("name").GetString()));
        Assert.Contains(doc.RootElement.GetProperty("acceptedExceptions").EnumerateArray(), e => e.GetProperty("code").GetString() == "UPF-040");

        // Facts, not readiness.
        foreach (var text in new[] { json, md, csv }) { Assert.DoesNotContain("ready for Production", text.Replace("is not an approval, a certification or a declaration that the environment is ready for Production", "")); Assert.DoesNotContain("PRODUCTION READY", text, StringComparison.OrdinalIgnoreCase); }
        Assert.Contains("not an approval", json);
        // Nothing secret, nothing from contracts, documents or organisations.
        foreach (var text in new[] { json, md, csv })
        {
            Assert.DoesNotContain("PRIVATE KEY", text);
            Assert.DoesNotContain("ECP_EVIDENCE_PASSWORD", text);
            Assert.DoesNotContain("ECP_EVIDENCE_USER", text);
            Assert.DoesNotContain("DEMO-AGR", text);
            Assert.DoesNotContain("Southern Cross", text);
            Assert.DoesNotContain("Demo Procurement Group", text);
            Assert.DoesNotContain("@a.example.test", text);                                                      // no tenant user's address
        }
        // The CSV neutralises a cell a spreadsheet could run.
        foreach (var line in csv.Split('\n')) foreach (var cell in line.Split(',')) Assert.False(cell.TrimStart('"').StartsWith('='), "A formula-looking cell was not neutralised: " + cell);
        Assert.Contains("HYPERLINK", csv);                                                                     // still readable as text
    }
}

[Collection("Api")]
[Trait("Category", "Evidence")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ImplementationEvidenceTests
{
    [Fact] public async Task The_pack_is_for_the_installation_administrator_and_states_facts_not_readiness() { using var app = DemoApp.Start(EvidencePackScenarios.Settings); await EvidencePackScenarios.The_pack_is_for_the_installation_administrator_and_states_facts_not_readiness(app); }
}

[Collection("Api")]
[Trait("Category", "SqlServer")]
public sealed class ImplementationEvidenceSqlServerTests
{
    [SqlServerFact] public async Task The_pack_states_facts_not_readiness_on_real_SQL_Server() { using var app = DemoApp.StartSqlServer(EvidencePackScenarios.Settings); await EvidencePackScenarios.The_pack_is_for_the_installation_administrator_and_states_facts_not_readiness(app); }
}
