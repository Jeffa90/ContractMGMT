using System.Text.Json.Nodes;
using Ecp.Configuration;
using Ecp.Configuration.Forms;
using Ecp.Lifecycle.Definitions;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// G-03 against the real validators: the second business (Harborline) opens each kind of document in its form, saves it without touching a field and the draft validates exactly as the pack's own document did; a wrong value
/// typed in a field is found by the same validator and placed on that field; a change made in the form is a draft that goes through submission and second-person publication like any other.
/// </summary>
internal static class FormEditorScenarios
{
    private static readonly string[] Kinds = [WorkflowKinds.ContractLifecycle, WorkflowKinds.Variation, Ecp.Approvals.ApprovalsModule.MatrixKind, Ecp.Notifications.Policy.NotificationPolicyKinds.Policy];

    public static async Task Each_kind_opened_and_saved_unchanged_validates_like_the_pack_and_a_wrong_value_lands_on_its_field(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        foreach (var kind in Kinds)
        {
            var released = await w.AsAsync("admin", s => s.Get<IConfigurationService>().LatestReleaseAsync(kind, w.Org));
            Assert.NotNull(released);
            var state = await w.AsAsync("admin", s => s.Get<IConfigurationFormEditor>().StartAsync(kind, w.Org));
            Assert.NotNull(state.Draft);
            var (document, bindProblems) = FormBinder.Bind(state.Schema, FormBinder.Flatten(state.Schema, state.Document));
            Assert.Empty(bindProblems);
            var draft = await w.AsAsync("admin", s => s.Get<IConfigurationFormEditor>().ValidateAsync(kind, w.Org, document));
            Assert.True(draft.Validation!.IsValid, kind + ": " + string.Join("; ", draft.Validation.Messages.Select(m => m.Path + " " + m.Message)));
            await w.AsAsync("admin", s => s.Get<IConfigurationService>().DiscardDraftAsync(draft.Id));
        }

        // A wrong value typed into a field is found by the platform's own validator, and shown on that field.
        var wf = await w.AsAsync("admin", s => s.Get<IConfigurationFormEditor>().StartAsync(WorkflowKinds.ContractLifecycle, w.Org));
        var edited = FormBinder.Flatten(wf.Schema, wf.Document);
        edited["f.transitions.0.to"] = ["NoSuchStage"];
        edited["f.settings.unlock.maxMinutes"] = ["2"];
        var (bad, problems) = FormBinder.Bind(wf.Schema, edited);
        Assert.Empty(problems);
        var checkedDraft = await w.AsAsync("admin", s => s.Get<IConfigurationFormEditor>().ValidateAsync(WorkflowKinds.ContractLifecycle, w.Org, bad));
        Assert.False(checkedDraft.Validation!.IsValid);
        var placed = await w.AsAsync("admin", s => Task.FromResult(s.Get<IConfigurationFormEditor>().PlaceProblems(wf.Schema, bad, checkedDraft.Validation!.Messages)));
        Assert.Contains(placed, p => p.IsError && p.Path == "transitions.0.to");
        Assert.Contains(placed, p => p.IsError && p.Path == "settings.unlock.maxMinutes");
        Assert.DoesNotContain(placed, p => p.IsError && p.Path.Length == 0 && p.Message.Contains("NoSuchStage"));
    }

    public static async Task A_change_made_in_the_form_is_a_draft_that_a_second_person_publishes_and_history_keeps_both_releases(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        await w.AddAdministratorAsync("admin2");
        await using (var s = w.AsScopeless()) { s.Get<ConfigurationOptions>().RequireSecondPersonPublish = true; }
        try
        {
            var kind = Ecp.Notifications.Policy.NotificationPolicyKinds.Policy;
            var before = await w.AsAsync("admin", s => s.Get<IConfigurationService>().LatestReleaseAsync(kind, w.Org));
            var state = await w.AsAsync("admin", s => s.Get<IConfigurationFormEditor>().StartAsync(kind, w.Org));
            var posted = FormBinder.Flatten(state.Schema, state.Document);
            var firstTemplate = state.Document["templates"]!.AsObject().First().Key;
            posted["f.templates.0.subject"] = ["Updated subject for {{contract.reference}}"];
            var (document, problems) = FormBinder.Bind(state.Schema, posted);
            Assert.Empty(problems);
            var draft = await w.AsAsync("admin", s => s.Get<IConfigurationFormEditor>().ValidateAsync(kind, w.Org, document));
            Assert.True(draft.Validation!.IsValid);

            var insight = await w.AsAsync("admin", s => s.Get<IConfigurationFormEditor>().InsightAsync(kind, w.Org, document));
            Assert.NotNull(insight);
            Assert.Contains(insight!.Previews.SelectMany(t => t.Rows), r => r.Any(c => c.Contains("Updated subject for HBL") || c.Contains("Updated subject for REF-000123")));

            var outcome = await w.AsAsync("admin", s => s.Get<IConfigurationService>().SubmitOrPublishAsync(draft.Id, "Clearer subject"));
            Assert.Null(outcome.Release);                                                  // submitted, not published
            await Assert.ThrowsAnyAsync<Exception>(() => w.AsAsync("admin", s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "mine")));     // the author cannot
            var published = await w.AsAsync("admin2", s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "Agreed"));
            Assert.Equal(before!.Number + 1, published.Number);
            Assert.Contains("Updated subject for", published.ContentJson);
            Assert.Contains(firstTemplate, published.ContentJson);
            var history = await w.AsAsync("admin", s => s.Get<IConfigurationService>().ReleasesAsync(kind, w.Org));
            Assert.Contains(history, r => r.Number == before.Number);
            Assert.DoesNotContain("Updated subject for", history.Single(r => r.Number == before.Number).ContentJson);
        }
        finally { await using var s = w.AsScopeless(); s.Get<ConfigurationOptions>().RequireSecondPersonPublish = false; }
    }
}

[Trait("Category", "Governance")]
public sealed class FormEditorSqliteTests
{
    [Fact] public Task Kinds() => ProviderRunner.SqliteAsync(FormEditorScenarios.Each_kind_opened_and_saved_unchanged_validates_like_the_pack_and_a_wrong_value_lands_on_its_field);
    [Fact] public Task Publication() => ProviderRunner.SqliteAsync(FormEditorScenarios.A_change_made_in_the_form_is_a_draft_that_a_second_person_publishes_and_history_keeps_both_releases);
}

[Trait("Category", "SqlServer")]
public sealed class FormEditorSqlServerTests
{
    [SqlServerFact] public Task Kinds() => ProviderRunner.SqlServerAsync(FormEditorScenarios.Each_kind_opened_and_saved_unchanged_validates_like_the_pack_and_a_wrong_value_lands_on_its_field);
    [SqlServerFact] public Task Publication() => ProviderRunner.SqlServerAsync(FormEditorScenarios.A_change_made_in_the_form_is_a_draft_that_a_second_person_publishes_and_history_keeps_both_releases);
}
