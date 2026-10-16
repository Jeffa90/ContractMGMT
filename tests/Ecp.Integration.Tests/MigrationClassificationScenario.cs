using Ecp.Contracts;
using Ecp.Contracts.Persistence;
using Microsoft.EntityFrameworkCore;
using Ecp.DataMigration;
using Ecp.Notifications;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

internal static class MigrationClassificationScenario
{
    /// <summary>Source classifications are kept, a blank one takes the tenant default, and one that is not on the scale is rejected rather than quietly relaxed. Who can see the result follows the level.</summary>
    public static async Task Migrated_records_keep_their_classification_and_are_protected_by_it(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app, extraMappingRows: "Classification,classification,Trim,,No,,,Source sensitivity label");
        string Line(string id, string reference, string classification) => MigrationWorld.Row(id, reference, supplier: "Supplier Class Pty Ltd", vendor: "710001") + "," + classification;
        var contracts = MigrationWorld.Csv(MigrationWorld.ContractsHeader + ",Classification",
            Line("K-1", "AGR-K1", "Confidential"), Line("K-2", "AGR-K2", "Restricted"), Line("K-3", "AGR-K3", ""), Line("K-4", "AGR-K4", "Top Secret"));
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: contracts);
        var summary = await mw.ValidateAsync(batch);
        Assert.Equal(3, summary.Accepted);
        Assert.Equal(1, summary.Rejected);
        Assert.Equal(1, summary.MessagesByCode["MIG-E004"]);                 // the unknown level is an error on its own row; nothing is guessed
        await mw.ValidateAndCommitAsync(batch);

        var byReference = await mw.TargetAsync(async s =>
            (await s.Get<ContractsDbContext>().Contracts.AsNoTracking().Where(c => c.AgreementReference.StartsWith("AGR-K")).ToListAsync()).ToDictionary(c => c.AgreementReference, c => (c.Id, c.Classification)));
        Assert.Equal("Confidential", byReference["AGR-K1"].Classification);
        Assert.Equal("Restricted", byReference["AGR-K2"].Classification);
        Assert.Equal("Internal", byReference["AGR-K3"].Classification);       // the tenant's default level
        Assert.False(byReference.ContainsKey("AGR-K4"));

        // The Viewer's ceiling is Confidential: the migrated Restricted record does not exist for them, in a list or by id.
        var list = await mw.World.AsAsync("viewerAll", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200)));
        Assert.Contains(list.Items, i => i.Id == byReference["AGR-K1"].Id);
        Assert.Contains(list.Items, i => i.Id == byReference["AGR-K3"].Id);
        Assert.DoesNotContain(list.Items, i => i.Id == byReference["AGR-K2"].Id);
        await Assert.ThrowsAsync<Ecp.SharedKernel.ResourceNotFoundException>(() => mw.World.AsAsync("viewerAll", s => s.Get<ISecuredContractService>().GetAsync(byReference["AGR-K2"].Id)));
    }

    /// <summary>MIG-024 / legacy loads: no workflow notification is produced for loaded records; reminders are the only thing they can cause, once, after the load.</summary>
    public static async Task A_legacy_load_causes_no_workflow_notifications_and_only_the_reminders_it_is_due(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        async Task<List<InboxItem>> Inbox() => (await mw.World.AsAsync("ownerPackaging", s => s.Get<INotificationInbox>().ListAsync(new InboxFilter(PageSize: 200)))).Items.ToList();
        Task Jobs() => mw.World.AsScopeless().Run(s => s.Get<INotificationJobs>().RunAllAsync());
        await Jobs();                                                    // drain what the set-up of the world produced
        var before = (await Inbox()).Count;

        var end = DateOnly.FromDateTime(app.Clock.UtcNow.AddDays(20)).ToString("yyyy-MM-dd");
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: MigrationWorld.Csv(MigrationWorld.ContractsHeader,
            MigrationWorld.Row("L-1", "AGR-L1", end: end, review: ""), MigrationWorld.Row("L-2", "AGR-L2", end: "2035-12-31", review: "")));
        await mw.ValidateAndCommitAsync(batch);
        await Jobs();
        await Jobs();                                                    // running again changes nothing

        var after = await Inbox();
        Assert.DoesNotContain(after, n => n.Kind != "Reminder" && n.CreatedUtc >= app.Clock.UtcNow.AddMinutes(-5) && n.EventType != "ownership.assigned");
        var reminders = after.Where(n => n.Kind == "Reminder").ToList();
        Assert.Equal(before + 1, after.Count);
        var only = Assert.Single(reminders);
        Assert.Equal("endDate", only.ReminderKind);
        Assert.Contains("AGR-L1", only.Title + only.Body);
        Assert.DoesNotContain("AGR-L2", only.Title + only.Body);
    }
}
