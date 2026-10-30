using System.Net;
using System.Text.RegularExpressions;
using Ecp.Contracts.Persistence;
using Ecp.Host.Pages.Admin.Fields;
using Ecp.Metadata;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The guided Fields pages, through the real host: an administrator adds three fields without code, a key, JSON or SQL (Insurance Expiry, Critical Supplier, Payment Terms), publishes them, and everything that
/// follows is proved over HTTP and in the database: the forms, saved values, details, editing, the record lock, filters, exports, a field that must stay hidden, retirement keeping history, and restoring a version.
/// The same scenario runs on SQLite and on real SQL Server.
/// </summary>
internal static class GuidedFieldScenarios
{
    public const string Admin = "orgadmin@a.example.test";
    public const string Owner = "catowner.packaging@a.example.test";
    public const string Viewer = "viewer.all@a.example.test";
    public const string Exporter = "procadmin@a.example.test";

    public static async Task<Guid> OrgAsync(DemoApp app)
    {
        var (tenant, _) = await app.FindUserAsync(Owner);
        return await app.WithTenantAsync(tenant, async sp =>
        {
            foreach (var candidate in await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync())
                if (await sp.GetRequiredService<Ecp.Contracts.ISupplierDirectory>().FindByVendorNumberAsync(candidate.Id, "900001") is not null) return candidate.Id;
            throw new InvalidOperationException("demo organisation missing");
        });
    }

