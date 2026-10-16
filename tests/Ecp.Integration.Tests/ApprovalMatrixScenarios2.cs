using Ecp.Approvals;
using Ecp.Approvals.Domain;
using Ecp.Approvals.Resolution;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecp.Integration.Tests.ApprovalMatrixWorld;

namespace Ecp.Integration.Tests;

/// <summary>Approval under a matrix, end to end through the real lifecycle: who may respond when, what the plan records, and that the reference model is untouched.</summary>
internal static class ApprovalMatrixScenarios2
{
    private const string FlowRows =
        "{ \"code\": \"standard\", \"kind\": \"Base\", \"priority\": 100, \"flow\": \"Sequential\", \"when\": { \"valueMin\": 0, \"valueMax\": 100000 }, \"steps\": [ STEP_FIRST, STEP_SECOND ] }," +
        "{ \"code\": \"large\", \"kind\": \"Base\", \"priority\": 100, \"flow\": \"Parallel\", \"when\": { \"valueMin\": 100000 }, \"steps\": [ STEP_FINANCE, STEP_TIER ] }";

    private static string Flow(string firstGroup = "TierA", string secondGroup = "TierB")
        => Doc(FlowRows.Replace("STEP_FIRST", Step("first", firstGroup), StringComparison.Ordinal).Replace("STEP_SECOND", Step("second", secondGroup), StringComparison.Ordinal)
            .Replace("STEP_FINANCE", Step("finance", "Finance", "AllRequired"), StringComparison.Ordinal).Replace("STEP_TIER", Step("tier", "TierA"), StringComparison.Ordinal));

