using Ecp.Approvals;
using Ecp.Approvals.Domain;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Lifecycle.Definitions;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecp.Integration.Tests.ApprovalMatrixWorld;

namespace Ecp.Integration.Tests;

/// <summary>The simulator, publication of a matrix (what blocks it, what it records) and the audit history.</summary>
internal static class ApprovalMatrixScenarios3
{
    public static async Task The_simulator_shows_the_resolution_without_touching_any_record_and_is_for_administrators_only(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.ApproverAsync("simA", "SimGroup");
        var startsLater = await w.ApproverAsync("simLater", "LaterGroup", from: w.W.Now.AddDays(40));
        var contract = await w.NewDraftAsync();
        var resource = await w.DescribeAsync(contract);
        await w.PublishMatrixAsync(Doc(Row("now", "{ \"businessUnits\": [\"NOW\"] }", Step("a", "SimGroup")) + "," + Row("later", "{ \"businessUnits\": [\"LATER\"] }", Step("a", "LaterGroup"))));
        var before = await w.FactsAsync(contract);
        SimulationRequest Req(string bu = "NOW", DateOnly? asOf = null, string matrix = "flow", MatrixSource? source = null) => new(w.W.Org1, matrix, Facts(bu: bu), resource, null, asOf, source ?? MatrixSource.Published);

        var result = await w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req()));
        Assert.True(result.Resolution.Resolved, result.Resolution.BlockMessage);
        Assert.False(result.IsFuture);
        Assert.Equal("release 1 (in force)", result.Resolution.SourceLabel);
        Assert.Contains(result.Resolution.Explanations, e => e.Name == "simA" && e.Included);
        Assert.Contains(result.Resolution.Trace, t => t.Stage == TraceStagesRef.Selection);
        // Nothing about the record changed, and no approval request exists.
        Assert.Equal(before.UpdatedUtc, (await w.FactsAsync(contract)).UpdatedUtc);
        Assert.Empty(await w.W.AsScopeless().Run(s => s.Get<Ecp.Lifecycle.Persistence.LifecycleDbContext>().Approvals.ToListAsync()));

        // The date can be moved forward: the group whose only member starts in forty days answers from then on, and the result says it is a future date.
        Assert.False((await w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req("LATER")))).Resolution.Resolved);
        var future = await w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req("LATER", DateOnly.FromDateTime(w.W.Now.AddDays(45)))));
        Assert.True(future.IsFuture);
        Assert.True(future.Resolution.Resolved, future.Resolution.BlockMessage);
        Assert.Equal([startsLater], future.Resolution.Plan!.Steps[0].Approvers.Select(a => a.UserId).ToArray());
        Assert.Contains(future.Notes, n => n.Contains("future", StringComparison.OrdinalIgnoreCase));
        var past = await w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req("NOW", DateOnly.FromDateTime(w.W.Now.AddDays(-30)))));
        Assert.True(past.IsPast);
        Assert.Contains(past.Notes, n => n.Contains("past", StringComparison.OrdinalIgnoreCase));

        // An unknown matrix is a blocked answer with a reason, not an error page.
        var unknown = await w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(matrix: "no-such-matrix")));
        Assert.False(unknown.Resolution.Resolved);
        Assert.Equal("matrix_not_found", unknown.Resolution.BlockCode);

        // Each run is audited, because it shows who the approvers are.
        var runs = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "approval.simulated").Select(e => e.ActorId + "|" + e.NewValuesJson).ToListAsync());
        Assert.True(runs.Count >= 5);
        Assert.All(runs, r => Assert.StartsWith(w.W.Users["procAdmin"].ToString(), r, StringComparison.Ordinal));
        Assert.Contains(runs, r => r.Contains("\"outcome\":\"resolved\"", StringComparison.Ordinal));

        // It reveals approvers, so it needs its own permission: the record's owner, a Viewer and the approvers themselves cannot use it.
        foreach (var user in new[] { "ownerPackaging", "viewerAll", "groupHead", "legalA" })
        {
            var denied = await Assert.ThrowsAnyAsync<Exception>(() => w.As(user, s => s.Get<IApprovalSimulator>().SimulateAsync(Req())));
            Assert.True(denied is ForbiddenException or ResourceNotFoundException, $"{user}: {denied.GetType().Name}");
        }
        // The auditor holds both rights (owner-confirmed allocation); their run is audited like anyone's.
        Assert.True((await w.As("auditor", s => s.Get<IApprovalSimulator>().SimulateAsync(Req()))).Resolution.Resolved);
        // Reading the matrices is a separate, read-only right that an auditor holds.
        Assert.Single(await w.As("auditor", s => s.Get<IApprovalSimulator>().MatricesAsync(w.W.Org1, MatrixSource.Published)));
        await Assert.ThrowsAnyAsync<Exception>(() => w.As("ownerPackaging", s => s.Get<IApprovalSimulator>().MatricesAsync(w.W.Org1, MatrixSource.Published)));
        // An administrator of one organisation cannot try a matrix in another.
        var other = await Assert.ThrowsAnyAsync<Exception>(() => w.As("orgAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req() with { OrganisationId = w.W.Org2 })));
        Assert.True(other is ForbiddenException or ResourceNotFoundException);
    }

    public static async Task The_simulator_can_try_a_numbered_release_or_an_unpublished_draft_before_anyone_relies_on_it(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.UserAsync("bca", Ecp.Identity.Domain.RoleCodes.BusinessConfigurationAdministrator, null, "Internal");
        await w.ApproverAsync("simA", "SimGroup");
        await w.ApproverAsync("simB", "OtherGroup");
        var resource = await w.DescribeAsync(await w.NewDraftAsync());
        await w.PublishMatrixAsync(Single("SimGroup"));                       // release 1
        await w.PublishMatrixAsync(Single("OtherGroup"));                     // release 2: now in force
        SimulationRequest Req(MatrixSource source) => new(w.W.Org1, "flow", Facts(), resource, null, null, source);

        var current = await w.As("bca", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(MatrixSource.Published)));
        Assert.Equal("OtherGroup", current.Resolution.Plan!.Steps[0].ApproverGroup);
        var history = await w.As("bca", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(MatrixSource.Release(1))));
        Assert.Equal(("SimGroup", 1), (history.Resolution.Plan!.Steps[0].ApproverGroup, history.Resolution.ReleaseNumber));
        Assert.Equal("release 1 (history)", history.Resolution.SourceLabel);
        await Assert.ThrowsAsync<DomainException>(() => w.As("bca", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(MatrixSource.Release(99)))));
        // Reading history is the version-history right: the Procurement Administrator can simulate what is in force but cannot reach back into history or drafts.
        Assert.True((await w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(MatrixSource.Published)))).Resolution.Resolved);
        await Assert.ThrowsAnyAsync<Exception>(() => w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(MatrixSource.Release(1)))));
        await Assert.ThrowsAnyAsync<Exception>(() => w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(MatrixSource.Draft))));

        // No draft yet: a clear error. A draft with a mistake can still be tried, with a note that it could not be published.
        var none = await Assert.ThrowsAsync<DomainException>(() => w.As("bca", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(MatrixSource.Draft))));
        Assert.Equal("no_draft", none.Code);
        var gappy = Doc(Row("low", "{ \"valueMin\": 0, \"valueMax\": 1000 }", Step("a", "SimGroup")) + "," + Row("high", "{ \"valueMin\": 5000 }", Step("a", "OtherGroup")));
        await w.As("bca", async s => { var d = await s.Get<IConfigurationService>().GetOrCreateDraftAsync(ApprovalsModule.MatrixKind, w.W.Org1, gappy); await s.Get<IConfigurationService>().SaveDraftAsync(d.Id, gappy); });
        var draft = await w.As("bca", s => s.Get<IApprovalSimulator>().SimulateAsync(new SimulationRequest(w.W.Org1, "flow", Facts(7000m), resource, null, null, MatrixSource.Draft)));
        Assert.True(draft.Resolution.Resolved);
        Assert.Equal("high", draft.Resolution.RowCodes.Single());
        Assert.Contains("your open draft", draft.Resolution.SourceLabel, StringComparison.Ordinal);
        Assert.Contains(draft.Notes, n => n.Contains("validation error", StringComparison.Ordinal) && n.Contains("gap", StringComparison.OrdinalIgnoreCase));
        var inGap = await w.As("bca", s => s.Get<IApprovalSimulator>().SimulateAsync(new SimulationRequest(w.W.Org1, "flow", Facts(3000m), resource, null, null, MatrixSource.Draft)));
        Assert.Equal("no_match", inGap.Resolution.BlockCode);                                 // exactly the thing the validator warned about
        // The published matrix was not affected by trying the draft.
        Assert.Equal(2, (await w.As("bca", s => s.Get<IApprovalSimulator>().SimulateAsync(Req(MatrixSource.Published)))).Resolution.ReleaseNumber);
    }

    public static async Task Publication_refuses_a_matrix_with_gaps_overlaps_or_ties_and_one_a_workflow_still_needs(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.ApproverAsync("tierA", "TierA");
        string Tier(string code, string min, string? max) => Row(code, $"{{ \"valueMin\": {min}{(max is null ? "" : $", \"valueMax\": {max}")} }}", Step("a", "TierA"));
        async Task<DomainException> Refused(string json) => await Assert.ThrowsAsync<DomainException>(() => w.PublishMatrixAsync(json));

        Assert.Contains("gap", (await Refused(Doc(Tier("a", "0", "100") + "," + Tier("b", "200", null)))).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overlap", (await Refused(Doc(Tier("a", "0", "300") + "," + Tier("b", "200", null)))).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same priority", (await Refused(Doc(Row("a", "{ \"businessUnits\": [\"X\"] }", Step("a", "TierA")) + "," + Row("b", "{ \"sites\": [\"S\"] }", Step("a", "TierA"))))).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Base row", (await Refused(Doc(Row("only-exception", "{}", Step("a", "TierA"), kind: "Exception", effect: "AddSteps")))).Message, StringComparison.Ordinal);
        Assert.Null(await w.W.AsScopeless().Run(s => s.Get<IConfigurationReleases>().LatestAsync(ApprovalsModule.MatrixKind, w.W.Org1)));      // none of them was published

        // A workflow cannot name a matrix that has not been published, and a matrix a workflow names cannot be removed.
        var missing = await Assert.ThrowsAsync<DomainException>(() => w.UseMatrixAsync("flow"));
        Assert.Contains("no approval matrix has been published", missing.Message, StringComparison.Ordinal);
        await w.PublishMatrixAsync(Doc(Tier("a", "0", null)));
        var wrongCode = await Assert.ThrowsAsync<DomainException>(() => w.UseMatrixAsync("typo-code"));
        Assert.Contains("not in the published approval matrix document", wrongCode.Message, StringComparison.Ordinal);
        await w.UseMatrixAsync("flow");
        var inUse = await Assert.ThrowsAsync<DomainException>(() => w.PublishMatrixAsync(Doc(Tier("a", "0", null), code: "renamed")));
        Assert.Contains("still refers to the matrix 'flow'", inUse.Message, StringComparison.Ordinal);
        // Pointing the workflow back at nothing frees it.
        await w.UseMatrixAsync(null);
        await w.PublishMatrixAsync(Doc(Tier("a", "0", null), code: "renamed"));
    }

    public static async Task A_group_nobody_holds_is_a_warning_at_publication_and_a_block_at_submission(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.UserAsync("bca", Ecp.Identity.Domain.RoleCodes.BusinessConfigurationAdministrator, null, "Internal");
        var doc = Doc(Row("only", "{}", Step("a", "GroupNobodyHolds")));
        var draft = await w.As("bca", async s =>
        {
            var d = await s.Get<IConfigurationService>().GetOrCreateDraftAsync(ApprovalsModule.MatrixKind, w.W.Org1, doc);
            await s.Get<IConfigurationService>().SaveDraftAsync(d.Id, doc);
            return await s.Get<IConfigurationService>().ValidateAsync(d.Id);
        });
        Assert.True(draft.IsPublishable);
        Assert.Contains(draft.Validation!.Messages, m => m.Severity == ValidationSeverity.Warning && m.Code == "group_empty" && m.Message.Contains("GroupNobodyHolds", StringComparison.Ordinal));
        // It can be published (people join and leave), and a submission that reaches it is then blocked with the reason.
        await w.PublishMatrixAsync(doc);
        var resolved = await w.ResolveAsync("flow", Facts(), await w.DescribeAsync(await w.NewDraftAsync()), null);
        Assert.Equal("missing_approvers", resolved.BlockCode);
        // Reference data adds warnings for codes the organisation does not have, without refusing a future-dated rule.
        var unknownBu = Doc(Row("only", "{ \"businessUnits\": [\"NO-SUCH-UNIT\"], \"categories\": [\"NO-SUCH-CATEGORY\"] }", Step("a", "GroupNobodyHolds")));
        var report = await w.As("bca", async s =>
        {
            var d = await s.Get<IConfigurationService>().GetOrCreateDraftAsync(ApprovalsModule.MatrixKind, w.W.Org1, unknownBu);
            await s.Get<IConfigurationService>().SaveDraftAsync(d.Id, unknownBu);
            return await s.Get<IConfigurationService>().ValidateAsync(d.Id);
        });
        Assert.True(report.IsPublishable);
        Assert.Equal(2, report.Validation!.Messages.Count(m => m.Code == "reference_unknown"));
    }

    public static async Task Every_publication_rollback_delegation_and_simulation_is_in_the_audit_history_with_what_changed(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.UserAsync("bca", Ecp.Identity.Domain.RoleCodes.BusinessConfigurationAdministrator, null, "Internal");
        await w.ApproverAsync("tierA", "TierA");
        await w.ApproverAsync("tierB", "TierB");
        var first = await w.PublishMatrixAsync(Doc(Row("one", "{ \"valueMin\": 0, \"valueMax\": 1000 }", Step("a", "TierA")) + "," + Row("two", "{ \"valueMin\": 1000 }", Step("a", "TierB"))));
        var second = await w.PublishMatrixAsync(Doc(Row("one", "{ \"valueMin\": 0, \"valueMax\": 2000 }", Step("a", "TierA")) + "," + Row("two", "{ \"valueMin\": 2000 }", Step("a", "TierB")) + "," + Row("three", "{ \"businessUnits\": [\"BU\"] }", Step("a", "TierB"), priority: 200)));
        Assert.Equal((1, 2), (first.Number, second.Number));
        var rolledBack = await w.As("bca", s => s.Get<IConfigurationService>().RollbackAsync(ApprovalsModule.MatrixKind, w.W.Org1, 1, "The new bands were wrong"));
        Assert.Equal((3, 1), (rolledBack.Number, rolledBack.RollbackOfNumber));
        Assert.Equal(first.ContentHash, rolledBack.ContentHash);
        var tierA2 = await w.ApproverAsync("tierA2", "TierA");
        await w.As("tierA", s => s.Get<IApprovalDelegations>().CreateAsync(new CreateDelegationRequest(w.W.Org1, "TierA", tierA2, w.W.Now, w.W.Now.AddDays(1), "Leave")));
        var resource = await w.DescribeAsync(await w.NewDraftAsync());
        var simulated = await w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(new SimulationRequest(w.W.Org1, "flow", Facts(), resource, null, null, MatrixSource.Published)));
        Assert.NotNull(simulated);

        var published = await w.As("tenantAdmin", s => s.Get<IAuditBrowser>().QueryAsync(new AuditFilter(Action: "approval.matrix.", PageSize: 50)));
        Assert.Equal(3, published.Total);
        var byRelease = published.Items.OrderBy(e => e.Sequence).ToList();
        Assert.Contains("matrices added: flow", byRelease[0].Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("rows changed: flow/one, flow/two", byRelease[1].Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("rows added: flow/three", byRelease[1].Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("rows removed: flow/three", byRelease[2].Reason ?? "", StringComparison.Ordinal);                      // the rollback undid the addition
        Assert.All(byRelease, e => Assert.Equal("ApprovalMatrix", e.EntityType));
        // The same history also carries the generic configuration events for each release.
        var configuration = await w.As("tenantAdmin", s => s.Get<IAuditBrowser>().QueryAsync(new AuditFilter(Action: "config.", EntityType: ApprovalsModule.MatrixKind)));
        Assert.True(configuration.Total >= 3);

        // Administrators who may read administrative events see the delegations and simulations too; ordinary users see none of it.
        var admin = await w.As("tenantAdmin", s => s.Get<IAuditBrowser>().QueryAsync(new AuditFilter(Action: "approval.", PageSize: 100)));
        Assert.Contains(admin.Items, e => e.Action == "approval.simulated");
        await Assert.ThrowsAnyAsync<Exception>(() => w.As("ownerPackaging", s => s.Get<IAuditBrowser>().QueryAsync(new AuditFilter(Action: "approval.matrix."))));
        // Each event names who did it.
        Assert.All(published.Items, e => Assert.False(string.IsNullOrWhiteSpace(e.ActorId)));
    }
}

/// <summary>Spelled out so the scenario reads without importing the resolver's namespace everywhere.</summary>
internal static class TraceStagesRef
{
    public const string Selection = Ecp.Approvals.Resolution.TraceStages.Selection;
}
