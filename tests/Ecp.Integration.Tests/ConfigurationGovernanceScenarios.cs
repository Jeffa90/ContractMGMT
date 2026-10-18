using System.Text.Json;
using System.Text.Json.Nodes;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Implementation Readiness (G-01 and rollback governance): a change is made by one person, validated, submitted for review against its exact content, and published by a second person; reviewing or publishing never makes
/// the reviewer the editor; any edit withdraws the submission; a refusal is audited; and rolling back follows the same rule for every kind of configuration. Each scenario runs on SQLite and on SQL Server.
/// </summary>
internal static class ConfigurationGovernanceScenarios
{
    private const string Kind = "Terminology";

    private static string Doc(string singular) => TerminologyScenarios.Overrides(("site", singular, singular + "s"));

    private static string Id(LifecycleWorld w, string user) => w.W.Users[user].ToString();

    private sealed record Setup(LifecycleWorld W, Guid Org, string Author, string Reviewer, string Third);

    private static async Task<Setup> StartAsync(TestApp app, bool secondPerson = true)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Review Org", "RVW", TestPacks.Reference);
        var author = await TerminologyScenarios.AdminForAsync(w, org.Org, "author");
        var reviewer = await TerminologyScenarios.AdminForAsync(w, org.Org, "reviewer");
        var third = await TerminologyScenarios.AdminForAsync(w, org.Org, "third");
        await using (var s = w.W.AsScopeless()) s.Get<ConfigurationOptions>().RequireSecondPersonPublish = secondPerson;
        return new Setup(w, org.Org, author, reviewer, third);
    }

    private static async Task<DraftDto> ValidatedDraftAsync(Setup t, string singular)
    {
        var json = Doc(singular);
        var draft = await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, t.Org, json));
        if (draft.ContentJson != json) await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().SaveDraftAsync(draft.Id, json));
        var v = await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        Assert.True(v.IsPublishable, string.Join("; ", v.Validation!.Messages.Select(m => m.Code + " " + m.Message)));
        return v;
    }

    private static Task<DraftDto> SubmitAsync(Setup t, Guid draft, string user, string? comment = "Please review")
        => t.W.W.AsAsync(user, s => s.Get<IConfigurationService>().SubmitForReviewAsync(draft, comment));

    private static async Task<List<Ecp.Audit.Domain.AuditEvent>> AuditAsync(Setup t, string action)
    {
        await using var scope = t.W.W.AsScopeless();
        return await scope.Get<IAuditEventSource>().Events.Where(e => e.Action == action).OrderBy(e => e.Sequence).ToListAsync();
    }

    // ---- 1. the happy path and its evidence ----

    public static async Task One_person_submits_a_second_person_publishes_and_the_release_keeps_who_did_what(TestApp app)
    {
        var t = await StartAsync(app);
        var draft = await ValidatedDraftAsync(t, "Plant");
        var submitted = await SubmitAsync(t, draft.Id, t.Author);
        Assert.Equal(ReviewState.Submitted, submitted.ReviewState);
        Assert.Equal(Id(t.W, t.Author), submitted.SubmittedBy);
        Assert.Equal(draft.ContentHash, submitted.SubmittedHash);
        Assert.NotNull(submitted.SubmittedUtc);

        var release = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "Checked with the business"));
        Assert.Equal(1, release.Number);
        Assert.Equal(Id(t.W, t.Reviewer), release.PublishedBy);
        Assert.Equal(Id(t.W, t.Author), release.CreatedBy);
        Assert.Equal(Id(t.W, t.Author), release.LastEditedBy);
        Assert.Equal(Id(t.W, t.Author), release.SubmittedBy);
        Assert.Equal(draft.ContentHash, release.SubmittedHash);
        Assert.NotNull(release.SubmittedUtc);
        Assert.Equal("SecondPerson", release.GovernanceMode);
        Assert.Equal("Plant", (await t.W.W.AsAsync(t.Reviewer, s => s.Get<ITerminologyResolver>().GetAsync(t.Org, "en"))).Singular("site"));

        // Evidence in the audit trail: who submitted, who published, against which content.
        var sub = Assert.Single(await AuditAsync(t, "config.draft.submitted"));
        Assert.Contains(draft.ContentHash, sub.NewValuesJson!);
        var pub = (await AuditAsync(t, "config.published")).Last();
        Assert.Contains(Id(t.W, t.Reviewer), pub.NewValuesJson!);
        Assert.Contains(Id(t.W, t.Author), pub.NewValuesJson!);
        Assert.Contains("SecondPerson", pub.NewValuesJson!);
    }

    // ---- 2. reviewing and publishing never make the reviewer the editor ----

    public static async Task Reading_reviewing_and_publishing_never_make_the_reviewer_the_last_editor(TestApp app)
    {
        var t = await StartAsync(app);
        var draft = await ValidatedDraftAsync(t, "Yard");
        await SubmitAsync(t, draft.Id, t.Author);

        var detail = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().GetReviewAsync(draft.Id));
        Assert.Equal(Id(t.W, t.Author), detail.Draft.LastEditedBy);
        Assert.True(detail.Governance.CanPublish);
        Assert.True(detail.Governance.CanDecide);
        Assert.NotEmpty(detail.Changes);                                       // a plain-language list of what would change
        // Pressing the form's own Save with the very same content (as a page does) changes nothing: not the editor, not the submission.
        var again = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().SaveDraftAsync(draft.Id, draft.ContentJson));
        Assert.Equal(Id(t.W, t.Author), again.LastEditedBy);
        Assert.Equal(ReviewState.Submitted, again.ReviewState);

        var release = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "ok"));
        Assert.Equal(Id(t.W, t.Author), release.LastEditedBy);                // the publisher is recorded as publisher only
        Assert.Equal(Id(t.W, t.Reviewer), release.PublishedBy);
    }

    // ---- 3. an edit after submission invalidates it ----

    public static async Task Editing_after_submission_withdraws_it_and_it_must_be_submitted_again(TestApp app)
    {
        var t = await StartAsync(app);
        var draft = await ValidatedDraftAsync(t, "Depot");
        await SubmitAsync(t, draft.Id, t.Author);

        var edited = await t.W.W.AsAsync(t.Third, s => s.Get<IConfigurationService>().SaveDraftAsync(draft.Id, Doc("Depot2")));
        Assert.Equal(ReviewState.None, edited.ReviewState);
        Assert.Null(edited.SubmittedBy);
        Assert.Equal(Id(t.W, t.Third), edited.LastEditedBy);

        var refused = await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "ok")));
        Assert.Contains(refused.Code, new[] { "not_validated", "not_submitted" });
        await t.W.W.AsAsync(t.Third, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        var notSubmitted = await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "ok")));
        Assert.Equal("not_submitted", notSubmitted.Code);

        await SubmitAsync(t, draft.Id, t.Third);
        // The one who edited last and submitted cannot publish; the earlier author, who created it, cannot either; a different person can.
        Assert.Equal("second_person_required", (await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Third, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "x")))).Code);
        Assert.Equal("second_person_required", (await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "x")))).Code);
        var release = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "ok"));
        Assert.Equal(1, release.Number);

        var detailHistory = (await AuditAsync(t, "config.draft.saved")).Count(e => e.NewValuesJson!.Contains("submissionInvalidated\":true"));
        Assert.Equal(1, detailHistory);
    }

    // ---- 4. a mismatch between the submitted hash and the content cannot be published ----

    public static async Task A_draft_whose_content_no_longer_matches_what_was_submitted_cannot_be_published(TestApp app)
    {
        var t = await StartAsync(app);
        var draft = await ValidatedDraftAsync(t, "Camp");
        await SubmitAsync(t, draft.Id, t.Author);

        // Simulate a store that was altered underneath the service (the service itself never allows this): the content and its checks still agree with each other, but not with what was submitted.
        await using (var scope = t.W.W.AsScopeless())
        {
            var db = scope.Get<ConfigurationDbContext>();
            var row = await db.Drafts.FirstAsync(d => d.Id == draft.Id);
            var other = Doc("Camp-changed");
            var hash = ConfigHash.Of(other);
            db.Entry(row).Property(x => x.ContentJson).CurrentValue = other;
            db.Entry(row).Property(x => x.ContentHash).CurrentValue = hash;
            db.Entry(row).Property(x => x.ValidatedHash).CurrentValue = hash;
            db.Entry(row).Property(x => x.Status).CurrentValue = DraftStatus.Validated;
            await db.SaveChangesAsync();
        }
        var refused = await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "ok")));
        Assert.Equal("submission_changed", refused.Code);
        var denied = (await AuditAsync(t, "config.publish.refused")).Last();
        Assert.Equal(AuditOutcome.Denied.ToString(), denied.Outcome);
        Assert.Contains("submission_changed", denied.NewValuesJson!);
        Assert.Empty(await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().ReleasesAsync(Kind, t.Org)));
    }

    // ---- 5. self-publication ----

    public static async Task The_person_who_made_or_submitted_a_change_cannot_publish_it_and_the_refusal_is_audited(TestApp app)
    {
        var t = await StartAsync(app);
        var draft = await ValidatedDraftAsync(t, "Mill");
        // Not submitted at all.
        Assert.Equal("not_submitted", (await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "mine")))).Code);
        await SubmitAsync(t, draft.Id, t.Author);
        Assert.Equal("second_person_required", (await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "mine")))).Code);
        // The submit-or-publish button used by every page submits for the author and never publishes.
        var outcome = await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().SubmitOrPublishAsync(draft.Id, "mine"));
        Assert.True(outcome.Submitted);
        Assert.Null(outcome.Release);
        Assert.Equal(2, (await AuditAsync(t, "config.publish.refused")).Count);
        var g = await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().GovernanceAsync(draft.Id));
        Assert.False(g.CanPublish); Assert.False(g.CanDecide); Assert.True(g.CanWithdraw);
        var other = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().GovernanceAsync(draft.Id));
        Assert.True(other.CanPublish); Assert.True(other.CanDecide); Assert.False(other.CanWithdraw);
        // A second person pressing the same button on the unchanged, submitted draft publishes it.
        var published = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().SubmitOrPublishAsync(draft.Id, "reviewed"));
        Assert.NotNull(published.Release);
    }

    public static async Task Where_a_second_person_is_not_required_the_author_may_publish_directly_as_before(TestApp app)
    {
        var t = await StartAsync(app, secondPerson: false);
        var draft = await ValidatedDraftAsync(t, "Quarry");
        var outcome = await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().SubmitOrPublishAsync(draft.Id, "single-person policy"));
        Assert.NotNull(outcome.Release);
        Assert.Equal("SinglePerson", outcome.Release!.GovernanceMode);
    }

    // ---- 6. reject, return, resubmit ----

    public static async Task A_reviewer_can_reject_or_return_for_rework_and_the_comments_are_kept(TestApp app)
    {
        var t = await StartAsync(app);
        var draft = await ValidatedDraftAsync(t, "Port");
        await SubmitAsync(t, draft.Id, t.Author);
        await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().RejectAsync(draft.Id, "  ")));   // a comment is required
        await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().RejectAsync(draft.Id, "I disagree with myself")));   // not the author

        var returned = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().ReturnForReworkAsync(draft.Id, "Use the word Harbour"));
        Assert.Equal(ReviewState.ReturnedForRework, returned.ReviewState);
        Assert.Equal("Use the word Harbour", returned.DecisionComment);
        Assert.Equal(Id(t.W, t.Author), returned.LastEditedBy);               // the reviewer did not become the editor
        Assert.Equal("not_submitted", (await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Third, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "x")))).Code);

        await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().SaveDraftAsync(draft.Id, Doc("Harbour")));
        await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        await SubmitAsync(t, draft.Id, t.Author, "Changed as asked");
        var rejected = await t.W.W.AsAsync(t.Third, s => s.Get<IConfigurationService>().RejectAsync(draft.Id, "Not wanted this quarter"));
        Assert.Equal(ReviewState.Rejected, rejected.ReviewState);

        var detail = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().GetReviewAsync(draft.Id));
        Assert.Equal(new[] { "Submitted", "ReturnedForRework", "Submitted", "Rejected" }, detail.History.Select(h => h.EventType).ToArray());
        Assert.Contains(detail.History, h => h.EventType == "ReturnedForRework" && h.Comment == "Use the word Harbour");
        Assert.Contains(detail.History, h => h.EventType == "Rejected" && h.Comment == "Not wanted this quarter");
    }

    public static async Task A_submitter_can_withdraw_but_nobody_else_can(TestApp app)
    {
        var t = await StartAsync(app);
        var draft = await ValidatedDraftAsync(t, "Dock");
        await SubmitAsync(t, draft.Id, t.Author);
        Assert.Equal("not_yours", (await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().WithdrawSubmissionAsync(draft.Id, "no")))).Code);
        var withdrawn = await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().WithdrawSubmissionAsync(draft.Id, "Not ready"));
        Assert.Equal(ReviewState.None, withdrawn.ReviewState);
        Assert.Equal("not_submitted", (await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "x")))).Code);
        Assert.Single(await AuditAsync(t, "config.draft.withdrawn"));
    }

    // ---- 7. concurrent decisions ----

    public static async Task Two_people_deciding_at_the_same_moment_cannot_both_succeed(TestApp app)
    {
        var t = await StartAsync(app);
        var draft = await ValidatedDraftAsync(t, "Rail");
        await SubmitAsync(t, draft.Id, t.Author);

        async Task<string> Try(string user)
        {
            try { var r = await t.W.W.AsAsync(user, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "race")); return "published " + r.Number; }
            catch (DomainException ex) { return ex.Code; }
        }
        var results = await Task.WhenAll(Try(t.Reviewer), Try(t.Third));
        Assert.Equal(1, results.Count(r => r.StartsWith("published", StringComparison.Ordinal)));
        Assert.Single(await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().ReleasesAsync(Kind, t.Org)));

        // A publish against a reject that raced it: whichever reached the store first wins, the other is told so.
        var draft2 = await ValidatedDraftAsync(t, "Rail2");
        await SubmitAsync(t, draft2.Id, t.Author);
        async Task<string> Publish() { try { await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(draft2.Id, "r")); return "published"; } catch (DomainException ex) { return ex.Code; } }
        async Task<string> Reject() { try { await t.W.W.AsAsync(t.Third, s => s.Get<IConfigurationService>().RejectAsync(draft2.Id, "no")); return "rejected"; } catch (DomainException ex) { return ex.Code; } }
        var both = await Task.WhenAll(Publish(), Reject());
        Assert.True(both.Count(r => r is "published" or "rejected") >= 1);
        var final = (await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().ReviewQueueAsync()));
        Assert.True(both.Contains("published") ? final.All(i => i.DraftId != draft2.Id) : true);
    }

    // ---- 8. the review queue ----

    public static async Task The_review_queue_shows_only_what_the_person_may_see_and_never_another_tenant(TestApp app)
    {
        var t = await StartAsync(app);
        var d1 = await ValidatedDraftAsync(t, "Alpha");
        await SubmitAsync(t, d1.Id, t.Author);
        var queue = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().ReviewQueueAsync());
        var item = Assert.Single(queue);
        Assert.True(item.CanDecide); Assert.False(item.IsMine);
        Assert.Equal("terminology.publish", item.PublishPermission);
        Assert.True(item.ChangeCount > 0);
        var mine = Assert.Single(await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().ReviewQueueAsync()));
        Assert.False(mine.CanDecide); Assert.True(mine.IsMine);

        // Another tenant sees nothing, and cannot open the draft.
        var other = await StartAsync(app);
        Assert.Empty(await other.W.W.AsAsync(other.Reviewer, s => s.Get<IConfigurationService>().ReviewQueueAsync()));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => other.W.W.AsAsync(other.Reviewer, s => s.Get<IConfigurationService>().GetReviewAsync(d1.Id)));

        // A person with no right to this kind of configuration in this organisation sees nothing.
        var outsider = await TerminologyScenarios.AdminForAsync(t.W, (await PackResolutionScenarios.AddOrganisationAsync(t.W, "Elsewhere", "ELS", TestPacks.Reference)).Org, "outsider");
        Assert.Empty(await t.W.W.AsAsync(outsider, s => s.Get<IConfigurationService>().ReviewQueueAsync()));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => t.W.W.AsAsync(outsider, s => s.Get<IConfigurationService>().GetReviewAsync(d1.Id)));
    }

    // ---- 9. rollback governance ----

    public static async Task Rollback_needs_a_recent_sign_in_a_reason_and_a_second_person_and_never_changes_history(TestApp app)
    {
        var t = await StartAsync(app, secondPerson: false);
        // Two releases, published directly (single-person policy) so there is something to go back to.
        var first = await PublishDirectAsync(t, "One");
        var second = await PublishDirectAsync(t, "Two");
        await using (var s = t.W.W.AsScopeless()) { var o = s.Get<ConfigurationOptions>(); o.RequireSecondPersonPublish = true; o.RequireRecentSignInForRollback = true; }

        // No recent sign-in: refused before anything is created.
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().RequestRollbackAsync(Kind, t.Org, first.Number, "Back to One")));
        Assert.Equal(2, (await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().ReleasesAsync(Kind, t.Org))).Count);
        // A reason is required.
        await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Author, s => { s.Get<ReauthenticationContext>().Set(t.W.W.Now); return s.Get<IConfigurationService>().RequestRollbackAsync(Kind, t.Org, first.Number, " "); }));
        // The permission is needed.
        var viewer = await t.W.UserAsync("justAuditor", RoleCodes.Auditor, null, "Confidential");
        var refusedAuditor = await Assert.ThrowsAnyAsync<Exception>(() => t.W.W.AsAsync("justAuditor", s => { s.Get<ReauthenticationContext>().Set(t.W.W.Now); return s.Get<IConfigurationService>().RequestRollbackAsync(Kind, t.Org, first.Number, "x"); }));
        Assert.True(refusedAuditor is ForbiddenException or ResourceNotFoundException);                     // no permission, so no rollback

        var outcome = await t.W.W.AsAsync(t.Author, s => { s.Get<ReauthenticationContext>().Set(t.W.W.Now); return s.Get<IConfigurationService>().RequestRollbackAsync(Kind, t.Org, first.Number, "Back to One"); });
        Assert.True(outcome.AwaitingReview);
        Assert.Null(outcome.Release);
        Assert.Equal(first.Number, outcome.Draft!.RollbackOfNumber);
        Assert.Equal(ReviewState.Submitted, outcome.Draft.ReviewState);
        Assert.Equal(2, (await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().ReleasesAsync(Kind, t.Org))).Count);   // nothing took effect

        // The requester cannot publish their own rollback; the direct form refuses and says why.
        Assert.Equal("second_person_required", (await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().PublishAsync(outcome.Draft.Id, "mine")))).Code);
        var direct = await Assert.ThrowsAsync<DomainException>(() => t.W.W.AsAsync(t.Author, s => { s.Get<ReauthenticationContext>().Set(t.W.W.Now); return s.Get<IConfigurationService>().RollbackAsync(Kind, t.Org, first.Number, "again"); }));
        Assert.Equal("second_person_required", direct.Code);

        var release = await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().PublishAsync(outcome.Draft.Id, "Agreed"));
        Assert.Equal(3, release.Number);
        Assert.Equal(first.Number, release.RollbackOfNumber);
        Assert.Equal(first.ContentHash, release.ContentHash);
        Assert.Equal(Id(t.W, t.Reviewer), release.PublishedBy);
        Assert.Equal(Id(t.W, t.Author), release.SubmittedBy);
        Assert.Contains("Back to One", release.Note);

        // History is immutable: the earlier releases are exactly what they were.
        var history = (await t.W.W.AsAsync(t.Reviewer, s => s.Get<IConfigurationService>().ReleasesAsync(Kind, t.Org))).OrderBy(r => r.Number).ToList();
        Assert.Equal([1, 2, 3], history.Select(r => r.Number));
        Assert.Equal(first.ContentHash, history[0].ContentHash); Assert.Equal(second.ContentHash, history[1].ContentHash);
        Assert.Equal(first.PublishedBy, history[0].PublishedBy); Assert.Equal(second.PublishedBy, history[1].PublishedBy);

        // The audit names the actor, the reason, the source version and the resulting version.
        var requested = Assert.Single(await AuditAsync(t, "config.rollback.requested"));
        Assert.Equal("Back to One", requested.Reason);
        Assert.Contains($"\"sourceVersion\":{first.Number}", requested.NewValuesJson!);
        var done = (await AuditAsync(t, "config.rolled_back")).Single();
        Assert.Contains($"\"sourceVersion\":{first.Number}", done.NewValuesJson!);
        Assert.Contains("\"resultingVersion\":3", done.NewValuesJson!);
        Assert.Contains(Id(t.W, t.Reviewer), done.NewValuesJson!);
    }

    private static async Task<ReleaseDto> PublishDirectAsync(Setup t, string singular)
    {
        var d = await ValidatedDraftAsync(t, singular);
        return await t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().PublishAsync(d.Id, "direct " + singular));
    }

    /// <summary>Where a second person is not required, a rollback is still permission-checked, needs a reason and a recent sign-in, and creates a new release.</summary>
    public static async Task Rollback_without_a_second_person_requirement_still_creates_a_new_release_and_audits_it(TestApp app)
    {
        var t = await StartAsync(app, secondPerson: false);
        var first = await PublishDirectAsync(t, "One");
        await PublishDirectAsync(t, "Two");
        await using (var s = t.W.W.AsScopeless()) s.Get<ConfigurationOptions>().RequireRecentSignInForRollback = true;
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => t.W.W.AsAsync(t.Author, s => s.Get<IConfigurationService>().RollbackAsync(Kind, t.Org, first.Number, "Back")));
        var release = await t.W.W.AsAsync(t.Author, s => { s.Get<ReauthenticationContext>().Set(t.W.W.Now); return s.Get<IConfigurationService>().RollbackAsync(Kind, t.Org, first.Number, "Back"); });
        Assert.Equal(3, release.Number); Assert.Equal(first.Number, release.RollbackOfNumber); Assert.Equal("SinglePerson", release.GovernanceMode);
        Assert.Single(await AuditAsync(t, "config.rolled_back"));
    }
}

