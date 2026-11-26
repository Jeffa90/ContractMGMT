using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

public sealed class ExcelTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..16];

    internal static byte[] Sheet(string[] headers, params object?[][] rows)
    {
        using var book = new XLWorkbook();
        var s = book.Worksheets.Add("Sheet1");
        for (var i = 0; i < headers.Length; i++) s.Cell(1, i + 1).Value = headers[i];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                switch (rows[r][c])
                {
                    case null: break;
                    case DateTime d: s.Cell(r + 2, c + 1).Value = d; break;
                    case int n: s.Cell(r + 2, c + 1).Value = n; break;
                    case double n: s.Cell(r + 2, c + 1).Value = n; break;
                    default: s.Cell(r + 2, c + 1).Value = rows[r][c]!.ToString(); break;
                }
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        return ms.ToArray();
    }

    internal static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string kind, string step, byte[] file, string name = "import.xlsx")
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(content, "file", name);
        var response = await client.PostAsync($"/api/excel/{kind}/{step}", form, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement);
    }

    internal static int Count(JsonElement body, string what) => body.GetProperty("counts").GetProperty(what).GetInt32();

    [Fact]
    public async Task People_preview_changes_nothing_then_import_adds_and_updates_and_refuses_bad_rows()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var (a, b) = (Unique("xa"), Unique("xb"));
        await app.AddPersonAsync(b, "Bea Before", AppFixture.NoRoles);
        var file = Sheet(["Windows logon", "Name", "Email", "Contract owner", "Legal", "Active"],
            [a, "Ann Imported", "ann.imported@example.com", "Yes", "No", null],
            [$@"test\{b}", null, null, null, "Yes", null],                       // existing: only Legal changes; blanks keep
            ["bad logon here", "X", "x@example.com", null, null, null],
            [Unique("xc"), "No Email", null, "Yes", null, null]);

        var (status, preview) = await PostAsync(admin, "people", "preview", file);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(preview.GetProperty("applied").GetBoolean());
        Assert.Equal((1, 1, 2), (Count(preview, "added"), Count(preview, "updated"), Count(preview, "refused")));
        await using var db = await app.OpenAsync();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person WHERE Name = 'Ann Imported'"));

        var (_, result) = await PostAsync(admin, "people", "import", file);
        Assert.True(result.GetProperty("applied").GetBoolean());
        var ann = await db.QuerySingleAsync<Person>("SELECT * FROM contracts.Person WHERE Name = 'Ann Imported'");
        Assert.Equal($@"TEST\{a}", ann.WindowsAccount);
        Assert.True(ann.IsContractOwner);
        var bea = await db.QuerySingleAsync<Person>("SELECT * FROM contracts.Person WHERE AccountKey = @k", new { k = $@"TEST\{b}".ToUpperInvariant() });
        Assert.Equal("Bea Before", bea.Name); // blank Name kept the existing value
        Assert.True(bea.IsLegal);
        Assert.Contains("(from Excel)", await db.QuerySingleAsync<string>("SELECT TOP 1 Summary FROM contracts.AuditEvent WHERE SubjectType = 'Person' AND SubjectId = @id ORDER BY Id DESC", new { id = ann.Id }));
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task Only_admins_import_people_and_a_bad_file_gets_a_plain_message()
    {
        var user = Unique("own");
        await app.AddPersonAsync(user, "Oscar Owner", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var owner = app.ClientFor($@"TEST\{user}");
        var file = Sheet(["Windows logon", "Name", "Email"], [Unique("z"), "Z", "z@example.com"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(owner, "people", "import", file)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/excel/people", Ct)).StatusCode);

        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var (status, body) = await PostAsync(admin, "people", "preview", "not a workbook"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("couldn't be opened", body.GetProperty("message").GetString());
        var (status2, body2) = await PostAsync(admin, "people", "preview", Sheet(["Name"], ["Someone"]));
        Assert.Equal(HttpStatusCode.BadRequest, status2);
        Assert.Contains("Windows logon", body2.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Lists_import_adds_categories_with_subcategories_in_the_same_sheet_and_exports_round_trip()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var top = Unique("Cat");
        // Family > Category > Subcategory (D-115), the parent given as a path; rows can come before their parents.
        var file = Sheet(["List", "Name", "Parent category", "Active"],
            ["Category", "Linehaul", $"{top} > Freight", null],
            ["Category", "Freight", top, null],
            ["Category", top, null, null],
            ["Category", "Deeper", $"{top} > Freight > Linehaul", null], // a fourth level: refused
            ["Agreement type", Unique("Type"), null, null],
            ["Currency", "jpy", null, null],
            ["Planet", "Mars", null, null]);
        var (_, result) = await PostAsync(admin, "lists", "import", file);
        Assert.Equal((5, 2), (Count(result, "added"), Count(result, "refused")));
        await using var db = await app.OpenAsync();
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM contracts.Category s JOIN contracts.Category p ON p.Id = s.ParentId JOIN contracts.Category f ON f.Id = p.ParentId
            WHERE f.Name = @top AND f.ParentId IS NULL AND p.Name = 'Freight' AND s.Name = 'Linehaul'
            """, new { top }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WHERE ListName = 'Currency' AND Value = 'JPY'"));

        // Export, then import the export unchanged: nothing changes.
        var export = await admin.GetAsync("/api/excel/lists", Ct);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
        var (_, again) = await PostAsync(admin, "lists", "preview", await export.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal((0, 0), (Count(again, "added"), Count(again, "refused")));
        // Other tests hide and show their own list entries in parallel, so check this test's rows.
        var mine = again.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("label").GetString()!.Contains(top)).ToList();
        Assert.Equal(3, mine.Count); // the family, its category and the subcategory
        Assert.All(mine, r => Assert.Equal("same", r.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task Suppliers_import_checks_ABNs_and_only_admins_import()
    {
        var user = Unique("sup");
        await app.AddPersonAsync(user, "Sid Supplier", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false, procurement = true });
        using var owner = app.ClientFor($@"TEST\{user}");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var (good, bad, hide) = (Unique("Good "), Unique("Bad "), Unique("Hide "));
        var file = Sheet(["Name", "ABN"], [good, "51 824 753 556"], [bad, "12345678901"], [hide, null]);
        // Only admins import (Ryan's feedback, D-110); everyone can still export.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(owner, "suppliers", "preview", file)).Status);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/excel/suppliers", Ct)).StatusCode);
        var (_, first) = await PostAsync(admin, "suppliers", "import", file);
        Assert.Equal((2, 1), (Count(first, "added"), Count(first, "refused")));
        var (_, second) = await PostAsync(admin, "suppliers", "preview", Sheet(["Name", "Active"], [hide, "No"]));
        Assert.Equal(1, Count(second, "updated"));
    }

    [Fact]
    public async Task Contracts_import_adds_drafts_with_lookups_updates_drafts_and_refuses_unknown_values()
    {
        // Only admins import (D-110); this one is also a contract owner, as JBS's admins are.
        var user = Unique("con");
        await app.AddPersonAsync(user, "Connie Contracts", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = true, auditor = false });
        using var owner = app.ClientFor($@"TEST\{user}");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var plainUser = Unique("cop");
        await app.AddPersonAsync(plainUser, "Pat Plain", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var plain = app.ClientFor($@"TEST\{plainUser}");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(plain, "contracts", "preview", Sheet(["Title"], ["Not allowed"]))).Status);
        var cat = Unique("Freight");
        await PostAsync(admin, "lists", "import", Sheet(["List", "Name", "Parent category"], ["Category", cat, null], ["Category", "Road", cat]));
        var supplier = Unique("Linfox ");
        var headers = new[] { "Reference", "Title", "Supplier", "Category", "Owner", "Value", "End date", "Notice period (days)", "Evergreen" };
        var (_, result) = await PostAsync(owner, "contracts", "import", Sheet(headers,
            [null, "Road freight 2027", supplier, $"{cat} > Road", user, "$1,200,000", new DateTime(2027, 12, 31), 90, "No"],
            [null, "Evergreen cleaning", null, null, null, 5000, "31/12/2027", null, "Yes"],
            [null, "Bad category", null, "Nonsense", null, null, null, null, null],
            [null, null, null, null, null, null, null, null, null, ]));
        Assert.Equal((1, 2), (Count(result, "added"), Count(result, "refused"))); // evergreen with an end date, and an unknown category, are refused

        await using var db = await app.OpenAsync();
        var row = await db.QuerySingleAsync("SELECT k.Id, k.Reference, k.Value, k.EndDate, s.Name AS SupplierName FROM contracts.Contract k LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId WHERE k.Title = 'Road freight 2027'");
        Assert.Equal(supplier, (string)row.SupplierName); // the new supplier was added
        Assert.Equal(1200000m, (decimal)row.Value);
        Assert.Equal(new DateTime(2027, 12, 31), (DateTime)row.EndDate);

        // Update the draft by reference; a blank cell keeps the existing value.
        var (_, update) = await PostAsync(owner, "contracts", "import", Sheet(["Reference", "Title", "Value"], [(string)row.Reference, "Road freight 2027-28", null]));
        Assert.Equal(1, Count(update, "updated"));
        Assert.Equal(1200000m, await db.ExecuteScalarAsync<decimal>("SELECT Value FROM contracts.Contract WHERE Id = @id", new { id = (int)row.Id }));
        Assert.Contains("(from Excel)", await db.QuerySingleAsync<string>("SELECT TOP 1 Summary FROM contracts.AuditEvent WHERE ContractId = @id ORDER BY Id DESC", new { id = (int)row.Id }));

        // The export lists it, and importing the export changes nothing.
        var export = await (await owner.GetAsync("/api/excel/contracts", Ct)).Content.ReadAsByteArrayAsync(Ct);
        var (_, again) = await PostAsync(owner, "contracts", "preview", export);
        Assert.Equal(0, Count(again, "added")); // rows with a reference never add
        // Other tests change their own contracts in parallel, so check this contract's row: re-importing the export changes nothing.
        var mine = again.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("label").GetString()!.StartsWith((string)row.Reference));
        Assert.True(mine.GetProperty("action").GetString() == "same", mine.ToString());

        // Once a contract has moved past Draft, an unchanged row is fine and a change is refused.
        var rowVersion = (await owner.GetFromJsonAsync<JsonElement>($"/api/contracts/{(int)row.Id}", Ct)).GetProperty("contract").GetProperty("rowVersion").GetString();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/contracts/{(int)row.Id}/withdraw", new { reason = "Supplier pulled out", rowVersion }, Ct)).StatusCode);
        var (_, unchanged) = await PostAsync(owner, "contracts", "preview", Sheet(["Reference", "Title"], [(string)row.Reference, "Road freight 2027-28"]));
        Assert.Equal("same", unchanged.GetProperty("rows")[0].GetProperty("action").GetString());
        var (_, changedLater) = await PostAsync(owner, "contracts", "preview", Sheet(["Reference", "Title"], [(string)row.Reference, "Road freight 2029"]));
        var refusedRow = changedLater.GetProperty("rows")[0];
        Assert.Equal("refused", refusedRow.GetProperty("action").GetString());
        Assert.Contains("so it can't be changed by import now. This row would change: title.", refusedRow.GetProperty("notes")[0].GetString());
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task Supplier_contacts_import_in_bulk_with_several_per_supplier_and_update_on_a_second_run()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var ownerUser = Unique("co");
        await app.AddPersonAsync(ownerUser, $"Cora Owner {ownerUser[^4..]}", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false, procurement = false });
        using var owner = app.ClientFor($@"TEST\{ownerUser}");
        var supplier = Unique("Contacts Co ");
        Assert.True((await admin.PostAsJsonAsync("/api/suppliers", new { name = supplier }, Ct)).IsSuccessStatusCode);
        string[] headers = ["Supplier", "Name", "Role", "Email", "Phone"];
        var file = Sheet(headers,
            [supplier, "Alex Account", "Account manager", "alex@example.com", "07 3000 0001"],
            [supplier, "Bo Billing", "Accounts", "bo@example.com", null],
            [supplier, "Cy Nomail", "Driver", null, null],
            [supplier, "Alex Again", null, "ALEX@example.com", null],
            ["Nobody Pty Ltd", "Dee Nowhere", null, null, null],
            [supplier, "Ed Bad", null, "not an email", null]);

        // Procurement and admins only.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(owner, "contacts", "preview", file)).Status);

        var (_, first) = await PostAsync(admin, "contacts", "import", file);
        Assert.Equal(3, Count(first, "added"));
        Assert.Equal(3, Count(first, "refused"));
        var notes = first.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("row").GetInt32(), r => string.Join(" ", r.GetProperty("notes").EnumerateArray().Select(n => n.GetString())));
        Assert.Contains("more than once", notes[5]);
        Assert.Contains("there's no supplier called", notes[6]);
        Assert.Contains("doesn't look like an email", notes[7]);
        await using var db = await app.OpenAsync();
        var id = await db.ExecuteScalarAsync<int>("SELECT Id FROM contracts.Supplier WHERE Name = @supplier", new { supplier });
        Assert.Equal(3, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.SupplierContact WHERE SupplierId = @id AND IsRemoved = 0", new { id }));

        // A second run matches by email (or name): it changes, and blank cells leave what's there.
        var (_, second) = await PostAsync(admin, "contacts", "import", Sheet(headers,
            [supplier, "Alex Account", "Key account manager", "Alex@Example.com", null],
            [supplier, "Cy Nomail", "Driver", null, "0400 000 000"],
            [supplier, "Bo Billing", "Accounts", "bo@example.com", null]));
        Assert.Equal(2, Count(second, "updated"));
        Assert.Equal(1, Count(second, "unchanged"));
        Assert.Equal(0, Count(second, "added"));
        var alex = await db.QuerySingleAsync<(string Role, string Phone)>("SELECT Role, Phone FROM contracts.SupplierContact WHERE SupplierId = @id AND Email = N'alex@example.com'", new { id });
        Assert.Equal(("Key account manager", "07 3000 0001"), alex);
        Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent WHERE Action = N'supplier.contact-changed' AND SubjectId = @id", new { id }) >= 2);

        // The export lists them, one row each.
        var book = new XLWorkbook(await (await owner.GetAsync("/api/excel/contacts", Ct)).Content.ReadAsStreamAsync(Ct));
        Assert.Equal(3, book.Worksheet(1).RowsUsed().Count(r => r.Cell(1).GetString() == supplier));
    }
}
