using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Notifications;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Emergency unlock, Variations, renewals, pinning and cross-tenant attempts.</summary>
internal static class LifecycleScenarios3
{
    private static Task<T> AsRecent<T>(LifecycleWorld w, string user, Func<TenantScope, Task<T>> work)
        => w.As(user, s => { s.Get<ReauthenticationContext>().Set(w.W.Now); return work(s); });

    public static async Task A_force_unlock_needs_permission_recent_sign_in_reason_and_a_time_limit_and_is_fully_audited(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToEffectiveInOrderAsync();
        var before = await w.FactsAsync(id);
        var approval = (await w.As("procAdmin", s => s.Get<IRegisterAuthorisationService>().HistoryAsync(id))).Single();
        var snapshotBefore = (await w.As("procAdmin", s => s.Get<IRegisterAuthorisationService>().GetAsync(approval.RequestId))).SnapshotSha256;
        var fields = new[] { "contractTitle", "description" };
        var reason = "Supplier trading name changed; correcting the title before the audit.";

        // Not without the permission, not without a recent sign-in, not without a real reason or a bounded time.
        await Assert.ThrowsAsync<ForbiddenException>(() => AsRecent(w, "ownerPackaging", s => s.Get<IRecordUnlockService>().OpenAsync(new UnlockRequest(id, reason, 30, fields))));
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.As("procAdmin", s => s.Get<IRecordUnlockService>().OpenAsync(new UnlockRequest(id, reason, 30, fields))));
        Assert.Equal("reason_too_short", (await Assert.ThrowsAsync<DomainException>(() => AsRecent(w, "procAdmin", s => s.Get<IRecordUnlockService>().OpenAsync(new UnlockRequest(id, "typo", 30, fields))))).Code);
        Assert.Equal("invalid_duration", (await Assert.ThrowsAsync<DomainException>(() => AsRecent(w, "procAdmin", s => s.Get<IRecordUnlockService>().OpenAsync(new UnlockRequest(id, reason, 24 * 60, fields))))).Code);
        Assert.Equal("field_not_changeable", (await Assert.ThrowsAsync<DomainException>(() => AsRecent(w, "procAdmin", s => s.Get<IRecordUnlockService>().OpenAsync(new UnlockRequest(id, reason, 30, ["status"]))))).Code);
        Assert.Equal("fields_required", (await Assert.ThrowsAsync<DomainException>(() => AsRecent(w, "procAdmin", s => s.Get<IRecordUnlockService>().OpenAsync(new UnlockRequest(id, reason, 30, []))))).Code);
        Assert.Null((await w.FactsAsync(id)).ActiveUnlockId);
        // Changing anything before an unlock is open is refused.
        Assert.Equal("no_open_unlock", (await Assert.ThrowsAsync<DomainException>(() => AsRecent(w, "procAdmin", s => s.Get<IRecordUnlockService>().ApplyChangesAsync(id, new Dictionary<string, string?> { ["contractTitle"] = "x" })))).Code);

