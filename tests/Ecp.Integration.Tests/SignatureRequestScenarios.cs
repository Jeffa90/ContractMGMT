using System.Text;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Portability slice S3 (ADR-0027): the signature boundary in provider-neutral terms. The scenarios drive the mock provider and a small test adapter that has its own vocabulary, secret and payload, to prove that
/// a provider's words are mapped explicitly, events are processed once, unknown words and requests fail safely, a provider cannot move a contract, and a new adapter needs no change to the lifecycle.
/// </summary>
internal static class SignatureRequestScenarios
{
    // ---- a test adapter with a vocabulary of its own ---------------------------------------------------------------------------------------------------------------------------------

    internal sealed class AcmeAdapter : ISignatureProvider, IExecutedDocumentRetriever, ISignatureStatusMapper
    {
        public static string? NextStatusToReport;
        public static byte[] ExecutedBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% executed by the test adapter\n%%EOF\n");
        public string Key => "acme-sign";
        string IExecutedDocumentRetriever.ProviderKey => Key;
        string ISignatureStatusMapper.ProviderKey => Key;
        public string Label => "Acme signing (test adapter)";
        public SignatureProviderKind Kind => SignatureProviderKind.Live;
        public string Maturity => "Implemented";
        public string Note => "Test adapter with its own status words.";
        public Task<ProviderAcceptance?> CreateRequestAsync(SignatureRequestSpec spec, CancellationToken ct = default)
            => Task.FromResult<ProviderAcceptance?>(new ProviderAcceptance("ACME-" + Guid.NewGuid().ToString("N")[..10], SignatureRequestStatus.Sent, "sent"));
        public Task<ProviderStatusReport?> GetStatusAsync(string providerRequestId, CancellationToken ct = default)
            => Task.FromResult(NextStatusToReport is null ? null : new ProviderStatusReport(NextStatusToReport, null));
        public Task CancelAsync(string providerRequestId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ExecutedContent?> RetrieveAsync(string providerRequestId, CancellationToken ct = default)
            => Task.FromResult<ExecutedContent?>(new ExecutedContent(ExecutedBytes, providerRequestId + ".pdf", "application/pdf"));
        public SignatureRequestStatus? Map(string providerStatus) => providerStatus switch
        {
            "sent" => SignatureRequestStatus.Sent, "viewed" => SignatureRequestStatus.Delivered, "signed" => SignatureRequestStatus.Completed, "refused" => SignatureRequestStatus.Declined,
            "timed_out" => SignatureRequestStatus.Expired, "error" => SignatureRequestStatus.Failed, _ => null
        };
    }

    internal sealed class AcmeVerifier : HmacCallbackVerifier
    {
        public const string SharedKey = "acme-test-adapter-shared-key-1";
        public override string ProviderKey => "acme-sign";
        protected override string SignatureHeader => "X-Acme-Signature";
        protected override string TimestampHeader => "X-Acme-Time";
        protected override byte[]? Secret() => Encoding.UTF8.GetBytes(SharedKey);
        protected override VerifiedProviderEvent? Parse(ProviderCallback callback)
        {
            // The adapter's own payload shape stays here: {"id":"..","ref":"..","state":"..","at":".."}
            using var doc = System.Text.Json.JsonDocument.Parse(callback.Body);
            var r = doc.RootElement;
            return new VerifiedProviderEvent(ProviderKey, r.GetProperty("id").GetString()!, r.GetProperty("ref").GetString()!, r.GetProperty("state").GetString()!, r.GetProperty("at").GetDateTime().ToUniversalTime());
        }
    }

    internal static readonly Action<IServiceCollection> WithAdapter = s =>
    {
        LifecycleScenarios.Scanner(s);
        var adapter = new AcmeAdapter();
        s.AddSingleton<ISignatureProvider>(adapter);
        s.AddSingleton<IExecutedDocumentRetriever>(adapter);
        s.AddSingleton<ISignatureStatusMapper>(adapter);
        s.AddSingleton<ISignatureCallbackVerifier, AcmeVerifier>();
        s.Configure<Ecp.Lifecycle.Definitions.LifecycleOptions>(o => o.MockSignatureCallbackKey = "mock-callback-key-for-tests-0001");
    };

