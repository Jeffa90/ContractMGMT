using System.Text.Json.Nodes;
using Ecp.Configuration;
using Ecp.Identity.Access;
using Ecp.Notifications.Policy;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// S2 of the portability remediation (P-02a): the recipient of a reminder for a category nobody owns is a rule of the notification policy, from the closed recipient vocabulary. A policy published
/// before the field existed reads as it always behaved; the Blank pack states that nobody is the fall-back; an unknown rule is refused when the policy is published.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ReminderFallbackPolicyTests
{
    private static string Pack(string pack) => File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", pack, "notifications", "notification-policy.json"));

    private static string With(string json, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        change(node["reminders"]!.AsObject());
        return node.ToJsonString();
    }

    private static NotificationPolicy Parse(string json)
    {
        var (policy, messages) = NotificationPolicyParser.Parse(json);
        Assert.NotNull(policy);
        Assert.Empty(messages);
        return policy!;
    }

    [Fact]
    public void The_reference_pack_states_the_organisation_administrators_as_the_fallback_and_the_blank_pack_states_nobody()
    {
        Assert.Equal(["OrganisationAdministrators"], Parse(Pack("jbs-procurement")).Reminders.UnownedFallback);
        Assert.Empty(Parse(Pack("blank")).Reminders.UnownedFallback);
    }

    [Fact]
    public void A_policy_published_before_the_field_existed_keeps_the_behaviour_it_had()
    {
        var legacy = With(Pack("jbs-procurement"), r => r.Remove("unownedFallback"));
        Assert.Equal(ReminderRules.LegacyUnownedFallback, Parse(legacy).Reminders.UnownedFallback);
        Assert.Equal(["OrganisationAdministrators"], ReminderRules.LegacyUnownedFallback);
    }

    [Fact]
    public void An_empty_list_means_nobody_and_is_not_mistaken_for_an_absent_field()
    {
        var none = With(Pack("jbs-procurement"), r => r["unownedFallback"] = new JsonArray());
        Assert.Empty(Parse(none).Reminders.UnownedFallback);
    }

    [Fact]
    public void Only_recipient_rules_from_the_closed_vocabulary_are_accepted_once_each()
    {
        Assert.Contains("OrganisationAdministrators", PolicyVocabulary.Recipients);
        var unknown = Parse(With(Pack("blank"), r => r["unownedFallback"] = new JsonArray("ProcurementAdministrator")));
        var errors = NotificationPolicyParser.Validate(unknown, ClassificationScale.Default).Where(m => m.Severity == ValidationSeverity.Error).ToList();
        Assert.Contains(errors, m => m.Code == "recipient_unknown" && m.Path == "$.reminders.unownedFallback");

        var twice = Parse(With(Pack("blank"), r => r["unownedFallback"] = new JsonArray("Actor", "Actor")));
        Assert.Contains(NotificationPolicyParser.Validate(twice, ClassificationScale.Default), m => m.Code == "recipient_duplicate");

        var valid = Parse(With(Pack("blank"), r => r["unownedFallback"] = new JsonArray("OrganisationAdministrators")));
        Assert.DoesNotContain(NotificationPolicyParser.Validate(valid, ClassificationScale.Default), m => m.Severity == ValidationSeverity.Error);
    }
}