    /// <summary>The words a page shows a person: tags, scripts and entities removed.</summary>
    public static string Text(string html)
    {
        var withoutScript = Regex.Replace(html, "(?s)<(script|style)\\b.*?</\\1>", " ");
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(withoutScript, "<[^>]+>", " "), "\\s+", " ")).Trim();
    }

    public static async Task<string> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode} {Text(body)[..Math.Min(300, Text(body).Length)]}");
        return body;
    }

    /// <summary>Asserts that a post was accepted (redirected on), and if it was not, says what the page told the person.</summary>
    public static async Task Accepted(HttpResponseMessage response, string what)
    {
        if (response.StatusCode == HttpStatusCode.Redirect) return;
        var text = Text(await response.Content.ReadAsStringAsync());
        var at = text.IndexOf("There is a problem", StringComparison.Ordinal);
        if (at < 0) at = Math.Max(0, text.IndexOf(" Back to Fields", StringComparison.Ordinal));
        Assert.Fail($"{what}: {(int)response.StatusCode} {text.Substring(at, Math.Min(500, text.Length - at))}");
    }

    public static Task<HttpResponseMessage> Post(HttpClient client, string pageUrl, IEnumerable<(string Name, string Value)> fields, string? postUrl = null)
        => DemoApp.PostFormAsync(client, pageUrl, fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), postUrl);

    /// <summary>What a browser would post for the first form of the page that holds <paramref name="marker"/>: every input, select and text area as it stands, so a test changes only what a person would change.</summary>
    public static List<(string Name, string Value)> ReadForm(string html, string marker)
    {
        var form = Regex.Matches(html, "(?s)<form\\b.*?</form>").Select(m => m.Value).First(f => f.Contains(marker, StringComparison.Ordinal));
        var fields = new List<(string, string)>();
        foreach (Match m in Regex.Matches(form, "(?s)<input\\b[^>]*>"))
        {
            var tag = m.Value;
            string? Attr(string n) { var a = Regex.Match(tag, $"\\b{n}=\"([^\"]*)\""); return a.Success ? WebUtility.HtmlDecode(a.Groups[1].Value) : null; }
            var name = Attr("name"); if (name is null) continue;
            var type = (Attr("type") ?? "text").ToLowerInvariant();
            if (type is "submit" or "button" or "file" or "radio" || name == "__RequestVerificationToken") continue;
            if (type == "checkbox") { if (Regex.IsMatch(tag, "\\bchecked\\b")) fields.Add((name, Attr("value") ?? "on")); continue; }
            fields.Add((name, Attr("value") ?? ""));
        }
        foreach (Match m in Regex.Matches(form, "(?s)<select\\b([^>]*)>(.*?)</select>"))
        {
            var name = Regex.Match(m.Groups[1].Value, "\\bname=\"([^\"]*)\"").Groups[1].Value; if (name.Length == 0) continue;
            var selected = Regex.Match(m.Groups[2].Value, "(?s)<option\\b[^>]*\\bselected\\b[^>]*>").Value;
            var picked = selected.Length > 0 ? selected : Regex.Match(m.Groups[2].Value, "<option\\b[^>]*>").Value;
            if (picked.Length > 0) fields.Add((name, WebUtility.HtmlDecode(Regex.Match(picked, "\\bvalue=\"([^\"]*)\"").Groups[1].Value)));
        }
        foreach (Match m in Regex.Matches(form, "(?s)<textarea\\b([^>]*)>(.*?)</textarea>"))
            fields.Add((Regex.Match(m.Groups[1].Value, "\\bname=\"([^\"]*)\"").Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value)));
        return fields;
    }

    public static List<(string Name, string Value)> With(List<(string Name, string Value)> form, params (string Name, string Value)[] changes)
    {
        var names = changes.Select(c => c.Name).ToHashSet();
        return form.Where(f => !names.Contains(f.Name)).Concat(changes).ToList();
    }

    private static string Url(Guid org, string path, string? key = null) => $"{path}?organisationId={org}" + (key is null ? "" : $"&key={Uri.EscapeDataString(key)}");

    /// <summary>The page fields a person fills in to add a field: a name, a type, where it goes and which standard rules are on. Nothing technical.</summary>
    public static List<(string, string)> NewField(string name, string type, string section, bool searchable, bool filterable, bool sortable, bool reportable, bool required = false, params (string, string)[] more)
    {
        var form = new List<(string, string)>
        {
            ("PreviousType", type), ("Form.Name", name), ("Form.Type", type), ("Form.Section", section), ("Form.NewSectionName", section == EditModel.NewSection ? "Risk & Compliance" : ""),
            ("Form.Position", "end"), ("Form.Required", required ? "true" : "false"), ("Form.Searchable", searchable ? "true" : "false"), ("Form.Filterable", filterable ? "true" : "false"),
            ("Form.Sortable", sortable ? "true" : "false"), ("Form.Reportable", reportable ? "true" : "false"), ("Form.Classification", "")
        };
        var replaced = more.Select(m => m.Item1).ToHashSet();
        form.RemoveAll(f => replaced.Contains(f.Item1));
        form.AddRange(more);
        return form;
    }

    public static async Task<Guid> ContractIdAsync(DemoApp app, Guid tenant, string title)
        => await app.WithTenantAsync(tenant, async sp => await sp.GetRequiredService<ContractsDbContext>().Contracts.AsNoTracking().Where(c => c.ContractTitle == title).Select(c => c.Id).FirstAsync());

    public static async Task RunAcceptanceAsync(DemoApp app)
    {
        var org = await OrgAsync(app);
        var (tenant, _) = await app.FindUserAsync(Owner);
        using var admin = await app.SignInAsync(Admin);
        using var owner = await app.SignInAsync(Owner);
        using var viewer = await app.SignInAsync(Viewer);
        using var exporter = await app.SignInAsync(Exporter);
        var migrationsBefore = await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<ContractsDbContext>().Database.GetAppliedMigrationsAsync()).Count());

        // 1. The administrator finds Fields in the Administration menu and lands on a page that says what it is for. No technical word is needed to use it.
        var adminHome = await GetAsync(admin, "/Admin");
        Assert.Contains("href=\"/Admin/Fields\"", adminHome);
        var landing = await GetAsync(admin, Url(org, "/Admin/Fields"));
        var landingText = Text(landing);
        foreach (var heading in new[] { "Field", "Type", "Section", "Used on", "Required", "Status" }) Assert.Contains(heading, landingText);
        Assert.Contains("+ Add field", landingText);
        Assert.Contains("Search", landingText);
        Assert.Contains("Version history", landingText);

        // 2. Add field: the page asks for a name and shows the type choices; choosing a type shows only that type's settings.
        var addPage = await GetAsync(admin, Url(org, "/Admin/Fields/Edit"));
        var addText = Text(addPage);
        foreach (var type in new[] { "Text", "Long text", "Number", "Decimal", "Money", "Percentage", "Date", "Date/time", "Yes/No", "Single choice", "Multiple choice", "Person", "Lookup", "Email", "URL", "File reference", "Calculated" })
            Assert.Contains(type, addText);
        foreach (var word in new[] { "JSON", "SQL", "migration", "CODE |", "Release number", "Edit.Key" }) Assert.DoesNotContain(word, addText + addPage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Choices", addText);                                               // a text field has no choice editor
        var dateSettings = Text(await (await Post(admin, Url(org, "/Admin/Fields/Edit"), NewField("", "TEXT", "general", true, true, true, true), Url(org, "/Admin/Fields/Edit") + "&handler=Refresh&NewType=DATE")).Content.ReadAsStringAsync());
        Assert.Contains("Earliest date allowed", dateSettings);
        Assert.DoesNotContain("Longest allowed", dateSettings);
        Assert.DoesNotContain("Searchable", dateSettings);                                        // a date is filtered, not text-searched: the box is not offered
        var choiceSettings = Text(await (await Post(admin, Url(org, "/Admin/Fields/Edit"), NewField("", "TEXT", "general", true, true, true, true), Url(org, "/Admin/Fields/Edit") + "&handler=Refresh&NewType=SINGLE_CHOICE")).Content.ReadAsStringAsync());
        Assert.Contains("Add option", choiceSettings);

        // 3. A mistake is explained in plain words, not as a rule code.
        var noName = await Post(admin, Url(org, "/Admin/Fields/Edit"), NewField("", "SINGLE_CHOICE", "general", true, true, true, true), Url(org, "/Admin/Fields/Edit"));
        var noNameText = Text(await noName.Content.ReadAsStringAsync());
        Assert.Contains("Give the field a name.", noNameText);
        Assert.Contains("Add at least one option for people to choose from.", noNameText);
        var duplicates = await Post(admin, Url(org, "/Admin/Fields/Edit"), NewField("Payment Terms", "SINGLE_CHOICE", "general", true, true, true, true,
            more: [("Form.Options[0].Label", "30 days"), ("Form.Options[0].Active", "true"), ("Form.Options[1].Label", "30 Days"), ("Form.Options[1].Active", "true")]), Url(org, "/Admin/Fields/Edit"));
        Assert.Contains("appears more than once", Text(await duplicates.Content.ReadAsStringAsync()));

        // 4. Insurance Expiry: a date, in a new section. Saved as a draft; the page then shows the plain-English review.
        var save1 = await Post(admin, Url(org, "/Admin/Fields/Edit"), NewField("Insurance Expiry", "DATE", EditModel.NewSection, false, true, true, true), Url(org, "/Admin/Fields/Edit"));
        await Accepted(save1, "save Insurance Expiry");
        var saved1 = await GetAsync(admin, save1.Headers.Location!.OriginalString);
        var review1 = Text(saved1);
        Assert.Matches(@"Insurance Expiry will appear under Risk & Compliance on New \w+, Edit \w+ and \w+ details\.", review1);
        Assert.Contains("It will be optional, filterable, sortable and included in exports and reports.", review1);
        Assert.Contains("Preview", review1);
        Assert.Contains("Changes waiting to be published", review1);                              // the panel under the review offers publishing...
        Assert.Matches(@"<button[^>]*>\s*Publish\s*</button>", saved1);                           // ...labelled for what will happen to this person
        Assert.Contains("#review-h", save1.Headers.Location!.OriginalString);                     // and the save lands on it
        Assert.Contains("name=\"cf.insurance_expiry\"", saved1);                                  // the preview draws the very input the Create form draws

        // 5. Critical Supplier (Yes/No) and Payment Terms (single choice: 30, 45, 60, 90 days).
        await Accepted(await Post(admin, Url(org, "/Admin/Fields/Edit"), NewField("Critical Supplier", "BOOLEAN", "risk_compliance", false, true, true, true), Url(org, "/Admin/Fields/Edit")), "post");
        var terms = NewField("Payment Terms", "SINGLE_CHOICE", "risk_compliance", true, true, true, true,
            more: [("Form.Options[0].Label", "30 days"), ("Form.Options[0].Active", "true"), ("Form.Options[1].Label", "45 days"), ("Form.Options[1].Active", "true"),
                   ("Form.Options[2].Label", "60 days"), ("Form.Options[2].Active", "true"), ("Form.Options[3].Label", "90 days"), ("Form.Options[3].Active", "true")]);
        await Accepted(await Post(admin, Url(org, "/Admin/Fields/Edit"), terms, Url(org, "/Admin/Fields/Edit")), "post");

        // 6. The landing page lists them as waiting to be published; nobody else sees them yet.
        var pending = await GetAsync(admin, Url(org, "/Admin/Fields"));
        var pendingText = Text(pending);
        Assert.Contains("Changes waiting to be published", pendingText);
        foreach (var name in new[] { "Insurance Expiry", "Critical Supplier", "Payment Terms" }) Assert.Contains(name, pendingText);
        Assert.Contains("New, not published yet", pendingText);
        Assert.DoesNotContain("Insurance Expiry", await GetAsync(owner, $"/Contracts/Create?organisationId={org}"));

        // 7. Publish. One press; no version number, key or note is required.
        var publish = await Post(admin, Url(org, "/Admin/Fields"), [("organisationId", org.ToString()), ("Note", "")], "/Admin/Fields?handler=Publish");
        Assert.Equal(HttpStatusCode.Redirect, publish.StatusCode);
        var after = await GetAsync(admin, publish.Headers.Location!.OriginalString);
        var afterText = Text(after);
        Assert.Contains("Published. The fields are on the forms", afterText);
        Assert.Matches(@"Insurance Expiry\s+Date\s+Risk & Compliance", afterText);
        Assert.Matches(@"Payment Terms\s+Single choice\s+Risk & Compliance", afterText);
        Assert.Matches(@"Critical Supplier\s+Yes/No", afterText);
        Assert.DoesNotContain("Changes waiting to be published", afterText);
        Assert.Contains("Published", afterText);
        var search = Text(await GetAsync(admin, Url(org, "/Admin/Fields") + "&q=payment"));
        Assert.Contains("Payment Terms", search); Assert.DoesNotContain("Insurance Expiry", search);

        // Publishing added fields without changing the database: no migration, no new table, no new column.
        Assert.Equal(migrationsBefore, await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<ContractsDbContext>().Database.GetAppliedMigrationsAsync()).Count()));

        // 8. All three appear on Create; values save; they appear on Details.
        var create = await GetAsync(owner, $"/Contracts/Create?organisationId={org}");
        foreach (var label in new[] { "Insurance Expiry", "Critical Supplier", "Payment Terms" }) Assert.Contains(label, create);
        Assert.Contains("Risk &amp; Compliance", create);
        foreach (var days in new[] { "30 days", "45 days", "60 days", "90 days" }) Assert.Contains($">{days}</option>", create);
        var categoryId = Regex.Match(create, "<option value=\"([0-9a-f-]{36})\"[^>]*>Packaging</option>").Groups[1].Value;
        var supplierId = Regex.Match(create, "<option value=\"([0-9a-f-]{36})\"[^>]*>Southern Cross Demo Supplies").Groups[1].Value;
        List<(string, string)> ContractForm(string title, string expiry, string critical, string payment) =>
        [
            ("OrganisationId", org.ToString()), ("Title", title), ("SupplierId", supplierId), ("CategoryId", categoryId), ("Classification", "Internal"), ("Input.CurrencyCode", "AUD"),
            ("cfp.insurance_expiry", "1"), ("cf.insurance_expiry", expiry), ("cfp.critical_supplier", "1"), ("cf.critical_supplier", critical), ("cfp.payment_terms", "1"), ("cf.payment_terms", payment)
        ];
        var bad = await Post(owner, $"/Contracts/Create?organisationId={org}", ContractForm("Guided fields bad date", "not-a-date", "true", "60 days"), "/Contracts/Create");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Insurance Expiry", Text(await bad.Content.ReadAsStringAsync()));
        var createdA = await Post(owner, $"/Contracts/Create?organisationId={org}", ContractForm("Guided fields contract A", "2027-03-31", "true", "60 days"), "/Contracts/Create");
        Assert.Equal(HttpStatusCode.Redirect, createdA.StatusCode);
        var detailsA = Text(await GetAsync(owner, createdA.Headers.Location!.OriginalString));
        Assert.Matches(@"Insurance Expiry\s+31/0?3/2027", detailsA);
        Assert.Matches(@"Critical Supplier\s+Yes", detailsA);
        Assert.Matches(@"Payment Terms\s+60 days", detailsA);
        var createdB = await Post(owner, $"/Contracts/Create?organisationId={org}", ContractForm("Guided fields contract B", "2028-05-01", "false", "30 days"), "/Contracts/Create");
        Assert.Equal(HttpStatusCode.Redirect, createdB.StatusCode);
        var idA = Guid.Parse(createdA.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);

        // 9. Values can be edited where the person is authorised, and the edit shows on Details.
        var editUrl = $"/Contracts/Edit/{idA}";
        var editHtml = await GetAsync(owner, editUrl);
        Assert.Contains("Payment Terms", editHtml);
        var editForm = With(ReadForm(editHtml, "name=\"Title\""), ("cf.payment_terms", "90 days"), ("cf.insurance_expiry", "2027-09-30"));
        await Accepted(await Post(owner, editUrl, editForm, editUrl), "post");
        var editedA = Text(await GetAsync(owner, $"/Contracts/Details/{idA}"));
        Assert.Matches(@"Payment Terms\s+90 days", editedA);
        Assert.Matches(@"Insurance Expiry\s+30/0?9/2027", editedA);
        Assert.Matches(@"Critical Supplier\s+Yes", editedA);

        // 10. The values are in the database in typed columns: a date, a yes/no and a text, under the field's own name.
        var stored = await app.WithTenantAsync(tenant, async sp => await sp.GetRequiredService<ContractsDbContext>().CustomValues.AsNoTracking().Where(v => v.ContractId == idA).OrderBy(v => v.FieldKey).ToListAsync());
        Assert.Equal(new DateOnly(2027, 9, 30), stored.Single(v => v.FieldKey == "insurance_expiry").DateValue);
        Assert.True(stored.Single(v => v.FieldKey == "critical_supplier").BoolValue);
        Assert.Equal("90 days", stored.Single(v => v.FieldKey == "payment_terms").TextValue);
        Assert.All(stored, v => Assert.Equal(tenant, v.TenantId));

        // 11. An effective record is still locked: the new fields show on it, but it cannot be edited.
        var effective = await ContractIdAsync(app, tenant, "Synthetic Packaging Supply Agreement");
        var lockedPage = await GetAsync(owner, $"/Contracts/Details/{effective}");
        Assert.Contains("Insurance Expiry", lockedPage);
        Assert.DoesNotContain("Edit details", lockedPage);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/Contracts/Edit/{effective}")).StatusCode);
        var lockedPost = await Post(owner, editUrl, [("Title", "Locked"), ("cfp.payment_terms", "1"), ("cf.payment_terms", "30 days")], $"/Contracts/Edit/{effective}");
        Assert.Equal(HttpStatusCode.Forbidden, lockedPost.StatusCode);

        // 12. Each can be filtered, through the register's own filter form, and the filter tells the contracts apart.
        async Task<string> Filtered(string filter) => Text(await GetAsync(owner, $"/Contracts?org={org}&f={Uri.EscapeDataString(filter)}"));
        var byDate = await Filtered("insurance_expiry|Between|2027-07-01,2027-12-31");
        Assert.True(byDate.Contains("Guided fields contract A"), byDate[(byDate.IndexOf("Filters", StringComparison.Ordinal))..Math.Min(byDate.Length, byDate.IndexOf("Filters", StringComparison.Ordinal) + 900)]); Assert.DoesNotContain("Guided fields contract B", byDate);
        var byDateB = await Filtered("insurance_expiry|Between|2028-01-01,2028-12-31");
        Assert.Contains("Guided fields contract B", byDateB); Assert.DoesNotContain("Guided fields contract A", byDateB);
        var byYes = await Filtered("critical_supplier|Is|true");
        Assert.Contains("Guided fields contract A", byYes); Assert.DoesNotContain("Guided fields contract B", byYes);
        var byTerms = await Filtered("payment_terms|Eq|90 days");
        Assert.Contains("Guided fields contract A", byTerms); Assert.DoesNotContain("Guided fields contract B", byTerms);
        var byTermsB = await Filtered("payment_terms|Eq|30 days");
        Assert.Contains("Guided fields contract B", byTermsB); Assert.DoesNotContain("Guided fields contract A", byTermsB);
        var registerPage = await GetAsync(owner, $"/Contracts?org={org}");
        foreach (var label in new[] { "Insurance Expiry", "Critical Supplier", "Payment Terms" }) Assert.Contains($">{label}</option>", registerPage);   // each is offered in the filter list

        // 13. An authorised export contains the fields and their values.
        async Task<string> ExportCsv(HttpClient who, string? filter = null)
        {
            var fields = new List<(string, string)> { ("View", "system:all-contracts"), ("Org", org.ToString()), ("ExportFormat", "csv"), ("ExportLarge", "false"), ("ExportAdditional", "true") };
            if (filter is not null) fields.Add(("F", filter));
            var response = await Post(who, $"/Contracts?org={org}", fields, "/Contracts?handler=Export");
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"export -> {(int)response.StatusCode}");
            return await response.Content.ReadAsStringAsync();
        }
        var csv = await ExportCsv(exporter, "payment_terms|Eq|90 days");
        var header = csv.Split('\n')[0];
        foreach (var label in new[] { "Insurance Expiry", "Critical Supplier", "Payment Terms" }) Assert.Contains(label, header);
        var rowA = csv.Split('\n').Single(r => r.Contains("Guided fields contract A"));
        Assert.Contains("90 days", rowA);
        Assert.DoesNotContain("Guided fields contract B", csv);

        // 14. A field that must stay hidden does not leak. "Negotiation Notes" is for category owners only; "Legal Hold Note" is held at a level above the owner's ceiling.
        var notes = NewField("Negotiation Notes", "TEXT", "risk_compliance", true, true, true, true, more: [("Form.VisibleToRoles", Ecp.Identity.Domain.RoleCodes.CategoryOwner)]);
        await Accepted(await Post(admin, Url(org, "/Admin/Fields/Edit"), notes, Url(org, "/Admin/Fields/Edit")), "post");
        var top = await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<Ecp.Identity.IAccessService>().ClassificationScaleAsync()).Levels.Last());
        var hold = NewField("Legal Hold Note", "TEXT", "risk_compliance", true, true, true, true, more: [("Form.Classification", top)]);
        await Accepted(await Post(admin, Url(org, "/Admin/Fields/Edit"), hold, Url(org, "/Admin/Fields/Edit")), "post");
        await Accepted(await Post(admin, Url(org, "/Admin/Fields"), [("organisationId", org.ToString()), ("Note", "Two more fields")], "/Admin/Fields?handler=Publish"), "post");
        var editHtml2 = await GetAsync(owner, editUrl);
        Assert.Contains("Negotiation Notes", editHtml2);
        var withNotes = With(ReadForm(editHtml2, "name=\"Title\""), ("cf.negotiation_notes", "Aim for 60 days"), ("cfp.negotiation_notes", "1"));
        await Accepted(await Post(owner, editUrl, withNotes, editUrl), "post");
        Assert.Contains("Aim for 60 days", Text(await GetAsync(owner, $"/Contracts/Details/{idA}")));
        var viewerDetails = await GetAsync(viewer, $"/Contracts/Details/{idA}");
        Assert.Contains("Insurance Expiry", viewerDetails);
        Assert.DoesNotContain("Negotiation Notes", viewerDetails); Assert.DoesNotContain("Aim for 60 days", viewerDetails);
        var viewerRegister = await GetAsync(viewer, $"/Contracts?org={org}&q=Aim");
        Assert.DoesNotContain("Guided fields contract A", Text(viewerRegister));                  // searching for a hidden value finds nothing
        Assert.DoesNotContain(">Negotiation Notes</option>", await GetAsync(viewer, $"/Contracts?org={org}"));
        var viewerFilter = await viewer.GetAsync($"/Contracts?org={org}&f={Uri.EscapeDataString("negotiation_notes|Contains|Aim")}");
        Assert.DoesNotContain("Guided fields contract A", Text(await viewerFilter.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(viewer, $"/Contracts?org={org}", [("View", "system:all-contracts"), ("Org", org.ToString()), ("ExportFormat", "csv"), ("ExportLarge", "false"), ("ExportAdditional", "true")], "/Contracts?handler=Export")).StatusCode);   // a viewer may not export at all
        var exportHeader = (await ExportCsv(exporter)).Split('\n')[0];
        Assert.Contains("Insurance Expiry", exportHeader);                                         // the exporter sees the ordinary fields...
        Assert.DoesNotContain("Negotiation Notes", exportHeader);                                  // ...but not the one that is for category owners only
        Assert.DoesNotContain("Aim for 60 days", await ExportCsv(exporter));
        Assert.DoesNotContain("Legal Hold Note", await GetAsync(owner, $"/Contracts/Details/{idA}"));
        Assert.DoesNotContain("Legal Hold Note", await GetAsync(owner, $"/Contracts/Edit/{idA}"));
        Assert.DoesNotContain("Legal Hold Note", await GetAsync(owner, $"/Contracts/Create?organisationId={org}"));

        // A creator below that level cannot write a value for it either (it would be one they could never read back).
        var gated = await Post(owner, $"/Contracts/Create?organisationId={org}", [.. ContractForm("Guided fields gated", "2027-03-31", "true", "60 days"), ("cfp.legal_hold_note", "1"), ("cf.legal_hold_note", "secret")], "/Contracts/Create");
        Assert.Equal(HttpStatusCode.OK, gated.StatusCode);
        Assert.Contains("You cannot change Legal Hold Note", Text(await gated.Content.ReadAsStringAsync()));
        Assert.Equal(0, await app.WithTenantAsync(tenant, async sp => await sp.GetRequiredService<ContractsDbContext>().Contracts.AsNoTracking().CountAsync(c => c.ContractTitle == "Guided fields gated")));

        // 15. Retire Critical Supplier: it leaves the forms, the history stays on the records and in exports.
        var retirePage = Text(await GetAsync(admin, Url(org, "/Admin/Fields/Retire", "critical_supplier")));
        Assert.Contains("record(s) already hold a value", retirePage);
        Assert.Contains("never deleted", retirePage);
        await Accepted(await Post(admin, Url(org, "/Admin/Fields/Retire", "critical_supplier"), [("organisationId", org.ToString()), ("key", "critical_supplier")], Url(org, "/Admin/Fields/Retire", "critical_supplier")), "post");
        Assert.Contains("Retiring, not published yet", Text(await GetAsync(admin, Url(org, "/Admin/Fields"))));
        await Accepted(await Post(admin, Url(org, "/Admin/Fields"), [("organisationId", org.ToString()), ("Note", "")], "/Admin/Fields?handler=Publish"), "post");
        Assert.Matches(@"Critical Supplier\s+Yes/No[^|]*?Retired", Text(await GetAsync(admin, Url(org, "/Admin/Fields"))));
        Assert.DoesNotContain("Critical Supplier", await GetAsync(owner, $"/Contracts/Create?organisationId={org}"));
        var retiredDetails = Text(await GetAsync(owner, $"/Contracts/Details/{idA}"));
        Assert.Matches(@"Critical Supplier\s+Retired field\s+Yes|Critical Supplier Retired field Yes", retiredDetails);
        Assert.Contains("Critical Supplier", (await ExportCsv(exporter)).Split('\n')[0]);
        var keptRows = await app.WithTenantAsync(tenant, async sp => await sp.GetRequiredService<ContractsDbContext>().CustomValues.AsNoTracking().CountAsync(v => v.FieldKey == "critical_supplier"));
        Assert.True(keptRows >= 2, "retiring a field must not delete the values already entered");

        // 16. Version history says what each version did, per field; an earlier version can be restored and nothing is erased.
        var history = Text(await GetAsync(admin, Url(org, "/Admin/Fields/History")));
        Assert.Contains("Added Insurance Expiry", history); Assert.Contains("Retired Critical Supplier", history);
        var oneField = Text(await GetAsync(admin, Url(org, "/Admin/Fields/History", "critical_supplier")));
        Assert.Contains("Retired Critical Supplier", oneField); Assert.DoesNotContain("Added Negotiation Notes", oneField);
        var restore = await Post(admin, Url(org, "/Admin/Fields/History"), [("organisationId", org.ToString()), ("ToRelease", "2"), ("Reason", "Critical Supplier is needed again")], "/Admin/Fields/History?handler=Rollback&organisationId=" + org);
        Assert.True(restore.StatusCode is HttpStatusCode.Redirect, $"restore -> {(int)restore.StatusCode} {Text(await restore.Content.ReadAsStringAsync())[..Math.Min(400, Text(await restore.Content.ReadAsStringAsync()).Length)]}");
        Assert.Contains("Critical Supplier", await GetAsync(owner, $"/Contracts/Create?organisationId={org}"));
        Assert.Matches(@"Critical Supplier\s+Yes", Text(await GetAsync(owner, $"/Contracts/Details/{idA}")));

        // 17. Another organisation's administrator cannot reach this organisation's fields, and the fields are not theirs.
        using var other = await app.SignInAsync("tenantadmin@b.example.test");
        var foreign = await other.GetAsync(Url(org, "/Admin/Fields"));
        Assert.True(foreign.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"foreign -> {(int)foreign.StatusCode}");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(Url(org, "/Admin/Fields"))).StatusCode);       // an owner may use the fields, not define them
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(Url(org, "/Admin/Fields/Edit"))).StatusCode);

        // 18. Nothing the administrator saw or did needed a key, JSON, SQL, a migration or code.
        foreach (var url in new[] { Url(org, "/Admin/Fields"), Url(org, "/Admin/Fields/Edit"), Url(org, "/Admin/Fields/Edit", "payment_terms"), Url(org, "/Admin/Fields/Preview", "payment_terms"), Url(org, "/Admin/Fields/History"), Url(org, "/Admin/Fields/Retire", "payment_terms") })
        {
            var text = Text(await GetAsync(admin, url));
            foreach (var word in new[] { "JSON", "SQL", "migration", "CODE |", "schema", "viewerKind" }) Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(migrationsBefore, await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<ContractsDbContext>().Database.GetAppliedMigrationsAsync()).Count()));
    }
}

