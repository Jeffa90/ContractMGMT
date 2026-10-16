using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "CustomFields")]
public sealed class CustomFieldScenarioTests_Sqlite
{
    [Fact] public Task Types_round_trip() => ProviderRunner.SqliteAsync(CustomFieldScenarios.Every_field_type_is_stored_read_changed_and_cleared_through_the_secured_path, LifecycleScenarios.Scanner);
    [Fact] public Task Validation_defaults_required() => ProviderRunner.SqliteAsync(CustomFieldScenarios.Invalid_values_defaults_and_requiredness_are_enforced_on_the_server_and_entered_values_are_kept, LifecycleScenarios.Scanner);
    [Fact] public Task Role_state_type_rules() => ProviderRunner.SqliteAsync(CustomFieldScenarios.A_field_is_shown_and_editable_only_to_the_roles_states_and_contract_types_it_names, LifecycleScenarios.Scanner);
    [Fact] public Task Classified_fields() => ProviderRunner.SqliteAsync(CustomFieldScenarios.Classified_and_privileged_fields_are_hidden_from_people_without_the_level, LifecycleScenarios.Scanner);
    [Fact] public Task Locked_contract() => ProviderRunner.SqliteAsync(CustomFieldScenarios.A_locked_contract_keeps_its_configured_values_until_a_Variation_or_audited_unlock_changes_them, LifecycleScenarios.Scanner);
    [Fact] public Task Versions_and_retirement() => ProviderRunner.SqliteAsync(CustomFieldScenarios.Definitions_are_versioned_retired_never_deleted_and_the_values_they_hold_are_protected, LifecycleScenarios.Scanner);
    [Fact] public Task Tenant_isolation_and_cache() => ProviderRunner.SqliteAsync(CustomFieldScenarios.Definitions_and_values_belong_to_one_tenant_and_the_published_cache_never_crosses_or_goes_stale, LifecycleScenarios.Scanner);
    [Fact] public Task Failed_publish() => ProviderRunner.SqliteAsync(CustomFieldScenarios.A_failed_publish_leaves_the_published_definitions_and_the_cache_unchanged, LifecycleScenarios.Scanner);
    [Fact] public Task Cache_failure() => ProviderRunner.SqliteAsync(CacheFailureScenarios.A_cache_that_fails_never_changes_an_answer_and_a_failed_invalidation_stops_the_publish, CacheFailureScenarios.Configure);
    [Fact] public Task Change_history() => ProviderRunner.SqliteAsync(ChangeHistoryScenarios.The_change_history_shows_each_edit_to_the_person_who_may_read_it_and_never_a_hidden_value, LifecycleScenarios.Scanner);
    [Fact] public Task Variation_changes_configured_fields() => ProviderRunner.SqliteAsync(VariationFieldScenarios.A_Variation_changes_configured_fields_only_the_person_may_see_and_only_when_approved, LifecycleScenarios.Scanner);
}

