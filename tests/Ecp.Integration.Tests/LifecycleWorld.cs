using System.Text;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>The standard access world plus the people the lifecycle needs: two Legal reviewers, the register-authorisation approver and a backup, and helpers to take a contract through its workflow.</summary>
internal sealed class LifecycleWorld
{
    public const string ApproverGroup = "GroupHeadProcurement";
    public AccessWorld W { get; }
    public TestApp App { get; }
    private int _n;

    private LifecycleWorld(AccessWorld w, TestApp app) { W = w; App = app; }

    public static async Task<LifecycleWorld> CreateAsync(TestApp app)
    {
        var w = await AccessWorld.CreateAsync(app, "TenantL" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());
        var lw = new LifecycleWorld(w, app);
        await lw.UserAsync("legalA", RoleCodes.Legal, null, "Restricted");
        await lw.UserAsync("legalB", RoleCodes.Legal, null, "Restricted");
        await lw.UserAsync("groupHead", RoleCodes.Approver, ApproverGroup, "Confidential");
        await lw.UserAsync("backupHead", RoleCodes.BackupApprover, ApproverGroup, "Confidential");
        await lw.UserAsync("otherApprover", RoleCodes.Approver, "SomeOtherGroup", "Confidential");
        return lw;
    }

    public async Task<Guid> UserAsync(string key, string role, string? group, string ceiling, bool privileged = false)
    {
        await using var s = W.AsScopeless();
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync(key, key + "@" + W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", key + "|" + W.TenantId, "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, null, ScopeSet.ForOrganisations([W.Org1], ceiling), group, EffectiveFromUtc: W.Now.AddDays(-5), ExplicitHighCeiling: true, PrivilegedAccess: privileged),
            AssignmentSource.Demo, "test");
        W.Users[key] = id;
        return id;
    }

    public static MemoryStream Bytes(string text = "synthetic document") => new(Encoding.UTF8.GetBytes(text));

    /// <summary>A new draft contract created by the Packaging owner through the secured path (so it is pinned to the workflow in force).</summary>
    public async Task<Guid> NewDraftAsync(string classification = "Internal", bool legal = true, string? title = null, DateOnly? end = null, DateOnly? review = null, string? extension = null, int? lead = null,
        int? notice = null, bool critical = false, string owner = "ownerPackaging", Guid? category = null)
    {
        var n = Interlocked.Increment(ref _n);
        var id = await W.AsAsync(owner, s => s.Get<ISecuredContractService>().CreateDraftAsync(
            new CreateContractRequest(W.Org1, $"LC-{W.TenantId.ToString("N")[..5].ToUpperInvariant()}-{n:000}", title ?? $"Lifecycle {n}", W.Sup1, category ?? W.CatPackaging, Classification: classification, LegalReviewRequired: legal,
                CurrencyCode: "AUD", EstimatedValue: 1000m, ExtensionType: extension, ReviewDate: review, EndDate: end ?? DateOnly.FromDateTime(W.Now.AddDays(300)))));
        if (lead is not null || notice is not null || critical)
            await W.AsAsync(owner, s => s.Get<ISecuredContractService>().UpdateReminderSettingsAsync(id, lead, notice, critical));
        return id;
    }

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(W.Now, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));

    public Task<IReadOnlyList<JobRow>> RunJobsAsync() => W.AsScopeless().Run(s => s.Get<INotificationJobs>().RunAllAsync());

    public async Task<List<InboxItem>> InboxAsync(string user) => (await As(user, s => s.Get<INotificationInbox>().ListAsync(new InboxFilter(PageSize: 200)))).Items.ToList();

    public async Task<List<Ecp.Notifications.Domain.CapturedEmail>> CapturedAsync()
        => await W.AsScopeless().Run(async s => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(s.Get<Ecp.Notifications.Persistence.NotificationsDbContext>().Captured));

    public Task<string> StatusAsync(Guid contractId) => W.AsScopeless().Run(async s => (await s.Get<IContractLifecyclePort>().FindAsync(contractId))!.Status);

    public Task<ContractFacts> FactsAsync(Guid contractId) => W.AsScopeless().Run(async s => (await s.Get<IContractLifecyclePort>().FindAsync(contractId))!);

    public Task<T> As<T>(string user, Func<TenantScope, Task<T>> work) => W.AsAsync(user, work);
    public Task As(string user, Func<TenantScope, Task> work) => W.AsAsync(user, work);

    public async Task AddDocumentAsync(Guid contractId, string type, string title, string user = "ownerPackaging")
        => await As(user, s => s.Get<ISecuredContractDocuments>().AddAsync(new AddContractDocumentRequest(contractId, type, title, title + ".pdf", "application/pdf", Bytes(title))));

    /// <summary>Takes a contract from Draft to Awaiting Approval, with Legal review first when it requires it.</summary>
    public async Task<Guid> ToAwaitingApprovalAsync(Guid contractId, bool legal = true)
    {
        if (legal)
        {
            await As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, contractId, "Please review")));
            var review = await As("ownerPackaging", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Contract, contractId));
            await As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(review!.ReviewId));
            await As("legalA", s => s.Get<ILegalReviewService>().ClearAsync(review!.ReviewId, "Acceptable"));
            await As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contractId, "RegisterInitial")));
        }
        else await As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contractId, "ProceedWithoutLegal")));
        await AddDocumentAsync(contractId, "FinalAgreement", "Final agreement");
        await As("ownerPackaging", s => s.Get<IExecutionService>().SendForSignatureAsync(SubjectKinds.Contract, contractId, "manual"));
        await As("ownerPackaging", s => s.Get<IExecutionService>().RecordExecutedCopyAsync(new RecordExecutedCopyRequest(SubjectKinds.Contract, contractId, "executed.pdf", "application/pdf", Bytes("executed"))));
        return await As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, contractId, "Executed copy attached"));
    }

    /// <summary>An Effective agreement (no Legal step) with the given dates and reminder settings.</summary>
    public async Task<Guid> ToEffectiveInOrderAsync(DateOnly? end = null, DateOnly? review = null, string? extension = null, int? lead = null, int? notice = null, bool critical = false)
    {
        var id = await NewDraftAsync(legal: false, end: end, review: review, extension: extension, lead: lead, notice: notice, critical: critical);
        var request = await ToAwaitingApprovalAsync(id, legal: false);
        await As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "Approved"));
        return id;
    }

    /// <summary>Takes a contract all the way to Effective.</summary>
    public async Task<Guid> ToEffectiveAsync(bool legal = true, string classification = "Internal")
    {
        var id = await NewDraftAsync(classification, legal);
        var request = await ToAwaitingApprovalAsync(id, legal);
        await As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "Approved"));
        return id;
    }

    public async Task<Guid> ToStateAsync(string state)
    {
        switch (state)
        {
            case "Effective": return await ToEffectiveInOrderAsync();
            case "Draft": return await NewDraftAsync();
        }
        var id = await NewDraftAsync(legal: state is "AwaitingLegal" or "InLegalReview" or "LegalReworkRequired" or "LegalCleared");
        Guid review = Guid.Empty;
        async Task<Guid> Review() => (await As("ownerPackaging", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Contract, id)))!.ReviewId;
        if (state is "AwaitingLegal" or "InLegalReview" or "LegalReworkRequired" or "LegalCleared")
        {
            await As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, "review")));
            if (state == "AwaitingLegal") return id;
            review = await Review();
            await As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(review));
            if (state == "InLegalReview") return id;
            if (state == "LegalReworkRequired") { await As("legalA", s => s.Get<ILegalReviewService>().ReturnForReworkAsync(review, "rework")); return id; }
            await As("legalA", s => s.Get<ILegalReviewService>().ClearAsync(review, null));
            return id;
        }
        await As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ProceedWithoutLegal")));
        if (state == "InitialCreation" || state == "Withdrawn")
        {
            if (state == "Withdrawn") await As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "WithdrawInitial", null, "no longer needed")));
            return id;
        }
        await AddDocumentAsync(id, "FinalAgreement", "Final agreement");
        await As("ownerPackaging", s => s.Get<IExecutionService>().SendForSignatureAsync(SubjectKinds.Contract, id, "manual"));
        if (state == "OutForSignature") return id;
        await As("ownerPackaging", s => s.Get<IExecutionService>().RecordExecutedCopyAsync(new RecordExecutedCopyRequest(SubjectKinds.Contract, id, "executed.pdf", "application/pdf", Bytes("executed"))));
        var request = await As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "go"));
        if (state == "AwaitingApproval") return id;
        await As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "ok"));
        switch (state)
        {
            case "VariationInProgress": await As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, SimpleVariation())); break;
            case "RenewalInProgress": await As("ownerPackaging", s => s.Get<IRenewalService>().StartAsync(id, null)); break;
            case "Suspended": await As("procAdmin", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "Suspend", null, "dispute under review"))); break;
            case "Ended": case "FlaggedForDisposition": case "DisposedArchived":
                await As("procAdmin", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "End")));
                if (state != "Ended") await As("procAdmin", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "FlagEnded", null, "retention period over")));
                if (state == "DisposedArchived") await As("groupHead", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ArchiveFlagged")));
                break;
            case "Terminated": await As("procAdmin", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "Terminate", null, "breach"))); break;
            default: throw new ArgumentException("Unknown state " + state);
        }
        return id;
    }

    public static VariationInput SimpleVariation(string? end = null, string? value = "2500", bool legal = false)
        => new("Extend and reprice", "Extension agreed with the supplier", "Commercial renegotiation", null, legal, 1500m, "AUD", null,
            new Dictionary<string, string?> { ["estimatedValue"] = value, ["endDate"] = end ?? "2031-06-30" });

    /// <summary>Takes a Variation from Draft to waiting for register authorisation (no Legal step).</summary>
    public async Task<(Guid VariationId, Guid RequestId)> VariationToApprovalAsync(Guid contractId, VariationInput? input = null)
    {
        var variation = await As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(contractId, input ?? SimpleVariation()));
        await As("ownerPackaging", s => s.Get<IVariationService>().TransitionAsync(variation, "ProceedWithoutLegal", null));
        await As("ownerPackaging", s => s.Get<IVariationService>().AddDocumentAsync(new AddVariationDocumentRequest(variation, "Final", "Deed of variation", "deed.pdf", "application/pdf", Bytes("deed"))));
        await As("ownerPackaging", s => s.Get<IExecutionService>().SendForSignatureAsync(SubjectKinds.Variation, variation, "manual"));
        await As("ownerPackaging", s => s.Get<IExecutionService>().RecordExecutedCopyAsync(new RecordExecutedCopyRequest(SubjectKinds.Variation, variation, "signed.pdf", "application/pdf", Bytes("signed"))));
        var request = await As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Variation, variation, "go"));
        return (variation, request);
    }
}

internal static class ScopeExtensions
{
    /// <summary>Runs work in a scope and disposes it.</summary>
    public static async Task<T> Run<T>(this TenantScope scope, Func<TenantScope, Task<T>> work)
    {
        await using (scope) return await work(scope);
    }
}