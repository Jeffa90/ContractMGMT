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
}
