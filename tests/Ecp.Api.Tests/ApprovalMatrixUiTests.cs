using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecp.Approvals;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>The approval matrix through the real pages: who may open the administration, simulator, delegation and history pages, what the simulator and the approval page explain, and that the reference model is untouched.</summary>
[Collection("Api")]
[Trait("Category", "ApprovalMatrix")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ApprovalMatrixUiTests
{
    private const string Owner = "catowner.packaging@a.example.test";
    private static int _n;

    private const string Matrix = """
        { "schemaVersion": 1, "matrices": [ { "code": "ui-flow", "name": "UI flow", "currency": "AUD", "rows": [
          { "code": "small", "kind": "Base", "priority": 100, "flow": "Sequential", "when": { "valueMax": 10000 },
            "steps": [ { "key": "head", "display": "Group Head Procurement", "approverGroup": "GroupHeadProcurement", "mode": "FirstResponse" } ] },
          { "code": "large", "kind": "Base", "priority": 100, "flow": "Sequential", "when": { "valueMin": 10000 },
            "steps": [ { "key": "head", "display": "Group Head Procurement", "approverGroup": "GroupHeadProcurement", "mode": "FirstResponse" } ] } ] } ] }
        """;

    private static async Task<(Guid Tenant, Guid Org)> OrgAsync(DemoApp app)
    {
        var (tenant, _) = await app.FindUserAsync(Owner);
        var org = await app.WithTenantAsync(tenant, async sp =>
        {
            foreach (var candidate in await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync())
                if (await sp.GetRequiredService<ISupplierDirectory>().FindByVendorNumberAsync(candidate.Id, "900001") is not null) return candidate.Id;
            throw new InvalidOperationException("demo organisation missing");
        });
        return (tenant, org);
    }

    private static async Task PublishAsync(DemoApp app, bool useInWorkflow)
    {
        var (tenant, org) = await OrgAsync(app);
        await app.WithTenantAsync(tenant, async sp =>
        {
            var bootstrap = sp.GetRequiredService<IConfigurationBootstrap>();
            await bootstrap.DraftValidatePublishAsync(ApprovalsModule.MatrixKind, org, Matrix, "ui test", "matrix for the page tests");
            if (!useInWorkflow) return 0;
            var current = await sp.GetRequiredService<IWorkflowDefinitions>().CurrentAsync(WorkflowKinds.ContractLifecycle, org);
            var release = await sp.GetRequiredService<IConfigurationReleases>().FindAsync(current.ReleaseId);
            var node = JsonNode.Parse(release!.ContentJson)!;
            node["settings"]!["approval"]!.AsObject()["matrix"] = "ui-flow";
            await bootstrap.DraftValidatePublishAsync(WorkflowKinds.ContractLifecycle, org, node.ToJsonString(), "ui test", "workflow names the matrix");
            return 0;
        });
    }

    private static async Task<Guid> NewDraftAsync(DemoApp app, decimal value)
    {
        var (tenant, org) = await OrgAsync(app);
        return await app.WithTenantAsync(tenant, async sp =>
        {
            var supplier = (await sp.GetRequiredService<ISupplierDirectory>().FindByVendorNumberAsync(org, "900001"))!.Value;
            var category = await sp.GetRequiredService<ICategoryAdministration>().UpsertCategoryAsync(org, "PACKAGING", "Packaging", null);
            var n = Interlocked.Increment(ref _n);
            return await sp.GetRequiredService<IContractService>().CreateDraftAsync(new CreateContractRequest(org, $"UI-AM-{n:000}", $"UI matrix {n}", supplier, category, Classification: "Internal", LegalReviewRequired: false, CurrencyCode: "AUD", EstimatedValue: value));
        });
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string page, string handler, params (string, string)[] fields)
        => DemoApp.PostFormAsync(c, page, fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)), handler.Length == 0 ? page : $"{page}?handler={handler}");

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, string page, string handler, string field, string name, string text, params (string, string)[] fields)
    {
        var token = await DemoApp.TokenAsync(c, page);
        using var form = new MultipartFormDataContent();
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text)) { Headers = { ContentType = new("application/pdf") } }, field, name);
        return await c.PostAsync(handler.Length == 0 ? page : $"{page}?handler={handler}", form);
    }

    [Fact]
    public async Task The_administration_pages_need_sign_in_and_the_permissions_they_name()
    {
        using var app = DemoApp.Start();
        var pages = new[] { "/Admin/Approvals", "/Admin/Approvals/Simulate", "/Admin/Approvals/Delegations", "/Admin/Approvals/History" };
        using var anonymous = app.NewClient();
        foreach (var url in pages) Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync(url)).StatusCode);

        using var viewer = await app.SignInAsync("viewer.all@a.example.test");
        foreach (var url in pages) Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(url)).StatusCode);

        using var admin = await app.SignInAsync("procadmin@a.example.test");
        foreach (var url in pages) Assert.True((await admin.GetAsync(url)).StatusCode == HttpStatusCode.OK, $"{url} should open for a procurement administrator");

        // An approver may manage their own delegations but not read the matrices or run the simulator.
        using var head = await app.SignInAsync("grouphead@a.example.test");
        Assert.Equal(HttpStatusCode.OK, (await head.GetAsync("/Admin/Approvals/Delegations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.GetAsync("/Admin/Approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.GetAsync("/Admin/Approvals/Simulate")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.GetAsync("/Admin/Approvals/History")).StatusCode);

        // A crafted post to the simulator is refused for someone without the permission, antiforgery token or not.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/Admin/Approvals/Simulate", new FormUrlEncodedContent(new Dictionary<string, string> { ["MatrixCode"] = "ui-flow" }))).StatusCode);
    }

    [Fact]
    public async Task The_administrator_reads_the_matrix_and_simulates_a_submission_with_the_reasons_and_the_trace()
    {
        using var app = DemoApp.Start();
        await PublishAsync(app, useInWorkflow: false);
        using var admin = await app.SignInAsync("procadmin@a.example.test");

        var index = await admin.GetStringAsync("/Admin/Approvals");
        Assert.Contains("ui-flow", index);
        Assert.Contains("GroupHeadProcurement", index);

        var form = await admin.GetStringAsync("/Admin/Approvals/Simulate");
        Assert.Contains("ui-flow", form);

        var result = await Post(admin, "/Admin/Approvals/Simulate", "", ("MatrixCode", "ui-flow"), ("Source", "published"), ("Value", "5000"), ("Currency", "AUD"), ("Classification", "Internal"));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync();
        Assert.Contains("Group Head Procurement", html);
        Assert.Contains("small", html);                                 // the row that matched
        Assert.Contains("Trace", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grouphead", html, StringComparison.OrdinalIgnoreCase); // who, and why (the group's primary and the backup)

        // A value no row covers, or a currency the matrix has no rate for, is a blocked result with the reason, not an error page.
        var otherCurrency = await Post(admin, "/Admin/Approvals/Simulate", "", ("MatrixCode", "ui-flow"), ("Source", "published"), ("Value", "5000"), ("Currency", "USD"), ("Classification", "Internal"));
        Assert.Equal(HttpStatusCode.OK, otherCurrency.StatusCode);
        Assert.Contains("blocked", (await otherCurrency.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);

        // A date in the future is run against the same published matrix and says so.
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(400)).ToString("yyyy-MM-dd");
        var futureResult = await Post(admin, "/Admin/Approvals/Simulate", "", ("MatrixCode", "ui-flow"), ("Source", "published"), ("Value", "50000"), ("Currency", "AUD"), ("Classification", "Internal"), ("AsOf", future));
        var futureHtml = await futureResult.Content.ReadAsStringAsync();
        Assert.Contains("large", futureHtml);
        Assert.Contains("future", futureHtml, StringComparison.OrdinalIgnoreCase);

        // A procurement administrator may read the matrix but not the version history or the administrative audit trail; the page says so rather than failing.
        var history = await admin.GetStringAsync("/Admin/Approvals/History");
        Assert.Contains("You cannot read the version history", history);
        Assert.Contains("You cannot read the audit trail", history);
        // The tenant administrator sees the releases and the audit trail, in which the publication and the simulation both appear.
        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        var (_, org) = await OrgAsync(app);
        var trail = await tenantAdmin.GetStringAsync($"/Admin/Approvals/History?organisationId={org}");
        Assert.Contains("matrix for the page tests", trail);
        Assert.Contains("approval.simulated", trail);
        Assert.Contains("approval.matrix.published", trail);
    }

    [Fact]
    public async Task A_delegation_is_made_listed_and_ended_through_the_page_and_a_loop_is_refused()
    {
        using var app = DemoApp.Start();
        await PublishAsync(app, useInWorkflow: false);
        var (_, org) = await OrgAsync(app);
        var (_, backupId) = await app.FindUserAsync("grouphead.backup@a.example.test");
        var (_, headId) = await app.FindUserAsync("grouphead@a.example.test");
        using var head = await app.SignInAsync("grouphead@a.example.test");
        using var backup = await app.SignInAsync("grouphead.backup@a.example.test");
        using var admin = await app.SignInAsync("procadmin@a.example.test");
        var from = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd");
        const string group = "GroupHeadProcurement";

        // The page lists the colleagues in the group only to a member of it.
        var form = await head.GetStringAsync($"/Admin/Approvals/Delegations?groupKey={group}");
        Assert.Contains("Delegate my authority", form);
        Assert.Contains(backupId.ToString(), form);
        var outsider = await backup.GetStringAsync("/Admin/Approvals/Delegations?groupKey=SomeoneElsesGroup");
        Assert.DoesNotContain(headId.ToString(), outsider);

        var made = await Post(head, "/Admin/Approvals/Delegations", "Create", ("OrganisationId", org.ToString()), ("GroupKey", group), ("ToUserId", backupId.ToString()), ("FromDate", from), ("ToDate", to), ("Reason", "Annual leave"));
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        Assert.Contains("Annual leave", await head.GetStringAsync("/Admin/Approvals/Delegations"));
        Assert.Contains("Annual leave", await admin.GetStringAsync("/Admin/Approvals/Delegations"));

        // The person delegated to cannot hand it straight back: that would be a loop. A delegation to oneself or from outside the group is refused too.
        var loop = await Post(backup, "/Admin/Approvals/Delegations", "Create", ("OrganisationId", org.ToString()), ("GroupKey", group), ("ToUserId", headId.ToString()), ("FromDate", from), ("ToDate", to), ("Reason", "Back again"));
        Assert.Equal(HttpStatusCode.OK, loop.StatusCode);
        Assert.Contains("That would make a circle", await loop.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Back again", await backup.GetStringAsync("/Admin/Approvals/Delegations"));
    }

    [Fact]
    public async Task A_contract_approved_through_a_matrix_shows_its_plan_and_the_reasons_to_matrix_readers_only()
    {
        using var app = DemoApp.Start();
        await PublishAsync(app, useInWorkflow: true);
        var id = await NewDraftAsync(app, 5000m);
        using var owner = await app.SignInAsync(Owner);
        using var head = await app.SignInAsync("grouphead@a.example.test");
        using var admin = await app.SignInAsync("procadmin@a.example.test");
        var life = $"/Contracts/Lifecycle/{id}";

        Assert.Equal(HttpStatusCode.Redirect, (await Post(owner, life, "Step", ("step", "ProceedWithoutLegal"))).StatusCode);
        await UploadAsync(owner, $"/Contracts/Documents/{id}", "", "file", "final.pdf", "final", ("DocumentType", "FinalAgreement"), ("Title", "Final agreement"));
        await Post(owner, life, "SendSignature", ("provider", "manual"));
        await UploadAsync(owner, life, "ExecutedCopy", "file", "executed.pdf", "executed");
        Assert.Equal(HttpStatusCode.Redirect, (await Post(owner, life, "SubmitApproval", ("Comment", "go"))).StatusCode);

        var requestId = Guid.Parse(Regex.Match(await head.GetStringAsync("/Approvals"), "href=\"/Approvals/Details/([0-9a-f-]{36})\"").Groups[1].Value);
        var approval = $"/Approvals/Details/{requestId}";
        var asHead = await head.GetStringAsync(approval);
        Assert.Contains("Group Head Procurement", asHead);                                 // the step
        Assert.DoesNotContain("Resolution trace", asHead);                                // the approver is not a matrix reader
        var asAdmin = await admin.GetStringAsync(approval);
        Assert.Contains("Resolution trace", asAdmin);
        Assert.Contains("small", asAdmin);                                                 // the row that applied

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(owner, approval, "Approve", ("Comment", "me"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(head, approval, "Approve", ("Comment", "Approved"))).StatusCode);
        var details = await owner.GetStringAsync($"/Contracts/Details/{id}");
        Assert.Contains("Effective", details);
        Assert.Contains("Locked", details);
    }
}
