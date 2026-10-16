using Ecp.Approvals;
using Ecp.Approvals.Domain;
using Ecp.Approvals.Persistence;
using Ecp.Approvals.Resolution;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecp.Integration.Tests.ApprovalMatrixWorld;

namespace Ecp.Integration.Tests;

/// <summary>The approval matrix resolver against real identity and access data: who is named, who is left out and why, on which dates, and what delegation can and cannot do.</summary>
internal static class ApprovalMatrixScenarios
{
    public static async Task Resolution_chooses_the_row_names_the_people_and_explains_every_choice(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var head = await w.ApproverAsync("tierAHead", "TierA");
        var backup = await w.ApproverAsync("tierABackup", "TierA", backup: true);
        await w.ApproverAsync("tierBHead", "TierB");
        var contract = await w.NewDraftAsync();
        var resource = await w.DescribeAsync(contract);
        await w.PublishMatrixAsync(Doc(
            Row("low", "{ \"valueMin\": 0, \"valueMax\": 100000 }", Step("approve", "TierA")) + "," +
            Row("high", "{ \"valueMin\": 100000 }", Step("first", "TierA") + "," + Step("second", "TierB"))));

        var low = await w.ResolveAsync("flow", Facts(50000m), resource, w.W.Users["ownerPackaging"]);
        Assert.True(low.Resolved, low.BlockMessage);
        var step = Assert.Single(low.Plan!.Steps);
        Assert.Equal(("low/approve", "TierA", "FirstResponse"), (step.Key, step.ApproverGroup, step.Mode));
        Assert.Equal([head, backup], step.Approvers.Select(a => a.UserId).OrderBy(x => x == head ? 0 : 1).ToArray());
        Assert.Equal(ParticipantRoles.Primary, step.Approvers.Single(a => a.UserId == head).Role);
        Assert.Equal(ParticipantRoles.Backup, step.Approvers.Single(a => a.UserId == backup).Role);
        Assert.Contains(low.Trace, e => e.Stage == TraceStages.Selection && e.Outcome == TraceOutcomes.Selected && e.Message.Contains("'low'", StringComparison.Ordinal));
        Assert.Contains(low.Trace, e => e.Stage == TraceStages.Approver && e.Outcome == TraceOutcomes.Included && e.Message.Contains("tierAHead", StringComparison.Ordinal));
        var why = Assert.Single(low.Explanations, e => e.UserId == head);
        Assert.True(why.Included);
        Assert.Contains("Approver of group TierA", why.Summary, StringComparison.Ordinal);
        Assert.Single(low.Plan.Steps.Select(s => s.Stage).Distinct());
        Assert.Equal(1, low.ReleaseNumber);

        var high = await w.ResolveAsync("flow", Facts(250000m), resource, w.W.Users["ownerPackaging"]);
        Assert.Equal(["TierA", "TierB"], high.Plan!.Steps.OrderBy(s => s.Stage).Select(s => s.ApproverGroup).ToArray());
        Assert.Equal([0, 1], high.Plan.Steps.OrderBy(s => s.Stage).Select(s => s.Stage).ToArray());

        // The same value on the boundary goes to the higher tier; one cent under stays in the lower.
        Assert.Equal("high", (await w.ResolveAsync("flow", Facts(100000m), resource, null)).RowCodes.Single());
        Assert.Equal("low", (await w.ResolveAsync("flow", Facts(99999.99m), resource, null)).RowCodes.Single());
    }

