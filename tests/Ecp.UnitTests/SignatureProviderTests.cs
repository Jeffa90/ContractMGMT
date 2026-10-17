using System.Text;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public sealed class SignatureProviderTests
{
    private readonly ISignatureProviderFactory _factory = Build();

    [Fact]
    public void The_mock_service_is_refused_in_Production_and_available_elsewhere()
    {
        var refused = Assert.Throws<DomainException>(() => _factory.Resolve("mock", EcpMode.Production));
        Assert.Equal("mock_in_production", refused.Code);
        Assert.Equal(SignatureProviderKind.Mock, _factory.Resolve("mock", EcpMode.Demo).Kind);
        Assert.Equal(SignatureProviderKind.Mock, _factory.Resolve("mock", EcpMode.Test).Kind);
        Assert.Equal("unknown_provider", Assert.Throws<DomainException>(() => _factory.Resolve("nope", EcpMode.Test)).Code);
        // The mock's callbacks are refused with it.
        Assert.Equal("mock_in_production", Assert.Throws<DomainException>(() => _factory.CallbackVerifier("mock", EcpMode.Production)).Code);
    }

    [Fact]
    public async Task The_unconnected_adapter_fails_closed_for_every_call()
    {
        var external = _factory.Resolve("external", EcpMode.Production);
        Assert.Equal(SignatureProviderKind.Live, external.Kind);
        var spec = new SignatureRequestSpec(Guid.NewGuid(), "R", "T", [], "k", "c");
        Assert.Equal("provider_not_configured", (await Assert.ThrowsAsync<DomainException>(() => external.CreateRequestAsync(spec))).Code);
        Assert.Equal("provider_not_configured", (await Assert.ThrowsAsync<DomainException>(() => external.GetStatusAsync("x"))).Code);
        Assert.Equal("provider_not_configured", (await Assert.ThrowsAsync<DomainException>(() => external.CancelAsync("x", "r"))).Code);
        Assert.Null(_factory.Retriever("external"));          // it supplies no executed document
    }

    [Fact]
    public async Task The_manual_provider_holds_no_request_and_the_mock_one_returns_a_labelled_synthetic_copy()
    {
        var spec = new SignatureRequestSpec(Guid.NewGuid(), "R", "T", [], "k", "c");
        Assert.Null(await _factory.Resolve("manual", EcpMode.Production).CreateRequestAsync(spec));
        var mock = _factory.Resolve("mock", EcpMode.Demo);
        var accepted = await mock.CreateRequestAsync(spec);
        Assert.StartsWith("MOCK-", accepted!.ProviderRequestId);
        Assert.Equal(SignatureRequestStatus.Sent, accepted.Status);
        var content = await _factory.Retriever("mock")!.RetrieveAsync(accepted.ProviderRequestId);
        Assert.Contains("SYNTHETIC", Encoding.ASCII.GetString(content!.Content));
    }

    [Fact]
    public void Honest_labels_are_part_of_what_the_screen_shows()
    {
        var production = _factory.Describe(EcpMode.Production).ToDictionary(p => p.Key);
        Assert.Equal("Implemented", production["manual"].Status);
        Assert.False(production["mock"].Available);
        Assert.Contains("Disabled", production["mock"].Status);
        Assert.Contains("Not Verified", production["external"].Status);
        Assert.False(production["external"].Available);
        Assert.Equal("Mocked", _factory.Describe(EcpMode.Demo).First(p => p.Key == "mock").Status);
    }

    [Theory]
    [InlineData("mock-sent", SignatureRequestStatus.Sent)]
    [InlineData("mock-delivered", SignatureRequestStatus.Delivered)]
    [InlineData("mock-completed", SignatureRequestStatus.Completed)]
    [InlineData("mock-declined", SignatureRequestStatus.Declined)]
    [InlineData("mock-voided", SignatureRequestStatus.Voided)]
    [InlineData("mock-expired", SignatureRequestStatus.Expired)]
    [InlineData("mock-failed", SignatureRequestStatus.Failed)]
    public void The_status_mapper_turns_a_provider_word_into_a_platform_status(string word, SignatureRequestStatus expected)
        => Assert.Equal(expected, _factory.StatusMapper("mock")!.Map(word));

    [Theory]
    [InlineData("completed")]            // another provider's word is not the mock's word
    [InlineData("")]
    [InlineData("MOCK-COMPLETED ")]
    [InlineData("something-new")]
    public void An_unknown_provider_status_maps_to_nothing_so_it_changes_nothing(string word)
    {
        Assert.Null(_factory.StatusMapper("mock")!.Map(word));
        Assert.Null(_factory.StatusMapper("no-such-provider"));
    }

    [Fact]
    public void Final_statuses_end_a_request_and_only_some_end_it_without_an_executed_copy()
    {
        foreach (var s in new[] { SignatureRequestStatus.Completed, SignatureRequestStatus.Declined, SignatureRequestStatus.Voided, SignatureRequestStatus.Expired, SignatureRequestStatus.Failed }) Assert.True(s.IsFinal());
        Assert.False(SignatureRequestStatus.Sent.IsFinal()); Assert.False(SignatureRequestStatus.Delivered.IsFinal());
        Assert.False(SignatureRequestStatus.Completed.EndedWithoutExecution());
        foreach (var s in new[] { SignatureRequestStatus.Declined, SignatureRequestStatus.Voided, SignatureRequestStatus.Expired, SignatureRequestStatus.Failed }) Assert.True(s.EndedWithoutExecution());
        Assert.True(SignatureRequestStatus.Sent.Rank() < SignatureRequestStatus.Delivered.Rank());
        Assert.True(SignatureRequestStatus.Delivered.Rank() < SignatureRequestStatus.Completed.Rank());
    }

    // ---- callback verification ------------------------------------------------------------------------------------------------------------------------------------------------------

    private const string Key = "unit-test-callback-key-0001";
    private static readonly DateTime Now = new(2026, 10, 18, 1, 0, 0, DateTimeKind.Utc);

    private static (ProviderCallback Callback, string Timestamp, byte[] Body) Signed(string? key = Key, DateTime? sentUtc = null, string status = "mock-completed", string eventId = "evt-1")
    {
        var ts = new DateTimeOffset(sentUtc ?? Now).ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes($$"""{"eventId":"{{eventId}}","requestId":"MOCK-1","status":"{{status}}","occurredUtc":"2026-10-18T01:00:00Z"}""");
        var sig = Convert.ToHexString(HmacCallbackVerifier.Sign(Encoding.UTF8.GetBytes(key ?? Key), ts, body));
        return (new ProviderCallback("mock", new Dictionary<string, string> { ["X-Signature"] = sig, ["x-signature-timestamp"] = ts }, body, Now), ts, body);
    }

    private static ISignatureCallbackVerifier Verifier(string? key = Key) => Build(key).CallbackVerifier("mock", EcpMode.Test);

    [Fact]
    public void A_correctly_signed_fresh_callback_is_verified_and_reduced_to_neutral_fields()
    {
        var result = Verifier().Verify(Signed().Callback);
        Assert.True(result.Verified);
        Assert.Equal(("mock", "evt-1", "MOCK-1", "mock-completed"), (result.Event!.ProviderKey, result.Event.ProviderEventId, result.Event.ProviderRequestId, result.Event.ProviderStatus));
    }

    [Fact]
    public void A_callback_is_refused_for_every_way_it_can_be_wrong()
    {
        Assert.Equal("signature_invalid", Verifier().Verify(Signed(key: "some-other-key-entirely-0002").Callback).FailureCode);
        var good = Signed();
        var tampered = good.Callback with { Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(good.Body).Replace("mock-completed", "mock-declined")) };
        Assert.Equal("signature_invalid", Verifier().Verify(tampered).FailureCode);
        Assert.Equal("timestamp_stale", Verifier().Verify(Signed(sentUtc: Now.AddMinutes(-6)).Callback).FailureCode);
        Assert.Equal("timestamp_stale", Verifier().Verify(Signed(sentUtc: Now.AddMinutes(6)).Callback).FailureCode);
        Assert.Equal("signature_missing", Verifier().Verify(good.Callback with { Headers = new Dictionary<string, string> { ["X-Signature-Timestamp"] = good.Timestamp } }).FailureCode);
        Assert.Equal("timestamp_missing", Verifier().Verify(good.Callback with { Headers = good.Callback.Headers.Where(h => !h.Key.Contains("timestamp", StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value) }).FailureCode);
        Assert.Equal("signature_invalid", Verifier().Verify(good.Callback with { Headers = new Dictionary<string, string> { ["X-Signature"] = "not hex", ["X-Signature-Timestamp"] = good.Timestamp } }).FailureCode);
        Assert.Equal("secret_not_configured", Verifier(key: null).Verify(good.Callback).FailureCode);          // no key set: every callback is refused
    }

    [Fact]
    public void A_signed_payload_the_adapter_does_not_understand_is_refused()
    {
        var ts = new DateTimeOffset(Now).ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var sig = Convert.ToHexString(HmacCallbackVerifier.Sign(Encoding.UTF8.GetBytes(Key), ts, body));
        var callback = new ProviderCallback("mock", new Dictionary<string, string> { ["X-Signature"] = sig, ["X-Signature-Timestamp"] = ts }, body, Now);
        Assert.Equal("payload_invalid", Verifier().Verify(callback).FailureCode);
    }

    /// <summary>The provider factory as the module registers it, without a container.</summary>
    private static ISignatureProviderFactory Build(string? callbackKey = Key)
    {
        var mock = new MockSignatureProvider();
        return new SignatureProviderFactory([new ManualSignatureProvider(), mock, new UnconnectedSignatureProvider()], [new MockStatusMapper()],
            [new MockCallbackVerifier(Options.Create(new LifecycleOptions { Mode = EcpMode.Test, MockSignatureCallbackKey = callbackKey }))], [mock]);
    }
}
