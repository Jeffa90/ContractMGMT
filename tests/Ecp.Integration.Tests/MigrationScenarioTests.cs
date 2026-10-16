using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Migration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class MigrationScenarioTests_Sqlite
{
    private static async Task RunAsync(Func<TestApp, Task> scenario)
    {
        await using var app = await TestApp.CreateSqliteAsync();
        await scenario(app);
    }

    private static async Task RunWithFaultsAsync(Func<TestApp, FaultPlan, Task> scenario)
    {
        var faults = new FaultPlan();
        await using var app = await TestApp.CreateSqliteAsync(configure: s => s.AddSingleton<Ecp.DataMigration.IMigrationFaults>(faults));
        await scenario(app, faults);
    }

    [Fact] public Task The_sample_contracts_manifest_and_archive_load_end_to_end() => RunAsync(MigrationScenarios.The_sample_contracts_manifest_and_archive_load_end_to_end);
    [Fact] public Task A_dry_run_writes_nothing_to_the_target() => RunAsync(MigrationScenarios.A_dry_run_writes_nothing_to_the_target);
    [Fact] public Task Error_and_reconciliation_totals_equal_the_processed_input() => RunAsync(MigrationScenarios.Error_and_reconciliation_totals_equal_the_processed_input_and_nothing_is_cut_or_executed);
    [Fact] public Task The_mapping_preview_shows_original_and_transformed_values() => RunAsync(MigrationScenarios.The_mapping_preview_shows_original_and_transformed_values_and_how_many_rows_each_mapping_changes);
    [Fact] public Task A_legacy_load_needs_a_policy_a_second_person_and_what_was_validated() => RunAsync(MigrationScenarios.A_legacy_load_needs_a_policy_a_second_person_and_exactly_what_was_validated);
    [Fact] public Task The_source_registered_date_follows_the_policy_and_is_never_made_up() => RunAsync(MigrationScenarios.The_source_registered_date_is_kept_only_when_the_policy_allows_it_and_never_made_up);
    [Fact] public Task A_stopped_commit_resumes_without_losing_or_duplicating_anything() => RunWithFaultsAsync(MigrationScenarios.A_stopped_commit_resumes_from_its_checkpoints_without_losing_or_duplicating_anything);
    [Fact] public Task Re_running_a_file_is_idempotent_and_updating_keeps_before_images() => RunAsync(MigrationScenarios.Re_running_a_file_is_idempotent_and_updating_changes_only_mapped_fields_with_before_images);
    [Fact] public Task Each_duplicate_strategy_behaves_as_specified() => RunAsync(MigrationScenarios.Each_duplicate_strategy_behaves_as_specified_and_agreement_references_are_unique_across_the_tenant);
    [Fact] public Task The_reference_policy_can_generate_every_target_reference() => RunAsync(MigrationScenarios.The_reference_policy_can_generate_every_target_reference_and_keeps_the_source_reference);
    [Fact] public Task Rollback_removes_only_the_selected_batch_and_keeps_shared_data() => RunAsync(MigrationScenarios.Rollback_removes_only_the_selected_batchs_reversible_changes_and_keeps_shared_data);
    [Fact] public Task A_record_that_has_moved_on_is_archived_not_deleted() => RunAsync(MigrationScenarios.A_record_that_has_moved_on_is_archived_not_deleted_and_a_batch_with_activity_still_rolls_back);
    [Fact] public Task After_acceptance_rollback_is_refused_and_compensation_keeps_everything() => RunAsync(MigrationScenarios.After_acceptance_rollback_is_refused_and_compensating_archival_keeps_everything);
}
