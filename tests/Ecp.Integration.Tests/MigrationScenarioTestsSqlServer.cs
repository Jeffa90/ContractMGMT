using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Migration")]
[Trait("Category", "SqlServer")]
public sealed class MigrationScenarioTests_SqlServer
{
    private static async Task RunAsync(Func<TestApp, Task> scenario)
    {
        await using var app = await TestApp.CreateSqlServerAsync();
        await scenario(app);
    }

    private static async Task RunWithScannerAsync(Func<TestApp, Task> scenario)
    {
        await using var app = await TestApp.CreateSqlServerAsync(migrate: true, configure: s => s.AddSingleton<Ecp.Documents.IMalwareScanner, Ecp.Documents.MockMalwareScanner>());
        await scenario(app);
    }

    private static async Task RunWithFaultsAsync(Func<TestApp, FaultPlan, Task> scenario)
    {
        var faults = new FaultPlan();
        await using var app = await TestApp.CreateSqlServerAsync(migrate: true, configure: s => s.AddSingleton<Ecp.DataMigration.IMigrationFaults>(faults));
        await scenario(app, faults);
    }

    [SqlServerFact] public Task Migrated_classification_is_kept_and_protects() => RunAsync(MigrationClassificationScenario.Migrated_records_keep_their_classification_and_are_protected_by_it);
    [SqlServerFact] public Task The_sample_contracts_manifest_and_archive_load_end_to_end() => RunAsync(MigrationScenarios.The_sample_contracts_manifest_and_archive_load_end_to_end);
    [SqlServerFact] public Task A_dry_run_writes_nothing_to_the_target() => RunAsync(MigrationScenarios.A_dry_run_writes_nothing_to_the_target);
    [SqlServerFact] public Task Error_and_reconciliation_totals_equal_the_processed_input() => RunAsync(MigrationScenarios.Error_and_reconciliation_totals_equal_the_processed_input_and_nothing_is_cut_or_executed);
    [SqlServerFact] public Task The_mapping_preview_shows_original_and_transformed_values() => RunAsync(MigrationScenarios.The_mapping_preview_shows_original_and_transformed_values_and_how_many_rows_each_mapping_changes);
    [SqlServerFact] public Task A_legacy_load_needs_a_policy_a_second_person_and_what_was_validated() => RunAsync(MigrationScenarios.A_legacy_load_needs_a_policy_a_second_person_and_exactly_what_was_validated);
    [SqlServerFact] public Task The_source_registered_date_follows_the_policy_and_is_never_made_up() => RunAsync(MigrationScenarios.The_source_registered_date_is_kept_only_when_the_policy_allows_it_and_never_made_up);
    [SqlServerFact] public Task A_stopped_commit_resumes_without_losing_or_duplicating_anything() => RunWithFaultsAsync(MigrationScenarios.A_stopped_commit_resumes_from_its_checkpoints_without_losing_or_duplicating_anything);
    [SqlServerFact] public Task Re_running_a_file_is_idempotent_and_updating_keeps_before_images() => RunAsync(MigrationScenarios.Re_running_a_file_is_idempotent_and_updating_changes_only_mapped_fields_with_before_images);
    [SqlServerFact] public Task Each_duplicate_strategy_behaves_as_specified() => RunAsync(MigrationScenarios.Each_duplicate_strategy_behaves_as_specified_and_agreement_references_are_unique_across_the_tenant);
    [SqlServerFact] public Task The_reference_policy_can_generate_every_target_reference() => RunAsync(MigrationScenarios.The_reference_policy_can_generate_every_target_reference_and_keeps_the_source_reference);
    [SqlServerFact] public Task Rollback_removes_only_the_selected_batch_and_keeps_shared_data() => RunAsync(MigrationScenarios.Rollback_removes_only_the_selected_batchs_reversible_changes_and_keeps_shared_data);
    [SqlServerFact] public Task A_record_that_has_moved_on_is_archived_not_deleted() => RunAsync(MigrationScenarios.A_record_that_has_moved_on_is_archived_not_deleted_and_a_batch_with_activity_still_rolls_back);
    [SqlServerFact] public Task After_acceptance_rollback_is_refused_and_compensation_keeps_everything() => RunAsync(MigrationScenarios.After_acceptance_rollback_is_refused_and_compensating_archival_keeps_everything);
    [SqlServerFact] public Task Unmatched_files_wait_in_a_queue_until_a_person_resolves_or_discards_them() => RunAsync(MigrationScenarios.Unmatched_files_wait_in_a_queue_until_a_person_resolves_or_discards_them);
    [SqlServerFact] public Task Attachments_match_in_a_fixed_order_and_the_platform_never_breaks_a_tie() => RunAsync(MigrationScenarios.Attachments_match_in_a_fixed_order_and_the_platform_never_breaks_a_tie);
    [SqlServerFact] public Task Unsafe_archive_entries_and_infected_files_never_reach_the_target() => RunWithScannerAsync(MigrationScenarios.Unsafe_archive_entries_and_infected_files_never_reach_the_target);
    [SqlServerFact] public Task Production_holds_attachments_when_no_scanner_can_clear_them() => RunAsync(MigrationScenarios.Production_holds_attachments_when_no_scanner_can_clear_them);
    [SqlServerFact] public Task Another_tenant_and_people_without_the_permission_cannot_see_or_run_a_migration() => RunAsync(MigrationScenarios.Another_tenant_and_people_without_the_permission_cannot_see_or_run_a_migration);
    [SqlServerFact] public Task The_cutover_freeze_stops_people_creating_contracts_but_not_the_migration() => RunAsync(MigrationScenarios.The_cutover_freeze_stops_people_creating_contracts_but_not_the_migration);
    [SqlServerFact] public Task A_batch_keeps_the_profile_version_it_was_created_with() => RunAsync(MigrationScenarios.A_batch_keeps_the_profile_version_it_was_created_with);
    [SqlServerFact] public Task Missing_categories_are_created_only_when_the_profile_says_so() => RunAsync(MigrationScenarios.Missing_categories_are_created_only_when_the_profile_says_so_and_rollback_removes_them);
    [SqlServerFact] public Task Spreadsheet_and_UTF16_sources_load_exactly_like_CSV() => RunAsync(MigrationScenarios.Spreadsheet_and_UTF16_sources_load_exactly_like_CSV);
}
