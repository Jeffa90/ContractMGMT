using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>G-31: the governance mode is explicit and fail-safe, and nothing lowers Production (ADR-0038, D14.2 to D14.4).</summary>
public class GovernanceModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Default")]
    [InlineData("Enforced")]
    [InlineData("Off")]
    [InlineData("false")]
    [InlineData("Simplified")]
    [InlineData("garbage")]
    public void Production_always_enforces_the_second_person_rule_whatever_the_setting_says(string? setting)
    {
        var mode = GovernanceMode.Resolve(EcpMode.Production, setting);
        Assert.Equal(GovernanceKind.Production, mode.Kind);
        Assert.True(mode.SecondPersonRequired);
        Assert.True(mode.IsProductionEquivalent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Default")]
    [InlineData("default")]
    public void Test_keeps_the_simplified_rule_unless_enforcement_is_asked_for(string? setting)
    {
        var mode = GovernanceMode.Resolve(EcpMode.Test, setting);
        Assert.Equal(GovernanceKind.TestSimplified, mode.Kind);
        Assert.False(mode.SecondPersonRequired);
        Assert.Contains("Enforced", mode.Note);
    }

    [Theory]
    [InlineData("Enforced")]
    [InlineData("enforced")]
    [InlineData(" Enforced ")]
    public void Test_enforces_the_production_rule_when_asked(string setting)
    {
        var mode = GovernanceMode.Resolve(EcpMode.Test, setting);
        Assert.Equal(GovernanceKind.TestEnforced, mode.Kind);
        Assert.True(mode.SecondPersonRequired);
        Assert.Equal("TestEnforced", mode.Name);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("false")]
    [InlineData("Enforce")]
    [InlineData("garbage")]
    public void An_unrecognised_value_in_Test_fails_safe_to_the_stricter_rule_and_says_so(string setting)
    {
        var mode = GovernanceMode.Resolve(EcpMode.Test, setting);
        Assert.Equal(GovernanceKind.TestEnforced, mode.Kind);
        Assert.True(mode.SecondPersonRequired);
        Assert.Contains("not recognised", mode.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Default")]
    [InlineData("Enforced")]
    public void Demo_ignores_the_setting_and_keeps_simplified_governance(string? setting)
    {
        var mode = GovernanceMode.Resolve(EcpMode.Demo, setting);
        Assert.Equal(GovernanceKind.DemoSimplified, mode.Kind);
        Assert.False(mode.SecondPersonRequired);
        Assert.Contains("Test mode", mode.Note);
    }

    [Fact]
    public void The_composition_derives_the_rule_only_from_the_resolved_mode()
    {
        // The host must never read the setting directly into the configuration options: the rule comes from GovernanceMode.Resolve, which Production cannot lower.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "src", "Ecp.Host", "PlatformComposition.cs"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var text = File.ReadAllText(Path.Combine(root!, "src", "Ecp.Host", "PlatformComposition.cs"));
        Assert.Contains("o.RequireSecondPersonPublish = governance.SecondPersonRequired", text);
        Assert.DoesNotContain("RequireSecondPersonPublish = production", text);
        Assert.DoesNotContain("RequireSecondPersonPublish = configuration", text);
    }
}