    private static async Task<(Guid Contract, SignatureRequestInfo Request)> OutForSignatureAsync(LifecycleWorld w, string provider)
    {
        var id = await w.ToStateAsync("InitialCreation");
        await w.AddDocumentAsync(id, "FinalAgreement", "Final agreement");
        var request = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, provider));
        return (id, request);
    }

    private static VerifiedProviderEvent Event(string provider, string eventId, string? requestId, string word, LifecycleWorld w)
        => new(provider, eventId, requestId ?? "", word, w.W.Now);

    private static Task<ProviderEventResult> Receive(LifecycleWorld w, VerifiedProviderEvent e) => w.W.AsScopeless().Run(s => s.Get<ISignatureEventInbox>().ReceiveAsync(e));

    private static Task<SignatureRequestInfo?> Current(LifecycleWorld w, Guid contract) => w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().CurrentAsync(SubjectKinds.Contract, contract));

    // ---- scenarios -------------------------------------------------------------------------------------------------------------------------------------------------------------------

    public static async Task Provider_events_are_mapped_processed_once_and_never_move_the_contract(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var (id, sent) = await OutForSignatureAsync(w, "mock");
        Assert.Equal("OutForSignature", await w.StatusAsync(id));

        // Delivered: the provider's word is kept separately; the platform status is the mapped value.
        var first = await Receive(w, Event("mock", "evt-1", sent.ProviderRequestId, "mock-delivered", w));
        Assert.Equal((ProviderEventOutcome.Applied, false, SignatureRequestStatus.Delivered), (first.Outcome, first.Duplicate, first.Status));
        var now = (await Current(w, id))!;
        Assert.Equal((SignatureRequestStatus.Delivered, "mock-delivered"), (now.Status, now.ProviderStatus));
        Assert.NotNull(now.LastProviderEventUtc);

        // The same event again (a provider retries) is acknowledged and changes nothing.
        var repeat = await Receive(w, Event("mock", "evt-1", sent.ProviderRequestId, "mock-delivered", w));
        Assert.True(repeat.Duplicate);
        Assert.Equal(1, await w.W.AsScopeless().Run(async s => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(s.Get<Ecp.Lifecycle.Persistence.LifecycleDbContext>().ProviderEvents)));

        // An older word arriving late is ignored, not applied backwards.
        var late = await Receive(w, Event("mock", "evt-2", sent.ProviderRequestId, "mock-sent", w));
        Assert.Equal(ProviderEventOutcome.Ignored, late.Outcome);
        Assert.Equal(SignatureRequestStatus.Delivered, (await Current(w, id))!.Status);

        // A word nobody mapped fails safely: kept, visible, and the platform status is untouched.
        var unknown = await Receive(w, Event("mock", "evt-3", sent.ProviderRequestId, "something-the-mapper-has-never-heard-of", w));
        Assert.Equal(ProviderEventOutcome.Unmapped, unknown.Outcome);
        var afterUnknown = (await Current(w, id))!;
        Assert.Equal(SignatureRequestStatus.Delivered, afterUnknown.Status);
        Assert.Equal("something-the-mapper-has-never-heard-of", afterUnknown.ProviderStatus);

        // An event for a request nobody holds is recorded, not applied, and says nothing about whose request it might be.
        var stranger = await Receive(w, Event("mock", "evt-4", "MOCK-DOES-NOT-EXIST", "mock-completed", w));
        Assert.Equal(ProviderEventOutcome.UnknownRequest, stranger.Outcome);
        Assert.Equal("OutForSignature", await w.StatusAsync(id));

        // Administrators see what needs attention; ordinary users do not.
        var attention = await w.As("tenantAdmin", s => s.Get<ISignatureEventInbox>().ListNeedingAttentionAsync());
        Assert.Contains(attention, e => e.ProviderEventId == "evt-3" && e.Outcome == ProviderEventOutcome.Unmapped);
        Assert.Contains(attention, e => e.ProviderEventId == "evt-4" && e.Outcome == ProviderEventOutcome.UnknownRequest);
        Assert.DoesNotContain(attention, e => e.ProviderEventId is "evt-1" or "evt-2");
        await Assert.ThrowsAnyAsync<Exception>(() => w.As("ownerPackaging", s => s.Get<ISignatureEventInbox>().ListNeedingAttentionAsync()));

        // Completed: the executed copy is stored through the normal document rules, and that is all. The contract stays where it was, unregistered and not Effective.
        var done = await Receive(w, Event("mock", "evt-5", sent.ProviderRequestId, "mock-completed", w));
        Assert.Equal((ProviderEventOutcome.Applied, SignatureRequestStatus.Completed), (done.Outcome, done.Status));
        var completed = (await Current(w, id))!;
        Assert.NotNull(completed.ExecutedDocumentId);
        Assert.NotNull(completed.CompletedUtc);
        var docs = await w.W.AsScopeless().Run(s => s.Get<IDocumentQuery>().ListForContractAsync(id));
        var executed = Assert.Single(docs, d => d.IsExecutedCopy);
        Assert.Equal(completed.ExecutedDocumentId, executed.DocumentId);
        var facts = await w.FactsAsync(id);
        Assert.Equal("OutForSignature", facts.Status);
        Assert.Null(facts.RegisteredDateUtc);

        // A request that has completed cannot be undone by a later word.
        var undo = await Receive(w, Event("mock", "evt-6", sent.ProviderRequestId, "mock-declined", w));
        Assert.Equal(ProviderEventOutcome.Ignored, undo.Outcome);
        Assert.Equal(SignatureRequestStatus.Completed, (await Current(w, id))!.Status);

        // Every change is in the audit trail, in platform words plus the provider's request id; none uses the old vendor-shaped name.
        var audit = await w.W.AsScopeless().Run(s => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(s.Get<IAuditEventSource>().Events.Where(e => e.Action.StartsWith("execution."))));
        Assert.Contains(audit, a => a.Action == "execution.started");
        Assert.Contains(audit, a => a.Action == "execution.status_changed");
        Assert.Contains(audit, a => a.Action == "execution.completed");
        Assert.Contains(audit, a => a.Action == "execution.provider_event_unhandled");
        Assert.DoesNotContain(audit, a => (a.NewValuesJson ?? "").Contains("envelope", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task A_completed_event_whose_document_the_platform_refuses_is_kept_visible_and_can_be_retried(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var (id, sent) = await OutForSignatureAsync(w, "mock");
        var options = app.Services.GetRequiredService<IOptions<DocumentsOptions>>().Value;
        var original = options.MaxFileBytes;
        options.MaxFileBytes = 40;                                                                 // the synthetic executed copy is larger than this, so the document rules refuse it
        try
        {
            var failed = await Receive(w, Event("mock", "evt-fail", sent.ProviderRequestId, "mock-completed", w));
            Assert.Equal(ProviderEventOutcome.Failed, failed.Outcome);
            var request = (await Current(w, id))!;
            Assert.NotEqual(SignatureRequestStatus.Completed, request.Status);
            Assert.Equal("executed_document_rejected", request.FailureCode);
            Assert.False(string.IsNullOrWhiteSpace(request.FailureMessage));
            Assert.DoesNotContain("   at ", request.FailureMessage!);
            Assert.DoesNotContain(await w.W.AsScopeless().Run(s => s.Get<IDocumentQuery>().ListForContractAsync(id)), d => d.IsExecutedCopy);
            Assert.Contains(await w.As("tenantAdmin", s => s.Get<ISignatureEventInbox>().ListNeedingAttentionAsync()), e => e.ProviderEventId == "evt-fail" && e.Outcome == ProviderEventOutcome.Failed);
        }
        finally { options.MaxFileBytes = original; }

        // The provider delivers the same event again, the problem is gone, and this time it is applied.
        var retried = await Receive(w, Event("mock", "evt-fail", sent.ProviderRequestId, "mock-completed", w));
        Assert.Equal((ProviderEventOutcome.Applied, false), (retried.Outcome, retried.Duplicate));
        var done = (await Current(w, id))!;
        Assert.Equal(SignatureRequestStatus.Completed, done.Status);
        Assert.Null(done.FailureCode);
        Assert.Equal(2, await w.W.AsScopeless().Run(async s => (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(s.Get<Ecp.Lifecycle.Persistence.LifecycleDbContext>().ProviderEvents)).Attempts));
    }

    public static async Task A_new_adapter_with_its_own_words_runs_end_to_end_with_no_change_to_the_lifecycle(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var (id, sent) = await OutForSignatureAsync(w, "acme-sign");
        Assert.StartsWith("ACME-", sent.ProviderRequestId);
        Assert.False(sent.IsSimulated);                                                           // a live-kind provider is not labelled simulated
        Assert.Equal("OutForSignature", await w.StatusAsync(id));

        // The adapter's verifier checks the callback with its own headers and secret and reads its own payload shape; the inbox sees only neutral fields.
        ProviderCallback Callback(string eventId, string state, string? key = null, DateTime? at = null)
        {
            var ts = new DateTimeOffset(w.W.Now).ToUnixTimeSeconds().ToString();
            var body = Encoding.UTF8.GetBytes($$"""{"id":"{{eventId}}","ref":"{{sent.ProviderRequestId}}","state":"{{state}}","at":"{{(at ?? w.W.Now):O}}"}""");
            var sig = Convert.ToHexString(HmacCallbackVerifier.Sign(Encoding.UTF8.GetBytes(key ?? AcmeVerifier.SharedKey), ts, body));
            return new ProviderCallback("acme-sign", new Dictionary<string, string> { ["X-Acme-Signature"] = sig, ["X-Acme-Time"] = ts }, body, w.W.Now);
        }
        var verifier = app.Services.GetRequiredService<ISignatureProviderFactory>().CallbackVerifier("acme-sign", Ecp.SharedKernel.EcpMode.Production);
        Assert.False(verifier.Verify(Callback("e0", "signed", key: "someone-elses-key-entirely-0002")).Verified);   // forged: never reaches the inbox

        var viewed = verifier.Verify(Callback("e1", "viewed"));
        Assert.True(viewed.Verified);
        Assert.Equal(SignatureRequestStatus.Delivered, (await Receive(w, viewed.Event!)).Status);

        var refused = verifier.Verify(Callback("e2", "refused"));
        Assert.Equal(SignatureRequestStatus.Declined, (await Receive(w, refused.Event!)).Status);
        // Declined ends the request without an executed copy, which is what lets the owner take the document back to drafting.
        await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ReturnToDrafting")));
        Assert.Equal("InitialCreation", await w.StatusAsync(id));

        // Send again; this time the provider's status is fetched by polling (the fallback) and applied through the same mapper.
        var again = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "acme-sign"));
        Assert.NotEqual(sent.ProviderRequestId, again.ProviderRequestId);
        AcmeAdapter.NextStatusToReport = "signed";
        try
        {
            var polled = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RefreshStatusAsync(SubjectKinds.Contract, id));
            Assert.Equal(SignatureRequestStatus.Completed, polled.Status);
            Assert.Equal("signed", polled.ProviderStatus);
            Assert.NotNull(polled.ExecutedDocumentId);
        }
        finally { AcmeAdapter.NextStatusToReport = null; }
        Assert.Equal("OutForSignature", await w.StatusAsync(id));                                  // the adapter completed the request; it did not touch the contract
        var approval = await w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, id, "Executed copy attached"));
        Assert.Equal("AwaitingApproval", await w.StatusAsync(id));
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(approval, "ok"));
        Assert.Equal("Effective", await w.StatusAsync(id));                                        // only register authorisation, by a person, makes it Effective
    }

    public static async Task Expired_and_failed_requests_can_be_taken_back_and_a_request_can_be_cancelled_and_repeated_safely(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var (id, sent) = await OutForSignatureAsync(w, "mock");

        // A repeat of the same request (same client request id) returns the first outcome and writes nothing new.
        var idem = await w.ToStateAsync("InitialCreation");
        await w.AddDocumentAsync(idem, "FinalAgreement", "Final agreement for the repeat");
        var one = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, idem, "mock", "client-request-1"));
        var two = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, idem, "mock", "client-request-1"));
        Assert.Equal(one.Id, two.Id);
        Assert.Equal(one.ProviderRequestId, two.ProviderRequestId);

        // Expired: the platform status is Expired, the document can go back to drafting, nothing else moves.
        Assert.Equal(SignatureRequestStatus.Expired, (await Receive(w, Event("mock", "exp-1", sent.ProviderRequestId, "mock-expired", w))).Status);
        await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ReturnToDrafting")));
        Assert.Equal("InitialCreation", await w.StatusAsync(id));

        // Failed behaves the same way.
        var again = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "mock"));
        Assert.Equal(SignatureRequestStatus.Failed, (await Receive(w, Event("mock", "fail-1", again.ProviderRequestId, "mock-failed", w))).Status);
        await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ReturnToDrafting")));

        // Cancelling ends the request as Voided without changing the contract; a request that has ended cannot be cancelled again.
        var third = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, id, "mock"));
        var cancelled = await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().CancelAsync(SubjectKinds.Contract, id, "Wrong counterparty"));
        Assert.Equal((SignatureRequestStatus.Voided, third.Id), (cancelled.Status, cancelled.Id));
        Assert.Equal("OutForSignature", await w.StatusAsync(id));
        Assert.Equal("request_final", (await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().CancelAsync(SubjectKinds.Contract, id, "again")))).Code);
        await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(id, "ReturnToDrafting")));
        Assert.Equal("InitialCreation", await w.StatusAsync(id));
        await Assert.ThrowsAnyAsync<Exception>(() => w.As("legalA", s => s.Get<ISignatureRequestService>().CancelAsync(SubjectKinds.Contract, idem, "not mine to cancel")));
    }

    public static async Task Provider_events_never_cross_tenants(TestApp app)
    {
        var a = await LifecycleWorld.CreateAsync(app);
        var b = await LifecycleWorld.CreateAsync(app);
        var (idA, sentA) = await OutForSignatureAsync(a, "mock");
        // Tenant B receives an event that carries tenant A's provider request id: it matches nothing there.
        var result = await Receive(b, Event("mock", "cross-1", sentA.ProviderRequestId, "mock-completed", b));
        Assert.Equal(ProviderEventOutcome.UnknownRequest, result.Outcome);
        Assert.Equal(SignatureRequestStatus.Sent, (await Current(a, idA))!.Status);
        // The same provider event id is a different event in each tenant.
        Assert.Equal(ProviderEventOutcome.Applied, (await Receive(a, Event("mock", "cross-1", sentA.ProviderRequestId, "mock-delivered", a))).Outcome);
    }
}