    public static async Task The_submitter_and_people_who_cannot_open_the_record_are_never_approvers(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var solo = await w.ApproverAsync("solo", "SoloGroup");
        await w.ApproverAsync("lowCeiling", "LowCeilingGroup", ceiling: "Internal");
        await w.ApproverAsync("plain", "PrivGroup", ceiling: "Legal Privileged");                     // ceiling reaches it, no privileged grant
        await w.ApproverAsync("granted", "PrivGrantedGroup", ceiling: "Legal Privileged", privileged: true);
        var confidential = await w.NewDraftAsync(classification: "Confidential");
        var privileged = await w.NewDraftAsync();
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractLifecyclePort>().ApplyControlledChangesAsync(privileged, new Dictionary<string, string?> { ["classification"] = "Legal Privileged" }, ["classification"], "test", "unlock", Guid.NewGuid(), "test"); return 0; });
        var resConfidential = await w.DescribeAsync(confidential);
        var resPrivileged = await w.DescribeAsync(privileged);
        await w.PublishMatrixAsync(Doc(Row("solo", "{ \"businessUnits\": [\"SOLO\"] }", Step("a", "SoloGroup")) + "," + Row("low", "{ \"businessUnits\": [\"LOW\"] }", Step("a", "LowCeilingGroup")) + "," +
            Row("priv", "{ \"businessUnits\": [\"PRIV\"] }", Step("a", "PrivGroup")) + "," + Row("granted", "{ \"businessUnits\": [\"GRANTED\"] }", Step("a", "PrivGrantedGroup")) + "," +
            Row("nobody", "{ \"businessUnits\": [\"EMPTY\"] }", Step("a", "NobodyHoldsThis"))));

        // Only the submitter holds the group: segregation of duties leaves nobody, and the explanation names the reason.
        var soloOnly = await w.ResolveAsync("flow", Facts(bu: "SOLO"), resConfidential, solo);
        Assert.False(soloOnly.Resolved);
        Assert.Equal("missing_approvers", soloOnly.BlockCode);
        Assert.Contains("segregation of duties", soloOnly.BlockMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(soloOnly.Explanations, e => e.UserId == solo && !e.Included && e.Reasons.Any(r => r.Contains("Submitted this request", StringComparison.Ordinal)));
        Assert.True((await w.ResolveAsync("flow", Facts(bu: "SOLO"), resConfidential, w.W.Users["ownerPackaging"])).Resolved);        // somebody else submitting: fine

        // The only member's ceiling is below the record's classification: they could not even open it, so they cannot approve it.
        var lowCeiling = await w.ResolveAsync("flow", Facts(bu: "LOW", classification: "Confidential"), resConfidential, null);
        Assert.False(lowCeiling.Resolved);
        Assert.Contains(lowCeiling.Explanations, e => !e.Included && e.Name == "lowCeiling" && e.Reasons.Any(r => r.Contains("Access rules", StringComparison.Ordinal)));
        Assert.True((await w.ResolveAsync("flow", Facts(bu: "LOW", classification: "Internal"), await w.DescribeAsync(await w.NewDraftAsync()), null)).Resolved);   // an Internal record is within their ceiling

        // A Legal Privileged record needs an explicit grant on top of a sufficient ceiling.
        var noGrant = await w.ResolveAsync("flow", Facts(bu: "PRIV", classification: "Legal Privileged"), resPrivileged, null);
        Assert.False(noGrant.Resolved);
        Assert.Contains(noGrant.Explanations, e => e.Name == "plain" && !e.Included && e.Reasons.Any(r => r.Contains("privileged", StringComparison.OrdinalIgnoreCase)));
        var withGrant = await w.ResolveAsync("flow", Facts(bu: "GRANTED", classification: "Legal Privileged"), resPrivileged, null);
        Assert.True(withGrant.Resolved, withGrant.BlockMessage);
        Assert.Equal(["granted"], withGrant.Names("a").ToArray());

        // A group nobody holds blocks too, with a message that says what to do.
        var empty = await w.ResolveAsync("flow", Facts(bu: "EMPTY"), resConfidential, null);
        Assert.False(empty.Resolved);
        Assert.Contains("nobody holds the approver group 'NobodyHoldsThis'", empty.BlockMessage!, StringComparison.Ordinal);
        Assert.Equal(TraceOutcomes.Blocked, empty.Trace[^1].Outcome);
        Assert.Null(empty.Plan);
    }

    public static async Task Backup_approvers_and_backup_groups_stand_in_where_the_step_allows_it(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var head = await w.ApproverAsync("head", "Heads");
        var inGroupBackup = await w.ApproverAsync("inGroupBackup", "Heads", backup: true);
        var externalBackup = await w.ApproverAsync("externalBackup", "StandIns");
        var contract = await w.NewDraftAsync();
        var resource = await w.DescribeAsync(contract);
        await w.PublishMatrixAsync(Doc(
            Row("with-backup-group", "{ \"businessUnits\": [\"A\"] }", Step("a", "Heads", backup: "StandIns")) + "," +
            Row("no-backups", "{ \"businessUnits\": [\"B\"] }", Step("a", "Heads", extra: ", \"allowBackup\": false")) + "," +
            Row("all-required", "{ \"businessUnits\": [\"C\"] }", Step("a", "Heads", mode: "AllRequired")) + "," +
            Row("all-required-backups", "{ \"businessUnits\": [\"D\"] }", Step("a", "Heads", mode: "AllRequired", extra: ", \"allowBackup\": true"))));

        var a = await w.ResolveAsync("flow", Facts(bu: "A"), resource, null);
        Assert.Equal(new[] { head, inGroupBackup, externalBackup }.OrderBy(x => x).ToArray(), a.Plan!.Steps[0].Approvers.Select(p => p.UserId).OrderBy(x => x).ToArray());
        Assert.Contains("backup group StandIns", a.Plan.Steps[0].Approvers.Single(p => p.UserId == externalBackup).Why, StringComparison.Ordinal);

        var b = await w.ResolveAsync("flow", Facts(bu: "B"), resource, null);
        Assert.Equal([head], b.Plan!.Steps[0].Approvers.Select(p => p.UserId).ToArray());
        Assert.Contains(b.Explanations, e => e.UserId == inGroupBackup && !e.Included && e.Reasons.Any(r => r.Contains("does not allow backup", StringComparison.Ordinal)));

        // An all-required step names the primary approvers and, unless it allows backups, leaves them out.
        var c = await w.ResolveAsync("flow", Facts(bu: "C"), resource, null);
        Assert.Equal([head], c.Plan!.Steps[0].Approvers.Select(p => p.UserId).ToArray());
        var d = await w.ResolveAsync("flow", Facts(bu: "D"), resource, null);
        Assert.Equal(2, d.Plan!.Steps[0].Approvers.Count);
        Assert.Equal([head], d.Plan.Steps[0].Primaries.Select(p => p.UserId).ToArray());

        // When the only primary is the submitter an all-required step cannot be satisfied even though a backup exists: backups only cover a seat that exists.
        var submitterIsHead = await w.ResolveAsync("flow", Facts(bu: "C"), resource, head);
        Assert.False(submitterIsHead.Resolved);
        Assert.Equal("missing_approvers", submitterIsHead.BlockCode);
    }

    public static async Task Effective_dates_decide_which_rows_rates_and_people_apply_on_a_given_day(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var contract = await w.NewDraftAsync();
        var resource = await w.DescribeAsync(contract);
        var now = w.W.Now;
        var startsSoon = await w.ApproverAsync("startsSoon", "FutureGroup", from: now.AddDays(10));
        await w.ApproverAsync("endedLastWeek", "EndedGroup", from: now.AddDays(-60), to: now.AddDays(-7));
        await w.ApproverAsync("current", "CurrentGroup", from: now.AddDays(-60));
        var later = DateOnly.FromDateTime(now.AddDays(30)).ToString("yyyy-MM-dd");
        var yesterday = DateOnly.FromDateTime(now.AddDays(-1)).ToString("yyyy-MM-dd");
        await w.PublishMatrixAsync(Doc(
            Row("future-people", "{ \"businessUnits\": [\"F\"] }", Step("a", "FutureGroup")) + "," +
            Row("ended-people", "{ \"businessUnits\": [\"E\"] }", Step("a", "EndedGroup")) + "," +
            Row("old-rule", "{ \"businessUnits\": [\"R\"] }", Step("a", "CurrentGroup"), window: $"\"effectiveTo\": \"{yesterday}\"") + "," +
            Row("new-rule", "{ \"businessUnits\": [\"R\"] }", Step("a", "FutureGroup"), window: $"\"effectiveFrom\": \"{later}\""),
            extra: $", \"rates\": [ {{ \"currency\": \"USD\", \"rate\": 2, \"effectiveFrom\": \"{later}\" }} ]"));

        // Today: the group's only member has not started yet, so a step for it cannot be answered.
        var today = await w.ResolveAsync("flow", Facts(bu: "F"), resource, null);
        Assert.False(today.Resolved);
        Assert.Contains(today.Trace, e => e.Stage == TraceStages.Step && e.Outcome == TraceOutcomes.Blocked);
        // Fifteen days on, they have started: the same question now has an answer. This is the future-date test.
        var future = await w.ResolveAsync("flow", Facts(bu: "F"), resource, null, asOfUtc: now.AddDays(15));
        Assert.True(future.Resolved, future.BlockMessage);
        Assert.Equal([startsSoon], future.Plan!.Steps[0].Approvers.Select(p => p.UserId).ToArray());
        // The assignment that ended last week: nobody now; a month ago it still answered (a past-date test).
        Assert.False((await w.ResolveAsync("flow", Facts(bu: "E"), resource, null)).Resolved);
        Assert.True((await w.ResolveAsync("flow", Facts(bu: "E"), resource, null, asOfUtc: now.AddDays(-30))).Resolved);

        // The rule that ended yesterday no longer applies, and the one that starts in a month is not yet in force.
        var rule = await w.ResolveAsync("flow", Facts(bu: "R"), resource, null);
        Assert.False(rule.Resolved);
        Assert.Equal("no_match", rule.BlockCode);
        Assert.True((await w.ResolveAsync("flow", Facts(bu: "R"), resource, null, asOfUtc: now.AddDays(-10))).Resolved);        // the old rule, ten days ago
        Assert.Equal("new-rule", (await w.ResolveAsync("flow", Facts(bu: "R"), resource, null, asOfUtc: now.AddDays(45))).RowCodes.Single());

        // A rate that starts in a month cannot be used today.
        Assert.Equal("no_rate", (await w.ResolveAsync("flow", Facts(100m, "USD", bu: "F"), resource, null)).BlockCode);
        Assert.Equal(200m, (await w.ResolveAsync("flow", Facts(100m, "USD", bu: "F"), resource, null, asOfUtc: now.AddDays(45))).Plan!.ConvertedValue);
    }

    public static async Task Delegation_is_bounded_audited_non_transitive_and_never_circular(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var a = await w.ApproverAsync("dA", "DGroup");
        var b = await w.ApproverAsync("dB", "DGroup");
        var c = await w.ApproverAsync("dC", "DGroup", backup: true);
        await w.ApproverAsync("outsider", "OtherGroup");
        var contract = await w.NewDraftAsync();
        var resource = await w.DescribeAsync(contract);
        await w.PublishMatrixAsync(Doc(Row("d", "{}", Step("a", "DGroup", mode: "AllRequired"))));
        var now = w.W.Now;
        CreateDelegationRequest Req(Guid to, int fromDays = 0, int days = 7, string reason = "On leave") => new(w.W.Org1, "DGroup", to, now.AddDays(fromDays), now.AddDays(fromDays + days), reason);

        // dA delegates to dB. It is recorded, audited and shows up as "on behalf of" when the plan is resolved.
        var first = await w.As("dA", s => s.Get<IApprovalDelegations>().CreateAsync(Req(b)));
        var resolved = await w.ResolveAsync("flow", Facts(), resource, w.W.Users["ownerPackaging"]);
        var delegateSeat = Assert.Single(resolved.Plan!.Steps[0].Approvers, p => p.Role == ParticipantRoles.Delegate);
        Assert.Equal((b, a), (delegateSeat.UserId, delegateSeat.OnBehalfOfUserId));
        Assert.Contains("on behalf of dA", delegateSeat.Why, StringComparison.Ordinal);
        Assert.Contains(resolved.Trace, t => t.Stage == TraceStages.Delegation && t.Outcome == TraceOutcomes.Included);
        var audit = await w.W.AsScopeless().Run(s => s.Get<Ecp.Audit.IAuditEventSource>().Events.Where(e => e.Action == "approval.delegation.created").Select(e => e.EntityId).ToListAsync());
        Assert.Contains(first.ToString(), audit);

        // A loop is refused, whether it has two members or three.
        Assert.Equal("circular_delegation", (await Assert.ThrowsAsync<DomainException>(() => w.As("dB", s => s.Get<IApprovalDelegations>().CreateAsync(Req(a))))).Code);
        await w.As("dB", s => s.Get<IApprovalDelegations>().CreateAsync(Req(c)));                      // dA to dB to dC is a chain, not a loop
        Assert.Equal("circular_delegation", (await Assert.ThrowsAsync<DomainException>(() => w.As("dC", s => s.Get<IApprovalDelegations>().CreateAsync(Req(a))))).Code);
        // The same loop is fine in a period that does not overlap the others.
        await w.As("dC", s => s.Get<IApprovalDelegations>().CreateAsync(Req(a, fromDays: 30)));
        Assert.Equal("delegation_exists", (await Assert.ThrowsAsync<DomainException>(() => w.As("dA", s => s.Get<IApprovalDelegations>().CreateAsync(Req(b, fromDays: 2))))).Code);

        // Only a member of the group can receive it; not yourself; not more than 90 days; not in the past; and only an approver can give it.
        Assert.Equal("delegate_not_eligible", (await Assert.ThrowsAsync<DomainException>(() => w.As("dA", s => s.Get<IApprovalDelegations>().CreateAsync(Req(w.W.Users["outsider"], fromDays: 40))))).Code);
        Assert.Equal("delegate_not_eligible", (await Assert.ThrowsAsync<DomainException>(() => w.As("dA", s => s.Get<IApprovalDelegations>().CreateAsync(Req(w.W.Users["ownerPackaging"], fromDays: 40))))).Code);
        Assert.Equal("delegation_self", (await Assert.ThrowsAsync<DomainException>(() => w.As("dA", s => s.Get<IApprovalDelegations>().CreateAsync(Req(a, fromDays: 40))))).Code);
        Assert.Equal("delegation_window", (await Assert.ThrowsAsync<DomainException>(() => w.As("dA", s => s.Get<IApprovalDelegations>().CreateAsync(Req(c, fromDays: 40, days: 91))))).Code);
        Assert.Equal("delegation_window", (await Assert.ThrowsAsync<DomainException>(() => w.As("dA", s => s.Get<IApprovalDelegations>().CreateAsync(Req(c, fromDays: -10, days: 5))))).Code);
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<IApprovalDelegations>().CreateAsync(Req(b, fromDays: 50))));
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("dA", s => s.Get<IApprovalDelegations>().CreateAsync(Req(c, fromDays: 60, reason: " "))));

        // Not transitive: dC acts for dB, who delegated to them; dC gets nothing from dA through dB.
        var chain = await w.ResolveAsync("flow", Facts(), resource, w.W.Users["ownerPackaging"]);
        var seats = chain.Plan!.Steps[0].Approvers.Where(p => p.Role == ParticipantRoles.Delegate).Select(p => (p.UserId, p.OnBehalfOfUserId)).OrderBy(x => x.UserId).ToArray();
        Assert.Equal(new[] { (b, (Guid?)a), (c, (Guid?)b) }.OrderBy(x => x.Item1).ToArray(), seats);

        // The delegator's own record of what they gave, and an administrator's view.
        var mine = await w.As("dA", s => s.Get<IApprovalDelegations>().MineAsync());
        Assert.Single(mine, d => d.FromUserId == a);
        Assert.Equal(2, mine.Count);                                                                   // the one dA gave, and the one dC will give dA next month
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<IApprovalDelegations>().ListAsync(w.W.Org1, false)));
        var listed = await w.As("procAdmin", s => s.Get<IApprovalDelegations>().ListAsync(w.W.Org1, true));
        Assert.True(listed.Total >= 3);

        // Ending it: the person who gave it can; someone else cannot; an administrator can. An ended delegation no longer appears.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("dB", s => s.Get<IApprovalDelegations>().RevokeAsync(first, "not mine")));
        // Procurement Administrators hold the administrative right only by explicit assignment, which no default role carries; the Organisation Administrator may end it, and must say why.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("procAdmin", s => s.Get<IApprovalDelegations>().RevokeAsync(first, "Administrator ended it")));
        await Assert.ThrowsAsync<DomainException>(() => w.As("orgAdmin", s => s.Get<IApprovalDelegations>().RevokeAsync(first, "  ")));
        await w.As("orgAdmin", s => s.Get<IApprovalDelegations>().RevokeAsync(first, "Administrator ended it"));
        Assert.Equal("delegation_revoked", (await Assert.ThrowsAsync<DomainException>(() => w.As("dA", s => s.Get<IApprovalDelegations>().RevokeAsync(first, "again")))).Code);
        var afterRevoke = await w.ResolveAsync("flow", Facts(), resource, w.W.Users["ownerPackaging"]);
        Assert.DoesNotContain(afterRevoke.Plan!.Steps[0].Approvers, p => p.Role == ParticipantRoles.Delegate && p.OnBehalfOfUserId == a);
        Assert.Contains(await w.W.AsScopeless().Run(s => s.Get<Ecp.Audit.IAuditEventSource>().Events.Where(e => e.Action == "approval.delegation.revoked").Select(e => e.EntityId).ToListAsync()), x => x == first.ToString());

        // Time moves on: a delegation that has ended stops counting without anyone doing anything.
        var cd = (await w.As("dB", s => s.Get<IApprovalDelegations>().MineAsync())).Single(d => d.FromUserId == b && d.ToUserId == c);
        Assert.True(cd.IsActive);
        w.App.Clock.Advance(TimeSpan.FromDays(8));
        var late = await w.ResolveAsync("flow", Facts(), resource, w.W.Users["ownerPackaging"], asOfUtc: w.W.Now);
        Assert.DoesNotContain(late.Plan!.Steps[0].Approvers, p => p.Role == ParticipantRoles.Delegate);
    }

    public static async Task A_delegation_from_someone_who_cannot_act_is_ignored_and_a_circle_in_the_data_gives_nobody_authority(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var a = await w.ApproverAsync("xA", "XGroup");
        var b = await w.ApproverAsync("xB", "XGroup");
        var c = await w.ApproverAsync("xC", "XGroup");
        var contract = await w.NewDraftAsync();
        var resource = await w.DescribeAsync(contract);
        await w.PublishMatrixAsync(Doc(Row("x", "{}", Step("a", "XGroup"))));
        var now = w.W.Now;
        await w.As("xA", s => s.Get<IApprovalDelegations>().CreateAsync(new CreateDelegationRequest(w.W.Org1, "XGroup", b, now, now.AddDays(5), "Leave")));

        // xA submitted the request, so xA has no authority on it and has nothing to hand on: the delegation is ignored.
        var bySubmitter = await w.ResolveAsync("flow", Facts(), resource, a);
        Assert.True(bySubmitter.Resolved);
        Assert.Contains(bySubmitter.Explanations, e => e.Role == ParticipantRoles.Delegate && !e.Included && e.Reasons.Any(r => r.Contains("no authority to delegate", StringComparison.Ordinal)));
        Assert.DoesNotContain(bySubmitter.Plan!.Steps[0].Approvers, p => p.Role == ParticipantRoles.Delegate);

        // A circle that got into the data some other way (here written directly, which the service would refuse) grants nobody anything.
        await w.W.AsScopeless().Run(async s =>
        {
            var db = s.Get<ApprovalsDbContext>();
            db.Delegations.Add(ApprovalDelegation.Create(w.W.TenantId, w.W.Org1, "XGroup", b, "xB", c, "xC", now, now.AddDays(5), "Loop 1", now));
            db.Delegations.Add(ApprovalDelegation.Create(w.W.TenantId, w.W.Org1, "XGroup", c, "xC", a, "xA", now, now.AddDays(5), "Loop 2", now));
            await db.SaveChangesAsync();
            return 0;
        });
        var circular = await w.ResolveAsync("flow", Facts(), resource, w.W.Users["ownerPackaging"]);
        Assert.True(circular.Resolved);
        Assert.DoesNotContain(circular.Plan!.Steps[0].Approvers, p => p.Role == ParticipantRoles.Delegate);
        Assert.Contains(circular.Trace, t => t.Stage == TraceStages.Delegation && t.Outcome == TraceOutcomes.Excluded && t.Message.Contains("circular", StringComparison.Ordinal));
        Assert.Equal(3, circular.Explanations.Count(e => e.Role == ParticipantRoles.Delegate && !e.Included));
    }

    public static async Task Matrices_delegations_and_resolutions_belong_to_one_tenant(TestApp app)
    {
        var a = await LifecycleWorld.CreateAsync(app);
        var b = await LifecycleWorld.CreateAsync(app);
        await a.ApproverAsync("aHead", "SharedName");
        await b.ApproverAsync("bHead", "SharedName");
        var aContract = await a.NewDraftAsync();
        var bContract = await b.NewDraftAsync();
        await a.PublishMatrixAsync(Doc(Row("only", "{}", Step("a", "SharedName"))));

        // Tenant B has published nothing: tenant A's matrix does not exist for it, even by the same code and group name.
        var resolvedB = await b.ResolveAsync("flow", Facts(), await b.DescribeAsync(bContract), null);
        Assert.False(resolvedB.Resolved);
        Assert.Equal("matrix_not_published", resolvedB.BlockCode);
        Assert.True((await a.ResolveAsync("flow", Facts(), await a.DescribeAsync(aContract), null)).Resolved);

        // A delegation in A is invisible in B: not in B's own queries, and A's organisation cannot be named.
        var aHead = a.W.Users["aHead"];
        await a.ApproverAsync("aOther", "SharedName");
        await a.As("aHead", s => s.Get<IApprovalDelegations>().CreateAsync(new CreateDelegationRequest(a.W.Org1, "SharedName", a.W.Users["aOther"], a.W.Now, a.W.Now.AddDays(3), "Leave")));
        Assert.Equal(0, await b.W.AsScopeless().Run(s => s.Get<ApprovalsDbContext>().Delegations.CountAsync()));
        Assert.Empty(await b.As("bHead", s => s.Get<IApprovalDelegations>().MineAsync()));
        Assert.ThrowsAny<Exception>(() => b.As("procAdmin", s => s.Get<IApprovalDelegations>().ListAsync(a.W.Org1, false)).GetAwaiter().GetResult());
        Assert.Empty(await b.W.AsScopeless().Run(s => s.Get<IApprovalDelegations>().ActiveAsync(a.W.Org1, new[] { "SharedName" }, a.W.Now)));
        Assert.Equal(1, await a.W.AsScopeless().Run(s => s.Get<ApprovalsDbContext>().Delegations.CountAsync()));

        // B's administrators cannot simulate, read or end anything in A's organisation.
        var simulate = new SimulationRequest(a.W.Org1, "flow", Facts(), await a.DescribeAsync(aContract), null, null, MatrixSource.Published);
        var denied = await Assert.ThrowsAnyAsync<Exception>(() => b.As("tenantAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(simulate)));
        Assert.True(denied is ForbiddenException or ResourceNotFoundException, denied.GetType().Name);
        await Assert.ThrowsAnyAsync<Exception>(() => b.As("tenantAdmin", s => s.Get<IApprovalSimulator>().MatricesAsync(a.W.Org1, MatrixSource.Published)));
        var delegationId = (await a.As("aHead", s => s.Get<IApprovalDelegations>().MineAsync())).Single().Id;
        await Assert.ThrowsAnyAsync<Exception>(() => b.As("tenantAdmin", s => s.Get<IApprovalDelegations>().RevokeAsync(delegationId, "not yours")));
        Assert.True((await a.As("aHead", s => s.Get<IApprovalDelegations>().MineAsync())).Single().IsActive);
        _ = aHead;
    }
}
