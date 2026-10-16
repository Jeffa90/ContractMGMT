using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>More notification and dashboard behaviour: ownership and disposition events, administrator scope, background job status, and dashboards that never show or count what the reader cannot open.</summary>
internal static class NotificationScenarios2
{
    public static async Task Ownership_changes_tell_only_the_person_affected(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.RunJobsAsync();                       // drain what the world's own set-up produced
        var before = (await w.InboxAsync("ownerEnergy")).Count(n => n.EventType.StartsWith("ownership.", StringComparison.Ordinal));
        await using (var s = w.W.AsScopeless())
        {
            var cats = s.Get<ICategoryAdministration>();
            await cats.AssignOwnerTrustedAsync(w.W.CatFlexible, w.W.Users["ownerEnergy"], true, w.W.Now.AddDays(-1), null, "test", "cover");
        }
        await w.RunJobsAsync();
        var mine = (await w.InboxAsync("ownerEnergy")).Where(n => n.EventType.StartsWith("ownership.", StringComparison.Ordinal)).ToList();
        Assert.Equal(before + 1, mine.Count);
        Assert.Contains(mine, n => n.EventType == "ownership.assigned");
        // Nobody else hears about it, and a second run does not repeat it.
        Assert.DoesNotContain(await w.InboxAsync("ownerPackaging"), n => n.EventType == "ownership.assigned" && n.CreatedUtc > w.W.Now.AddMinutes(-1));
        Assert.DoesNotContain(await w.InboxAsync("legalA"), n => n.EventType.StartsWith("ownership.", StringComparison.Ordinal));
        await w.RunJobsAsync();
        Assert.Equal(before + 1, (await w.InboxAsync("ownerEnergy")).Count(n => n.EventType.StartsWith("ownership.", StringComparison.Ordinal)));

        // Ending the assignment tells the same person.
        await using (var s = w.W.AsScopeless())
        {
            var cats = s.Get<ICategoryAdministration>();
            var row = (await cats.ListAllAsync()).Single(c => c.Id == w.W.CatFlexible).Owners.First(o => o.UserId == w.W.Users["ownerEnergy"] && o.EffectiveToUtc == null);
            await cats.EndOwnerTrustedAsync(row.AssignmentId, "cover ended");
        }
        await w.RunJobsAsync();
        Assert.Contains(await w.InboxAsync("ownerEnergy"), n => n.EventType == "ownership.ended");
    }

    public static async Task A_disposition_request_reaches_the_approver_group_and_no_other_group(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToStateAsync("FlaggedForDisposition");
        await w.RunJobsAsync();
        foreach (var user in new[] { "groupHead", "backupHead" })
        {
            var item = Assert.Single(await w.InboxAsync(user), n => n.EventType == "disposition.requested");
            Assert.True(item.Mandatory);
            Assert.Equal(id, item.ContractId);
        }
        Assert.DoesNotContain(await w.InboxAsync("otherApprover"), n => n.EventType == "disposition.requested");
        Assert.DoesNotContain(await w.InboxAsync("ownerEnergy"), n => n.EventType == "disposition.requested");
        await w.RunJobsAsync();
        Assert.Single(await w.InboxAsync("groupHead"), n => n.EventType == "disposition.requested");
    }

