using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ReadinessScenarioTests_Sqlite
{
    private static async Task RunAsync(Func<TestApp, Task> scenario) { await using var app = await TestApp.CreateSqliteAsync(); await scenario(app); }

    [Fact] public Task What_is_Required_comes_from_the_pack() => RunAsync(ReadinessScenarios.What_is_Required_comes_from_the_selected_pack_and_the_platform_insists_only_on_its_universal_items);
    [Fact] public Task Publishing_is_refused_while_a_Required_item_is_open() => RunAsync(ReadinessScenarios.Publishing_is_refused_in_words_while_a_Required_item_of_the_pack_is_open_and_allowed_once_it_is_done);
    [Fact] public Task An_owner_without_the_role_needs_attention() => RunAsync(ReadinessScenarios.An_owner_without_the_role_that_lets_owners_act_needs_attention_not_a_green_tick);
    [Fact] public Task The_checklist_is_authorised() => RunAsync(ReadinessScenarios.The_checklist_is_authorised_like_the_rest_of_onboarding);
    [Fact] public void The_vocabulary_matches_the_checklist_and_the_shipped_packs() => ReadinessVocabularyTests.The_checklist_and_the_pack_vocabulary_name_the_same_items_and_the_shipped_packs_use_them_correctly();
}

[Trait("Category", "SqlServer")]
public sealed class ReadinessScenarioTests_SqlServer
{
    private static async Task RunAsync(Func<TestApp, Task> scenario) { await using var app = await TestApp.CreateSqlServerAsync(); await scenario(app); }

    [SqlServerFact] public Task What_is_Required_comes_from_the_pack() => RunAsync(ReadinessScenarios.What_is_Required_comes_from_the_selected_pack_and_the_platform_insists_only_on_its_universal_items);
    [SqlServerFact] public Task Publishing_is_refused_while_a_Required_item_is_open() => RunAsync(ReadinessScenarios.Publishing_is_refused_in_words_while_a_Required_item_of_the_pack_is_open_and_allowed_once_it_is_done);
    [SqlServerFact] public Task An_owner_without_the_role_needs_attention() => RunAsync(ReadinessScenarios.An_owner_without_the_role_that_lets_owners_act_needs_attention_not_a_green_tick);
    [SqlServerFact] public Task The_checklist_is_authorised() => RunAsync(ReadinessScenarios.The_checklist_is_authorised_like_the_rest_of_onboarding);
}
