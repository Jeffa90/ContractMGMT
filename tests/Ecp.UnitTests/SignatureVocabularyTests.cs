using System.Text.Json;
using Ecp.Lifecycle;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>Portability slice S3 (ADR-0027): the frozen register-authorisation snapshots written before the vocabulary was neutral must still read, and new ones must not carry the old name.</summary>
[Trait("Category", "Unit")]
public sealed class SignatureVocabularyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static RegisterSnapshot Snapshot(string? requestId) => new(
        new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "Contract", "REF-1", "Title", null, "OutForSignature", "Internal", "Supplier", "Category", "AUD", 100m, null, null, null, null, null, null, false,
        false, null, 0, null, null, "mock", requestId, "Completed", null, null, null, "workflow@1", "Approvers", "owner", [], null, null);

    [Fact]
    public void A_new_snapshot_writes_the_neutral_name_and_not_the_old_one()
    {
        var text = JsonSerializer.Serialize(Snapshot("PROVIDER-REQ-9"), Json);
        Assert.Contains("\"providerRequestId\":\"PROVIDER-REQ-9\"", text);
        Assert.DoesNotContain("envelope", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_snapshot_frozen_under_the_old_name_still_reads_and_is_left_exactly_as_stored()
    {
        // What was stored before: the request identifier under its old key. The stored text is evidence; it is hashed as it is and never rewritten.
        var stored = JsonSerializer.Serialize(Snapshot(null), Json).Replace("\"providerRequestId\":null", "\"envelopeId\":\"OLD-ENV-1\"");
        Assert.Contains("\"envelopeId\":\"OLD-ENV-1\"", stored);
        var read = JsonSerializer.Deserialize<RegisterSnapshot>(stored, Json)!.WithLegacyNames();
        Assert.Equal("OLD-ENV-1", read.ProviderRequestId);
        Assert.Null(read.LegacyRequestId);
        Assert.Equal("mock", read.ExecutionProvider);
        Assert.Equal("Completed", read.ExecutionStatus);
    }

    [Fact]
    public void A_snapshot_with_both_names_keeps_the_new_one()
    {
        var read = (JsonSerializer.Deserialize<RegisterSnapshot>(JsonSerializer.Serialize(Snapshot("NEW-1"), Json).TrimEnd('}') + ",\"envelopeId\":\"OLD-1\"}", Json))!.WithLegacyNames();
        Assert.Equal("NEW-1", read.ProviderRequestId);
    }
}
