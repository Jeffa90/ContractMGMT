using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

// One test per scenario per provider, so a missing SQL Server shows as NOT VERIFIED per scenario rather than silently shrinking the suite.
[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class SetupScenarioTests_Sqlite
{
    private static async Task RunAsync(Func<TestApp, Task> scenario, EcpMode mode = EcpMode.Test)
    {
        await using var app = await TestApp.CreateSqliteAsync(mode);
        await scenario(app);
    }

    [Fact] public Task A_full_installation_completes() => RunAsync(SetupScenarios.A_full_installation_completes_and_leaves_a_consistent_system);
    [Fact] public Task Setup_resumes_after_restart() => RunAsync(SetupScenarios.Setup_resumes_after_a_restart_without_repeating_or_duplicating_work);
    [Fact] public Task Database_copy_restores_state() => RunAsync(SetupScenarios.The_database_copy_restores_state_when_the_bootstrap_file_is_lost);
    [Fact] public Task Stages_are_idempotent() => RunAsync(SetupScenarios.Every_configuring_stage_is_idempotent);
    [Fact] public Task Dependencies_are_enforced() => RunAsync(SetupScenarios.Stages_cannot_run_before_the_stages_they_depend_on);
    [Fact] public Task Invalid_input_is_refused() => RunAsync(SetupScenarios.Invalid_input_is_refused_with_a_message_and_changes_nothing);
    [Fact] public Task Token_and_session_rules() => RunAsync(SetupScenarios.The_token_is_single_use_expires_and_only_one_session_can_be_open);
    [Fact] public Task Wrong_tokens_lock_entry() => RunAsync(SetupScenarios.Repeated_wrong_tokens_lock_the_entry_for_a_while);
    [Fact] public Task State_file_holds_hash_only() => RunAsync(SetupScenarios.The_state_file_holds_a_token_hash_never_the_token);
    [Fact] public Task Setup_closes_after_completion() => RunAsync(SetupScenarios.After_completion_setup_is_closed_and_only_amendable_stages_can_be_reopened);
    [Fact] public Task Report_is_sanitised() => RunAsync(SetupScenarios.The_report_contains_no_secrets_tokens_references_or_personal_addresses);
    [Fact] public Task Readiness_gates_completion() => RunAsync(SetupScenarios.Readiness_blocks_completion_until_blocking_checks_pass_and_warnings_are_acknowledged);
    [Fact] public Task Production_refuses_demonstration_features() => RunAsync(SetupScenarios.Production_refuses_demonstration_features_and_unsafe_configuration, EcpMode.Production);
}

[Trait("Category", "SqlServer")]
public sealed class SetupScenarioTests_SqlServer
{
    private static async Task RunAsync(Func<TestApp, Task> scenario, EcpMode mode = EcpMode.Test)
    {
        await using var app = await TestApp.CreateSqlServerAsync(setupMode: mode);
        await scenario(app);
    }

    [SqlServerFact] public Task A_full_installation_completes() => RunAsync(SetupScenarios.A_full_installation_completes_and_leaves_a_consistent_system);
    [SqlServerFact] public Task Setup_resumes_after_restart() => RunAsync(SetupScenarios.Setup_resumes_after_a_restart_without_repeating_or_duplicating_work);
    [SqlServerFact] public Task Database_copy_restores_state() => RunAsync(SetupScenarios.The_database_copy_restores_state_when_the_bootstrap_file_is_lost);
    [SqlServerFact] public Task Stages_are_idempotent() => RunAsync(SetupScenarios.Every_configuring_stage_is_idempotent);
    [SqlServerFact] public Task Dependencies_are_enforced() => RunAsync(SetupScenarios.Stages_cannot_run_before_the_stages_they_depend_on);
    [SqlServerFact] public Task Invalid_input_is_refused() => RunAsync(SetupScenarios.Invalid_input_is_refused_with_a_message_and_changes_nothing);
    [SqlServerFact] public Task Token_and_session_rules() => RunAsync(SetupScenarios.The_token_is_single_use_expires_and_only_one_session_can_be_open);
    [SqlServerFact] public Task Wrong_tokens_lock_entry() => RunAsync(SetupScenarios.Repeated_wrong_tokens_lock_the_entry_for_a_while);
    [SqlServerFact] public Task State_file_holds_hash_only() => RunAsync(SetupScenarios.The_state_file_holds_a_token_hash_never_the_token);
    [SqlServerFact] public Task Setup_closes_after_completion() => RunAsync(SetupScenarios.After_completion_setup_is_closed_and_only_amendable_stages_can_be_reopened);
    [SqlServerFact] public Task Report_is_sanitised() => RunAsync(SetupScenarios.The_report_contains_no_secrets_tokens_references_or_personal_addresses);
    [SqlServerFact] public Task Readiness_gates_completion() => RunAsync(SetupScenarios.Readiness_blocks_completion_until_blocking_checks_pass_and_warnings_are_acknowledged);
    [SqlServerFact] public Task Production_refuses_demonstration_features() => RunAsync(SetupScenarios.Production_refuses_demonstration_features_and_unsafe_configuration, EcpMode.Production);
}
