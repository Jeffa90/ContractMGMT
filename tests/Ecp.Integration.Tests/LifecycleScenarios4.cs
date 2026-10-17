using Ecp.Contracts;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

internal static class LifecycleScenarios4
{
    public static async Task The_signature_boundary_stores_the_request_and_status_and_a_completed_request_only_supplies_the_executed_copy(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var id = await w.ToStateAsync("InitialCreation");
        await w.AddDocumentAsync(id, "FinalAgreement", "Final agreement");

        // The external adapter is a scaffold: it fails closed and the contract does not move.
        var external = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "external")));
        Assert.Equal("provider_not_configured", external.Code);
        await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "carrier-pigeon")));
        Assert.Equal("InitialCreation", await w.StatusAsync(id));
        Assert.Null(await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().CurrentAsync(SubjectKinds.Contract, id)));

        // Sent through the mock service: a provider request id and status are stored, and the contract is out for signature.
        var sent = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "mock"));
        Assert.StartsWith("MOCK-", sent.ProviderRequestId);
        Assert.Equal(SignatureRequestStatus.Sent, sent.Status);
        Assert.True(sent.IsSimulated);
        Assert.Equal("OutForSignature", await w.StatusAsync(id));

        // Not yet signed: register authorisation is refused (clearance, signature and authorisation are separate).
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "too early")));
        Assert.Equal(SignatureRequestStatus.Delivered, (await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Contract, id, SignatureRequestStatus.Delivered))).Status);

        // Declined: the owner takes it back to drafting and sends it again; the earlier request is superseded.
        await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Contract, id, SignatureRequestStatus.Declined));
        await Assert.ThrowsAnyAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "declined")));
        await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ReturnToDrafting")));
        Assert.Equal("InitialCreation", await w.StatusAsync(id));
        var again = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "mock"));
        Assert.NotEqual(sent.ProviderRequestId, again.ProviderRequestId);
        Assert.Equal(again.Id, (await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().CurrentAsync(SubjectKinds.Contract, id)))!.Id);

        // Completed: only the executed-copy check is satisfied. The contract is not registered and not locked until an approver responds.
        var done = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Contract, id, SignatureRequestStatus.Completed));
        Assert.Equal(SignatureRequestStatus.Completed, done.Status);
        Assert.NotNull(done.ExecutedDocumentId);
        var facts = await w.FactsAsync(id);
        Assert.Equal("OutForSignature", facts.Status);
        Assert.Null(facts.RegisteredDateUtc);
        Assert.False(facts.RecordLocked);
        var request = await w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "Executed copy attached"));
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "ok"));
        Assert.Equal("Effective", await w.StatusAsync(id));

        // Only the owner side drives the boundary: Legal and viewers cannot send, upload or simulate.
        var other = await w.ToStateAsync("InitialCreation");
        await w.AddDocumentAsync(other, "FinalAgreement", "Final agreement 2");
        foreach (var user in new[] { "legalA", "viewerAll", "groupHead" })
            Assert.True((await Assert.ThrowsAnyAsync<Exception>(() => w.As(user, s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, other, "mock")))) is ForbiddenException or ResourceNotFoundException);
        Assert.Equal("InitialCreation", await w.StatusAsync(other));
    }
}
