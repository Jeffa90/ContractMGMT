using Ecp.Audit;
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

/// <summary>Transitions, approvals, locking, unlocks, Variations and renewals on a real provider.</summary>
internal static class LifecycleScenarios2
{
    private static readonly string[] AllStates =
        ["Draft", "AwaitingLegal", "InLegalReview", "LegalReworkRequired", "LegalCleared", "InitialCreation", "OutForSignature", "AwaitingApproval", "Effective", "VariationInProgress", "RenewalInProgress",
         "Suspended", "Ended", "Terminated", "FlaggedForDisposition", "DisposedArchived", "Withdrawn"];

    /// <summary>Someone who holds the permission a transition needs, so a refusal can only be about the state.</summary>
    private static string ActorFor(string permission) => permission switch
    {
        "legal.review" or "legal.clear" => "legalA",
        "approvals.action" or "disposition.approve" => "groupHead",
        "contracts.suspend" or "contracts.close" or "disposition.request" => "procAdmin",
        _ => "ownerPackaging"
    };

    public static async Task Every_state_offers_exactly_its_transitions_and_every_other_step_is_refused(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var def = (await w.W.AsScopeless().Run(s => s.Get<IWorkflowDefinitions>().CurrentAsync(WorkflowKinds.ContractLifecycle, w.W.Org1))).Definition;
        Assert.Equal(AllStates.Order(), def.States.Select(s => s.Code).Order());
        var refusedAsInvalid = 0;
        foreach (var state in AllStates)
        {
            var id = await w.ToStateAsync(state);
            Assert.Equal(state, await w.StatusAsync(id));
            var offered = (await w.As("procAdmin", s => s.Get<ILifecycleService>().AvailableAsync(id))).Select(t => t.Code).Order().ToArray();
            Assert.Equal(def.From(state).Select(t => t.Code).Order().ToArray(), offered);
            // Every step the definition does not allow from here is refused, with a plain message, whoever holds the permission. Nothing changes.
            foreach (var t in def.Transitions.Where(t => !t.From.Contains(state)))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => w.As(ActorFor(t.Permission), s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, t.Code, "x", "x"))));
                // Who may act is decided first (an approver has no standing on a contract that is not waiting for a decision, and an owner has none on a locked one); otherwise the step is not available from here.
                if (ex is DomainException domain) { Assert.Equal("invalid_transition", domain.Code); Assert.Contains(def.DisplayOf(state), domain.Message); refusedAsInvalid++; }
                else Assert.IsType<ForbiddenException>(ex);
            }
            Assert.Equal(state, await w.StatusAsync(id));
        }
        Assert.True(refusedAsInvalid > 200, $"only {refusedAsInvalid} crafted steps were refused as invalid transitions");
    }

    public static async Task Plain_steps_after_effectiveness_move_the_contract_and_keep_it_locked_with_a_history(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToEffectiveInOrderAsync();
        async Task Step(string user, string code, string? reason = null) => await w.As(user, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, code, reason, reason)));
        // A reason is required where the definition says so.
        var noReason = await Assert.ThrowsAsync<TransitionBlockedException>(() => w.As("procAdmin", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "Suspend"))));
        Assert.Contains("reason", noReason.Message, StringComparison.OrdinalIgnoreCase);
        await Step("procAdmin", "Suspend", "Supplier dispute");
        Assert.True((await w.FactsAsync(id)).RecordLocked);
        await Step("procAdmin", "Reinstate", "Dispute resolved");
        await Step("procAdmin", "End");
        await Step("procAdmin", "FlagEnded", "Retention period over");
        // The person who flagged it cannot approve its disposition; the approver can; nobody else.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("procAdmin", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ArchiveFlagged"))));
        await Step("groupHead", "ArchiveFlagged");
        var facts = await w.FactsAsync(id);
        Assert.Equal("DisposedArchived", facts.Status);
        Assert.True(facts.RecordLocked);
        var history = await w.As("procAdmin", s => s.Get<ILifecycleService>().HistoryAsync(id));
        Assert.Equal(["SubmitApproval", "Approve", "Suspend", "Reinstate", "End", "FlagEnded", "ArchiveFlagged"], history.Select(h => h.TransitionCode).Skip(history.Count - 7).ToArray());
        // Terminal: nothing further.
        Assert.Empty(await w.As("procAdmin", s => s.Get<ILifecycleService>().AvailableAsync(id)));
    }

    public static async Task Register_authorisation_rules_self_approval_backup_rejection_and_registered_date(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        // A person who holds both the owner's role and the approver's still cannot approve what they submitted.
        var dual = await w.UserAsync("dual", RoleCodes.CategoryOwner, null, "Confidential");
        await using (var s = w.W.AsScopeless())
        {
            await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(w.W.CatPackaging, dual, false, w.W.Now.AddDays(-2), null, "test", null);
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(dual, RoleCodes.Approver, null, ScopeSet.ForOrganisations([w.W.Org1], "Confidential"), LifecycleWorld.ApproverGroup, EffectiveFromUtc: w.W.Now.AddDays(-2), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        }
        var id = await w.NewDraftAsync(legal: false, owner: "dual");
        await w.As("dual", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ProceedWithoutLegal")));
        await w.AddDocumentAsync(id, "FinalAgreement", "Final", "dual");
        await w.As("dual", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "manual"));
        await w.As("dual", s => s.Get<ISignatureRequestService>().RecordExecutedCopyAsync(new RecordExecutedCopyRequest(SubjectKinds.Contract, id, "e.pdf", "application/pdf", LifecycleWorld.Bytes("e"))));
        var request = await w.As("dual", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "go"));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("dual", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "me")));
        // Others cannot: another group's approver, Legal, an administrator.
        foreach (var user in new[] { "otherApprover", "legalA", "procAdmin", "ownerEnergy" })
            await Assert.ThrowsAnyAsync<Exception>(() => w.As(user, s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "x")));
        Assert.Null((await w.FactsAsync(id)).RegisteredDateUtc);

        // The backup rejects first; a rejection needs a comment; the primary then cannot approve the same request.
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("backupHead", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, "  ")));
        await w.As("backupHead", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, "Supplier name is wrong on page 3"));
        var late = await Assert.ThrowsAsync<DomainException>(() => w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "late")));
        Assert.Equal("already_decided", late.Code);
        var facts = await w.FactsAsync(id);
        Assert.Equal("InitialCreation", facts.Status);
        Assert.Null(facts.RegisteredDateUtc);
        Assert.False(facts.RecordLocked);
        var detail = await w.As("dual", s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
        Assert.Equal(ApprovalStatus.Rejected, detail.Status);
        Assert.Equal("BackupApprover", detail.Decision!.DecidedAsRole);
        Assert.Equal("Supplier name is wrong on page 3", detail.Decision.Comment);
        Assert.True(detail.SnapshotIntact);

        // After the fix it is submitted again; the primary approves. Registered Date is stamped once, in UTC, and is not the start or effective date.
        await w.As("dual", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "manual"));
        var second = await w.As("dual", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "fixed"));
        Assert.NotEqual(request, second);
        w.App.Clock.Advance(TimeSpan.FromHours(3));
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(second, "ok"));
        facts = await w.FactsAsync(id);
        Assert.Equal("Effective", facts.Status);
        Assert.True(facts.RecordLocked);
        Assert.Equal(w.W.Now, facts.RegisteredDateUtc);
        Assert.Equal(DateTimeKind.Utc, facts.RegisteredDateUtc!.Value.Kind);
        Assert.Null(facts.StartDate);
        Assert.Null(facts.EffectiveDate);
        Assert.Equal("Approver", (await w.As("dual", s => s.Get<IRegisterAuthorisationService>().GetAsync(second))).Decision!.DecidedAsRole);
        // History keeps both requests and both decisions.
        Assert.Equal(2, (await w.As("dual", s => s.Get<IRegisterAuthorisationService>().HistoryAsync(id))).Count);
        // The registration was audited with the actor, and the snapshot of the first request is unchanged.
        var firstAgain = await w.As("dual", s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
        Assert.Equal(detail.SnapshotSha256, firstAgain.SnapshotSha256);
    }

    public static async Task Two_people_deciding_at_once_produce_one_decision_and_one_registration(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        for (var round = 0; round < 3; round++)
        {
            var id = await w.NewDraftAsync(legal: false);
            var request = await w.ToAwaitingApprovalAsync(id, legal: false);
            var results = await Task.WhenAll(
                Capture(() => w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "primary"))),
                Capture(() => w.As("backupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "backup"))),
                Capture(() => w.As("backupHead", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, "reject"))));
            Assert.Single(results, r => r is null);                                                     // exactly one response wins
            Assert.All(results.Where(r => r is not null), r => Assert.True(r is DomainException { Code: "already_decided" or "concurrency_conflict" or "state_changed" or "invalid_transition" }, r!.ToString()));
            var detail = await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
            Assert.NotEqual(ApprovalStatus.Pending, detail.Status);
            var history = await w.As("procAdmin", s => s.Get<ILifecycleService>().HistoryAsync(id));
            Assert.Single(history, h => h.TransitionCode is "Approve" or "Reject");                       // one decision recorded in the status history
            var facts = await w.FactsAsync(id);
            Assert.Equal(detail.Status == ApprovalStatus.Approved, facts.RegisteredDateUtc is not null);
        }
    }

    public static async Task The_same_step_taken_twice_at_once_happens_once(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.NewDraftAsync(legal: false);
        var results = await Task.WhenAll(
            Capture(() => w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ProceedWithoutLegal")))),
            Capture(() => w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ProceedWithoutLegal")))),
            Capture(() => w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "WithdrawDraft", null, "changed my mind")))));
        Assert.Single(results, r => r is null);
        var history = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().HistoryAsync(id));
        Assert.Single(history);
        // The same request id repeated returns the first outcome and writes nothing more.
        var other = await w.NewDraftAsync(legal: false);
        var first = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(other, "ProceedWithoutLegal", null, null, null, "req-1")));
        var again = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(other, "ProceedWithoutLegal", null, null, null, "req-1")));
        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Single(await w.As("ownerPackaging", s => s.Get<ILifecycleService>().HistoryAsync(other)));
        var reused = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(other, "WithdrawInitial", null, "x", null, "req-1"))));
        Assert.Equal("idempotency_conflict", reused.Code);
        // A stale expected state fails clearly.
        var stale = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(other, "WithdrawInitial", null, "x", "Draft"))));
        Assert.Equal("state_changed", stale.Code);
    }

    private static async Task<Exception?> Capture(Func<Task> work)
    {
        try { await work(); return null; }
        catch (Exception ex) { return ex; }
    }

    public static async Task Legal_rounds_are_kept_and_a_privileged_review_is_invisible_to_those_without_the_grant(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.NewDraftAsync();
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, "round one")));
        var review = (await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Contract, id)))!;
        await w.As("legalA", s => s.Get<ILegalReviewService>().AssignAsync(review.ReviewId, w.W.Users["legalB"]));
        await w.As("legalB", s => s.Get<ILegalReviewService>().BeginAsync(review.ReviewId));
        var comment = await w.As("legalB", s => s.Get<ILegalReviewService>().CommentAsync(new AddLegalCommentRequest(review.ReviewId, "Clause 4 is open-ended.")));
        await w.As("legalB", s => s.Get<ILegalReviewService>().AttachAsync(new AddLegalAttachmentRequest(review.ReviewId, LegalAttachmentKind.Markup, "markup-v1.docx", "application/pdf", LifecycleWorld.Bytes("markup 1"), "Legal mark-up")));
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("legalB", s => s.Get<ILegalReviewService>().ReturnForReworkAsync(review.ReviewId, " ")));
        await w.As("legalB", s => s.Get<ILegalReviewService>().ReturnForReworkAsync(review.ReviewId, "Cap the liability."));
        // The owner replies in thread, and resubmits for round two; round one is untouched.
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().CommentAsync(new AddLegalCommentRequest(review.ReviewId, "Capped at 12 months' fees.", comment)));
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, "round two")));
        var two = await w.As("legalA", s => s.Get<ILegalReviewService>().GetAsync(review.ReviewId));
        Assert.Equal(2, two.CurrentRound);
        var round1 = two.Rounds.Single(r => r.Number == 1);
        Assert.Equal(LegalRoundOutcome.ReturnedForRework, round1.Outcome);
        Assert.Equal("Cap the liability.", round1.ReworkReason);
        Assert.Equal(2, round1.Comments.Count);
        Assert.Contains(round1.Comments, c => c.ParentCommentId == comment && c.Side == "Owner");
        Assert.Single(round1.Attachments, a => a.Kind == LegalAttachmentKind.Markup);
        Assert.Equal(LegalRoundOutcome.Submitted, two.Rounds.Single(r => r.Number == 2).Outcome);
        Assert.Equal(["Submitted", "Assigned", "Started", "LegalComment", "MarkupAdded", "Returned", "OwnerComment", "Resubmitted"], two.Timeline.Select(t => t.Kind).ToArray());
        // The mark-up is a document of the contract at the contract's classification, with its own version.
        var docs = await w.As("legalA", s => s.Get<ISecuredContractDocuments>().ListAsync(id));
        var markup = Assert.Single(docs, d => d.DocumentType == "LegalMarkup");
        Assert.Equal("Internal", (await w.AsDocAsync(markup.DocumentId)).Classification);
        await w.As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(review.ReviewId));
        await w.As("legalA", s => s.Get<ILegalReviewService>().ClearAsync(review.ReviewId, "ok"));
        var cleared = await w.As("legalA", s => s.Get<ILegalReviewService>().GetAsync(review.ReviewId));
        Assert.Equal(LegalReviewStatus.Cleared, cleared.Status);
        Assert.Equal(2, cleared.Rounds.Count);
        // Clearance did not register or lock anything.
        var facts = await w.FactsAsync(id);
        Assert.Null(facts.RegisteredDateUtc);
        Assert.False(facts.RecordLocked);

        // A privileged contract's review exists only for people cleared for it.
        var priv = await w.UserAsync("privOwner", RoleCodes.CategoryOwner, null, "Legal Privileged", privileged: true);
        await w.UserAsync("legalPriv", RoleCodes.Legal, null, "Legal Privileged");
        Guid cat;
        await using (var s = w.W.AsScopeless())
        {
            cat = await s.Get<ICategoryAdministration>().UpsertCategoryAsync(w.W.Org1, "PRIV2", "Privileged two", null);
            await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(cat, priv, true, w.W.Now.AddDays(-5), null, "test", null);
        }
        var secret = await w.NewDraftAsync("Legal Privileged", owner: "privOwner", category: cat);
        await w.As("privOwner", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, secret, "privileged")));
        var privReview = (await w.As("privOwner", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Contract, secret)))!;
        Assert.NotNull(await w.As("legalPriv", s => s.Get<ILegalReviewService>().GetAsync(privReview.ReviewId)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.As("legalA", s => s.Get<ILegalReviewService>().GetAsync(privReview.ReviewId)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(privReview.ReviewId)));
        Assert.DoesNotContain((await w.As("legalA", s => s.Get<ILegalReviewService>().QueueAsync(new LegalQueueFilter()))).Items, i => i.ContractId == secret);
        Assert.Contains((await w.As("legalPriv", s => s.Get<ILegalReviewService>().QueueAsync(new LegalQueueFilter()))).Items, i => i.ContractId == secret);
        // Queue filters.
        var queue = await w.As("legalA", s => s.Get<ILegalReviewService>().QueueAsync(new LegalQueueFilter(Status: LegalReviewStatus.Cleared, OpenOnly: false)));
        Assert.Contains(queue.Items, i => i.ContractId == id);
        Assert.Empty((await w.As("legalA", s => s.Get<ILegalReviewService>().QueueAsync(new LegalQueueFilter(CategoryId: w.W.CatEnergy, OpenOnly: false))).ConfigureAwait(false)).Items);
    }

    private static async Task<DocumentSummary> AsDocAsync(this LifecycleWorld w, Guid documentId)
        => (await w.W.AsScopeless().Run(s => s.Get<IDocumentQuery>().GetAsync(documentId)))!;
}