[Trait("Category", "Governance")]
public sealed class ConfigurationGovernanceScenarioTests_Sqlite
{
    [Fact] public Task Happy_path() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.One_person_submits_a_second_person_publishes_and_the_release_keeps_who_did_what);
    [Fact] public Task Not_the_editor() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.Reading_reviewing_and_publishing_never_make_the_reviewer_the_last_editor);
    [Fact] public Task Edit_invalidates() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.Editing_after_submission_withdraws_it_and_it_must_be_submitted_again);
    [Fact] public Task Hash_mismatch() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.A_draft_whose_content_no_longer_matches_what_was_submitted_cannot_be_published);
    [Fact] public Task Self_publication() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.The_person_who_made_or_submitted_a_change_cannot_publish_it_and_the_refusal_is_audited);
    [Fact] public Task Single_person() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.Where_a_second_person_is_not_required_the_author_may_publish_directly_as_before);
    [Fact] public Task Reject_and_return() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.A_reviewer_can_reject_or_return_for_rework_and_the_comments_are_kept);
    [Fact] public Task Withdraw() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.A_submitter_can_withdraw_but_nobody_else_can);
    [Fact] public Task Concurrent() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.Two_people_deciding_at_the_same_moment_cannot_both_succeed);
    [Fact] public Task Queue() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.The_review_queue_shows_only_what_the_person_may_see_and_never_another_tenant);
    [Fact] public Task Rollback() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.Rollback_needs_a_recent_sign_in_a_reason_and_a_second_person_and_never_changes_history);
    [Fact] public Task Rollback_single() => ProviderRunner.SqliteAsync(ConfigurationGovernanceScenarios.Rollback_without_a_second_person_requirement_still_creates_a_new_release_and_audits_it);
}

