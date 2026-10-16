using System.Net;
using System.Text.RegularExpressions;
using Ecp.Contracts;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>The lifecycle through the real pages: Legal review, execution, register authorisation, locking and the access rules a crafted post meets.</summary>
[Collection("Api")]
[Trait("Category", "Lifecycle")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class LifecycleUiTests
{
    private const string Owner = "catowner.packaging@a.example.test";
    private static int _n;

    private static async Task<Guid> NewDraftAsync(DemoApp app, bool legal = true, string classification = "Internal")
    {
        var (tenant, _) = await app.FindUserAsync(Owner);
        return await app.WithTenantAsync(tenant, async sp =>
        {
            Ecp.Tenancy.OrganisationSummary? org = null; Guid? supplierId = null;
            foreach (var candidate in await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync())
                if (await sp.GetRequiredService<ISupplierDirectory>().FindByVendorNumberAsync(candidate.Id, "900001") is { } found) { org = candidate; supplierId = found; break; }
            if (org is null || supplierId is null) throw new InvalidOperationException("demo supplier missing");
            var supplier = supplierId.Value;
            var category = await sp.GetRequiredService<ICategoryAdministration>().UpsertCategoryAsync(org.Id, "PACKAGING", "Packaging", null);
            var n = Interlocked.Increment(ref _n);
            return await sp.GetRequiredService<IContractService>().CreateDraftAsync(new CreateContractRequest(org.Id, $"UI-LC-{n:000}", $"UI lifecycle {n}", supplier, category, Classification: classification, LegalReviewRequired: legal, CurrencyCode: "AUD", EstimatedValue: 5000m));
        });
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string page, string handler, params (string, string)[] fields)
        => DemoApp.PostFormAsync(c, page, fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)), $"{page}?handler={handler}");

    private static async Task<HttpResponseMessage> Upload(HttpClient c, string page, string handler, string field, string name, string text, params (string, string)[] fields)
    {
        var token = await DemoApp.TokenAsync(c, page);
        using var form = new MultipartFormDataContent();
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text)) { Headers = { ContentType = new("application/pdf") } }, field, name);
        return await c.PostAsync(handler.Length == 0 ? page : $"{page}?handler={handler}", form);
    }

    private static Guid Match(string html, string pattern) => Guid.Parse(Regex.Match(html, pattern).Groups[1].Value);

    private static async Task<string> Text(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    [Fact]
    public async Task A_contract_travels_through_Legal_review_signature_and_register_authorisation_using_only_the_pages()
    {
        using var app = DemoApp.Start();
        var id = await NewDraftAsync(app);
        using var owner = await app.SignInAsync(Owner);
        using var legal = await app.SignInAsync("legal@a.example.test");
        using var head = await app.SignInAsync("grouphead@a.example.test");
        var life = $"/Contracts/Lifecycle/{id}";

        // The generic step call refuses a step that carries its own data, however it is posted.
        var crafted = await Post(owner, life, "Step", ("step", "SubmitForLegal"));
        Assert.Contains("taken from its own page", await Text(crafted), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Draft", await owner.GetStringAsync(life));

        Assert.Equal(HttpStatusCode.Redirect, (await Post(owner, life, "LegalSubmit", ("Comment", "Please review"))).StatusCode);
        var queue = await legal.GetStringAsync("/Legal");
        var reviewId = Match(queue, "href=\"/Legal/Review/([0-9a-f-]{36})\"");
        Assert.Contains("Waiting on you", queue);

        // Legal begins, comments, and cannot return it without a reason.
        var review = $"/Legal/Review/{reviewId}";
        Assert.Equal(HttpStatusCode.Redirect, (await Post(legal, review, "Begin")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(legal, review, "Comment", ("Text", "Please cap the liability in clause 4."))).StatusCode);
        var noReason = await Post(legal, review, "Return", ("Text", ""));
        Assert.Equal(HttpStatusCode.OK, noReason.StatusCode);
        Assert.Contains("comment is required", await Text(noReason), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(legal, review, "Return", ("Text", "Liability cap missing"))).StatusCode);

        // The owner sees the return, answers and resubmits; round 2 opens and round 1 is kept.
        Assert.Contains("Liability cap missing", await owner.GetStringAsync(review));
        Assert.Equal(HttpStatusCode.Redirect, (await Post(owner, review, "Resubmit", ("Text", "Clause 4 now has a cap."))).StatusCode);
        var round2 = await legal.GetStringAsync(review);
        Assert.Contains("Round 2", round2);
        Assert.Contains("Round 1", round2);
        Assert.Contains("Liability cap missing", round2);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(legal, review, "Begin")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(legal, review, "Clear", ("Text", "Acceptable"))).StatusCode);
        Assert.Contains("Legal Cleared", await owner.GetStringAsync(life));
        Assert.Contains("does not register the agreement", await owner.GetStringAsync(review), StringComparison.OrdinalIgnoreCase);

        // Initial Creation, the final document, signature (manual), the executed copy, then the submission for register authorisation.
        Assert.Equal(HttpStatusCode.Redirect, (await Post(owner, life, "Step", ("step", "RegisterInitial"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Upload(owner, $"/Contracts/Documents/{id}", "", "file", "final.pdf", "final agreement text", ("DocumentType", "FinalAgreement"), ("Title", "Final agreement"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(owner, life, "SendSignature", ("provider", "manual"))).StatusCode);
        Assert.Contains("Out for Signature", await owner.GetStringAsync(life));
        var early = await Post(owner, life, "SubmitApproval", ("Comment", "go"));
        Assert.Contains("executed copy", await Text(early), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Redirect, (await Upload(owner, life, "ExecutedCopy", "file", "executed.pdf", "executed text")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(owner, life, "SubmitApproval", ("Comment", "Executed copy attached"))).StatusCode);
        Assert.Contains("Awaiting Approval", await owner.GetStringAsync(life));

        // The submitter cannot decide; the assigned approver can; the record is then registered, Effective and locked.
        var pending = await head.GetStringAsync("/Approvals");
        var requestId = Match(pending, "href=\"/Approvals/Details/([0-9a-f-]{36})\"");
        var approval = $"/Approvals/Details/{requestId}";
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(owner, approval, "Approve", ("Comment", "me"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(head, approval, "Approve", ("Comment", "Approved"))).StatusCode);
        var details = await owner.GetStringAsync($"/Contracts/Details/{id}");
        Assert.Contains("Effective", details);
        Assert.Contains("Locked", details);
        Assert.DoesNotContain("Not yet registered", details);
        // A second decision (from the same approver, or a backup) is refused with who decided first.
        var again = await Post(head, approval, "Approve", ("Comment", "again"));
        Assert.Contains("already", await Text(again), StringComparison.OrdinalIgnoreCase);
        // The locked record rejects a crafted step and a crafted edit.
        var edit = await owner.GetAsync($"/Contracts/Edit/{id}");
        Assert.NotEqual(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(owner, life, "Step", ("step", "WithdrawInitial"))).StatusCode);       // the lock refuses an owner's step
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(owner, life, "Step", ("step", "Approve"))).StatusCode);             // and a crafted Approve is not the owner's to take
        var unknown = await Post(owner, life, "Step", ("step", "SetStatusToEffective"));
        Assert.Contains("not part of this workflow", await Text(unknown), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_authorisation_is_refused_to_everyone_but_the_assigned_approvers_and_to_the_second_responder()
    {
        using var app = DemoApp.Start();
        var id = await NewDraftAsync(app, legal: false);
        using var owner = await app.SignInAsync(Owner);
        using var head = await app.SignInAsync("grouphead@a.example.test");
        using var backup = await app.SignInAsync("grouphead.backup@a.example.test");
        using var legal = await app.SignInAsync("legal@a.example.test");
        using var viewer = await app.SignInAsync("viewer.all@a.example.test");
        var life = $"/Contracts/Lifecycle/{id}";
        Assert.Equal(HttpStatusCode.Redirect, (await Post(owner, life, "Step", ("step", "ProceedWithoutLegal"))).StatusCode);
        await Upload(owner, $"/Contracts/Documents/{id}", "", "file", "final.pdf", "final", ("DocumentType", "FinalAgreement"));
        await Post(owner, life, "SendSignature", ("provider", "manual"));
        await Upload(owner, life, "ExecutedCopy", "file", "executed.pdf", "executed");
        await Post(owner, life, "SubmitApproval", ("Comment", "go"));

        // Approvers (primary and backup) see it waiting; others do not see an approval to act on.
        var requestId = Match(await head.GetStringAsync("/Approvals"), "href=\"/Approvals/Details/([0-9a-f-]{36})\"");
        Assert.Contains($"/Approvals/Details/{requestId}", await backup.GetStringAsync("/Approvals"));
        var approval = $"/Approvals/Details/{requestId}";
        Assert.Contains("Backup", await head.GetStringAsync(approval));                       // who can act, backups included
        var legalView = await legal.GetStringAsync(approval);                                   // anyone who can read the record may see the request, but only approvers can act on it
        Assert.Contains("Only the assigned approver or a backup can decide", legalView);
        Assert.DoesNotContain("asp-page-handler=\"Approve\"", legalView);
        Assert.DoesNotContain("formaction=\"/Approvals/Details/" + requestId + "?handler=Approve\"", legalView);
        Assert.Contains("Only the assigned approver or a backup can decide", await viewer.GetStringAsync(approval));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(legal, approval, "Approve", ("Comment", "x"))).StatusCode);

        // A rejection needs a comment; the backup's rejection (first response) stands and the primary cannot then approve.
        var noComment = await Post(backup, approval, "Reject", ("Comment", ""));
        Assert.Equal(HttpStatusCode.OK, noComment.StatusCode);
        Assert.Contains("comment", await Text(noComment), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(backup, approval, "Reject", ("Comment", "Wrong supplier on page 3"))).StatusCode);
        var late = await Post(head, approval, "Approve", ("Comment", "too late"));
        Assert.Contains("already", await Text(late), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Initial Creation", await owner.GetStringAsync(life));
        var decided = await owner.GetStringAsync(approval);
        Assert.Contains("Wrong supplier on page 3", decided);
        Assert.Contains("on behalf of", decided, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_new_pages_need_sign_in_and_the_permissions_they_name()
    {
        using var app = DemoApp.Start();
        using var anonymous = app.NewClient();
        foreach (var url in new[] { "/Dashboards", "/Notifications", "/Legal", "/Approvals", "/Variations", "/Renewals", "/Admin/Notifications", "/Admin/EmailCapture", "/Admin/Configuration" })
            Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync(url)).StatusCode);
        using var viewer = await app.SignInAsync("viewer.all@a.example.test");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/Dashboards")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/Notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/EmailCapture")).StatusCode);
        using var admin = await app.SignInAsync("procadmin@a.example.test");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin/Notifications")).StatusCode);
        Assert.Contains("Development mailbox", await admin.GetStringAsync("/Admin/EmailCapture"));
        Assert.Contains("Awaiting My Action", await admin.GetStringAsync("/Dashboards"));
    }
}
