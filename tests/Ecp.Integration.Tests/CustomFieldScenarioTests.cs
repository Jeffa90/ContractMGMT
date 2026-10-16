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
