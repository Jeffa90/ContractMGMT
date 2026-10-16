using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications;
using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Notifications, reminders and dashboards: idempotent, classification-safe, following current ownership.</summary>
internal static class NotificationScenarios
{
    public static async Task A_workflow_event_tells_the_right_people_once_and_a_second_run_changes_nothing(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.NewDraftAsync();
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, "Please review")));
        await w.RunJobsAsync();

        // The Legal pool is told; the person who submitted is not told about their own action.
        var legalA = await w.InboxAsync("legalA");
        var legalB = await w.InboxAsync("legalB");
        Assert.Single(legalA, n => n.EventType == "legal.submitted");
        Assert.Single(legalB, n => n.EventType == "legal.submitted");
        Assert.DoesNotContain(await w.InboxAsync("ownerPackaging"), n => n.EventType == "legal.submitted");
        // People without access to the record hear nothing (Viewer in another scope, an approver of another group).
        Assert.Empty(await w.InboxAsync("otherApprover"));
        var captured = await w.CapturedAsync();
        Assert.Equal(3, captured.Count(c => c.Subject.StartsWith("Legal review requested", StringComparison.Ordinal)));      // the Legal pool: legal, legalA and legalB

        // Running everything again, or the same events being read twice, creates nothing new.
        var before = await CountNotificationsAsync(w);
        await w.RunJobsAsync();
        await w.RunJobsAsync();
        Assert.Equal(before, await CountNotificationsAsync(w));
        Assert.Equal(3, (await w.CapturedAsync()).Count(c => c.Subject.StartsWith("Legal review requested", StringComparison.Ordinal)));

        // A return for rework is mandatory and reaches the category owner.
        var review = (await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Contract, id)))!;
        await w.As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(review.ReviewId));
        await w.As("legalA", s => s.Get<ILegalReviewService>().ReturnForReworkAsync(review.ReviewId, "Clause 4 needs a cap on liability."));
        await w.RunJobsAsync();
        var returned = (await w.InboxAsync("ownerPackaging")).Single(n => n.EventType == "legal.returned");
        Assert.True(returned.Mandatory);
        Assert.Contains("rework", returned.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<int> CountNotificationsAsync(LifecycleWorld w) => w.W.AsScopeless().Run(s => s.Get<NotificationsDbContext>().Notifications.CountAsync());

    public static async Task Notification_content_never_leaks_a_classified_record_to_people_who_could_not_open_it(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        // A privileged record in its own category, owned by someone cleared for it; one Legal reviewer is cleared, the others are not.
        var priv = await w.UserAsync("privOwner", RoleCodes.CategoryOwner, null, "Legal Privileged", privileged: true);
        await w.UserAsync("legalPriv", RoleCodes.Legal, null, "Legal Privileged");
        Guid cat;
        await using (var s = w.W.AsScopeless())
        {
            cat = await s.Get<ICategoryAdministration>().UpsertCategoryAsync(w.W.Org1, "PRIVILEGED", "Privileged matters", null);
            await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(cat, priv, true, w.W.Now.AddDays(-5), null, "test", null);
        }
        var id = await w.NewDraftAsync("Legal Privileged", title: "Secret settlement with Acme ZEBRA", owner: "privOwner", category: cat);
        await w.As("privOwner", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, "Privileged advice needed")));
        await w.RunJobsAsync();

        // Only the cleared reviewer is told, and the message names neither the reference nor the title.
        var told = await w.InboxAsync("legalPriv");
        var message = Assert.Single(told, n => n.EventType == "legal.submitted");
        Assert.DoesNotContain("ZEBRA", message.Title + message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", message.Title + message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LC-", message.Title + message.Body, StringComparison.Ordinal);
        Assert.Empty(await w.InboxAsync("legalA"));
        Assert.Empty(await w.InboxAsync("legalB"));
        // Nothing sent by email carries the title either.
        var mails = (await w.CapturedAsync()).ToList();
        Assert.All(mails, m => Assert.DoesNotContain("ZEBRA", m.Subject + m.Body, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mails, m => m.ToAddress.StartsWith("legalA@", StringComparison.Ordinal));
    }

    public static async Task A_title_is_withheld_from_the_message_at_the_restricted_level_but_the_reference_remains(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.UserAsync("legalRestricted", RoleCodes.Legal, null, "Restricted");
        var priv = await w.UserAsync("restrictedOwner", RoleCodes.CategoryOwner, null, "Restricted");
        Guid cat;
        await using (var s = w.W.AsScopeless())
        {
            cat = await s.Get<ICategoryAdministration>().UpsertCategoryAsync(w.W.Org1, "RESTRICTED", "Restricted matters", null);
            await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(cat, priv, true, w.W.Now.AddDays(-5), null, "test", null);
        }
        var id = await w.NewDraftAsync("Restricted", title: "Board remuneration PELICAN", owner: "restrictedOwner", category: cat);
        await w.As("restrictedOwner", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, null)));
        await w.RunJobsAsync();
        var message = Assert.Single(await w.InboxAsync("legalRestricted"), n => n.EventType == "legal.submitted");
        Assert.DoesNotContain("PELICAN", message.Title + message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("title withheld", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LC-", message.Title, StringComparison.Ordinal);
    }

    public static async Task Reminders_are_sent_once_per_window_and_follow_current_ownership(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var end = w.Today.AddDays(125);
        var id = await w.ToEffectiveInOrderAsync(end, lead: 120);
        // Not yet in the 120-day window: nothing to send.
        var first = await Scan(w);
        Assert.Equal(0, first.RemindersSent);
        Assert.DoesNotContain(await w.InboxAsync("ownerPackaging"), n => n.Kind == "Reminder");

        // Five days later the 120-day window opens and the current owner is reminded.
        w.App.Clock.Advance(TimeSpan.FromDays(6));
        var second = await Scan(w);
        Assert.Equal(1, second.RemindersSent);
        var reminder = Assert.Single(await w.InboxAsync("ownerPackaging"), n => n.Kind == "Reminder" && n.ReminderKind == "endDate");
        Assert.Equal(120, reminder.WindowDays);
        Assert.True(reminder.RequiresAcknowledgement);
        // Running the scan again, or on the same day by another worker, adds nothing.
        Assert.Equal(0, (await Scan(w)).RemindersSent);
        Assert.Equal(0, (await Scan(w)).NotificationsCreated);
        Assert.Single(await w.InboxAsync("ownerPackaging"), n => n.Kind == "Reminder" && n.ReminderKind == "endDate");

        // Ownership moves: the next window goes to the new owner and not the old one.
        await using (var s = w.W.AsScopeless())
        {
            var cats = s.Get<ICategoryAdministration>();
            var current = (await cats.ListAllAsync()).Single(c => c.Id == w.W.CatPackaging).Owners.First(o => o.UserId == w.W.Users["ownerPackaging"] && o.EffectiveToUtc == null);
            await cats.EndOwnerTrustedAsync(current.AssignmentId, "handover");
            await cats.AssignOwnerTrustedAsync(w.W.CatPackaging, w.W.Users["ownerEnergy"], true, w.W.Now.AddDays(-1), null, "test", "handover");
        }
        w.App.Clock.Advance(TimeSpan.FromDays(31));          // now 90 days from the end date
        var third = await Scan(w);
        Assert.Equal(1, third.RemindersSent);
        Assert.Single(await w.InboxAsync("ownerEnergy"), n => n.Kind == "Reminder" && n.WindowDays == 90);
        Assert.Single(await w.InboxAsync("ownerPackaging"), n => n.Kind == "Reminder" && n.ReminderKind == "endDate");      // still only the first one
        _ = id;
    }

    public static async Task Evergreen_agreements_are_reminded_of_their_review_date_but_never_an_end_date(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var review = w.Today.AddDays(20);
        var id = await w.ToEffectiveInOrderAsync(w.Today.AddDays(25), review, "Evergreen", lead: 30);
        var result = await Scan(w);
        Assert.Equal(1, result.RemindersSent);
        var items = (await w.InboxAsync("ownerPackaging")).Where(n => n.Kind == "Reminder").ToList();
        Assert.Single(items);
        Assert.Equal("reviewDate", items[0].ReminderKind);
        _ = id;
    }

    public static async Task Critical_notice_agreements_get_earlier_alerts_against_their_notice_deadline(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        // Ends in 60 days with 30 days' notice: the deadline is today + 30, so the 30-day alert is due now, and the lengthened lead catches the end date earlier too.
        var id = await w.ToEffectiveInOrderAsync(w.Today.AddDays(60), lead: 90, notice: 30, critical: true);
        var result = await Scan(w);
        Assert.True(result.RemindersSent >= 2);
        var reminders = (await w.InboxAsync("ownerPackaging")).Where(n => n.Kind == "Reminder").ToList();
        Assert.Contains(reminders, n => n.ReminderKind == "criticalNotice" && n.WindowDays == 30);
        Assert.Contains(reminders, n => n.ReminderKind == "endDate");
        _ = id;
    }

    public static async Task A_reminder_is_acknowledged_by_a_current_owner_and_the_dashboard_follows(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToEffectiveInOrderAsync(w.Today.AddDays(20), lead: 30);
        await Scan(w);
        var reminder = (await w.InboxAsync("ownerPackaging")).Single(n => n.Kind == "Reminder");
        var cards = await w.As("ownerPackaging", s => s.Get<IDashboardService>().CardAsync(DashboardKeys.RemindedUnacknowledged));
        Assert.Contains(cards.Rows, r => r.ContractId == id);

        // Another person cannot acknowledge for the owner.
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.As("ownerEnergy", s => s.Get<INotificationInbox>().AcknowledgeAsync(reminder.Id)));
        await w.As("ownerPackaging", s => s.Get<INotificationInbox>().AcknowledgeAsync(reminder.Id));
        var after = (await w.InboxAsync("ownerPackaging")).Single(n => n.Id == reminder.Id);
        Assert.NotNull(after.AcknowledgedUtc);
        Assert.NotNull(after.AcknowledgedBy);
        var cardsAfter = await w.As("ownerPackaging", s => s.Get<IDashboardService>().CardAsync(DashboardKeys.RemindedUnacknowledged));
        Assert.DoesNotContain(cardsAfter.Rows, r => r.ContractId == id);
    }

    public static async Task Failed_email_is_retried_with_a_back_off_then_dead_lettered_where_administrators_can_see_and_retry_it(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var options = app.Services.GetRequiredService<IOptions<NotificationsOptions>>().Value;
        options.EmailProvider = "graph";                       // scaffold: fails closed
        try
        {
            var id = await w.NewDraftAsync();
            await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, null)));
            for (var i = 0; i < 8; i++) { await w.RunJobsAsync(); w.App.Clock.Advance(TimeSpan.FromHours(13)); }
            var problems = await w.As("procAdmin", s => s.Get<INotificationAdmin>().DeliveriesAsync("problems"));
            Assert.NotEmpty(problems.Items);
            Assert.All(problems.Items, d => { Assert.Equal("DeadLetter", d.Status); Assert.Equal(5, d.Attempts); Assert.Contains("provider_not_configured", d.LastError); });
            var attempts = await w.As("procAdmin", s => s.Get<INotificationAdmin>().AttemptsAsync(problems.Items[0].DeliveryId));
            Assert.Equal(5, attempts.Count);
            Assert.All(attempts, a => Assert.False(string.IsNullOrWhiteSpace(a.CorrelationId)));
            // The in-app notification is unaffected by the email failure.
            Assert.Single(await w.InboxAsync("legalA"), n => n.EventType == "legal.submitted");

            // An ordinary user cannot see failures; an administrator can retry once the provider is fixed.
            await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<INotificationAdmin>().DeliveriesAsync("problems")));
            options.EmailProvider = "dev-capture";
            await w.As("procAdmin", s => s.Get<INotificationAdmin>().RetryAsync(problems.Items[0].DeliveryId));
            var afterRetry = await w.As("procAdmin", s => s.Get<INotificationAdmin>().DeliveriesAsync("pending"));
            Assert.Single(afterRetry.Items, d => d.DeliveryId == problems.Items[0].DeliveryId);
            var jobs = await w.RunJobsAsync();
            Assert.True(jobs.All(j => j.Status == "Succeeded"), string.Join(" | ", jobs.Select(j => j.JobName + ":" + j.Status + ":" + j.Processed + ":" + j.Detail)));
            var again = await w.As("procAdmin", s => s.Get<INotificationAdmin>().DeliveriesAsync("problems"));
            Assert.True(again.Total < problems.Total, string.Join(" | ", again.Items.Select(i => i.Status + ":" + i.Provider + ":" + i.Attempts + ":" + i.LastError)) + " / retried " + problems.Items[0].DeliveryId);
            Assert.NotEmpty(await w.CapturedAsync());
        }
        finally { options.EmailProvider = "dev-capture"; }
    }

    public static async Task A_person_who_loses_access_to_a_record_sees_nothing_about_it_in_their_inbox(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.NewDraftAsync(title: "Sensitive title OSPREY");
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, null)));
        await w.RunJobsAsync();
        Assert.Contains("OSPREY", (await w.InboxAsync("legalA")).Single(n => n.EventType == "legal.submitted").Body);
        // The record is reclassified above Legal's ceiling; the old message no longer reveals it.
        await w.UserAsync("classifier", RoleCodes.Legal, null, "Legal Privileged", privileged: true);
        await w.As("ownerPackaging", s => s.Get<ISecuredContractService>().ChangeClassificationAsync(id, "Confidential", null));
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractLifecyclePort>().ApplyControlledChangesAsync(id, new Dictionary<string, string?> { ["classification"] = "Legal Privileged" }, ["classification"], "test", "unlock", Guid.NewGuid(), "test"); return 0; });
        var redacted = (await w.InboxAsync("legalA")).Single(n => n.EventType == "legal.submitted");
        Assert.DoesNotContain("OSPREY", redacted.Title + redacted.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(redacted.LinkPath);
    }

    private static Task<ReminderScanResult> Scan(LifecycleWorld w) => w.W.AsScopeless().Run(s => s.Get<IReminderScanner>().ScanAsync());
}