[Collection("Api")]
[Trait("Category", "Studio")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class GuidedFieldsUiTests
{
    [Fact]
    public async Task An_administrator_adds_three_fields_without_code_and_every_consequence_is_proved_over_HTTP()
    {
        using var app = DemoApp.Start();
        await GuidedFieldScenarios.RunAcceptanceAsync(app);
    }

    [Fact]
    public async Task Where_a_second_person_must_publish_the_page_says_submit_and_the_reviewer_publishes()
    {
        using var app = DemoApp.Start(GovernanceScenarios.Settings("Test", "Enforced"));
        var org = await GuidedFieldScenarios.OrgAsync(app);
        using var author = await app.SignInAsync("bizconfig@a.example.test");
        using var reviewer = await app.SignInAsync("orgadmin@a.example.test");
        using var owner = await app.SignInAsync(GuidedFieldScenarios.Owner);
        var edit = $"/Admin/Fields/Edit?organisationId={org}";
        var tiers = GuidedFieldScenarios.NewField("Value Band", "SINGLE_CHOICE", "general", true, true, true, true,
            more: [("Form.Options[0].Label", "Low"), ("Form.Options[0].Active", "true"), ("Form.Options[1].Label", "High"), ("Form.Options[1].Active", "true")]);
        await GuidedFieldScenarios.Accepted(await GuidedFieldScenarios.Post(author, edit, tiers), "author saves the draft");

        // The author is offered "Submit for publication", never "Publish", and is told why.
        var panel = await GuidedFieldScenarios.GetAsync(author, $"/Admin/Fields?organisationId={org}");
        Assert.Matches(@"<button[^>]*>\s*Submit for publication\s*</button>", panel);
        Assert.DoesNotMatch(@"<button[^>]*>\s*Publish\s*</button>", panel);
        Assert.Contains("needs a second person to publish", GuidedFieldScenarios.Text(panel));
        var submit = await GuidedFieldScenarios.Post(author, $"/Admin/Fields?organisationId={org}", [("organisationId", org.ToString()), ("Note", "Add the value band")], "/Admin/Fields?handler=Publish");
        await GuidedFieldScenarios.Accepted(submit, "author submits");
        var afterSubmit = GuidedFieldScenarios.Text(await GuidedFieldScenarios.GetAsync(author, submit.Headers.Location!.OriginalString));
        Assert.Contains("Submitted for publication. A second person has to review and publish it", afterSubmit);
        Assert.Contains("Waiting for review", afterSubmit);
        Assert.DoesNotContain("Value Band", await GuidedFieldScenarios.GetAsync(owner, $"/Contracts/Create?organisationId={org}"));      // nothing is on the forms yet

        // A second person is offered the review, and publishing it puts the field on the forms.
        var reviewerPage = await GuidedFieldScenarios.GetAsync(reviewer, $"/Admin/Fields?organisationId={org}");
        var review = Regex.Match(reviewerPage, @"/Admin/Reviews/Details\?id=([0-9a-f-]{36})");
        Assert.True(review.Success, "the reviewer is shown the submission");
        var details = $"/Admin/Reviews/Details?id={review.Groups[1].Value}";
        await GuidedFieldScenarios.Accepted(await GuidedFieldScenarios.Post(reviewer, details, [("Note", "Looks right")], details + "&handler=Publish"), "reviewer publishes");
        var create = await GuidedFieldScenarios.GetAsync(owner, $"/Contracts/Create?organisationId={org}");
        Assert.Contains("Value Band", create);
        Assert.Contains(">Low</option>", create);
    }

    [Fact]
    public async Task The_guided_pages_meet_the_structural_accessibility_checks()
    {
        using var app = DemoApp.Start();
        var org = await GuidedFieldScenarios.OrgAsync(app);
        using var admin = await app.SignInAsync(GuidedFieldScenarios.Admin);
        var edit = $"/Admin/Fields/Edit?organisationId={org}";
        await GuidedFieldScenarios.Accepted(await GuidedFieldScenarios.Post(admin, edit, GuidedFieldScenarios.NewField("Review Date", "DATE", "general", false, true, true, true)), "date");
        await GuidedFieldScenarios.Accepted(await GuidedFieldScenarios.Post(admin, edit, GuidedFieldScenarios.NewField("Service Level", "MULTI_CHOICE", "general", true, true, false, true,
            more: [("Form.Options[0].Label", "Gold"), ("Form.Options[0].Active", "true"), ("Form.Options[1].Label", "Silver"), ("Form.Options[1].Active", "false")])), "choice");
        foreach (var url in new[]
        {
            $"/Admin/Fields?organisationId={org}", edit, $"{edit}&type=SINGLE_CHOICE", $"{edit}&type=CALCULATED_DISPLAY", $"{edit}&type=LOOKUP", $"{edit}&type=CURRENCY",
            $"{edit}&key=review_date", $"{edit}&key=service_level", $"/Admin/Fields/Preview?organisationId={org}&key=service_level", $"/Admin/Fields/Retire?organisationId={org}&key=review_date",
            $"/Admin/Fields/History?organisationId={org}"
        })
            Accessibility.AssertBasics(await GuidedFieldScenarios.GetAsync(admin, url), url);
    }

    [Theory]
    [InlineData("Insurance Expiry", "insurance_expiry")]
    [InlineData("  Payment   Terms (days) ", "payment_terms_days")]
    [InlineData("Café Review", "cafe_review")]
    [InlineData("2nd opinion", "field_2nd_opinion")]
    [InlineData("!!!", "field")]
    public void A_field_key_is_made_from_the_name(string name, string expected) => Assert.Equal(expected, FieldGuide.KeyFromName(name, []));

    [Fact]
    public void A_generated_key_never_clashes_with_a_core_field_or_an_existing_field()
    {
        Assert.NotEqual("status", FieldGuide.KeyFromName("Status", []));
        Assert.Equal("status_2", FieldGuide.KeyFromName("Status", []));
        Assert.Equal("insurance_expiry_2", FieldGuide.KeyFromName("Insurance Expiry", ["insurance_expiry"]));
        Assert.Equal("insurance_expiry_3", FieldGuide.KeyFromName("Insurance Expiry", ["INSURANCE_EXPIRY", "insurance_expiry_2"]));
        Assert.Matches("^[a-z][a-z0-9_]{1,62}$", FieldGuide.KeyFromName(new string('x', 200), []));
    }

    [Fact]
    public void A_choice_code_reads_naturally_and_is_unique()
    {
        Assert.Equal("30 days", FieldGuide.CodeFromLabel("30 days", []));
        Assert.Equal("30 Days 2", FieldGuide.CodeFromLabel("30 Days", ["30 days"]));
        Assert.Equal("Yes_No", FieldGuide.CodeFromLabel("Yes|No", []));
        Assert.Equal("Option", FieldGuide.CodeFromLabel("***", []));
    }

    [Fact]
    public void Only_the_rules_that_make_sense_for_a_type_are_offered()
    {
        Assert.False(FieldGuide.CanSearch(FieldTypes.Date)); Assert.True(FieldGuide.CanFilter(FieldTypes.Date)); Assert.True(FieldGuide.CanSort(FieldTypes.Date));
        Assert.False(FieldGuide.CanFilter(FieldTypes.LongText)); Assert.True(FieldGuide.CanSearch(FieldTypes.LongText));
        Assert.False(FieldGuide.CanSort(FieldTypes.MultiChoice));
        Assert.False(FieldGuide.CanRequire(FieldTypes.Calculated)); Assert.False(FieldGuide.CanFilter(FieldTypes.Calculated));
        Assert.False(FieldGuide.CanUseInApprovals(FieldTypes.Url));
        Assert.True(FieldGuide.HasOptions(FieldTypes.SingleChoice) && !FieldGuide.HasOptions(FieldTypes.Text));
        Assert.True(FieldGuide.HasDateRange(FieldTypes.Date) && !FieldGuide.HasLength(FieldTypes.Date));
        Assert.Equal(FieldTypes.All.Count, FieldGuide.Types.Count);                                  // every engine type has a plain description
    }
}

[Collection("Api")]
[Trait("Category", "SqlServer")]
public sealed class GuidedFieldsSqlServerTests
{
    [SqlServerFact]
    public async Task The_same_acceptance_scenario_over_real_SQL_Server_with_typed_columns_and_row_level_security()
    {
        using var app = DemoApp.StartSqlServer();
        await GuidedFieldScenarios.RunAcceptanceAsync(app);
        // On SQL Server the values really are in typed columns, and the table has not changed shape since the fields were published.
        var (tenant, _) = await app.FindUserAsync(GuidedFieldScenarios.Owner);
        var columns = await app.WithTenantAsync(tenant, async sp => await sp.GetRequiredService<ContractsDbContext>().Database
            .SqlQueryRaw<string>("select data_type + ':' + column_name as Value from information_schema.columns where table_schema = 'contracts' and table_name = 'ContractCustomValue' and column_name in ('DateValue','BoolValue','TextValue') order by column_name").ToListAsync());
        Assert.Equal(["bit:BoolValue", "date:DateValue", "nvarchar:TextValue"], columns);
        // Straight from the table, as a database administrator would look: the three fields' values sit in their typed columns under the field's own name, tenant-stamped.
        var rows = await app.WithTenantAsync(tenant, async sp => await sp.GetRequiredService<ContractsDbContext>().Database
            .SqlQueryRaw<string>("select FieldKey + '=' + coalesce(convert(varchar(10), DateValue, 23), case BoolValue when 1 then 'yes' when 0 then 'no' end, TextValue, '') as Value from contracts.ContractCustomValue where FieldKey in ('insurance_expiry','critical_supplier','payment_terms') and ContractId in (select Id from contracts.Contract where ContractTitle = 'Guided fields contract A') order by FieldKey").ToListAsync());
        Assert.Equal(["critical_supplier=yes", "insurance_expiry=2027-09-30", "payment_terms=90 days"], rows);
    }
}
