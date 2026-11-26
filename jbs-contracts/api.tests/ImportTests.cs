using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using JbsContracts.Api.Features;

namespace JbsContracts.Api.Tests;

/// <summary>Slice 9 (D-91): importing old contracts from a fictional Medius-style export, then their files from a ZIP.</summary>
public sealed class ImportTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..12];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static readonly string[] Headers = ["Agreement No", "Agreement Name", "Vendor", "Category", "Contract Owner", "Start Date", "Expiry Date", "Notice Period", "Status", "Value", "Currency", "Approved Date"];

    private static byte[] Workbook(IEnumerable<object?[]> rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Export");
        for (var i = 0; i < Headers.Length; i++) sheet.Cell(1, i + 1).Value = Headers[i];
        var r = 2;
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
                switch (row[i])
                {
                    case null: break;
                    case DateOnly d: sheet.Cell(r, i + 1).Value = d.ToDateTime(TimeOnly.MinValue); break;
                    case int n: sheet.Cell(r, i + 1).Value = n; break;
                    default: sheet.Cell(r, i + 1).Value = row[i]!.ToString(); break;
                }
            r++;
        }
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Form(byte[] file, string name, Dictionary<string, string>? mapping = null, int? defaultOwnerId = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(file), "file", name } };
        if (mapping is not null) form.Add(new StringContent(JsonSerializer.Serialize(mapping)), "mapping");
        if (defaultOwnerId is { } o) form.Add(new StringContent(o.ToString()), "defaultOwnerId");
        return form;
    }

    [Fact]
    public async Task Old_contracts_import_with_column_matching_a_dry_run_and_their_files_from_a_zip()
    {
        var u = Unique("i");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var ownerUser = Unique("io");
        var owner = await app.AddPersonAsync(ownerUser, $"Imogen Owner {u}", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        var ownerId = owner.GetProperty("id").GetInt32();
        using var ownerClient = app.ClientFor($@"TEST\{ownerUser}");
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = $"Freight {u}" }, Ct));
        var supplier = await ReadAsync(await admin.PostAsJsonAsync("/api/suppliers", new { name = $"Known Supplier {u}" }, Ct));
        string Ref(int n) => $"MED-{u}-{n}";
        var rows = new List<object?[]>
        {
            new object?[] { Ref(1), "Linehaul freight", $"Known Supplier {u}", $"Freight {u}", $@"TEST\{ownerUser}", Today.AddDays(-300), Today.AddDays(400), 60, "Active", "1,250,000", "AUD", Today.AddDays(-310) },
            new object?[] { Ref(2), "Old cartons", $"Known Supplier {u}", $"Freight {u}", $"Imogen Owner {u}", Today.AddDays(-900), Today.AddDays(-30), 30, "Expired", "80000", null, null },
            new object?[] { Ref(3), "Pallet hire", $"New Vendor {u}", $"Freight {u}", null, Today.AddDays(-100), Today.AddDays(200), null, null, null, null, null }, // no owner: default
            new object?[] { Ref(4), "Unknown owner", $"Known Supplier {u}", $"Freight {u}", "Nobody Atall", Today.AddDays(-100), Today.AddDays(200), 30, "Active", null, null, null },
            new object?[] { Ref(5), "Unknown category", $"Known Supplier {u}", $"Nope {u}", $@"TEST\{ownerUser}", Today.AddDays(-100), Today.AddDays(200), 30, "Active", null, null, null },
            new object?[] { Ref(6), "Not signed yet", $"Known Supplier {u}", $"Freight {u}", $@"TEST\{ownerUser}", Today.AddDays(-10), Today.AddDays(500), 30, "Pending approval", null, null, null },
            new object?[] { Ref(1), "Duplicate row", $"Known Supplier {u}", $"Freight {u}", $@"TEST\{ownerUser}", Today.AddDays(-10), Today.AddDays(500), 30, "Active", null, null, null },
        };
        var file = Workbook(rows);

        // Admins only.
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PostAsync("/api/import/legacy/columns", Form(file, "medius.xlsx"), Ct)).StatusCode);

        // Columns are suggested from their names.
        var columns = await ReadAsync(await admin.PostAsync("/api/import/legacy/columns", Form(file, "medius.xlsx"), Ct));
        Assert.Equal(7, columns.GetProperty("rows").GetInt32());
        var suggested = columns.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("header").GetString()!, c => c.GetProperty("suggested").GetString());
        Assert.Equal("legacyReference", suggested["Agreement No"]);
        Assert.Equal("title", suggested["Agreement Name"]);
        Assert.Equal("supplier", suggested["Vendor"]);
        Assert.Equal("owner", suggested["Contract Owner"]);
        Assert.Equal("endDate", suggested["Expiry Date"]);
        Assert.Equal("noticePeriodDays", suggested["Notice Period"]);
        Assert.Equal("registeredDate", suggested["Approved Date"]);
        var mapping = suggested.Where(s => s.Value is not null).ToDictionary(s => s.Key, s => s.Value!);

        // A needed field that isn't matched is refused up front.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/import/legacy/preview", Form(file, "medius.xlsx", mapping.Where(m => m.Value != "supplier").ToDictionary()), Ct)).StatusCode);

        // The dry run: row by row, nothing changes.
        var plan = await ReadAsync(await admin.PostAsync("/api/import/legacy/preview", Form(file, "medius.xlsx", mapping, ownerId), Ct));
        var byRow = plan.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("row").GetInt32());
        Assert.Equal("Effective", byRow[2].GetProperty("outcome").GetString());
        Assert.Equal("Ended", byRow[3].GetProperty("outcome").GetString());
        Assert.Equal("Effective", byRow[4].GetProperty("outcome").GetString());
        Assert.Contains("No notice period", byRow[4].GetProperty("notes")[1].GetString());
        Assert.Contains("isn't an active contract owner", byRow[5].GetProperty("problems")[0].GetString());
        Assert.Contains("there's no category called", byRow[6].GetProperty("problems")[0].GetString());
        Assert.Contains("isn't clearly a current or ended contract", byRow[7].GetProperty("problems")[0].GetString());
        Assert.Contains("more than once", byRow[8].GetProperty("problems")[0].GetString());
        Assert.Equal([$"New Vendor {u}"], plan.GetProperty("newSuppliers").EnumerateArray().Select(s => s.GetString()));
        await using var db = await app.OpenAsync();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Contract WHERE LegacyReference LIKE @like", new { like = $"MED-{u}-%" }));

        // The import: ready rows go in as Effective (locked, original dates) or Ended.
        var result = await ReadAsync(await admin.PostAsync("/api/import/legacy/import", Form(file, "medius.xlsx", mapping, ownerId), Ct));
        Assert.Equal(3, result.GetProperty("imported").GetInt32());
        var imported = (await db.QueryAsync<(int Id, string Reference, string LegacyReference, string Stage, bool IsLocked, DateTime? RegisteredUtc, DateTime? EndedOn, string ImportedFrom, int OwnerId, decimal? Value)>(
            "SELECT Id, Reference, LegacyReference, Stage, IsLocked, RegisteredUtc, EndedOn, ImportedFrom, OwnerId, Value FROM contracts.Contract WHERE LegacyReference LIKE @like",
            new { like = $"MED-{u}-%" })).ToDictionary(x => x.LegacyReference);
        Assert.Equal("Effective", imported[Ref(1)].Stage);
        Assert.True(imported[Ref(1)].IsLocked);
        Assert.Equal(Today.AddDays(-310), DateOnly.FromDateTime(imported[Ref(1)].RegisteredUtc!.Value));
        Assert.Equal(1_250_000m, imported[Ref(1)].Value);
        Assert.Equal("Ended", imported[Ref(2)].Stage);
        Assert.Equal(Today.AddDays(-30), DateOnly.FromDateTime(imported[Ref(2)].EndedOn!.Value));
        Assert.Equal(ownerId, imported[Ref(3)].OwnerId); // the default owner
        Assert.Equal("medius.xlsx", imported[Ref(1)].ImportedFrom);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Supplier WHERE Name = @name", new { name = $"New Vendor {u}" }));
        // The contract shows its old reference and is found by it.
        var detail = await ReadAsync(await ownerClient.GetAsync($"/api/contracts/{imported[Ref(1)].Id}", Ct));
        Assert.Equal(Ref(1), detail.GetProperty("contract").GetProperty("legacyReference").GetString());
        Assert.Contains((await ReadAsync(await ownerClient.GetAsync($"/api/contracts?view=all&q={Ref(1)}", Ct))).EnumerateArray(), c => c.GetProperty("id").GetInt32() == imported[Ref(1)].Id);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent WHERE Action = N'contract.imported' AND ContractId = @id", new { id = imported[Ref(1)].Id }));

        // A second run skips what's already in, and the matching is remembered.
        var again = await ReadAsync(await admin.PostAsync("/api/import/legacy/preview", Form(file, "medius.xlsx", mapping, ownerId), Ct));
        Assert.Equal(3, again.GetProperty("already").GetInt32());
        Assert.Equal(0, again.GetProperty("effective").GetInt32() + again.GetProperty("ended").GetInt32());
        Assert.True((await ReadAsync(await admin.PostAsync("/api/import/legacy/columns", Form(file, "medius.xlsx"), Ct))).GetProperty("remembered").GetBoolean());

        // A CSV works the same way.
        var csv = new StringBuilder("Agreement No,Agreement Name,Vendor,Category,Contract Owner,Start Date,Expiry Date,Status\r\n")
            .Append($"{Ref(7)},\"Cleaning, south plant\",Known Supplier {u},Freight {u},TEST\\{ownerUser},1/07/2025,30/06/{Today.Year + 2},Current\r\n").ToString();
        var csvPlan = await ReadAsync(await admin.PostAsync("/api/import/legacy/preview", Form(Encoding.UTF8.GetBytes(csv), "export.csv", mapping), Ct));
        var csvRow = csvPlan.GetProperty("rows")[0];
        Assert.Equal("Effective", csvRow.GetProperty("outcome").GetString());
        Assert.Equal("Cleaning, south plant", csvRow.GetProperty("label").GetString());

        // Files from a ZIP, matched by the old reference in the name or folder.
        byte[] Zip()
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                void Add(string path, string content) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(content); }
                Add($"{Ref(1)} signed.pdf", "%PDF linehaul signed");
                Add($"{Ref(2)}/agreement.pdf", "%PDF cartons");
                Add($"{Ref(2)}/agreement copy.pdf", "%PDF cartons"); // the same file twice goes in once
                Add("misc/unrelated.pdf", "%PDF nothing");
                Add($"{Ref(1)} tool.exe", "MZ");
            }
            return stream.ToArray();
        }
        MultipartFormDataContent ZipForm() => new() { { new ByteArrayContent(Zip()), "file", "files.zip" }, { new StringContent("SignedCopy"), "kind" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PostAsync("/api/import/files/preview", ZipForm(), Ct)).StatusCode);
        var files = await ReadAsync(await admin.PostAsync("/api/import/files/preview", ZipForm(), Ct));
        Assert.Equal(2, files.GetProperty("matched").GetInt32());
        Assert.Equal(1, files.GetProperty("already").GetInt32());
        Assert.Equal(1, files.GetProperty("unmatched").GetInt32());
        Assert.Equal(1, files.GetProperty("refused").GetInt32());
        Assert.Equal(2, (await ReadAsync(await admin.PostAsync("/api/import/files/import", ZipForm(), Ct))).GetProperty("imported").GetInt32());
        var docs = await db.QueryAsync<(string Kind, string Title)>("SELECT Kind, Title FROM contracts.Document WHERE ContractId = @id", new { id = imported[Ref(1)].Id });
        Assert.Equal([("SignedCopy", $"{Ref(1)} signed")], docs);
        // Importing the same ZIP again adds nothing.
        Assert.Equal(0, (await ReadAsync(await admin.PostAsync("/api/import/files/preview", ZipForm(), Ct))).GetProperty("matched").GetInt32());

        // One ZIP with every kind mixed together (D-95): each file's kind from its folder or name, a default for the rest, and the admin's own choice.
        byte[] Mixed()
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                void Add(string path, string content) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(content); }
                Add($"{Ref(3)}/Signed/agreement.pdf", "%PDF pallet hire signed");
                Add($"{Ref(3)}/Pallet hire DRAFT v2.docx", "draft two");
                Add($"{Ref(3)} Deed of Variation (signed).pdf", "%PDF variation");
                Add($"{Ref(3)} Cover page - sign off.pdf", "%PDF cover page");
                Add($"{Ref(3)} pricing.xlsx", "pricing");
                Add($"{Ref(3)} notes.txt", "notes");
            }
            return stream.ToArray();
        }
        var choices = JsonSerializer.Serialize(new Dictionary<string, string> { [$"{Ref(3)} notes.txt"] = "Correspondence" });
        MultipartFormDataContent MixedForm() => new() { { new ByteArrayContent(Mixed()), "file", "mixed.zip" }, { new StringContent("Supporting"), "kind" }, { new StringContent(choices), "kinds" } };
        var mixed = await ReadAsync(await admin.PostAsync("/api/import/files/preview", MixedForm(), Ct));
        var kinds = mixed.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => (Kind: f.GetProperty("kind").GetString(), Why: f.GetProperty("kindWhy").GetString()));
        Assert.Equal(("SignedCopy", "folder \"Signed\""), kinds[$"{Ref(3)}/Signed/agreement.pdf"]);
        Assert.Equal("Draft", kinds[$"{Ref(3)}/Pallet hire DRAFT v2.docx"].Kind);
        Assert.Equal("Variation", kinds[$"{Ref(3)} Deed of Variation (signed).pdf"].Kind);
        Assert.Equal("InternalSignOff", kinds[$"{Ref(3)} Cover page - sign off.pdf"].Kind);
        Assert.Equal(("Supporting", "your default"), kinds[$"{Ref(3)} pricing.xlsx"]);
        Assert.Equal(("Correspondence", "you chose"), kinds[$"{Ref(3)} notes.txt"]);
        Assert.Equal(6, (await ReadAsync(await admin.PostAsync("/api/import/files/import", MixedForm(), Ct))).GetProperty("imported").GetInt32());
        var saved = (await db.QueryAsync<(string Kind, string Title)>("SELECT Kind, Title FROM contracts.Document WHERE ContractId = @id", new { id = imported[Ref(3)].Id })).ToList();
        Assert.Contains(("InternalSignOff", $"{Ref(3)} Cover page - sign off"), saved);
        Assert.Contains(("Correspondence", $"{Ref(3)} notes"), saved);
        Assert.Equal(6, saved.Count);
    }

    [Theory]
    [InlineData("MED-1/Signed/agreement.pdf", "SignedCopy")]
    [InlineData("MED-1/Drafts/agreement v3.docx", "Draft")]
    [InlineData("MED-1 executed agreement.pdf", "SignedCopy")]
    [InlineData("MED-1 unsigned agreement.pdf", "Supporting")]
    [InlineData("MED-1 Variation 2 signed.pdf", "Variation")]
    [InlineData("MED-1 Amendment.pdf", "Variation")]
    [InlineData("MED-1 Internal approval form.pdf", "InternalSignOff")]
    [InlineData("MED-1 CoverSheet.pdf", "InternalSignOff")]
    [InlineData("MED-1 signed off by GM.pdf", "InternalSignOff")]
    [InlineData("MED-1 Schedule 2 rates.pdf", "Schedule")]
    [InlineData("MED-1 redline.docx", "LegalMarkup")]
    [InlineData("MED-1 from supplier.msg", "Correspondence")]
    [InlineData("MED-1 SignedAgreement.pdf", "SignedCopy")]
    public void A_files_kind_comes_from_its_folders_then_its_name_then_the_default(string path, string kind) =>
        Assert.Equal(kind, LegacyImportEndpoints.KindFor(path, "Supporting", new Dictionary<string, string>()).Kind);

    [Fact]
    public async Task A_Medius_export_matches_its_columns_lists_what_to_set_up_first_and_leaves_out_archived()
    {
        var u = Unique("x");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var ownerUser = Unique("mo");
        var ownerName = $"Morgan Medius {u}";
        await app.AddPersonAsync(ownerUser, ownerName, new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        // The Export Search Results columns, in Medius's order (fictional values).
        string[] headers = ["Save To Folder", "Agreement Header", "Entity Name", "Agreement Reference", "Agreement Status", "Vendor Name", "Vendor User", "Vendor Number",
            "SAP Outline Agreement Number", "Agreement Information", "Title", "Description", "Business Unit", "Plant", "Agreement Type", "Function", "Agreement Owner",
            "Legal Agreement", "Agreement Currency", "Estimated Agreement Value", "Agreement Dates", "Start Date", "Effective Date", "End Date", "Review Date",
            "Extension Type", "Notice Period", "Approvals", "Approver", "Additional Information", "Team", "PORTFOLIO/CATEGORY", "SUB-CATEGORY"];
        string Ref(int n) => $"MX{u}{n}";
        object?[] Row(int n, string status, string category, string sub, string plant) =>
        [
            null, null, $"Fictional Entity {u}", Ref(n), status, $"Medius Vendor {u}", null, "1234567", n == 1 ? "4600001234" : null, null, $"Contract {n}", null, null, plant,
            null, null, ownerName, null, "AUD", "150000", null, Today.AddDays(-200), Today.AddDays(-200), Today.AddDays(500), Today.AddDays(400),
            "Manual", "3 Months", null, null, null, null, category, sub,
        ];
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Export");
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        object?[][] rows = [Row(1, "Effective", $"Logistics/Freight {u}", "Linehaul", $"Plant {u}"), Row(2, "Approved", $"Logistics/Freight {u}", "Cold chain", $"Plant {u}"),
            Row(3, "Archived", $"Old Category {u}", "Old sub", $"Old plant {u}")];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (rows[r][c] is DateOnly d) sheet.Cell(r + 2, c + 1).Value = d.ToDateTime(TimeOnly.MinValue);
                else if (rows[r][c] is { } v) sheet.Cell(r + 2, c + 1).Value = v.ToString();
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        var file = stream.ToArray();
        MultipartFormDataContent F(Dictionary<string, string>? mapping = null, bool leaveOut = true, string? download = null)
        {
            var form = Form(file, "ContractsExport_All.xlsx", mapping);
            if (leaveOut) form.Add(new StringContent("true"), "leaveOutArchived");
            if (download is not null) form.Add(new StringContent(download), "download");
            return form;
        }

        var columns = await ReadAsync(await admin.PostAsync("/api/import/legacy/columns", F(), Ct));
        var suggested = columns.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("header").GetString()!, c => c.GetProperty("suggested").GetString());
        Assert.Equal("legacyReference", suggested["Agreement Reference"]);
        Assert.Equal("status", suggested["Agreement Status"]);
        Assert.Equal("supplier", suggested["Vendor Name"]);
        Assert.Equal("supplierVendorNumber", suggested["Vendor Number"]);
        Assert.Equal("owner", suggested["Agreement Owner"]);
        Assert.Equal("value", suggested["Estimated Agreement Value"]);
        Assert.Equal("currency", suggested["Agreement Currency"]);
        Assert.Equal("site", suggested["Plant"]);
        Assert.Equal("category", suggested["PORTFOLIO/CATEGORY"]);
        Assert.Equal("subcategory", suggested["SUB-CATEGORY"]);
        Assert.Equal("autoRenews", suggested["Extension Type"]);
        Assert.Equal("entity", suggested["Entity Name"]);
        Assert.Null(suggested["Save To Folder"]);
        var mapping = suggested.Where(s => s.Value is not null).ToDictionary(s => s.Key, s => s.Value!);

        // The dry run lists what to add under Lists first (only from the rows that would come in) and leaves the archived one out.
        var plan = await ReadAsync(await admin.PostAsync("/api/import/legacy/preview", F(mapping), Ct));
        Assert.Equal(1, plan.GetProperty("leftOut").GetInt32());
        Assert.Equal(2, plan.GetProperty("refused").GetInt32());
        var missing = plan.GetProperty("missing").EnumerateArray().ToDictionary(m => m.GetProperty("list").GetString()!, m => m.GetProperty("names").EnumerateArray().Select(x => x.GetString()).ToList());
        Assert.Equal([$"Logistics/Freight {u}", $"Logistics/Freight {u} > Cold chain", $"Logistics/Freight {u} > Linehaul"], missing["Category"].Order());
        Assert.Equal([$"Plant {u}"], missing["Site"]);
        Assert.Equal([$"Fictional Entity {u}"], missing["Entity"]);
        Assert.DoesNotContain(missing["Category"], n => n!.StartsWith("Old Category"));

        // The download is a Lists sheet that imports as it is.
        var sheetBytes = await (await admin.PostAsync("/api/import/legacy/preview", F(mapping, download: "missing"), Ct)).Content.ReadAsByteArrayAsync(Ct);
        var (_, listsResult) = await ExcelTests.PostAsync(admin, "lists", "import", sheetBytes);
        Assert.Equal(5, ExcelTests.Count(listsResult, "added"));

        // Now both rows are ready: Approved (signed, starting later) comes in as Effective; the vendor number is saved on the new supplier.
        plan = await ReadAsync(await admin.PostAsync("/api/import/legacy/preview", F(mapping), Ct));
        Assert.Equal(2, plan.GetProperty("effective").GetInt32());
        Assert.Equal(0, plan.GetProperty("refused").GetInt32());
        Assert.Empty(plan.GetProperty("missing").EnumerateArray());
        await ReadAsync(await admin.PostAsync("/api/import/legacy/import", F(mapping), Ct));
        await using var db = await app.OpenAsync();
        Assert.Equal("1234567", await db.ExecuteScalarAsync<string>("SELECT VendorNumber FROM contracts.Supplier WHERE Name = @name", new { name = $"Medius Vendor {u}" }));
        var imported = (await db.QueryAsync<(string Reference, string Category, string Parent, string Sap, int? Notice)>("""
            SELECT k.Reference, c.Name, p.Name, k.SapOutlineAgreement, k.NoticePeriodDays FROM contracts.Contract k
            JOIN contracts.Category c ON c.Id = k.CategoryId JOIN contracts.Category p ON p.Id = c.ParentId WHERE k.LegacyReference LIKE @like
            """, new { like = $"MX{u}%" })).ToDictionary(x => x.Reference);
        Assert.Equal(2, imported.Count);
        Assert.Equal(("Linehaul", $"Logistics/Freight {u}"), (imported[Ref(1)].Category, imported[Ref(1)].Parent));
        Assert.Equal("4600001234", imported[Ref(1)].Sap);
        Assert.NotNull(imported[Ref(1)].Notice);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Contract WHERE LegacyReference = @r", new { r = Ref(3) }));
    }

    [Fact]
    public async Task Old_drop_down_values_translate_to_your_own_list_values_and_the_translations_are_remembered_and_downloadable()
    {
        var u = Unique("tr");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var ownerUser = Unique("to");
        var ownerName = $"Tess Translate {u}";
        await app.AddPersonAsync(ownerUser, ownerName, new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        // Your own clean lists: Freight > Road, and the agreement type "Services".
        var freight = (await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = $"Freight {u}" }, Ct))).GetProperty("id").GetInt32();
        var road = (await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = "Road", parentId = freight }, Ct))).GetProperty("id").GetInt32();
        await ReadAsync(await admin.PostAsJsonAsync("/api/lists/items", new { listName = "AgreementType", value = $"Services {u}" }, Ct));

        string[] headers = ["Agreement Reference", "Agreement Status", "Vendor Name", "Title", "Agreement Owner", "Start Date", "End Date", "Agreement Type", "Plant", "PORTFOLIO/CATEGORY", "SUB-CATEGORY"];
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Export");
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        object?[][] rows =
        [
            [$"T{u}1", "Effective", $"Vendor {u}", "One", ownerName, "2025-01-01", "2027-12-31", "SVC Agreement (old)", "Old Plant A", "LOGISTICS (old)", "Road freight"],
            [$"T{u}2", "Effective", $"Vendor {u}", "Two", $"Former Employee {u}", "2025-01-01", "2027-12-31", "svc agreement (OLD)", "Old Plant A", "LOGISTICS (old)", "Road freight"],
        ];
        for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) sheet.Cell(r + 2, c + 1).Value = rows[r][c]!.ToString();
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        var file = stream.ToArray();
        var columns = await ReadAsync(await admin.PostAsync("/api/import/legacy/columns", Form(file, "export.xlsx"), Ct));
        var mapping = columns.GetProperty("columns").EnumerateArray().Where(c => c.GetProperty("suggested").GetString() is not null)
            .ToDictionary(c => c.GetProperty("header").GetString()!, c => c.GetProperty("suggested").GetString()!);
        MultipartFormDataContent F(object? translations = null, string? download = null, byte[]? translationsFile = null)
        {
            var form = Form(file, "export.xlsx", mapping);
            if (translations is not null) form.Add(new StringContent(JsonSerializer.Serialize(translations)), "translations");
            if (download is not null) form.Add(new StringContent(download), "download");
            if (translationsFile is not null) form.Add(new ByteArrayContent(translationsFile), "translationsFile", "translations.xlsx");
            return form;
        }

        // The old values, with how many contracts use each (values that differ only in case count as one).
        var values = await ReadAsync(await admin.PostAsync("/api/import/legacy/values", F(), Ct));
        var fields = values.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        var cat = fields["category"].GetProperty("values").EnumerateArray().Single();
        Assert.Equal(("LOGISTICS (old) > Road freight", 2), (cat.GetProperty("value").GetString(), cat.GetProperty("count").GetInt32()));
        Assert.Equal(JsonValueKind.Null, cat.GetProperty("to").ValueKind);
        Assert.Equal(2, fields["agreementType"].GetProperty("values").EnumerateArray().Single().GetProperty("count").GetInt32());
        Assert.Contains($"Freight {u} > Road", values.GetProperty("choices").GetProperty("Category").EnumerateArray().Select(x => x.GetString()));
        // Owners: the current one is chosen by name; the past employee isn't, and is offered the current contract owners.
        var owners = fields["owner"].GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("value").GetString()!, v => v.GetProperty("to"));
        Assert.Equal(ownerName, owners[ownerName].GetString());
        Assert.Equal(JsonValueKind.Null, owners[$"Former Employee {u}"].ValueKind);
        Assert.Contains(ownerName, values.GetProperty("choices").GetProperty("Owner").EnumerateArray().Select(x => x.GetString()));

        // Without translations they're flagged; translated (and the plant left blank), both rows are ready.
        Assert.Equal(2, (await ReadAsync(await admin.PostAsync("/api/import/legacy/preview", F(), Ct))).GetProperty("refused").GetInt32());
        var translations = new Dictionary<string, Dictionary<string, string>>
        {
            ["category"] = new() { ["LOGISTICS (old) > Road freight"] = $"Freight {u} > Road" },
            ["agreementType"] = new() { ["SVC Agreement (old)"] = $"Services {u}" },
            ["site"] = new() { ["Old Plant A"] = "" },
            ["owner"] = new() { [$"Former Employee {u}"] = ownerName },
        };
        var plan = await ReadAsync(await admin.PostAsync("/api/import/legacy/preview", F(translations), Ct));
        Assert.Equal(2, plan.GetProperty("effective").GetInt32());
        Assert.Empty(plan.GetProperty("missing").EnumerateArray());

        // Download the translations; import (which remembers them); they come back next time, and from the file.
        var sheetBytes = await (await admin.PostAsync("/api/import/legacy/values", F(translations, download: "translations"), Ct)).Content.ReadAsByteArrayAsync(Ct);
        using (var tbook = new XLWorkbook(new MemoryStream(sheetBytes)))
            Assert.Contains(tbook.Worksheet(1).RowsUsed(), r => r.Cell(1).GetString() == "Site" && r.Cell(2).GetString() == "Old Plant A" && r.Cell(3).GetString() == "(leave blank)");
        await ReadAsync(await admin.PostAsync("/api/import/legacy/import", F(translations), Ct));
        await using var db = await app.OpenAsync();
        var imported = (await db.QueryAsync<(int CategoryId, int? SiteId, string Type, string Owner)>("""
            SELECT k.CategoryId, k.SiteId, t.Value, o.Name FROM contracts.Contract k JOIN contracts.ListItem t ON t.Id = k.AgreementTypeId
            JOIN contracts.Person o ON o.Id = k.OwnerId WHERE k.LegacyReference LIKE @like
            """, new { like = $"T{u}%" })).ToList();
        Assert.Equal(2, imported.Count);
        Assert.All(imported, x => Assert.Equal((road, (int?)null, $"Services {u}", ownerName), (x.CategoryId, x.SiteId, x.Type, x.Owner)));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person WHERE Name = @n", new { n = $"Former Employee {u}" }));
        var again = await ReadAsync(await admin.PostAsync("/api/import/legacy/values", F(), Ct));
        var site = again.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "site").GetProperty("values")[0];
        Assert.True(site.GetProperty("remembered").GetBoolean());
        Assert.Equal("", site.GetProperty("to").GetString());
        var fromFile = await ReadAsync(await admin.PostAsync("/api/import/legacy/values", F(translationsFile: sheetBytes), Ct));
        Assert.Equal($"Services {u}", fromFile.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "agreementType").GetProperty("values")[0].GetProperty("to").GetString());
    }
}
