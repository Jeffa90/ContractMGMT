using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Features;
using Microsoft.Extensions.DependencyInjection;

namespace JbsContracts.Api.Tests;

/// <summary>
/// Supplier documents and relationships (D-88, D-90): the Procurement tick, supplier documents with versions, expiry and opt-in reminders,
/// documents from contracts, supplier details, contacts, business reviews with actions, Home and Team. Assertions only look at this test's rows.
/// </summary>
public sealed class SupplierTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, int Id, string Name)> PersonAsync(string name, object roles)
    {
        var user = Unique("su");
        var full = $"{name} {user[^4..]}";
        var person = await app.AddPersonAsync(user, full, roles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32(), full);
    }

    private static object Roles(bool owner = false, bool procurement = false, bool admin = false) =>
        new { contractOwner = owner, legal = false, approver = false, groupHead = false, admin, auditor = false, procurement };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url) => await ReadAsync(await client.GetAsync(url, Ct));

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, int supplierId, string fileName, object? typeId = null, int? documentId = null, int? reviewId = null, string? expiresOn = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF supplier document"u8.ToArray()), "file", fileName);
        if (typeId is not null) form.Add(new StringContent(typeId.ToString()!), "typeId");
        if (documentId is { } d) form.Add(new StringContent(d.ToString()), "documentId");
        if (reviewId is { } r) form.Add(new StringContent(r.ToString()), "reviewId");
        if (expiresOn is not null) form.Add(new StringContent(expiresOn), "expiresOn");
        return await client.PostAsync($"/api/suppliers/{supplierId}/documents", form, Ct);
    }

    private async Task<int> ListItemAsync(HttpClient client, string list, string value) =>
        (await GetAsync(client, "/api/lists")).GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("listName").GetString() == list && i.GetProperty("value").GetString() == value).GetProperty("id").GetInt32();

    [Fact]
    public async Task Supplier_documents_versions_expiry_reminders_and_documents_from_contracts()
    {
        var (procurement, procurementId, _) = await PersonAsync("Pia Procure", Roles(procurement: true));
        var (owner, ownerId, ownerName) = await PersonAsync("Oli Owner", Roles(owner: true));
        var (viewer, viewerId, _) = await PersonAsync("Vic Viewer", Roles());

        // The Procurement tick comes back on the person.
        Assert.True((await GetAsync(procurement, "/api/me")).GetProperty("person").GetProperty("roles").GetProperty("procurement").GetBoolean());

        var supplier = await ReadAsync(await owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct));
        var sid = supplier.GetProperty("id").GetInt32();
        var insurance = await ListItemAsync(owner, "SupplierDocumentType", "Insurance certificate");

        // Only Procurement (and admins) add supplier documents; the kind must come from the list.
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(owner, sid, "cert.pdf", insurance)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(procurement, sid, "cert.pdf", typeId: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(procurement, sid, "cert.pdf", typeId: 999999)).StatusCode);
        var added = await ReadAsync(await UploadAsync(procurement, sid, "Public liability 2026.pdf", insurance, expiresOn: D(60)));
        var docId = added.GetProperty("id").GetInt32();
        // A renewed certificate: a new version with a new expiry date.
        await ReadAsync(await UploadAsync(procurement, sid, "Public liability 2027.pdf", documentId: docId, expiresOn: D(400)));
        await ReadAsync(await UploadAsync(procurement, sid, "Public liability 2026 again.pdf", documentId: docId)); // no date: keeps the 2027 one

        var page = await GetAsync(viewer, $"/api/suppliers/{sid}");
        Assert.False(page.GetProperty("can").GetProperty("manage").GetBoolean());
        var doc = page.GetProperty("documents").EnumerateArray().Single();
        Assert.Equal("Public liability 2026", doc.GetProperty("title").GetString());
        Assert.Equal("Insurance certificate", doc.GetProperty("typeName").GetString());
        Assert.Equal(D(400), doc.GetProperty("expiresOn").GetString());
        Assert.Equal([3, 2, 1], doc.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("number").GetInt32()));
        // Everyone can download it.
        var versionId = doc.GetProperty("versions")[0].GetProperty("id").GetInt32();
        Assert.Equal("%PDF supplier document", await (await viewer.GetAsync($"/api/supplier-documents/{versionId}/file", Ct)).Content.ReadAsStringAsync(Ct));

        // Remind me: off unless ticked, per person; the job emails 60 then 30 days before, each once.
        Assert.Equal(HttpStatusCode.OK, (await procurement.PutAsJsonAsync($"/api/suppliers/{sid}/documents/{docId}", new { typeId = insurance, title = "Public liability", expiresOn = D(30) }, Ct)).StatusCode);
        await ReadAsync(await viewer.PostAsJsonAsync($"/api/suppliers/{sid}/documents/{docId}/remind", new { on = true }, Ct));
        Assert.True((await GetAsync(viewer, $"/api/suppliers/{sid}")).GetProperty("documents")[0].GetProperty("watching").GetBoolean());
        Assert.False((await GetAsync(owner, $"/api/suppliers/{sid}")).GetProperty("documents")[0].GetProperty("watching").GetBoolean());
        var job = app.Factory.Services.GetRequiredService<ReminderJob>();
        await job.RunAsync(Today, Ct);
        await job.RunAsync(Today, Ct);
        await using var db = await app.OpenAsync();
        async Task<int> Emails(int personId, string kind) => await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE PersonId = @personId AND Kind = @kind", new { personId, kind });
        Assert.Equal(1, await Emails(viewerId, "supplier-expiry"));
        Assert.Equal(0, await Emails(ownerId, "supplier-expiry"));
        var subject = await db.ExecuteScalarAsync<string>("SELECT Subject FROM contracts.EmailOutbox WHERE PersonId = @viewerId AND Kind = 'supplier-expiry'", new { viewerId });
        Assert.Contains("Public liability expires in 30 days", subject);
        // Home shows it to the person reminded.
        var home = await GetAsync(viewer, "/api/home");
        Assert.Contains(home.GetProperty("suppliers").EnumerateArray(), x => x.GetProperty("kind").GetString() == "Expiring" && x.GetProperty("supplierId").GetInt32() == sid);
        await ReadAsync(await viewer.PostAsJsonAsync($"/api/suppliers/{sid}/documents/{docId}/remind", new { on = false }, Ct));
        Assert.DoesNotContain((await GetAsync(viewer, "/api/home")).GetProperty("suppliers").EnumerateArray(), x => x.GetProperty("supplierId").GetInt32() == sid);

        // Removed documents are hidden, not deleted.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/suppliers/{sid}/documents/{docId}/remove", new { }, Ct)).StatusCode);
        await ReadAsync(await procurement.PostAsJsonAsync($"/api/suppliers/{sid}/documents/{docId}/remove", new { }, Ct));
        Assert.Empty((await GetAsync(viewer, $"/api/suppliers/{sid}")).GetProperty("documents").EnumerateArray());
        Assert.Equal(3, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.SupplierDocumentVersion WHERE DocumentId = @docId", new { docId }));

        // Documents from its contracts, respecting Confidential.
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat") }, Ct));
        async Task<int> ContractWithDocAsync(bool confidential, string file)
        {
            var created = await ReadAsync(await owner.PostAsJsonAsync("/api/contracts", new { title = Unique("Contract "), supplierId = sid, categoryId = category.GetProperty("id").GetInt32(), isConfidential = confidential }, Ct));
            var id = created.GetProperty("id").GetInt32();
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent("%PDF draft"u8.ToArray()), "file", file);
            form.Add(new StringContent("Draft"), "kind");
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
            return id;
        }
        await ContractWithDocAsync(false, "open draft.pdf");
        await ContractWithDocAsync(true, "secret draft.pdf");
        Assert.Equal(["open draft"], (await GetAsync(viewer, $"/api/suppliers/{sid}")).GetProperty("contractDocuments").EnumerateArray().Select(d => d.GetProperty("title").GetString()));
        Assert.Equal(2, (await GetAsync(owner, $"/api/suppliers/{sid}")).GetProperty("contractDocuments").GetArrayLength());
        _ = (procurementId, ownerName);
    }

    [Fact]
    public async Task Supplier_details_contacts_and_business_reviews_with_actions()
    {
        var (procurement, _, _) = await PersonAsync("Pat Procure", Roles(procurement: true));
        var (relationship, relationshipId, relationshipName) = await PersonAsync("Rae Relate", Roles(owner: true));
        var (doer, doerId, doerName) = await PersonAsync("Dom Doer", Roles(owner: true));
        var (other, _, _) = await PersonAsync("Ola Other", Roles(owner: true));
        var supplier = await ReadAsync(await other.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct));
        var sid = supplier.GetProperty("id").GetInt32();
        var strategic = await ListItemAsync(other, "SupplierTier", "Strategic");

        // Details: Procurement and admins only; Medium or High risk needs a reason.
        Assert.Equal(HttpStatusCode.Forbidden, (await relationship.PutAsJsonAsync($"/api/suppliers/{sid}/relationship", new { relationshipOwnerId = relationshipId }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await procurement.PutAsJsonAsync($"/api/suppliers/{sid}/relationship", new { riskRating = "High" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await procurement.PutAsJsonAsync($"/api/suppliers/{sid}/relationship", new { tierId = 999999 }, Ct)).StatusCode);
        await ReadAsync(await procurement.PutAsJsonAsync($"/api/suppliers/{sid}/relationship", new
        {
            relationshipOwnerId = relationshipId, tierId = strategic, riskRating = "High", riskReason = "Single source for cartons.", nextReviewDue = D(20),
        }, Ct));
        var page = await GetAsync(relationship, $"/api/suppliers/{sid}");
        var s = page.GetProperty("supplier");
        Assert.Equal(relationshipName, s.GetProperty("relationshipOwnerName").GetString());
        Assert.Equal("Strategic", s.GetProperty("tierName").GetString());
        Assert.Equal("High", s.GetProperty("riskRating").GetString());
        Assert.True(page.GetProperty("can").GetProperty("review").GetBoolean()); // the relationship owner can add reviews
        Assert.False(page.GetProperty("can").GetProperty("manage").GetBoolean());

        // Contacts.
        Assert.Equal(HttpStatusCode.BadRequest, (await procurement.PostAsJsonAsync($"/api/suppliers/{sid}/contacts", new { name = "Dana", email = "not an email" }, Ct)).StatusCode);
        var contact = await ReadAsync(await procurement.PostAsJsonAsync($"/api/suppliers/{sid}/contacts", new { name = "Dana Brooks", role = "Account manager", email = "dana@example.com", phone = "07 3000 0000" }, Ct));
        Assert.Equal("Dana Brooks", (await GetAsync(other, $"/api/suppliers/{sid}")).GetProperty("contacts")[0].GetProperty("name").GetString());
        await ReadAsync(await procurement.PostAsJsonAsync($"/api/suppliers/{sid}/contacts/{contact.GetProperty("id").GetInt32()}/remove", new { }, Ct));
        Assert.Empty((await GetAsync(other, $"/api/suppliers/{sid}")).GetProperty("contacts").EnumerateArray());

        // Business reviews: not in the future, scores 1 to 5, and someone outside Procurement who isn't the relationship owner can't add one.
        object Review(string heldOn, int quality = 4, object? actions = null) => new
        {
            heldOn, summary = "Delivery is back on track; prices hold until June.", supplierAttendees = "Dana Brooks (Account manager)", attendeeIds = new[] { relationshipId, doerId },
            rating = "NeedsImprovement", qualityScore = quality, deliveryScore = 3, serviceScore = 4, costScore = 5,
            actions = actions ?? new[] { new { text = "Send the revised delivery schedule", ownerId = doerId, dueOn = D(-2) } }, nextReviewDue = D(90),
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"/api/suppliers/{sid}/reviews", Review(D(-1)), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await relationship.PostAsJsonAsync($"/api/suppliers/{sid}/reviews", Review(D(1)), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await relationship.PostAsJsonAsync($"/api/suppliers/{sid}/reviews", Review(D(-1), quality: 6), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await relationship.PostAsJsonAsync($"/api/suppliers/{sid}/reviews", Review(D(-1), actions: new[] { new { text = "", ownerId = doerId, dueOn = D(5) } }), Ct)).StatusCode);
        var review = await ReadAsync(await relationship.PostAsJsonAsync($"/api/suppliers/{sid}/reviews", Review(D(-1)), Ct));
        var reviewId = review.GetProperty("id").GetInt32();
        // The deck is attached to the review by the relationship owner.
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(relationship, sid, "Review deck.pdf", reviewId: reviewId)).StatusCode);

        page = await GetAsync(other, $"/api/suppliers/{sid}");
        Assert.Equal(D(90), page.GetProperty("supplier").GetProperty("nextReviewDue").GetString());
        var r = page.GetProperty("reviews").EnumerateArray().Single();
        Assert.Equal("Needs improvement", r.GetProperty("ratingName").GetString());
        Assert.Equal(5, r.GetProperty("costScore").GetInt32());
        Assert.Equal(2, r.GetProperty("attendees").GetArrayLength());
        Assert.Equal("Review deck", r.GetProperty("documents")[0].GetProperty("title").GetString());
        Assert.Empty(page.GetProperty("documents").EnumerateArray()); // the deck sits with the review, not the supplier's documents
        var action = r.GetProperty("actions")[0];
        Assert.False(action.GetProperty("canTick").GetBoolean()); // "other" can't tick it

        // The action's owner is emailed, sees it on Home and the Team page, and ticks it done.
        await using var db = await app.OpenAsync();
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE PersonId = @doerId AND Kind = 'review-action'", new { doerId }));
        var home = await GetAsync(doer, "/api/home");
        var item = home.GetProperty("suppliers").EnumerateArray().Single(x => x.GetProperty("supplierId").GetInt32() == sid);
        Assert.Equal("Action", item.GetProperty("kind").GetString());
        Assert.Equal(-2, item.GetProperty("days").GetInt32());
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var team = await GetAsync(admin, "/api/team?days=30");
        Assert.Equal(1, team.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == doerId).GetProperty("supplierWork").GetInt32());
        var actionId = action.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"/api/review-actions/{actionId}/done", new { done = true }, Ct)).StatusCode);
        await ReadAsync(await doer.PostAsJsonAsync($"/api/review-actions/{actionId}/done", new { done = true }, Ct));
        Assert.DoesNotContain((await GetAsync(doer, "/api/home")).GetProperty("suppliers").EnumerateArray(), x => x.GetProperty("supplierId").GetInt32() == sid);
        var done = (await GetAsync(other, $"/api/suppliers/{sid}")).GetProperty("reviews")[0].GetProperty("actions")[0];
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.Equal(doerName, done.GetProperty("doneByName").GetString());

        // Review due: the relationship owner is reminded (30 days before by default) and sees it on Home.
        await ReadAsync(await procurement.PutAsJsonAsync($"/api/suppliers/{sid}/relationship", new { relationshipOwnerId = relationshipId, tierId = strategic, riskRating = "Low", nextReviewDue = D(30) }, Ct));
        await app.Factory.Services.GetRequiredService<ReminderJob>().RunAsync(Today, Ct);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE PersonId = @relationshipId AND Kind = 'supplier-review'", new { relationshipId }));
        Assert.Contains((await GetAsync(relationship, "/api/home")).GetProperty("suppliers").EnumerateArray(), x => x.GetProperty("kind").GetString() == "ReviewDue" && x.GetProperty("supplierId").GetInt32() == sid);

        // Everything was recorded.
        var audit = await db.QueryAsync<string>("SELECT Action FROM contracts.AuditEvent WHERE SubjectType = N'Supplier' AND SubjectId = @sid", new { sid });
        Assert.Contains("supplier.relationship-changed", audit);
        Assert.Contains("supplier.contact-added", audit);
        Assert.Contains("supplier.review-added", audit);
        Assert.Contains("supplier.action-added", audit);
        Assert.Contains("supplier.action-done", audit);
    }
}