        var unlock = await AsRecent(w, "procAdmin", s => s.Get<IRecordUnlockService>().OpenAsync(new UnlockRequest(id, reason, 30, fields)));
        Assert.True(unlock.IsOpen);
        Assert.Equal(unlock.Id, (await w.FactsAsync(id)).ActiveUnlockId);
        Assert.Equal(unlock.Id, (await w.As("ownerPackaging", s => s.Get<ISecuredContractService>().GetAsync(id))).Protection!.ActiveUnlockId);   // the record shows its state to everyone who can read it
        // Only the fields it names, only by the person who opened it.
        await Assert.ThrowsAsync<DomainException>(() => w.As("procAdmin", s => s.Get<IRecordUnlockService>().ApplyChangesAsync(id, new Dictionary<string, string?> { ["endDate"] = "2030-01-01" })));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<IRecordUnlockService>().ApplyChangesAsync(id, new Dictionary<string, string?> { ["contractTitle"] = "Owner edit" })));
        var changed = await w.As("procAdmin", s => s.Get<IRecordUnlockService>().ApplyChangesAsync(id, new Dictionary<string, string?> { ["contractTitle"] = "Corrected trading name" }));
        Assert.Equal("contractTitle", Assert.Single(changed).Field);

        // Status, Registered Date and the original approval are untouched; the lock is still the lock for everything else.
        var during = await w.FactsAsync(id);
        Assert.Equal("Corrected trading name", during.Title);
        Assert.Equal(before.Status, during.Status);
        Assert.Equal(before.RegisteredDateUtc, during.RegisteredDateUtc);
        Assert.Equal(snapshotBefore, (await w.As("procAdmin", s => s.Get<IRegisterAuthorisationService>().GetAsync(approval.RequestId))).SnapshotSha256);
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<ISecuredContractService>().UpdateDetailsAsync(id, "Sneaky", null)));

        // Every change carries the unlock id in the audit trail, and opening and closing are recorded.
        var events = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.ElevationId == unlock.Id).Select(e => e.Action).ToListAsync());
        Assert.Contains("unlock.opened", events);
        Assert.Contains("contract.fields_changed", events);

        // At the time limit it closes by itself and the record is locked again.
        w.App.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal("no_open_unlock", (await Assert.ThrowsAsync<DomainException>(() => w.As("procAdmin", s => s.Get<IRecordUnlockService>().ApplyChangesAsync(id, new Dictionary<string, string?> { ["contractTitle"] = "Late" })))).Code);
        Assert.Equal(1, await w.W.AsScopeless().Run(s => s.Get<IRecordUnlockService>().CloseExpiredAsync()));
        Assert.Null((await w.FactsAsync(id)).ActiveUnlockId);
        Assert.Equal(0, await w.W.AsScopeless().Run(s => s.Get<IRecordUnlockService>().CloseExpiredAsync()));          // idempotent
        var history = await w.As("procAdmin", s => s.Get<IRecordUnlockService>().HistoryAsync(id));
        Assert.Equal("Expired", Assert.Single(history).CloseKind);
        Assert.Equal(1, history[0].ChangeCount);

        // An early close works too, and a second unlock can follow.
        var second = await AsRecent(w, "procAdmin", s => s.Get<IRecordUnlockService>().OpenAsync(new UnlockRequest(id, reason + " Again.", 15, fields)));
        await w.As("procAdmin", s => s.Get<IRecordUnlockService>().CloseAsync(id));
        Assert.Equal("Early", (await w.As("procAdmin", s => s.Get<IRecordUnlockService>().HistoryAsync(id))).First(h => h.Id == second.Id).CloseKind);
        Assert.Null((await w.FactsAsync(id)).ActiveUnlockId);
    }

    public static async Task A_Variation_changes_an_effective_contract_only_when_approved_and_keeps_before_and_after(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToEffectiveInOrderAsync(end: new DateOnly(2027, 12, 31));
        var beforeFacts = await w.FactsAsync(id);
        var (variation, request) = await w.VariationToApprovalAsync(id);

        // While it is open the parent stays locked, in its own state, and a second Variation cannot start.
        var parent = await w.FactsAsync(id);
        Assert.Equal("VariationInProgress", parent.Status);
        Assert.True(parent.RecordLocked);
        Assert.Equal(beforeFacts.EndDate, parent.EndDate);
        Assert.Equal(beforeFacts.EstimatedValue, parent.EstimatedValue);
        var second = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, LifecycleWorld.SimpleVariation())));
        Assert.Equal("invalid_transition", second.Code);
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<ISecuredContractService>().UpdateDetailsAsync(id, "Edited", null)));

        // The comparison shows what is proposed against what is in force.
        var pending = await w.As("ownerPackaging", s => s.Get<IVariationService>().GetAsync(variation));
        var endRow = pending.Comparison.Single(c => c.Field == "endDate");
        Assert.Equal("2027-12-31", endRow.Before);
        Assert.Equal("2031-06-30", endRow.After);
        Assert.True(endRow.Changed);

        // Self-approval, and an owner's attempt to push it through, fail; the approver applies it.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "me")));
        w.App.Clock.Advance(TimeSpan.FromHours(1));
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "Approved"));
        var after = await w.FactsAsync(id);
        Assert.Equal("Effective", after.Status);
        Assert.True(after.RecordLocked);
        Assert.Equal(new DateOnly(2031, 6, 30), after.EndDate);
        Assert.Equal(2500m, after.EstimatedValue);
        Assert.Equal(beforeFacts.RegisteredDateUtc, after.RegisteredDateUtc);                 // the original registration is not re-stamped
        var done = await w.As("ownerPackaging", s => s.Get<IVariationService>().GetAsync(variation));
        Assert.Equal(VariationOutcome.Applied, done.Summary.Outcome);
        Assert.False(done.Summary.IsOpen);
        var applied = done.Comparison.Single(c => c.Field == "estimatedValue");
        Assert.Equal("1000.00", applied.Before is null ? null : decimal.Parse(applied.Before, System.Globalization.CultureInfo.InvariantCulture).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("2500", applied.After);
        // The amendment is in the contract's history and the audit trail names the Variation.
        var amendments = await w.As("ownerPackaging", s => s.Get<IVariationService>().ListForContractAsync(id));
        Assert.Equal(VariationOutcome.Applied, Assert.Single(amendments).Outcome);
        var audit = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "variation.applied" || e.Action == "contract.fields_changed").Select(e => e.Action + "|" + e.NewValuesJson).ToListAsync());
        Assert.Contains(audit, a => a.StartsWith("contract.fields_changed|", StringComparison.Ordinal) && a.Contains("variation", StringComparison.Ordinal));
        Assert.Contains(audit, a => a.StartsWith("variation.applied|", StringComparison.Ordinal));
        // Another Variation can follow now.
        await w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, LifecycleWorld.SimpleVariation(value: "3000")));
    }

    public static async Task Cancelling_or_rejecting_a_Variation_leaves_the_parent_exactly_as_it_was(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToEffectiveInOrderAsync(end: new DateOnly(2027, 12, 31));
        var before = await w.FactsAsync(id);

        var cancelled = await w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, LifecycleWorld.SimpleVariation()));
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().CancelAsync(cancelled, " ")));
        await w.As("ownerPackaging", s => s.Get<IVariationService>().CancelAsync(cancelled, "Supplier withdrew the proposal"));
        var afterCancel = await w.FactsAsync(id);
        Assert.Equal("Effective", afterCancel.Status);
        Assert.Equal(before.EndDate, afterCancel.EndDate);
        Assert.Equal(before.EstimatedValue, afterCancel.EstimatedValue);
        Assert.Equal(before.RegisteredDateUtc, afterCancel.RegisteredDateUtc);
        Assert.True(afterCancel.RecordLocked);
        var detail = await w.As("ownerPackaging", s => s.Get<IVariationService>().GetAsync(cancelled));
        Assert.Equal(VariationOutcome.Cancelled, detail.Summary.Outcome);
        Assert.Equal("Supplier withdrew the proposal", detail.OutcomeReason);
        await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().UpdateAsync(cancelled, LifecycleWorld.SimpleVariation())));       // a finished Variation is closed

        var (rejected, request) = await w.VariationToApprovalAsync(id, LifecycleWorld.SimpleVariation(value: "9999"));
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, "")));
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, "Price increase is not justified"));
        var afterReject = await w.FactsAsync(id);
        Assert.Equal("Effective", afterReject.Status);
        Assert.Equal(before.EstimatedValue, afterReject.EstimatedValue);
        Assert.Equal(VariationOutcome.Rejected, (await w.As("ownerPackaging", s => s.Get<IVariationService>().GetAsync(rejected))).Summary.Outcome);

        // A Variation may name only the fields the Variation workflow allows, and invalid values are refused when drafted.
        var bad = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, LifecycleWorld.SimpleVariation() with { ProposedChanges = new Dictionary<string, string?> { ["contractTitle"] = "Renamed" } })));
        Assert.Equal("field_not_changeable", bad.Code);
        Assert.Equal("Effective", await w.StatusAsync(id));
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, LifecycleWorld.SimpleVariation(value: "-5"))));
        Assert.Equal("Effective", await w.StatusAsync(id));
    }

    public static async Task A_Variation_that_needs_Legal_goes_through_Legal_rounds_before_it_can_be_signed(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToEffectiveInOrderAsync();
        var variation = await w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, LifecycleWorld.SimpleVariation(legal: true)));
        var skip = await Assert.ThrowsAsync<TransitionBlockedException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().TransitionAsync(variation, "ProceedWithoutLegal", null)));
        Assert.Contains("Legal", skip.Message);
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Variation, variation, "Please review the deed")));
        var review = (await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Variation, variation)))!;
        Assert.Equal(id, review.ContractId);
        await w.As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(review.ReviewId));
        await w.As("legalA", s => s.Get<ILegalReviewService>().ReturnForReworkAsync(review.ReviewId, "Dates are inconsistent"));
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Variation, variation, "Fixed")));
        await w.As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(review.ReviewId));
        await w.As("legalA", s => s.Get<ILegalReviewService>().ClearAsync(review.ReviewId, "Fine"));
        Assert.Equal(2, (await w.As("legalA", s => s.Get<ILegalReviewService>().GetAsync(review.ReviewId))).Rounds.Count);
        await w.As("ownerPackaging", s => s.Get<IVariationService>().TransitionAsync(variation, "PrepareAfterLegal", null));
        Assert.Equal("Prepared", (await w.As("ownerPackaging", s => s.Get<IVariationService>().GetAsync(variation))).Summary.Status);
        // The parent was locked all along and is untouched.
        Assert.Equal("VariationInProgress", await w.StatusAsync(id));
    }

    public static async Task A_renewal_copies_only_what_is_configured_links_both_ways_and_never_alters_the_predecessor(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var predecessor = await w.NewDraftAsync(legal: false, end: new DateOnly(2027, 6, 30), review: new DateOnly(2027, 3, 1), critical: true, notice: 45, lead: 60);
        await w.As("ownerPackaging", s => s.Get<ISecuredContractService>().UpdateDetailsAsync(predecessor, "Supply of film", "Annual film supply for the Springvale plant"));
        await w.AddDocumentAsync(predecessor, "Schedule", "Pricing schedule");
        await w.AddDocumentAsync(predecessor, "Correspondence", "Negotiation email");
        var request = await w.ToAwaitingApprovalAsync(predecessor, legal: false);
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "ok"));
        var before = await w.FactsAsync(predecessor);

        var renewal = await w.As("ownerPackaging", s => s.Get<IRenewalService>().StartAsync(predecessor, "Renew for two years"));
        Assert.Equal(RenewalStatus.InProgress, renewal.Status);
        var parent = await w.FactsAsync(predecessor);
        var successor = await w.FactsAsync(renewal.SuccessorContractId);
        Assert.Equal("RenewalInProgress", parent.Status);
        Assert.Equal(renewal.SuccessorContractId, parent.SuccessorContractId);
        Assert.Equal(predecessor, successor.PredecessorContractId);
        Assert.Equal("Draft", successor.Status);                                              // the configured starting state
        Assert.False(successor.RecordLocked);
        // Only the configured fields came across; dates are the renewal's own; nothing of the predecessor's history did.
        Assert.Equal(before.Description, successor.Description);
        Assert.Equal(before.EstimatedValue, successor.EstimatedValue);
        Assert.Equal(before.NoticePeriodDays, successor.NoticePeriodDays);
        Assert.Equal(before.CriticalNotice, successor.CriticalNotice);
        Assert.Equal(new DateOnly(2027, 7, 1), successor.StartDate);
        Assert.Null(successor.EndDate);
        Assert.Null(successor.ReviewDate);
        Assert.Null(successor.RegisteredDateUtc);
        Assert.NotEqual(before.AgreementReference, successor.AgreementReference);
        var docs = await w.As("ownerPackaging", s => s.Get<ISecuredContractDocuments>().ListAsync(renewal.SuccessorContractId));
        Assert.Contains(docs, d => d.DocumentType == "Schedule");                              // a configured type
        Assert.DoesNotContain(docs, d => d.DocumentType == "Correspondence");                  // not configured
        Assert.DoesNotContain(docs, d => d.IsExecutedCopy);                                    // an executed copy is never carried across
        var history = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().HistoryAsync(renewal.SuccessorContractId));
        Assert.Equal("CreatedAsRenewal", Assert.Single(history).TransitionCode);

        // Links are visible in the register and on the details.
        var list = await w.As("ownerPackaging", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(Ids: [predecessor, renewal.SuccessorContractId])));
        Assert.Equal(renewal.SuccessorContractId, list.Items.Single(i => i.Id == predecessor).SuccessorContractId);
        Assert.Equal(predecessor, list.Items.Single(i => i.Id == renewal.SuccessorContractId).PredecessorContractId);
        var details = await w.As("ownerPackaging", s => s.Get<ISecuredContractService>().GetAsync(renewal.SuccessorContractId));
        Assert.Equal(before.AgreementReference, details.Renewal!.PredecessorReference);
        // Ownership is whoever owns the category now, not who started the renewal.
        var owners = await w.W.AsScopeless().Run(s => s.Get<IContractLifecyclePort>().CurrentOwnersAsync(renewal.SuccessorContractId));
        Assert.Contains(w.W.Users["ownerPackaging"], owners);

        // A second renewal cannot start, and the renewal cannot complete until the successor is registered.
        await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IRenewalService>().StartAsync(predecessor, null)));
        var blocked = await Assert.ThrowsAsync<TransitionBlockedException>(() => w.As("ownerPackaging", s => s.Get<IRenewalService>().CompleteAsync(predecessor, null)));
        Assert.Contains("not been registered", blocked.Message);
        var successorRequest = await w.ToAwaitingApprovalAsync(renewal.SuccessorContractId, legal: false);
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(successorRequest, "ok"));
        await w.As("ownerPackaging", s => s.Get<IRenewalService>().CompleteAsync(predecessor, "Successor registered"));

        // Completing returns the predecessor to Effective with nothing else changed; ending it is a separate, explicit step.
        var after = await w.FactsAsync(predecessor);
        Assert.Equal("Effective", after.Status);
        Assert.Equal(before.EndDate, after.EndDate);
        Assert.Equal(before.ReviewDate, after.ReviewDate);
        Assert.Equal(before.EstimatedValue, after.EstimatedValue);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.RegisteredDateUtc, after.RegisteredDateUtc);
        Assert.Equal(RenewalStatus.Completed, (await w.As("ownerPackaging", s => s.Get<IRenewalService>().ListForContractAsync(predecessor))).Single().Status);
    }

    public static async Task Cancelling_a_renewal_withdraws_the_draft_successor_and_restores_the_predecessor(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var predecessor = await w.ToEffectiveInOrderAsync();
        var renewal = await w.As("ownerPackaging", s => s.Get<IRenewalService>().StartAsync(predecessor, null));
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IRenewalService>().CancelAsync(predecessor, " ")));
        await w.As("ownerPackaging", s => s.Get<IRenewalService>().CancelAsync(predecessor, "Supplier not being retained"));
        Assert.Equal("Effective", await w.StatusAsync(predecessor));
        Assert.Equal("Withdrawn", await w.StatusAsync(renewal.SuccessorContractId));
        Assert.Equal(RenewalStatus.Cancelled, (await w.As("ownerPackaging", s => s.Get<IRenewalService>().ListForContractAsync(predecessor))).Single().Status);
        // A new renewal can start afterwards only if there is no successor; the link is kept as history, so it cannot.
        Assert.NotNull((await w.FactsAsync(predecessor)).SuccessorContractId);
    }

    public static async Task Another_tenants_records_do_not_exist_for_any_lifecycle_call(TestApp app)
    {
        var a = await LifecycleWorld.CreateAsync(app);
        var b = await LifecycleWorld.CreateAsync(app);
        var id = await a.ToEffectiveInOrderAsync();
        var draft = await a.NewDraftAsync();
        await a.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, draft, "x")));
        var review = (await a.As("ownerPackaging", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Contract, draft)))!.ReviewId;
        var approval = (await a.As("procAdmin", s => s.Get<IRegisterAuthorisationService>().HistoryAsync(id))).Single().RequestId;
        var variation = await a.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, LifecycleWorld.SimpleVariation()));

        foreach (var user in new[] { "ownerPackaging", "procAdmin", "legalA", "groupHead", "tenantAdmin" })
        {
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(draft, "WithdrawDraft", "x", "x"))));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<ILifecycleService>().AvailableAsync(id)));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<ILifecycleService>().HistoryAsync(id)));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<ILegalReviewService>().GetAsync(review)));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<ILegalReviewService>().BeginAsync(review)));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<IRegisterAuthorisationService>().GetAsync(approval)));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<IRegisterAuthorisationService>().ApproveAsync(approval, "x")));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<IVariationService>().GetAsync(variation)));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<IVariationService>().CancelAsync(variation, "x")));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<IRenewalService>().StartAsync(id, null)));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<IRecordUnlockService>().CurrentAsync(id)));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => b.As(user, s => s.Get<ISignatureRequestService>().CurrentAsync(SubjectKinds.Contract, id)));
        }
        Assert.Equal("VariationInProgress", await a.StatusAsync(id));
        // The other tenant's queues and dashboards show nothing of this tenant's work.
        Assert.Empty((await b.As("legalA", s => s.Get<ILegalReviewService>().QueueAsync(new LegalQueueFilter()))).Items);
        Assert.Empty((await b.As("groupHead", s => s.Get<IRegisterAuthorisationService>().PendingAsync(false))).Items);
        Assert.Empty((await b.As("ownerPackaging", s => s.Get<IVariationService>().InProgressAsync())).Items);
        foreach (var card in await b.As("procAdmin", s => s.Get<IDashboardService>().OverviewAsync()))
            Assert.DoesNotContain(card.Rows, r => r.ContractId == id || r.ContractId == draft);
    }

    public static async Task A_published_workflow_applies_to_new_contracts_while_existing_ones_stay_pinned_and_an_invalid_one_is_refused(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var old = await w.NewDraftAsync();
        var oldState = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().StateAsync(old));
        var current = await w.W.AsScopeless().Run(s => s.Get<IConfigurationReleases>().LatestAsync(WorkflowKinds.ContractLifecycle, w.W.Org1));
        Assert.Equal(oldState.ReleaseNumber, current!.Number);

        // A definition that breaks the rules cannot be published: an unknown guard, an unlocking transition, a dead end.
        var broken = current.ContentJson.Replace("\"RetentionChecksPass\"", "\"RunSomeScript\"", StringComparison.Ordinal);
        var refusal = await Assert.ThrowsAsync<DomainException>(() => w.W.AsScopeless().Run(s => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(WorkflowKinds.ContractLifecycle, w.W.Org1, broken, "test", "bad")));
        Assert.Equal("validation_failed", refusal.Code);
        Assert.Contains("RunSomeScript", refusal.Message);

        // A good change (a renamed state) is published as a new release.
        var edited = current.ContentJson.Replace("\"Awaiting Legal\"", "\"With Legal\"", StringComparison.Ordinal);
        var release = await w.W.AsScopeless().Run(s => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(WorkflowKinds.ContractLifecycle, w.W.Org1, edited, "test", "Rename the Legal queue"));
        Assert.Equal(current.Number + 1, release.Number);

        var fresh = await w.NewDraftAsync();
        var freshState = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().StateAsync(fresh));
        Assert.Equal(release.Number, freshState.ReleaseNumber);
        Assert.True(freshState.IsPinned);
        // The existing contract keeps the release it was pinned to when created, and still moves.
        var oldAgain = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().StateAsync(old));
        Assert.Equal(oldState.ReleaseNumber, oldAgain.ReleaseNumber);
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, old, "x")));
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, fresh, "x")));
        Assert.Equal("Awaiting Legal", (await w.As("ownerPackaging", s => s.Get<ILifecycleService>().StateAsync(old))).StatusDisplay);
        Assert.Equal("With Legal", (await w.As("ownerPackaging", s => s.Get<ILifecycleService>().StateAsync(fresh))).StatusDisplay);
        var history = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().HistoryAsync(fresh));
        Assert.EndsWith("@" + release.Number, history.Single().WorkflowRef);
    }
}
