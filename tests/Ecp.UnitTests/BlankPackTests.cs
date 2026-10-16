using Ecp.Configuration;
using Ecp.Identity.Access;
using Ecp.Lifecycle.Definitions;
using Ecp.Notifications.Policy;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// S1 of the portability remediation: the Blank pack ships a neutral lifecycle, Variation workflow and notification policy as data, so an organisation on it has a working starting point that names no
/// other business. They go through exactly the validators the JBS reference definitions do. The state codes are chosen not to collide with the JBS lifecycle, which is what lets the proof test
/// (PRT-002, first step) tell the two apart.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BlankPackTests
{
    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", "blank", relative));
    private static IReadOnlyList<ValidationMessage> Validate(string json, string subject)
    {
        var (def, parse) = WorkflowDefinitionParser.Parse(json);
        Assert.NotNull(def);
        Assert.Empty(parse);
        return WorkflowDefinitionParser.Validate(def!, subject, PermissionMatrix.Instance);
    }

    [Fact]
    public void The_neutral_lifecycle_is_valid_and_names_no_jbs_state()
    {
        var json = Read("workflows/contract-lifecycle.json");
        var messages = Validate(json, "Contract");
        Assert.Empty(PackFiles.Errors(messages));
        var def = WorkflowDefinitionParser.Parse(json).Definition!;
        Assert.Equal("Open", def.Initial.Code);
        Assert.False(def.Initial.Locked);
        var jbs = PackFiles.Lifecycle().States.Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
        Assert.All(def.States, s => Assert.DoesNotContain(s.Code, jbs));
        // An active agreement is locked: changes need a Variation or an audited unlock, the same invariant as every pack.
        Assert.True(def.State("Active")!.Locked);
        // No Legal review, signature or approval step: the engine reports the steps this lifecycle does not offer as warnings, not errors.
        Assert.All(messages.Where(m => m.Code == "role_missing"), m => Assert.Equal(ValidationSeverity.Warning, m.Severity));
        Assert.NotEmpty(def.WithRole("openVariation"));
        Assert.NotEmpty(def.WithRole("applyVariation"));
    }

    [Fact]
    public void The_neutral_variation_workflow_is_valid()
    {
        var json = Read("workflows/variation.json");
        Assert.Empty(PackFiles.Errors(Validate(json, "Variation")));
        var def = WorkflowDefinitionParser.Parse(json).Definition!;
        Assert.Equal("applied", def.State(Assert.Single(def.WithRole("approve")).To)!.Role);
        Assert.Equal("None", def.Settings.Variation.LegalReview);
    }

    [Fact]
    public void The_neutral_notification_policy_is_valid_and_defines_the_reminder_rules()
    {
        var (policy, parse) = NotificationPolicyParser.Parse(Read("notifications/notification-policy.json"));
        Assert.NotNull(policy);
        Assert.Empty(parse);
        var messages = NotificationPolicyParser.Validate(policy!, ClassificationScale.Default);
        Assert.Empty(messages);                                        // not even a warning: all three reminder rules are present
        Assert.NotNull(policy!.RuleFor("reminder.reviewDate"));
        Assert.NotNull(policy.RuleFor("reminder.endDate"));
        Assert.NotNull(policy.RuleFor("reminder.criticalNotice"));
    }

    [Fact]
    public void No_blank_pack_file_names_another_business()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot.Find(), "templates", "packs", "blank"), "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var word in new[] { "jbs", "procurement", "medius", "qlik", "docusign", "GroupHead" })
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} contains '{word}'");
        }
    }
}
