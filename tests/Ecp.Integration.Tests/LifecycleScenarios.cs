using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>The contract lifecycle, Legal review, execution, register authorisation and locking, end to end on a real provider.</summary>
internal static class LifecycleScenarios
{
    public static readonly Action<IServiceCollection> Scanner = s => s.AddSingleton<IMalwareScanner, MockMalwareScanner>();

    public static async Task A_contract_goes_from_draft_to_effective_through_every_stage(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.NewDraftAsync();
        Assert.Equal("Draft", await w.StatusAsync(id));
        // The step to Legal carries its own data, so the generic call refuses it.
        var managed = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "SubmitForLegal"))));
        Assert.Equal("managed_transition", managed.Code);

        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, id, "Please review")));
        Assert.Equal("AwaitingLegal", await w.StatusAsync(id));
        var review = (await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Contract, id)))!;
        Assert.Equal(LegalReviewStatus.AwaitingLegal, review.Status);
        await w.As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(review.ReviewId));
        Assert.Equal("InLegalReview", await w.StatusAsync(id));
        await w.As("legalA", s => s.Get<ILegalReviewService>().ClearAsync(review.ReviewId, "Acceptable"));
        Assert.Equal("LegalCleared", await w.StatusAsync(id));
        await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "RegisterInitial")));
        Assert.Equal("InitialCreation", await w.StatusAsync(id));

        await w.AddDocumentAsync(id, "FinalAgreement", "Final agreement");
        await w.As("ownerPackaging", s => s.Get<IExecutionService>().SendForSignatureAsync(SubjectKinds.Contract, id, "manual"));
        Assert.Equal("OutForSignature", await w.StatusAsync(id));
        // Register submission needs the executed copy.
        var noCopy = await Assert.ThrowsAsync<TransitionBlockedException>(() => w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, null)));
        Assert.Contains("executed copy", noCopy.Message, StringComparison.OrdinalIgnoreCase);
        await w.As("ownerPackaging", s => s.Get<IExecutionService>().RecordExecutedCopyAsync(new RecordExecutedCopyRequest(SubjectKinds.Contract, id, "executed.pdf", "application/pdf", LifecycleWorld.Bytes("executed"))));
        var request = await w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "Executed"));
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));

        // Legal clearance and execution did not register anything; the record is not locked or registered.
        var before = await w.FactsAsync(id);
        Assert.Null(before.RegisteredDateUtc);
        Assert.False(before.RecordLocked);

        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "Approved"));
        var after = await w.FactsAsync(id);
        Assert.Equal("Effective", after.Status);
        Assert.True(after.RecordLocked);
        Assert.NotNull(after.RegisteredDateUtc);
        Assert.Equal(DateTimeKind.Utc, after.RegisteredDateUtc!.Value.Kind);

        var history = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().HistoryAsync(id));
        Assert.Equal(["SubmitForLegal", "BeginLegalReview", "ClearLegal", "RegisterInitial", "SendForSignature", "SubmitApproval", "Approve"], history.Select(h => h.TransitionCode).ToArray());
    }

    public static async Task A_locked_contract_refuses_every_crafted_step_and_edit_whoever_asks(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToEffectiveInOrderAsync();
        var lifecycle = (Func<TenantScope, ILifecycleService>)(s => s.Get<ILifecycleService>());
        // Every read the lifecycle page makes works on an Effective record.
        var steps = await w.As("ownerPackaging", s => lifecycle(s).AvailableAsync(id));
        Assert.Contains(steps, t => t.Code == "CreateVariation" && t.Allowed);
        Assert.Equal("Effective", (await w.As("ownerPackaging", s => lifecycle(s).StateAsync(id))).Status);
        Assert.NotEmpty(await w.As("ownerPackaging", s => lifecycle(s).HistoryAsync(id)));
        Assert.Empty(await w.As("ownerPackaging", s => s.Get<IVariationService>().ListForContractAsync(id)));
        Assert.Empty(await w.As("ownerPackaging", s => s.Get<IRenewalService>().ListForContractAsync(id)));
        // The owner cannot withdraw, approve or invent a step on an Effective record.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => lifecycle(s).TransitionAsync(new TransitionRequest(id, "WithdrawInitial", "x", "x"))));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => lifecycle(s).TransitionAsync(new TransitionRequest(id, "Approve", "x"))));
        var unknown = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => lifecycle(s).TransitionAsync(new TransitionRequest(id, "SetStatusToDraft"))));
        Assert.Equal("unknown_transition", unknown.Code);
        // A step that exists but not from here fails clearly, and the contract is unchanged.
        var invalid = await Assert.ThrowsAsync<DomainException>(() => w.As("legalA", s => lifecycle(s).TransitionAsync(new TransitionRequest(id, "ClearLegal", "x"))));
        Assert.Contains("not available", invalid.Message);
        Assert.Equal("Effective", await w.StatusAsync(id));
        // Edits to the record's content are refused for the owner and for the administrator alike.
        await Assert.ThrowsAnyAsync<Exception>(() => w.As("ownerPackaging", s => s.Get<ISecuredContractService>().UpdateDetailsAsync(id, "Renamed", null)));
        await Assert.ThrowsAnyAsync<Exception>(() => w.As("procAdmin", s => s.Get<ISecuredContractService>().UpdateDetailsAsync(id, "Renamed", null)));
        Assert.Equal("Effective", await w.StatusAsync(id));
        Assert.Equal("Lifecycle", (await w.FactsAsync(id)).Title[..9]);
    }
}
