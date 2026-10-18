using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Contracts.Register;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Representative business journeys, run on an upgraded copy through the real services as the real people of each business (docs/UPGRADES.md, "Post-upgrade validation"): a Blank contract is created and moved; a
/// reference-pack contract takes a lifecycle action; a Harborline subcontract is signed, submitted, approved by the people its matrix names and becomes Active; the register is searched and exported; notifications are
/// captured; a document is downloaded; and every audit chain verifies. Each journey asserts; the returned lines are the evidence of what ran.
/// </summary>
internal static class UpgradeJourneys
{
    private static async Task<T> As<T>(TestApp app, Guid tenant, Guid user, Func<TenantScope, Task<T>> work)
    {
        await using var s = app.ScopeFor(tenant);
        s.Get<CurrentUser>().Set(user);
        var actor = s.Get<ActorContext>(); actor.Type = ActorType.User; actor.ActorId = user.ToString(); actor.Source = "Test";
        return await work(s);
    }

    public static async Task<List<string>> RunAsync(SyntheticInstallation data, TestApp copy)
    {
        var log = new List<string>();
        var tenant = data.TenantId;
        var blankUser = data.Lw.W.Users[data.Blank.User];

        // 1. Blank: create a contract and move it.
        var blankId = await As(copy, tenant, blankUser, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(data.Blank.Org, "BRB-900001", "Journey agreement (blank)", data.Blank.Supplier, data.Blank.Category,
            CurrencyCode: "AUD", EstimatedValue: 750m, EndDate: DateOnly.FromDateTime(copy.Clock.UtcNow.AddDays(200)))));
        var before = (await As(copy, tenant, blankUser, s => s.Get<ILifecycleService>().StateAsync(blankId))).Status;
        await As(copy, tenant, blankUser, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(blankId, "Activate")));
        var after = (await As(copy, tenant, blankUser, s => s.Get<ILifecycleService>().StateAsync(blankId))).Status;
        Assert.NotEqual(before, after);
        log.Add($"Blank: created a contract and moved it from {before} to {after}.");

        // 2. Reference pack: a lifecycle action on the contract that was in flight before the upgrade.
        var jbsUser = data.Lw.W.Users[data.Jbs.User];
        var available = await As(copy, tenant, jbsUser, s => s.Get<ILifecycleService>().AvailableAsync(data.JbsContract));
        Assert.NotEmpty(available);
        var pick = available.FirstOrDefault(t => t.Allowed && !t.CommentRequired && !t.ReasonRequired);
        if (pick is not null)
        {
            var was = (await As(copy, tenant, jbsUser, s => s.Get<ILifecycleService>().StateAsync(data.JbsContract))).Status;
            await As(copy, tenant, jbsUser, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(data.JbsContract, pick.Code)));
            var now = (await As(copy, tenant, jbsUser, s => s.Get<ILifecycleService>().StateAsync(data.JbsContract))).Status;
            Assert.Equal(pick.ToStatus, now);
            log.Add($"Reference pack: the in-flight contract took '{pick.Code}' from {was} to {now}.");
        }
        else log.Add($"Reference pack: the in-flight contract offers {available.Count} transition(s), none that can be taken without a comment or reason.");

        // 3. Harborline: sign, submit, approve with the people the matrix names, Active.
        var harbor = data.Harbor.On(copy);
        await harbor.SignAsync(data.HarborContract);
        var request = await harbor.AsAsync("packageManagerA", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, data.HarborContract, "Executed copy attached"));
        await harbor.ApproveAllAsync(request);
        Assert.Equal("Active", await harbor.StatusAsync(data.HarborContract));
        log.Add("Harborline: the subcontract was signed on the mock provider, submitted, approved by the matrix's own approvers and is Active.");

        // 4. Search and export.
        var page = await harbor.AsAsync("commercial", s => s.Get<IContractRegister>().SearchAsync(new ContractQuery(OrganisationId: harbor.Org, PageSize: 50)));
        Assert.True(page.Rows.Count >= 1);
        var export = await harbor.AsAsync("commercial", s => s.Get<IContractExports>().ExportAsync(new ExportRequest(new ContractQuery(OrganisationId: harbor.Org, PageSize: 50), ExportFormat.Csv, Columns: ["agreementReference", "contractTitle", "supplier", "status", "estimatedValue", "endDate"])));
        Assert.True(export.Rows >= 1);
        Assert.StartsWith("text/csv", export.ContentType);
        log.Add($"Search and export: {page.Rows.Count} row(s) found and {export.Rows} exported as {export.FileName}.");

        // 5. Notifications: the outbox runs and the messages are captured, once each.
        var captured0 = await harbor.AsScopeless().Run(async s => await s.Get<Ecp.Notifications.Persistence.NotificationsDbContext>().Captured.CountAsync());
        await harbor.RunJobsAsync();
        var captured1 = await harbor.AsScopeless().Run(async s => await s.Get<Ecp.Notifications.Persistence.NotificationsDbContext>().Captured.CountAsync());
        Assert.True(captured1 >= captured0);
        await harbor.RunJobsAsync();
        var captured2 = await harbor.AsScopeless().Run(async s => await s.Get<Ecp.Notifications.Persistence.NotificationsDbContext>().Captured.CountAsync());
        Assert.Equal(captured1, captured2);       // idempotent: running the jobs again sends nothing twice
        log.Add($"Notifications: {captured1 - captured0} message(s) captured by the job run, none repeated by a second run.");

        // 6. A document is downloaded, and its bytes are the ones that were stored.
        var documents = await harbor.AsAsync("packageManagerA", s => s.Get<ISecuredContractDocuments>().ListAsync(data.HarborContract));
        var downloadable = documents.First(d => d.CanDownload);
        var download = await harbor.AsAsync("packageManagerA", s => s.Get<ISecuredContractDocuments>().OpenAsync(downloadable.DocumentId));
        Assert.NotNull(download);
        using var buffer = new MemoryStream();
        await download!.Content.CopyToAsync(buffer);
        await download.Content.DisposeAsync();
        Assert.True(buffer.Length > 0);
        Assert.Equal(downloadable.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())));
        log.Add("Document download: the executed copy downloaded and hashed to its recorded SHA-256.");

        // 7. Audit verification for the tenant that did all this.
        await using (var s = copy.ScopeFor(tenant))
        {
            var verified = await s.Get<AuditChainVerifier>().VerifyAsync();
            Assert.True(verified.IsValid, verified.Reason);
            log.Add($"Audit: the tenant's chain verifies ({verified.EventsChecked} events), including everything these journeys added.");
        }
        return log;
    }
}

