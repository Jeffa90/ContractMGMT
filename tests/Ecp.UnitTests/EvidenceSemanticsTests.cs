using Ecp.Recovery;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>G-37: Ready is Passed; Ready with warnings is Passed only with explicit acceptance; Blocked is Failed; Not Verified stays Not Verified; the original outcome is kept (D14.13, D14.14).</summary>
public class EvidenceSemanticsTests
{
    [Fact]
    public void Ready_is_Passed()
    {
        var j = EvidenceSemantics.Judge(PreflightOutcome.Ready);
        Assert.Equal(EvidenceVerdict.Passed, j.Verdict);
        Assert.True(j.CountsAsSuccess);
        Assert.Equal("Ready", j.Operational);
    }

    [Fact]
    public void Blocked_is_always_Failed_and_never_a_success()
    {
        var j = EvidenceSemantics.Judge(PreflightOutcome.Blocked, ["UPF-001"], ["UPF-001"]);
        Assert.Equal(EvidenceVerdict.Failed, j.Verdict);
        Assert.False(j.CountsAsSuccess);
        Assert.Equal("Blocked", j.Operational);                      // the original outcome is preserved
        Assert.Equal("EVD-020", j.Code);
    }

    [Fact]
    public void Not_Verified_stays_Not_Verified_whatever_is_accepted()
    {
        var j = EvidenceSemantics.Judge(PreflightOutcome.NotVerified, ["UPF-009"], ["UPF-009"]);
        Assert.Equal(EvidenceVerdict.NotVerified, j.Verdict);
        Assert.False(j.CountsAsSuccess);
        Assert.Equal("Not Verified", j.Operational);
    }

    [Fact]
    public void Ready_with_warnings_is_Passed_only_when_every_warning_is_accepted()
    {
        Assert.Equal(EvidenceVerdict.Warning, EvidenceSemantics.Judge(PreflightOutcome.ReadyWithWarnings, ["W1", "W2"], []).Verdict);
        Assert.Equal(EvidenceVerdict.Warning, EvidenceSemantics.Judge(PreflightOutcome.ReadyWithWarnings, ["W1", "W2"], ["W1"]).Verdict);
        var all = EvidenceSemantics.Judge(PreflightOutcome.ReadyWithWarnings, ["W1", "W2"], ["W1", "W2", "W9"]);
        Assert.Equal(EvidenceVerdict.Passed, all.Verdict);
        Assert.Equal("EVD-010", all.Code);
        Assert.Equal("Ready with warnings", all.Operational);        // still reported as what it was
    }

    [Fact]
    public void Ready_with_warnings_that_are_not_listed_cannot_be_shown_as_accepted()
        => Assert.Equal(EvidenceVerdict.Warning, EvidenceSemantics.Judge(PreflightOutcome.ReadyWithWarnings, [], ["anything"]).Verdict);

    [Theory]
    [InlineData("Blocked")]
    [InlineData("blocked")]
    [InlineData("Failed")]
    [InlineData("Unhealthy")]
    public void Failure_words_are_Failed_even_when_warnings_are_said_to_be_accepted(string word)
        => Assert.Equal(EvidenceVerdict.Failed, EvidenceSemantics.Judge(word, warningsAccepted: true).Verdict);

    [Theory]
    [InlineData("Not Verified")]
    [InlineData("NOT VERIFIED")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Probably fine")]
    [InlineData("Skipped")]
    public void Not_Verified_and_unrecognised_words_are_never_a_pass(string? word)
        => Assert.Equal(EvidenceVerdict.NotVerified, EvidenceSemantics.Judge(word, warningsAccepted: true).Verdict);

    [Theory]
    [InlineData("Ready")]
    [InlineData("Verified")]
    [InlineData("Succeeded")]
    public void Success_words_are_Passed(string word) => Assert.True(EvidenceSemantics.Judge(word).CountsAsSuccess);

    [Fact]
    public void Dashboard_and_validation_statuses_map_without_converting_failures()
    {
        Assert.Equal(EvidenceVerdict.Failed, EvidenceSemantics.Judge(OpsStatus.Failed).Verdict);
        Assert.Equal(EvidenceVerdict.NotVerified, EvidenceSemantics.Judge(OpsStatus.NotVerified).Verdict);
        Assert.Equal(EvidenceVerdict.Warning, EvidenceSemantics.Judge(OpsStatus.Warning).Verdict);
        Assert.Equal(EvidenceVerdict.Failed, EvidenceSemantics.Judge(CheckOutcome.Failed).Verdict);
        Assert.Equal(EvidenceVerdict.NotVerified, EvidenceSemantics.Judge(CheckOutcome.NotVerified).Verdict);
        Assert.Equal(EvidenceVerdict.Passed, EvidenceSemantics.Judge(CheckOutcome.Passed).Verdict);
    }

    [Fact]
    public void Every_preflight_outcome_has_exactly_one_verdict_and_only_Ready_passes_without_acceptance()
    {
        foreach (var outcome in Enum.GetValues<PreflightOutcome>())
        {
            var j = EvidenceSemantics.Judge(outcome);
            Assert.Equal(outcome == PreflightOutcome.Ready, j.CountsAsSuccess);
        }
    }

    [Fact]
    public void Labels_are_stable()
    {
        Assert.Equal("Passed", EvidenceSemantics.Label(EvidenceVerdict.Passed));
        Assert.Equal("Failed", EvidenceSemantics.Label(EvidenceVerdict.Failed));
        Assert.Equal("Not Verified", EvidenceSemantics.Label(EvidenceVerdict.NotVerified));
        Assert.Equal("Warning (not accepted)", EvidenceSemantics.Label(EvidenceVerdict.Warning));
    }
}