    public static async Task Notification_administrators_see_only_their_own_organisations_deliveries(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.NewDraftAsync();
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, null)));
        await w.RunJobsAsync();
        var org1 = await w.As("procAdmin", s => s.Get<INotificationAdmin>().DeliveriesAsync(null));
        Assert.NotEmpty(org1.Items);

        // An administrator of the other organisation sees none of it and cannot reach it by id.
        await using (var s = w.W.AsScopeless())
        {
            var userId = await s.Get<IUserService>().CreateActiveTrustedAsync("admin2", "admin2@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", "admin2|" + w.W.TenantId, "test");
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(userId, RoleCodes.ProcurementAdministrator, null, ScopeSet.ForOrganisations([w.W.Org2], "Confidential"), null,
                EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
            w.W.Users["admin2"] = userId;
        }
        var other = await w.As("admin2", s => s.Get<INotificationAdmin>().DeliveriesAsync(null));
        Assert.Empty(other.Items);
        var summaryOther = await w.As("admin2", s => s.Get<INotificationAdmin>().SummaryAsync());
        Assert.Equal(0, summaryOther.Pending + summaryOther.Failed + summaryOther.DeadLetter);
        var delivery = org1.Items[0].DeliveryId;
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.As("admin2", s => s.Get<INotificationAdmin>().AttemptsAsync(delivery)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.As("admin2", s => s.Get<INotificationAdmin>().RetryAsync(delivery)));
        // People without the permission get a plain refusal.
        foreach (var user in new[] { "ownerPackaging", "legalA", "viewerAll", "groupHead" })
            await Assert.ThrowsAsync<ForbiddenException>(() => w.As(user, s => s.Get<INotificationAdmin>().SummaryAsync()));
    }

    public static async Task Background_job_runs_are_recorded_and_visible_only_to_administrators(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var rows = await w.RunJobsAsync();
        Assert.Equal(["lifecycle-events", "email-delivery", "reminder-scan", "unlock-expiry"], rows.Select(r => r.JobName));
        Assert.All(rows, r => { Assert.Equal("Succeeded", r.Status); Assert.NotNull(r.FinishedUtc); });
        var seen = await w.As("procAdmin", s => s.Get<INotificationAdmin>().JobsAsync());
        Assert.True(seen.Count >= 4);
        Assert.Contains(seen, j => j.JobName == "reminder-scan" && j.Detail!.Contains("contracts scanned", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<INotificationAdmin>().JobsAsync()));
        // The development mailbox is a Test/Development feature and exists for administrators only.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<INotificationAdmin>().CapturedEmailsAsync()));
    }

    public static async Task Dashboards_never_show_or_count_a_record_the_reader_could_not_open(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var visible = await w.ToEffectiveInOrderAsync(w.Today.AddDays(20), lead: 30);
        var hidden = await w.ToEffectiveInOrderAsync(w.Today.AddDays(20), lead: 30);
        // The second agreement is reclassified above every ordinary ceiling.
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractLifecyclePort>().ApplyControlledChangesAsync(hidden, new Dictionary<string, string?> { ["classification"] = "Legal Privileged" }, ["classification"], "test", "unlock", Guid.NewGuid(), "test"); return 0; });
        await w.W.AsScopeless().Run(s => s.Get<IReminderScanner>().ScanAsync());

        foreach (var user in new[] { "ownerPackaging", "procAdmin", "viewerAll", "legalA" })
        {
            var cards = await w.As(user, s => s.Get<IDashboardService>().OverviewAsync(200));
            Assert.Equal(DashboardKeys.All, cards.Select(c => c.Key));
            foreach (var card in cards)
            {
                Assert.DoesNotContain(card.Rows, r => r.ContractId == hidden);
                Assert.DoesNotContain(card.Rows, r => r.Classification == "Legal Privileged");
                Assert.True(card.Count >= card.Rows.Count, $"{user}/{card.Key}: count {card.Count} is less than the {card.Rows.Count} rows shown");
            }
        }
        // The count on a card is the count of the list behind it, so the two cannot disagree.
        var mine = (await w.As("ownerPackaging", s => s.Get<IDashboardService>().CardAsync(DashboardKeys.MyContracts)));
        var register = await w.As("ownerPackaging", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(OwnedByMe: true, PageSize: 200)));
        Assert.Equal(register.Total, mine.Count);
        Assert.Contains(mine.Rows, r => r.ContractId == visible);
        var expiring = await w.As("ownerPackaging", s => s.Get<IDashboardService>().CardAsync(DashboardKeys.ExpiringSoon, 1, 200));
        Assert.Contains(expiring.Rows, r => r.ContractId == visible);
        Assert.Equal(expiring.Count, expiring.Rows.Count);

        // A person with no scope sees empty cards, and a card key outside the list is refused.
        var nobody = await w.As("viewerRestricted", s => s.Get<IDashboardService>().OverviewAsync(200));
        Assert.DoesNotContain(nobody.SelectMany(c => c.Rows), r => r.ContractId == visible || r.ContractId == hidden);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.As("ownerPackaging", s => s.Get<IDashboardService>().CardAsync("not-a-card")));
    }
}