    private static async Task<LifecycleWorld> FlowWorldAsync(TestApp app, string? firstGroup = null, string? secondGroup = null)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.ApproverAsync("tierA", "TierA");
        await w.ApproverAsync("tierB", "TierB");
        await w.ApproverAsync("fin1", "Finance");
        await w.ApproverAsync("fin2", "Finance");
        await w.ApproverAsync("fin3", "Finance", backup: true);
        await w.PublishMatrixAsync(Flow(firstGroup ?? "TierA", secondGroup ?? "TierB"));
        await w.UseMatrixAsync("flow");
        return w;
    }

    private static Task Approve(LifecycleWorld w, string user, Guid request, string comment = "Approved") => w.As(user, s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, comment));
    private static Task<ApprovalDetail> Detail(LifecycleWorld w, string user, Guid request) => w.As(user, s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
    private static async Task<Exception?> Capture(Func<Task> work) { try { await work(); return null; } catch (Exception ex) { return ex; } }

    public static async Task A_two_stage_approval_opens_each_stage_in_turn_and_registers_only_at_the_end(TestApp app)
    {
        var w = await FlowWorldAsync(app);
        var id = await w.NewDraftAsync(legal: false);
        var request = await w.ToAwaitingApprovalAsync(id, legal: false);

        var start = await Detail(w, "tierA", request);
        var plan = start.Plan!;
        Assert.Equal(("flow", "standard"), (plan.MatrixCode, start.Plan!.Steps[0].RowCode));
        Assert.Equal(["Open", "Waiting"], plan.Steps.Select(s => s.Status).ToArray());
        Assert.Equal("first", plan.YourPart);
        Assert.True(start.CanApprove);
        Assert.Null(plan.Trace);                                                           // the trace and the reasons are for matrix administrators, not for approvers
        Assert.Equal(1, plan.Release);

        // Out of turn, outside the plan, or the person who submitted it: none of them can respond.
        Assert.Contains((await Capture(() => Approve(w, "tierB", request)))!.GetType().Name, new[] { nameof(ForbiddenException), nameof(ResourceNotFoundException) });
        await Assert.ThrowsAnyAsync<Exception>(() => Approve(w, "groupHead", request));          // the reference model's approver is not part of this plan
        await Assert.ThrowsAnyAsync<Exception>(() => Approve(w, "ownerPackaging", request));
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));

        await Approve(w, "tierA", request);
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));                         // not registered yet
        var facts = await w.FactsAsync(id);
        Assert.Null(facts.RegisteredDateUtc);
        Assert.False(facts.RecordLocked && facts.Status == "Effective");
        var mid = await Detail(w, "tierB", request);
        Assert.Equal(ApprovalStatus.Pending, mid.Status);
        Assert.Equal(["Satisfied", "Open"], mid.Plan!.Steps.Select(s => s.Status).ToArray());
        Assert.Equal("second", mid.Plan.YourPart);
        Assert.Single(mid.Plan.Steps[0].Responses);
        // The first approver's access ends with their step: the contract is no longer theirs to open, and they cannot respond twice.
        await Assert.ThrowsAnyAsync<Exception>(() => Detail(w, "tierA", request));
        await Assert.ThrowsAnyAsync<Exception>(() => Approve(w, "tierA", request));
        // The second stage's approvers were told when it opened.
        await w.RunJobsAsync();
        Assert.Contains(await w.InboxAsync("tierB"), n => n.EventType == "approval.submitted");

        await Approve(w, "tierB", request, "Final");
        Assert.Equal("Effective", await w.StatusAsync(id));
        var done = await w.FactsAsync(id);
        Assert.NotNull(done.RegisteredDateUtc);
        Assert.True(done.RecordLocked);
        var finished = await w.As("procAdmin", s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
        Assert.Equal(ApprovalStatus.Approved, finished.Status);
        Assert.Equal(["Satisfied", "Satisfied"], finished.Plan!.Steps.Select(s => s.Status).ToArray());
        Assert.Equal(["tierA", "tierB"], finished.Plan.Steps.SelectMany(s => s.Responses).Select(r => r.Name).ToArray());
        Assert.NotNull(finished.Plan.Trace);                                                // a matrix administrator sees how it was reached
        // Everything about the decision is in the audit trail, including the matrix and the plan checksum.
        var events = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "approval.step_approved" || e.Action == "approval.approved").Select(e => e.Action + "|" + e.NewValuesJson).ToListAsync());
        Assert.Contains(events, e => e.StartsWith("approval.step_approved|", StringComparison.Ordinal) && e.Contains("first", StringComparison.Ordinal));
        Assert.Contains(events, e => e.StartsWith("approval.approved|", StringComparison.Ordinal) && e.Contains("planSha256", StringComparison.Ordinal));
    }

    public static async Task A_rejection_at_any_open_step_returns_the_contract_and_ends_the_plan(TestApp app)
    {
        var w = await FlowWorldAsync(app);
        var id = await w.NewDraftAsync(legal: false);
        var request = await w.ToAwaitingApprovalAsync(id, legal: false);
        await Approve(w, "tierA", request);
        var empty = await Assert.ThrowsAsync<DomainException>(() => w.As("tierB", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, " ")));
        Assert.Equal("comment_required", empty.Code);
        await w.As("tierB", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, "Price is not justified"));
        Assert.Equal("InitialCreation", await w.StatusAsync(id));
        var detail = await w.As("procAdmin", s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
        Assert.Equal(ApprovalStatus.Rejected, detail.Status);
        Assert.Equal("Rejected", detail.Plan!.Steps[1].Status);
        Assert.Null((await w.FactsAsync(id)).RegisteredDateUtc);
        // Nobody can respond to a decided request, and the same contract can be submitted again under a fresh plan.
        var late = await Assert.ThrowsAsync<DomainException>(() => Approve(w, "tierB", request));
        Assert.Equal("already_decided", late.Code);
        var again = await w.As("ownerPackaging", s => s.Get<IExecutionService>().SendForSignatureAsync(SubjectKinds.Contract, id, "manual"));
        Assert.NotNull(again);
    }

    public static async Task Parallel_steps_open_together_an_all_required_step_needs_every_member_and_a_delegate_covers_one(TestApp app)
    {
        var w = await FlowWorldAsync(app);
        // fin2 is away and hands their seat to fin3 (a backup in the Finance group). The delegation is recorded before the request is made, so the plan names fin3 as fin2's delegate.
        var fin3 = w.W.Users["fin3"]; var fin2 = w.W.Users["fin2"];
        await w.As("fin2", s => s.Get<IApprovalDelegations>().CreateAsync(new CreateDelegationRequest(w.W.Org1, "Finance", fin3, w.W.Now, w.W.Now.AddDays(14), "On leave")));
        var id = await w.NewDraftAsync(legal: false, value: 250000m);
        var request = await w.ToAwaitingApprovalAsync(id, legal: false);

        var start = await Detail(w, "fin1", request);
        Assert.Equal("large", start.Plan!.Steps[0].RowCode);
        Assert.Equal(["Open", "Open"], start.Plan.Steps.Select(s => s.Status).ToArray());                // finance and the tier are open at once
        var finance = start.Plan.Steps.Single(s => s.Key.EndsWith("/finance", StringComparison.Ordinal));
        Assert.Equal(["fin1", "fin2", "fin3"], finance.Approvers.Select(a => a.Name).OrderBy(x => x).ToArray());
        Assert.Equal("Delegate", finance.Approvers.Single(a => a.Name == "fin3").Role);
        Assert.Equal("fin2", finance.Approvers.Single(a => a.Name == "fin3").OnBehalfOfName);
        Assert.Equal(["fin1", "fin2"], finance.Outstanding.OrderBy(x => x).ToArray());

        await Approve(w, "fin1", request);
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));
        var repeat = await Capture(() => Approve(w, "fin1", request));
        Assert.NotNull(repeat);                                                              // a person responds once to a step
        await Approve(w, "tierA", request);                                                  // the other open step, in either order
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));                           // fin2's seat is still open
        var half = await Detail(w, "fin3", request);
        Assert.Equal(["Open", "Satisfied"], half.Plan!.Steps.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Status).ToArray());
        Assert.Equal("finance (on behalf of fin2)", half.Plan.YourPart);

        await Approve(w, "fin3", request, "Covering for fin2");
        Assert.Equal("Effective", await w.StatusAsync(id));
        var done = await w.As("procAdmin", s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
        var recorded = done.Plan!.Steps.Single(s => s.Key.EndsWith("/finance", StringComparison.Ordinal)).Responses;
        Assert.Equal(2, recorded.Count);
        var onBehalf = Assert.Single(recorded, r => r.Name == "fin3");
        Assert.Equal(("Delegate", "fin2"), (onBehalf.Role, onBehalf.OnBehalfOfName));
        _ = fin2;
    }

    public static async Task A_backup_without_a_delegation_cannot_complete_an_all_required_step(TestApp app)
    {
        var w = await FlowWorldAsync(app);
        var id = await w.NewDraftAsync(legal: false, value: 250000m);
        var request = await w.ToAwaitingApprovalAsync(id, legal: false);
        var detail = await Detail(w, "fin1", request);
        var finance = detail.Plan!.Steps.Single(s => s.Key.EndsWith("/finance", StringComparison.Ordinal));
        Assert.DoesNotContain(finance.Approvers, a => a.Name == "fin3");                    // backups are left out of an all-required step that does not allow them
        await Assert.ThrowsAnyAsync<Exception>(() => Approve(w, "fin3", request));
        await Approve(w, "fin1", request); await Approve(w, "tierA", request);
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));
        await Approve(w, "fin2", request);
        Assert.Equal("Effective", await w.StatusAsync(id));
    }

    public static async Task A_submission_nobody_can_approve_is_blocked_with_the_reason_and_can_be_resubmitted_once_fixed(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.ApproverAsync("tierA", "TierA");
        await w.PublishMatrixAsync(Doc(Row("only", "{}", Step("a", "TierA") + "," + Step("b", "NobodyHoldsThis"))));
        await w.UseMatrixAsync("flow");
        var id = await w.NewDraftAsync(legal: false);
        var blocked = await Assert.ThrowsAsync<DomainException>(() => w.ToAwaitingApprovalAsync(id, legal: false));
        Assert.Equal("approval_blocked", blocked.Code);
        Assert.Contains("nobody holds the approver group 'NobodyHoldsThis'", blocked.Message, StringComparison.Ordinal);
        Assert.Equal("OutForSignature", await w.StatusAsync(id));                           // nothing moved
        Assert.Empty(await w.W.AsScopeless().Run(s => s.Get<Ecp.Lifecycle.Persistence.LifecycleDbContext>().Approvals.ToListAsync()));
        var denied = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "approval.resolution_blocked").Select(e => e.Reason).ToListAsync());
        Assert.Contains(denied, r => r!.Contains("NobodyHoldsThis", StringComparison.Ordinal));

        await w.ApproverAsync("fixer", "NobodyHoldsThis");
        var request = await w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "Now there is an approver"));
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));
        Assert.NotEqual(Guid.Empty, request);
    }

    public static async Task A_plan_is_pinned_to_the_release_it_was_made_under(TestApp app)
    {
        var w = await FlowWorldAsync(app);
        await w.ApproverAsync("tierC", "TierC");
        var id = await w.NewDraftAsync(legal: false);
        var request = await w.ToAwaitingApprovalAsync(id, legal: false);
        // The matrix changes after submission: the second stage now belongs to a different group.
        var second = await w.PublishMatrixAsync(Flow(secondGroup: "TierC"));
        Assert.Equal(2, second.Number);
        await Approve(w, "tierA", request);
        var detail = await Detail(w, "tierB", request);
        Assert.Equal(1, detail.Plan!.Release);                                              // still release 1
        Assert.Equal("TierB", detail.Plan.Steps[1].ApproverGroup);
        await Assert.ThrowsAnyAsync<Exception>(() => Approve(w, "tierC", request));            // the new group has no say in an in-flight request
        await Approve(w, "tierB", request);
        Assert.Equal("Effective", await w.StatusAsync(id));
        // A new request made now uses release 2.
        var next = await w.NewDraftAsync(legal: false);
        var nextRequest = await w.ToAwaitingApprovalAsync(next, legal: false);
        Assert.Equal(2, (await Detail(w, "tierA", nextRequest)).Plan!.Release);
    }

    public static async Task The_simulator_and_a_real_submission_resolve_to_the_same_steps_and_people(TestApp app)
    {
        var w = await FlowWorldAsync(app);
        var id = await w.NewDraftAsync(legal: false, value: 1000m);
        var resource = await w.DescribeAsync(id);
        var simulated = (await w.As("procAdmin", s => s.Get<IApprovalSimulator>().SimulateAsync(new SimulationRequest(w.W.Org1, "flow", Facts(1000m), resource, w.W.Users["ownerPackaging"], null, MatrixSource.Published)))).Resolution;
        Assert.True(simulated.Resolved, simulated.BlockMessage);

        var request = await w.ToAwaitingApprovalAsync(id, legal: false);
        var real = (await Detail(w, "tierA", request)).Plan!;
        Assert.Equal(simulated.Plan!.Steps.Select(x => (x.Key, x.ApproverGroup, x.Stage + 1)).ToArray(), real.Steps.Select(x => (x.Key, x.ApproverGroup, x.Stage)).ToArray());
        Assert.Equal(simulated.Plan.Steps.Select(x => x.Approvers.Select(a => a.UserId).Order().ToArray()).ToArray(), real.Steps.Select(x => x.Approvers.Select(a => a.UserId).Order().ToArray()).ToArray());
        Assert.Equal(simulated.RowCodes, real.Steps.Select(x => x.RowCode).Distinct().ToArray());
        Assert.Equal(simulated.ReleaseNumber, real.Release);
    }

    public static async Task The_last_response_is_decided_once_even_when_two_approvers_answer_at_the_same_moment(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.ApproverAsync("headOne", "Heads");
        await w.ApproverAsync("headTwo", "Heads");
        await w.PublishMatrixAsync(Single("Heads"));
        await w.UseMatrixAsync("flow");
        for (var round = 0; round < 3; round++)
        {
            var id = await w.NewDraftAsync(legal: false);
            var request = await w.ToAwaitingApprovalAsync(id, legal: false);
            var results = await Task.WhenAll(Capture(() => Approve(w, "headOne", request)), Capture(() => Approve(w, "headTwo", request)),
                Capture(() => w.As("headTwo", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, "no"))));
            Assert.Single(results, r => r is null);
            Assert.All(results.Where(r => r is not null), r => Assert.True(r is DomainException { Code: "already_decided" or "concurrency_conflict" or "state_changed" or "invalid_transition" or "plan_settled" }, r!.ToString()));
            var history = await w.As("procAdmin", s => s.Get<ILifecycleService>().HistoryAsync(id));
            Assert.Single(history, h => h.TransitionCode is "Approve" or "Reject");
            Assert.Equal(1, await w.W.AsScopeless().Run(s => s.Get<Ecp.Lifecycle.Persistence.LifecycleDbContext>().StepResponses.CountAsync(x => x.RequestId == request)));
        }
    }

    public static async Task Two_members_of_an_all_required_step_answering_together_are_both_recorded(TestApp app)
    {
        var w = await FlowWorldAsync(app);
        var id = await w.NewDraftAsync(legal: false, value: 250000m);
        var request = await w.ToAwaitingApprovalAsync(id, legal: false);
        await Approve(w, "tierA", request);
        var results = await Task.WhenAll(Capture(() => Approve(w, "fin1", request)), Capture(() => Approve(w, "fin2", request)));
        // One of them may be asked to try again if both reached for the same place in the record; nobody's response is ever lost or counted twice.
        foreach (var failed in results.Select((r, i) => (r, i)).Where(x => x.r is not null))
        {
            Assert.True(failed.r is DomainException { Code: "concurrency_conflict" }, failed.r!.ToString());
            await Approve(w, failed.i == 0 ? "fin1" : "fin2", request);
        }
        Assert.Equal("Effective", await w.StatusAsync(id));
        Assert.Equal(3, await w.W.AsScopeless().Run(s => s.Get<Ecp.Lifecycle.Persistence.LifecycleDbContext>().StepResponses.CountAsync(x => x.RequestId == request)));
    }

    public static async Task The_reference_model_is_unchanged_when_no_matrix_is_named(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.NewDraftAsync(legal: false);
        var request = await w.ToAwaitingApprovalAsync(id, legal: false);
        var detail = await Detail(w, "groupHead", request);
        Assert.Null(detail.Plan);
        Assert.Equal(LifecycleWorld.ApproverGroup, detail.ApproverGroupKey);
        var row = await w.W.AsScopeless().Run(s => s.Get<Ecp.Lifecycle.Persistence.LifecycleDbContext>().Approvals.AsNoTracking().SingleAsync(a => a.Id == request));
        Assert.False(row.HasPlan);
        Assert.Null(row.PlanJson); Assert.Null(row.MatrixCode); Assert.Null(row.ActiveGroupKeys);
        Assert.Equal([LifecycleWorld.ApproverGroup], row.PendingGroups);
        Assert.Equal(0, await w.W.AsScopeless().Run(s => s.Get<Ecp.Lifecycle.Persistence.LifecycleDbContext>().StepResponses.CountAsync()));
        // The definition shipped for the reference model names no matrix at all.
        var def = await w.W.AsScopeless().Run(s => s.Get<IWorkflowDefinitions>().CurrentAsync(WorkflowKinds.ContractLifecycle, w.W.Org1));
        Assert.Null(def.Definition.Settings.Approval.Matrix);
        // Primary and backup answer exactly as before: the backup on behalf of the single primary, first response wins.
        await w.As("backupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "backup"));
        var done = await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
        Assert.Equal((ApprovalStatus.Approved, "BackupApprover"), (done.Status, done.Decision!.DecidedAsRole));
        Assert.Equal("groupHead", done.Decision.OnBehalfOfName is null ? null : done.Decision.OnBehalfOfName);
        Assert.Equal("Effective", await w.StatusAsync(id));
        // And a matrix may exist in the organisation without touching it.
        await w.ApproverAsync("tierA", "TierA");
        await w.PublishMatrixAsync(Single("TierA"));
        var other = await w.NewDraftAsync(legal: false);
        var otherRequest = await w.ToAwaitingApprovalAsync(other, legal: false);
        Assert.Null((await Detail(w, "groupHead", otherRequest)).Plan);
    }

    public static async Task A_matrix_that_models_the_reference_choice_names_exactly_the_same_people(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishMatrixAsync(Single(LifecycleWorld.ApproverGroup));
        var id = await w.NewDraftAsync(legal: false);
        var legacy = await w.As("procAdmin", s => s.Get<IRegisterAuthorisationService>().EligibleApproversAsync(LifecycleWorld.ApproverGroup));
        var resolved = await w.ResolveAsync("flow", Facts(), await w.DescribeAsync(id), w.W.Users["ownerPackaging"]);
        Assert.True(resolved.Resolved, resolved.BlockMessage);
        Assert.Equal(legacy.Select(l => l.UserId).OrderBy(x => x).ToArray(), resolved.Plan!.Steps.Single().Approvers.Select(a => a.UserId).OrderBy(x => x).ToArray());
        Assert.Equal(legacy.Where(l => l.IsBackup).Select(l => l.UserId).ToArray(), resolved.Plan.Steps.Single().Approvers.Where(a => a.Role == ParticipantRoles.Backup).Select(a => a.UserId).ToArray());
        Assert.Equal("FirstResponse", resolved.Plan.Steps.Single().Mode);
        Assert.Single(resolved.Plan.Steps);
    }

    public static async Task A_Variation_is_approved_through_the_matrix_its_workflow_names(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.ApproverAsync("tierA", "TierA");
        await w.ApproverAsync("tierB", "TierB");
        var id = await w.ToEffectiveInOrderAsync(end: new DateOnly(2027, 12, 31));
        await w.PublishMatrixAsync(Flow());
        await w.UseMatrixAsync("flow", WorkflowKinds.Variation);
        // The proposed value (2,500) is what the matrix reads, and it is small: the two-stage row applies.
        var (variation, request) = await w.VariationToApprovalAsync(id, LifecycleWorld.SimpleVariation(value: "2500"));
        var detail = await Detail(w, "tierA", request);
        Assert.Equal("standard", detail.Plan!.Steps[0].RowCode);
        Assert.Equal(2, detail.Plan.Steps.Count);
        await Approve(w, "tierA", request);
        Assert.Equal(1000m, (await w.FactsAsync(id)).EstimatedValue);                       // nothing applied after the first stage
        await Approve(w, "tierB", request);
        Assert.Equal(2500m, (await w.FactsAsync(id)).EstimatedValue);                       // applied at the end
        Assert.Equal("Effective", await w.StatusAsync(id));
        Assert.Equal(VariationOutcome.Applied, (await w.As("ownerPackaging", s => s.Get<IVariationService>().GetAsync(variation))).Summary.Outcome);
    }

    public static async Task Row_conditions_on_classification_and_exceptions_shape_who_approves(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.ApproverAsync("manager", "Managers");
        await w.ApproverAsync("exec", "Executives", ceiling: "Restricted");
        await w.ApproverAsync("sustain", "Sustainability", ceiling: "Restricted");
        await w.ApproverAsync("lowCeil", "LowCeilGroup");
        await w.PublishMatrixAsync(Doc(
            Row("base", "{}", Step("manager", "Managers")) + "," +
            Row("restricted", "{ \"classifications\": [\"Restricted\"] }", Step("exec", "Executives"), priority: 300, kind: "Exception", effect: "ReplaceSteps") + "," +
            Row("energy", "{ \"categories\": [\"ENERGY\"] }", Step("sustain", "Sustainability"), priority: 200, kind: "Exception", effect: "AddSteps") + "," +
            Row("low", "{ \"categories\": [\"LOW\"] }", Step("low", "LowCeilGroup"), priority: 150, kind: "Exception", effect: "AddSteps")));
        var internalRecord = await w.NewDraftAsync();
        var restrictedRecord = await w.NewDraftAsync();
        await w.W.AsScopeless().Run(async s => { await s.Get<Ecp.Contracts.IContractLifecyclePort>().ApplyControlledChangesAsync(restrictedRecord, new Dictionary<string, string?> { ["classification"] = "Restricted" }, ["classification"], "test", "unlock", Guid.NewGuid(), "test"); return 0; });
        var plain = await w.ResolveAsync("flow", Facts(category: ["PACKAGING"]), await w.DescribeAsync(internalRecord), null);
        Assert.Equal(["base/manager"], plain.Plan!.Steps.Select(s => s.Key).ToArray());
        var energy = await w.ResolveAsync("flow", Facts(category: ["ENERGY-ELEC", "ENERGY"]), await w.DescribeAsync(internalRecord), null);
        Assert.Equal(["base/manager", "energy/sustain"], energy.Plan!.Steps.Select(s => s.Key).ToArray());
        // A Restricted record replaces the base steps with the executive group, whose members hold a Restricted ceiling; an extra exception still adds its step after.
        var restricted = await w.ResolveAsync("flow", Facts(category: ["ENERGY"], classification: "Restricted"), await w.DescribeAsync(restrictedRecord), null);
        Assert.Equal(["restricted/exec", "energy/sustain"], restricted.Plan!.Steps.Select(s => s.Key).ToArray());
        Assert.Contains(restricted.Trace, t => t.Stage == TraceStages.Exception && t.Code == "restricted" && t.Message.Contains("replaces the steps of 'base'", StringComparison.Ordinal));
        // An added step whose only member's ceiling is below Restricted has nobody for a Restricted record, so the whole submission is blocked rather than that step being skipped.
        var blocked = await w.ResolveAsync("flow", Facts(category: ["LOW"], classification: "Restricted"), await w.DescribeAsync(restrictedRecord), null);
        Assert.False(blocked.Resolved);
        Assert.Equal("missing_approvers", blocked.BlockCode);
        Assert.Contains(blocked.Explanations, e => e.Name == "lowCeil" && !e.Included);
        Assert.True((await w.ResolveAsync("flow", Facts(category: ["LOW"]), await w.DescribeAsync(internalRecord), null)).Resolved);          // the same step is fine for an Internal record
    }
}