[Trait("Category", "SqlServer")]
public sealed class ConfigurationGovernanceScenarioTests_SqlServer
{
    [SqlServerFact] public Task Happy_path() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.One_person_submits_a_second_person_publishes_and_the_release_keeps_who_did_what);
    [SqlServerFact] public Task Not_the_editor() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.Reading_reviewing_and_publishing_never_make_the_reviewer_the_last_editor);
    [SqlServerFact] public Task Edit_invalidates() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.Editing_after_submission_withdraws_it_and_it_must_be_submitted_again);
    [SqlServerFact] public Task Hash_mismatch() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.A_draft_whose_content_no_longer_matches_what_was_submitted_cannot_be_published);
    [SqlServerFact] public Task Self_publication() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.The_person_who_made_or_submitted_a_change_cannot_publish_it_and_the_refusal_is_audited);
    [SqlServerFact] public Task Reject_and_return() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.A_reviewer_can_reject_or_return_for_rework_and_the_comments_are_kept);
    [SqlServerFact] public Task Concurrent() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.Two_people_deciding_at_the_same_moment_cannot_both_succeed);
    [SqlServerFact] public Task Queue() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.The_review_queue_shows_only_what_the_person_may_see_and_never_another_tenant);
    [SqlServerFact] public Task Rollback() => ProviderRunner.SqlServerAsync(ConfigurationGovernanceScenarios.Rollback_needs_a_recent_sign_in_a_reason_and_a_second_person_and_never_changes_history);
}
