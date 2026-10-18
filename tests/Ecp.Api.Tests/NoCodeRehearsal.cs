using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Configuration.Forms;
using Ecp.Lifecycle.Definitions;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>One line of the evidence pack: a step of the journey, who did it in which role, through which screen, and what came of it.</summary>
public sealed record RehearsalStep(int Number, string Title, string Persona, string Role, bool TechnicalRole, string Via, string Outcome, bool Passed);

/// <summary>
/// The no-code implementation rehearsal (docs/NO_CODE_REHEARSAL.md). A synthetic company is set up from nothing and brought to a working, loaded register by the people who would do it, using only the application's pages:
/// no source edit, no SQL, no Git and no JSON typed by a person for the business roles. The rehearsal records every step in a ledger, and the ledger is the proof that role separation held: the steps that need a technical role are
/// named as such and every other step names the page it used. It runs against the lightweight provider always and against real SQL Server when one is available.
/// </summary>
public sealed class RehearsalLedger
{
    private readonly List<RehearsalStep> _steps = [];
    public IReadOnlyList<RehearsalStep> Steps => _steps;

    public async Task Step(int number, string title, string persona, string role, string via, Func<Task<string>> body, bool technical = false)
    {
        try { var outcome = await body(); _steps.Add(new RehearsalStep(number, title, persona, role, technical, via, outcome, true)); }
        catch (Exception ex)
        {
            _steps.Add(new RehearsalStep(number, title, persona, role, technical, via, ex.GetType().Name + ": " + ex.Message.Split('\n')[0], false));
            throw;
        }
    }

    public string Markdown(string provider, string verification)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# No-code implementation rehearsal: evidence").AppendLine();
        sb.AppendLine($"- Provider: {provider} ({verification})").AppendLine($"- Steps: {_steps.Count} ({_steps.Count(s => s.Passed)} passed, {_steps.Count(s => !s.Passed)} failed)");
        sb.AppendLine($"- Steps needing a technical role: {_steps.Count(s => s.TechnicalRole)}; business-role steps: {_steps.Count(s => !s.TechnicalRole)}").AppendLine();
        sb.AppendLine("| # | Step | Who | Role | Technical role | Screen | Result |").AppendLine("|---|------|-----|------|----------------|--------|--------|");
        foreach (var s in _steps) sb.AppendLine($"| {s.Number} | {s.Title} | {s.Persona} | {s.Role} | {(s.TechnicalRole ? "yes" : "no")} | {s.Via} | {(s.Passed ? "Passed" : "Failed")}: {s.Outcome} |");
        return sb.ToString();
    }
}

internal static class NoCodeRehearsal
{
    public const string TenantAdmin = "tenantadmin@a.example.test";
    public const string OrgAdmin = "orgadmin@a.example.test";
    public const string BizConfig = "bizconfig@a.example.test";
    public const string ProcAdmin = "procadmin@a.example.test";
    public const string ProcAdmin2 = "procadmin2@a.example.test";
    public const string SysAdmin = "sysadmin@installation.example.test";
    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    public static async Task<string> GetAsync(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"GET {url} returned {(int)r.StatusCode}.");
        return body;
    }

    private static string Visible(string html) => Regex.Replace(Regex.Replace(html, "<script.*?</script>|<style.*?</style>", "", RegexOptions.Singleline), "<[^>]+>", " ");

    /// <summary>What the page says once its markup is removed, for assertions about words a person would read.</summary>
    public static string Words(string html) => Regex.Replace(System.Net.WebUtility.HtmlDecode(Visible(html)), @"\s+", " ");

    public static async Task<HttpResponseMessage> PostAsync(HttpClient c, string pageUrl, string? handler, IEnumerable<KeyValuePair<string, string>> fields, string? query = null)
        => await DemoApp.PostFormAsync(c, pageUrl, fields, handler is null ? pageUrl : pageUrl + (pageUrl.Contains('?') ? "&" : "?") + "handler=" + handler + (query is null ? "" : "&" + query));

    public static IEnumerable<KeyValuePair<string, string>> Fields(params (string Key, string Value)[] pairs) => pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value));

    public static async Task<(string Body, HttpStatusCode Status, string? Location)> Send(HttpClient c, string pageUrl, string? handler, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var r = await PostAsync(c, pageUrl, handler, fields);
        return (await r.Content.ReadAsStringAsync(), r.StatusCode, r.Headers.Location?.ToString());
    }

    public static void ExpectDone((string Body, HttpStatusCode Status, string? Location) r, string what)
    {
        if (r.Status == HttpStatusCode.Redirect) return;
        var shown = Regex.Match(r.Body, "class=\"error-summary\"[^>]*>(.*?)</div>", RegexOptions.Singleline);
        throw new InvalidOperationException($"{what} was not accepted ({(int)r.Status}): {Words(shown.Success ? shown.Value : r.Body)[..Math.Min(500, Words(shown.Success ? shown.Value : r.Body).Length)]}");
    }

    /// <summary>
    /// What the browser would post for the open draft of a kind (or, with none, the release in force), with the changes the person made to its fields applied first. The document is read here only to know what each field currently
    /// holds; the person's side of the journey is the fields.
    /// </summary>
    public static async Task<List<KeyValuePair<string, string>>> FormPostAsync(DemoApp app, string kind, Guid org, Func<JsonObject, JsonObject>? change = null)
    {
        var (tenant, _) = await app.FindUserAsync(TenantAdmin);
        var (json, schema) = await app.WithTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<Ecp.Configuration.Persistence.ConfigurationDbContext>();
            var draft = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Drafts).Where(d => d.OrganisationId == org && d.Kind == kind && (d.Status == Ecp.Configuration.Domain.DraftStatus.Draft || d.Status == Ecp.Configuration.Domain.DraftStatus.Validated)));
            var latest = await sp.GetRequiredService<IConfigurationReleases>().LatestAsync(kind, org);
            return (draft?.ContentJson ?? latest?.ContentJson, sp.GetRequiredService<IConfigurationFormEditor>().SchemaOf(kind));
        });
        var document = JsonNode.Parse(json ?? "{\"schemaVersion\":1}")!.AsObject();
        if (change is not null) document = change(document);
        var posted = FormBinder.Flatten(schema, document).SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v))).ToList();
        posted.Add(new("Kind", kind)); posted.Add(new("OrganisationId", org.ToString()));
        return posted;
    }

    public static async Task<(string Body, HttpStatusCode Status, string? Location)> UploadAsync(HttpClient c, string pageUrl, string handler, string fileName, byte[] content, params (string Key, string Value)[] fields)
    {
        var token = await DemoApp.TokenAsync(c, pageUrl);
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        var r = await c.PostAsync($"{pageUrl}?handler={handler}", form);
        return (await r.Content.ReadAsStringAsync(), r.StatusCode, r.Headers.Location?.ToString());
    }

    public static Guid FirstGuid(string text, string pattern) => Guid.Parse(Regex.Match(text, pattern).Groups[1].Value);
    public static string Pattern => GuidPattern;
}