[Trait("Category", "CustomFields")]
[Trait("Category", "SqlServer")]
public sealed class CustomFieldScenarioTests_SqlServer
{
    [SqlServerFact] public Task Types_round_trip() => ProviderRunner.SqlServerAsync(CustomFieldScenarios.Every_field_type_is_stored_read_changed_and_cleared_through_the_secured_path, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Validation_defaults_required() => ProviderRunner.SqlServerAsync(CustomFieldScenarios.Invalid_values_defaults_and_requiredness_are_enforced_on_the_server_and_entered_values_are_kept, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Role_state_type_rules() => ProviderRunner.SqlServerAsync(CustomFieldScenarios.A_field_is_shown_and_editable_only_to_the_roles_states_and_contract_types_it_names, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Classified_fields() => ProviderRunner.SqlServerAsync(CustomFieldScenarios.Classified_and_privileged_fields_are_hidden_from_people_without_the_level, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Locked_contract() => ProviderRunner.SqlServerAsync(CustomFieldScenarios.A_locked_contract_keeps_its_configured_values_until_a_Variation_or_audited_unlock_changes_them, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Versions_and_retirement() => ProviderRunner.SqlServerAsync(CustomFieldScenarios.Definitions_are_versioned_retired_never_deleted_and_the_values_they_hold_are_protected, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Tenant_isolation_and_cache() => ProviderRunner.SqlServerAsync(CustomFieldScenarios.Definitions_and_values_belong_to_one_tenant_and_the_published_cache_never_crosses_or_goes_stale, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Failed_publish() => ProviderRunner.SqlServerAsync(CustomFieldScenarios.A_failed_publish_leaves_the_published_definitions_and_the_cache_unchanged, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Cache_failure() => ProviderRunner.SqlServerAsync(CacheFailureScenarios.A_cache_that_fails_never_changes_an_answer_and_a_failed_invalidation_stops_the_publish, CacheFailureScenarios.Configure);
    [SqlServerFact] public Task Change_history() => ProviderRunner.SqlServerAsync(ChangeHistoryScenarios.The_change_history_shows_each_edit_to_the_person_who_may_read_it_and_never_a_hidden_value, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Variation_changes_configured_fields() => ProviderRunner.SqlServerAsync(VariationFieldScenarios.A_Variation_changes_configured_fields_only_the_person_may_see_and_only_when_approved, LifecycleScenarios.Scanner);
}

[Trait("Category", "Studio")]
public sealed class StudioScenarioTests_Sqlite
{
    [Fact] public Task Draft_to_rollback() => ProviderRunner.SqliteAsync(StudioScenarios.A_draft_is_edited_ordered_validated_published_retired_and_rolled_back_through_the_studio, LifecycleScenarios.Scanner);
}

[Trait("Category", "Studio")]
[Trait("Category", "SqlServer")]
public sealed class StudioScenarioTests_SqlServer
{
    [SqlServerFact] public Task Draft_to_rollback() => ProviderRunner.SqlServerAsync(StudioScenarios.A_draft_is_edited_ordered_validated_published_retired_and_rolled_back_through_the_studio, LifecycleScenarios.Scanner);
}

[Trait("Category", "Register")]
public sealed class RegisterScenarioTests_Sqlite
{
    [Fact] public Task Typed_search() => ProviderRunner.SqliteAsync(RegisterScenarios.Typed_search_filters_sorts_and_pages_correctly_over_core_fields, LifecycleScenarios.Scanner);
    [Fact] public Task Scope_and_hidden_fields() => ProviderRunner.SqliteAsync(RegisterScenarios.The_register_only_ever_returns_what_the_person_may_read_and_hidden_fields_cannot_be_used_to_find_anything, LifecycleScenarios.Scanner);
}

[Trait("Category", "Register")]
[Trait("Category", "SqlServer")]
public sealed class RegisterScenarioTests_SqlServer
{
    [SqlServerFact] public Task Typed_search() => ProviderRunner.SqlServerAsync(RegisterScenarios.Typed_search_filters_sorts_and_pages_correctly_over_core_fields, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Scope_and_hidden_fields() => ProviderRunner.SqlServerAsync(RegisterScenarios.The_register_only_ever_returns_what_the_person_may_read_and_hidden_fields_cannot_be_used_to_find_anything, LifecycleScenarios.Scanner);
}


[Trait("Category", "ApprovalFacts")]
public sealed class ApprovalFactScenarioTests_Sqlite
{
    [Fact] public Task Live_and_simulated_facts_agree() => ProviderRunner.SqliteAsync(ApprovalFactScenarios.A_live_submission_and_a_simulation_from_the_same_contract_choose_the_same_steps, LifecycleScenarios.Scanner);
    [Fact] public Task Only_usable_fields_feed_a_matrix() => ProviderRunner.SqliteAsync(ApprovalFactScenarios.A_matrix_cannot_use_a_field_that_is_not_marked_usable_in_approvals, LifecycleScenarios.Scanner);
}

[Trait("Category", "ApprovalFacts")]
[Trait("Category", "SqlServer")]
public sealed class ApprovalFactScenarioTests_SqlServer
{
    [SqlServerFact] public Task Live_and_simulated_facts_agree() => ProviderRunner.SqlServerAsync(ApprovalFactScenarios.A_live_submission_and_a_simulation_from_the_same_contract_choose_the_same_steps, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Only_usable_fields_feed_a_matrix() => ProviderRunner.SqlServerAsync(ApprovalFactScenarios.A_matrix_cannot_use_a_field_that_is_not_marked_usable_in_approvals, LifecycleScenarios.Scanner);
}


[Trait("Category", "Register")]
public sealed class RegisterViewScenarioTests_Sqlite
{
    [Fact] public Task Shipped_views() => ProviderRunner.SqliteAsync(RegisterViewScenarios.The_eleven_shipped_views_run_and_select_what_their_names_promise, LifecycleScenarios.Scanner);
    [Fact] public Task Personal_views() => ProviderRunner.SqliteAsync(RegisterViewScenarios.Personal_views_are_private_validated_and_safe_to_save_and_delete, LifecycleScenarios.Scanner);
    [Fact] public Task Shared_views() => ProviderRunner.SqliteAsync(RegisterViewScenarios.Shared_views_are_published_through_configuration_and_each_viewer_still_sees_only_their_own_access, LifecycleScenarios.Scanner);
    [Fact] public Task Exports() => ProviderRunner.SqliteAsync(RegisterViewScenarios.An_export_carries_the_same_columns_and_filters_and_only_what_the_person_may_export, LifecycleScenarios.Scanner);
    [Fact] public Task Reporting_schema() => ProviderRunner.SqliteAsync(RegisterViewScenarios.The_reporting_schema_is_stable_and_follows_what_the_person_may_export, LifecycleScenarios.Scanner);
}

[Trait("Category", "Register")]
[Trait("Category", "SqlServer")]
public sealed class RegisterViewScenarioTests_SqlServer
{
    [SqlServerFact] public Task Shipped_views() => ProviderRunner.SqlServerAsync(RegisterViewScenarios.The_eleven_shipped_views_run_and_select_what_their_names_promise, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Personal_views() => ProviderRunner.SqlServerAsync(RegisterViewScenarios.Personal_views_are_private_validated_and_safe_to_save_and_delete, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Shared_views() => ProviderRunner.SqlServerAsync(RegisterViewScenarios.Shared_views_are_published_through_configuration_and_each_viewer_still_sees_only_their_own_access, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Exports() => ProviderRunner.SqlServerAsync(RegisterViewScenarios.An_export_carries_the_same_columns_and_filters_and_only_what_the_person_may_export, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Reporting_schema() => ProviderRunner.SqlServerAsync(RegisterViewScenarios.The_reporting_schema_is_stable_and_follows_what_the_person_may_export, LifecycleScenarios.Scanner);
}


[Trait("Category", "Migration")]
public sealed class MigrationCustomFieldScenarioTests_Sqlite
{
    [Fact] public Task Load_and_report() => ProviderRunner.SqliteAsync(MigrationCustomFieldScenarios.Configured_fields_and_the_new_core_fields_load_with_every_bad_value_reported_and_nothing_guessed, LifecycleScenarios.Scanner);
    [Fact] public Task Unusable_targets() => ProviderRunner.SqliteAsync(MigrationCustomFieldScenarios.A_profile_that_names_an_unknown_retired_calculated_or_person_field_stops_the_dry_run, LifecycleScenarios.Scanner);
    [Fact] public Task Rerun_update_rollback() => ProviderRunner.SqliteAsync(MigrationCustomFieldScenarios.A_rerun_is_idempotent_an_update_keeps_a_before_image_and_a_rollback_restores_or_removes, LifecycleScenarios.Scanner);
    [Fact] public Task Retired_after_loading() => ProviderRunner.SqliteAsync(MigrationCustomFieldScenarios.A_field_retired_after_loading_keeps_its_values_and_refuses_a_changed_load, LifecycleScenarios.Scanner);
}

[Trait("Category", "Migration")]
[Trait("Category", "SqlServer")]
public sealed class MigrationCustomFieldScenarioTests_SqlServer
{
    [SqlServerFact] public Task Load_and_report() => ProviderRunner.SqlServerAsync(MigrationCustomFieldScenarios.Configured_fields_and_the_new_core_fields_load_with_every_bad_value_reported_and_nothing_guessed, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Unusable_targets() => ProviderRunner.SqlServerAsync(MigrationCustomFieldScenarios.A_profile_that_names_an_unknown_retired_calculated_or_person_field_stops_the_dry_run, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Rerun_update_rollback() => ProviderRunner.SqlServerAsync(MigrationCustomFieldScenarios.A_rerun_is_idempotent_an_update_keeps_a_before_image_and_a_rollback_restores_or_removes, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Retired_after_loading() => ProviderRunner.SqlServerAsync(MigrationCustomFieldScenarios.A_field_retired_after_loading_keeps_its_values_and_refuses_a_changed_load, LifecycleScenarios.Scanner);
}
