using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "ApprovalMatrix")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ApprovalMatrixScenarioTests_Sqlite
{
    [Fact] public Task Resolution_explains() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios.Resolution_chooses_the_row_names_the_people_and_explains_every_choice, LifecycleScenarios.Scanner);
    [Fact] public Task Ineligible_people() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios.The_submitter_and_people_who_cannot_open_the_record_are_never_approvers, LifecycleScenarios.Scanner);
    [Fact] public Task Backups() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios.Backup_approvers_and_backup_groups_stand_in_where_the_step_allows_it, LifecycleScenarios.Scanner);
    [Fact] public Task Effective_dates() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios.Effective_dates_decide_which_rows_rates_and_people_apply_on_a_given_day, LifecycleScenarios.Scanner);
    [Fact] public Task Delegation_rules() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios.Delegation_is_bounded_audited_non_transitive_and_never_circular, LifecycleScenarios.Scanner);
    [Fact] public Task Delegation_edge_cases() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios.A_delegation_from_someone_who_cannot_act_is_ignored_and_a_circle_in_the_data_gives_nobody_authority, LifecycleScenarios.Scanner);
    [Fact] public Task Tenant_isolation() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios.Matrices_delegations_and_resolutions_belong_to_one_tenant, LifecycleScenarios.Scanner);
    [Fact] public Task Two_stage_flow() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.A_two_stage_approval_opens_each_stage_in_turn_and_registers_only_at_the_end, LifecycleScenarios.Scanner);
    [Fact] public Task Rejection() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.A_rejection_at_any_open_step_returns_the_contract_and_ends_the_plan, LifecycleScenarios.Scanner);
    [Fact] public Task Parallel_all_required_delegate() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.Parallel_steps_open_together_an_all_required_step_needs_every_member_and_a_delegate_covers_one, LifecycleScenarios.Scanner);
    [Fact] public Task Backup_needs_delegation() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.A_backup_without_a_delegation_cannot_complete_an_all_required_step, LifecycleScenarios.Scanner);
    [Fact] public Task Blocked_submission() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.A_submission_nobody_can_approve_is_blocked_with_the_reason_and_can_be_resubmitted_once_fixed, LifecycleScenarios.Scanner);
    [Fact] public Task Pinned_plan() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.A_plan_is_pinned_to_the_release_it_was_made_under, LifecycleScenarios.Scanner);
    [Fact] public Task Reference_model_unchanged() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.The_reference_model_is_unchanged_when_no_matrix_is_named, LifecycleScenarios.Scanner);
    [Fact] public Task Matrix_equals_reference_choice() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.A_matrix_that_models_the_reference_choice_names_exactly_the_same_people, LifecycleScenarios.Scanner);
    [Fact] public Task Variation_through_matrix() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.A_Variation_is_approved_through_the_matrix_its_workflow_names, LifecycleScenarios.Scanner);
    [Fact] public Task Classification_and_exceptions() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.Row_conditions_on_classification_and_exceptions_shape_who_approves, LifecycleScenarios.Scanner);
    [Fact] public Task Simulator_is_read_only_and_audited() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios3.The_simulator_shows_the_resolution_without_touching_any_record_and_is_for_administrators_only, LifecycleScenarios.Scanner);
    [Fact] public Task Simulator_sources() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios3.The_simulator_can_try_a_numbered_release_or_an_unpublished_draft_before_anyone_relies_on_it, LifecycleScenarios.Scanner);
    [Fact] public Task Publication_rules() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios3.Publication_refuses_a_matrix_with_gaps_overlaps_or_ties_and_one_a_workflow_still_needs, LifecycleScenarios.Scanner);
    [Fact] public Task Empty_group_warning() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios3.A_group_nobody_holds_is_a_warning_at_publication_and_a_block_at_submission, LifecycleScenarios.Scanner);
    [Fact] public Task Simulator_matches_a_real_submission() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios2.The_simulator_and_a_real_submission_resolve_to_the_same_steps_and_people, LifecycleScenarios.Scanner);
    [Fact] public Task Audit_history() => ProviderRunner.SqliteAsync(ApprovalMatrixScenarios3.Every_publication_rollback_delegation_and_simulation_is_in_the_audit_history_with_what_changed, LifecycleScenarios.Scanner);
}

[Trait("Category", "ApprovalMatrix")]
[Trait("Category", "SqlServer")]
public sealed class ApprovalMatrixScenarioTests_SqlServer
{
    [SqlServerFact] public Task Resolution_explains() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios.Resolution_chooses_the_row_names_the_people_and_explains_every_choice, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Ineligible_people() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios.The_submitter_and_people_who_cannot_open_the_record_are_never_approvers, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Backups() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios.Backup_approvers_and_backup_groups_stand_in_where_the_step_allows_it, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Effective_dates() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios.Effective_dates_decide_which_rows_rates_and_people_apply_on_a_given_day, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Delegation_rules() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios.Delegation_is_bounded_audited_non_transitive_and_never_circular, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Delegation_edge_cases() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios.A_delegation_from_someone_who_cannot_act_is_ignored_and_a_circle_in_the_data_gives_nobody_authority, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Tenant_isolation() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios.Matrices_delegations_and_resolutions_belong_to_one_tenant, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Two_stage_flow() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.A_two_stage_approval_opens_each_stage_in_turn_and_registers_only_at_the_end, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Rejection() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.A_rejection_at_any_open_step_returns_the_contract_and_ends_the_plan, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Parallel_all_required_delegate() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.Parallel_steps_open_together_an_all_required_step_needs_every_member_and_a_delegate_covers_one, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Backup_needs_delegation() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.A_backup_without_a_delegation_cannot_complete_an_all_required_step, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Blocked_submission() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.A_submission_nobody_can_approve_is_blocked_with_the_reason_and_can_be_resubmitted_once_fixed, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Pinned_plan() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.A_plan_is_pinned_to_the_release_it_was_made_under, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Last_response_race() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.The_last_response_is_decided_once_even_when_two_approvers_answer_at_the_same_moment, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task All_required_race() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.Two_members_of_an_all_required_step_answering_together_are_both_recorded, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Reference_model_unchanged() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.The_reference_model_is_unchanged_when_no_matrix_is_named, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Matrix_equals_reference_choice() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.A_matrix_that_models_the_reference_choice_names_exactly_the_same_people, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Variation_through_matrix() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.A_Variation_is_approved_through_the_matrix_its_workflow_names, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Classification_and_exceptions() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.Row_conditions_on_classification_and_exceptions_shape_who_approves, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Simulator_is_read_only_and_audited() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios3.The_simulator_shows_the_resolution_without_touching_any_record_and_is_for_administrators_only, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Simulator_sources() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios3.The_simulator_can_try_a_numbered_release_or_an_unpublished_draft_before_anyone_relies_on_it, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Publication_rules() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios3.Publication_refuses_a_matrix_with_gaps_overlaps_or_ties_and_one_a_workflow_still_needs, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Empty_group_warning() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios3.A_group_nobody_holds_is_a_warning_at_publication_and_a_block_at_submission, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Simulator_matches_a_real_submission() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios2.The_simulator_and_a_real_submission_resolve_to_the_same_steps_and_people, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Audit_history() => ProviderRunner.SqlServerAsync(ApprovalMatrixScenarios3.Every_publication_rollback_delegation_and_simulation_is_in_the_audit_history_with_what_changed, LifecycleScenarios.Scanner);
}
