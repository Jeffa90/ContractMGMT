using Ecp.Recovery;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>G-37 regression: whatever an operation reports, the ledger and its report never convert Blocked or Failed into a pass, or Not Verified into anything.</summary>
[Trait("Category", "Evidence")]
public sealed class RehearsalLedgerSemanticsTests
{
    private static Task<string> Never() => throw new InvalidOperationException("not used");

    [Theory]
    [InlineData(PreflightOutcome.Blocked, EvidenceVerdict.Failed, "Blocked", "Failed")]
    [InlineData(PreflightOutcome.NotVerified, EvidenceVerdict.NotVerified, "Not Verified", "Not Verified")]
    [InlineData(PreflightOutcome.Ready, EvidenceVerdict.Passed, "Ready", "Passed")]
    public async Task The_ledger_records_the_operational_outcome_and_the_honest_verdict(PreflightOutcome outcome, EvidenceVerdict verdict, string operational, string label)
    {
        var ledger = new RehearsalLedger();
        await ledger.JudgedStep(1, "Pre-flight", "System Administrator", "SystemAdministrator (technical)", "/Admin/Operations/UpgradeReadiness", () => Task.FromResult(("outcome", EvidenceSemantics.Judge(outcome))), technical: true);
        var step = Assert.Single(ledger.Steps);
        Assert.Equal(operational, step.Operational);
        Assert.Equal(verdict, step.Verdict);
        var markdown = ledger.Markdown("test", "test");
        Assert.Contains($"| {operational} | {label} |", markdown);
        Assert.DoesNotContain("| Blocked | Passed |", markdown);
        Assert.DoesNotContain("| Not Verified | Passed |", markdown);
        Assert.DoesNotContain("| Failed | Passed |", markdown);
    }

    [Fact]
    public async Task Ready_with_warnings_is_not_a_pass_until_every_warning_is_accepted()
    {
        var ledger = new RehearsalLedger();
        await ledger.JudgedStep(1, "Pre-flight", "SA", "SystemAdministrator (technical)", "/x", () => Task.FromResult(("two warnings", EvidenceSemantics.Judge(PreflightOutcome.ReadyWithWarnings, ["UPF-040", "UPF-056"], ["UPF-040"]))), technical: true);
        await ledger.JudgedStep(2, "Pre-flight", "SA", "SystemAdministrator (technical)", "/x", () => Task.FromResult(("both accepted", EvidenceSemantics.Judge(PreflightOutcome.ReadyWithWarnings, ["UPF-040", "UPF-056"], ["UPF-040", "UPF-056"]))), technical: true);
        Assert.Equal(EvidenceVerdict.Warning, ledger.Steps[0].Verdict);
        Assert.Equal(EvidenceVerdict.Passed, ledger.Steps[1].Verdict);
        Assert.Equal("Ready with warnings", ledger.Steps[0].Operational);   // the original outcome is never rewritten
        Assert.Equal("Ready with warnings", ledger.Steps[1].Operational);
    }

    [Fact]
    public async Task A_step_that_throws_is_recorded_as_failed_and_rethrown()
    {
        var ledger = new RehearsalLedger();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.Step(1, "Boom", "P", "R", "/x", Never));
        Assert.Equal(EvidenceVerdict.Failed, Assert.Single(ledger.Steps).Verdict);
    }
}