[Trait("Category", "Lifecycle")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class SignatureRequestScenarioTests_Sqlite
{
    [Fact] public Task Mapped_once_and_never_moves_the_contract() => ProviderRunner.SqliteAsync(SignatureRequestScenarios.Provider_events_are_mapped_processed_once_and_never_move_the_contract, SignatureRequestScenarios.WithAdapter);
    [Fact] public Task Refused_document_is_kept_and_retried() => ProviderRunner.SqliteAsync(SignatureRequestScenarios.A_completed_event_whose_document_the_platform_refuses_is_kept_visible_and_can_be_retried, SignatureRequestScenarios.WithAdapter);
    [Fact] public Task New_adapter_end_to_end() => ProviderRunner.SqliteAsync(SignatureRequestScenarios.A_new_adapter_with_its_own_words_runs_end_to_end_with_no_change_to_the_lifecycle, SignatureRequestScenarios.WithAdapter);
    [Fact] public Task Expired_failed_cancelled_and_repeated() => ProviderRunner.SqliteAsync(SignatureRequestScenarios.Expired_and_failed_requests_can_be_taken_back_and_a_request_can_be_cancelled_and_repeated_safely, SignatureRequestScenarios.WithAdapter);
    [Fact] public Task Tenants_are_isolated() => ProviderRunner.SqliteAsync(SignatureRequestScenarios.Provider_events_never_cross_tenants, SignatureRequestScenarios.WithAdapter);
}

[Trait("Category", "SqlServer")]
public sealed class SignatureRequestScenarioTests_SqlServer
{
    [SqlServerFact] public Task Mapped_once_and_never_moves_the_contract() => ProviderRunner.SqlServerAsync(SignatureRequestScenarios.Provider_events_are_mapped_processed_once_and_never_move_the_contract, SignatureRequestScenarios.WithAdapter);
    [SqlServerFact] public Task Refused_document_is_kept_and_retried() => ProviderRunner.SqlServerAsync(SignatureRequestScenarios.A_completed_event_whose_document_the_platform_refuses_is_kept_visible_and_can_be_retried, SignatureRequestScenarios.WithAdapter);
    [SqlServerFact] public Task New_adapter_end_to_end() => ProviderRunner.SqlServerAsync(SignatureRequestScenarios.A_new_adapter_with_its_own_words_runs_end_to_end_with_no_change_to_the_lifecycle, SignatureRequestScenarios.WithAdapter);
    [SqlServerFact] public Task Expired_failed_cancelled_and_repeated() => ProviderRunner.SqlServerAsync(SignatureRequestScenarios.Expired_and_failed_requests_can_be_taken_back_and_a_request_can_be_cancelled_and_repeated_safely, SignatureRequestScenarios.WithAdapter);
    [SqlServerFact] public Task Tenants_are_isolated() => ProviderRunner.SqlServerAsync(SignatureRequestScenarios.Provider_events_never_cross_tenants, SignatureRequestScenarios.WithAdapter);
}
