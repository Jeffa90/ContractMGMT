using Ecp.Lifecycle;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public sealed class SignatureProviderTests
{
    private readonly ESignatureProviderFactory _factory = new();

    [Fact]
    public void The_mock_service_is_refused_in_Production_and_available_elsewhere()
    {
        var refused = Assert.Throws<DomainException>(() => _factory.Resolve("mock", EcpMode.Production));
        Assert.Equal("mock_in_production", refused.Code);
        Assert.True(_factory.Resolve("mock", EcpMode.Demo).IsSimulated);
        Assert.True(_factory.Resolve("mock", EcpMode.Test).IsSimulated);
        Assert.Equal("unknown_provider", Assert.Throws<DomainException>(() => _factory.Resolve("nope", EcpMode.Test)).Code);
    }

    [Fact]
    public async Task The_external_adapter_fails_closed_for_every_call()
    {
        var external = _factory.Resolve("external", EcpMode.Production);
        Assert.False(external.IsSimulated);
        Assert.Equal("provider_not_configured", (await Assert.ThrowsAsync<DomainException>(() => external.CreateEnvelopeAsync(new EnvelopeRequest(Guid.NewGuid(), "R", "T", [])))).Code);
        Assert.Equal("provider_not_configured", (await Assert.ThrowsAsync<DomainException>(() => external.GetStatusAsync("x"))).Code);
        Assert.Equal("provider_not_configured", (await Assert.ThrowsAsync<DomainException>(() => external.FetchExecutedAsync("x"))).Code);
        Assert.Equal("provider_not_configured", (await Assert.ThrowsAsync<DomainException>(() => external.VoidAsync("x", "r"))).Code);
    }

    [Fact]
    public async Task The_manual_provider_creates_no_envelope_and_the_mock_one_returns_a_labelled_synthetic_copy()
    {
        Assert.Null(await _factory.Resolve("manual", EcpMode.Production).CreateEnvelopeAsync(new EnvelopeRequest(Guid.NewGuid(), "R", "T", [])));
        var mock = _factory.Resolve("mock", EcpMode.Demo);
        var envelope = await mock.CreateEnvelopeAsync(new EnvelopeRequest(Guid.NewGuid(), "R", "T", []));
        Assert.StartsWith("MOCK-", envelope!.EnvelopeId);
        var content = await mock.FetchExecutedAsync(envelope.EnvelopeId);
        Assert.Contains("SYNTHETIC", System.Text.Encoding.ASCII.GetString(content!.Content));
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
}
