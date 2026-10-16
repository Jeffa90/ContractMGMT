using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Elevation")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ElevationScenarioTests_Sqlite
{
    [Fact] public Task Opening_access_needs_a_recent_sign_in_a_reason_and_a_limit() => ProviderRunner.SqliteAsync(ElevationScenarios.Opening_access_needs_the_permission_a_recent_sign_in_a_real_reason_and_a_limited_time);
    [Fact] public Task Support_access_is_read_only_and_confined_to_one_tenant() => ProviderRunner.SqliteAsync(ElevationScenarios.Support_access_is_read_only_bounded_by_the_ceiling_and_confined_to_one_tenant);
    [Fact] public Task Emergency_access_never_alters_an_effective_record() => ProviderRunner.SqliteAsync(ElevationScenarios.Emergency_access_adds_only_account_recovery_and_can_never_alter_an_effective_record);
    [Fact] public Task Access_ends_on_expiry_or_when_ended() => ProviderRunner.SqliteAsync(ElevationScenarios.Access_ends_on_expiry_or_when_ended_and_the_tenants_administrators_see_all_of_it);
    [Fact] public Task Emergency_access_waits_for_a_second_administrator() => ProviderRunner.SqliteAsync(ElevationScenarios.Emergency_access_waits_for_a_second_installation_administrator_where_the_installation_requires_it);
    [Fact] public Task Every_action_carries_the_elevation_identifier() => ProviderRunner.SqliteAsync(ElevationScenarios.Every_action_under_an_elevation_carries_its_identifier_in_the_audit_trail);
}

[Trait("Category", "Elevation")]
[Trait("Category", "SqlServer")]
public sealed class ElevationScenarioTests_SqlServer
{
    [SqlServerFact] public Task Opening_access_needs_a_recent_sign_in_a_reason_and_a_limit() => ProviderRunner.SqlServerAsync(ElevationScenarios.Opening_access_needs_the_permission_a_recent_sign_in_a_real_reason_and_a_limited_time);
    [SqlServerFact] public Task Support_access_is_read_only_and_confined_to_one_tenant() => ProviderRunner.SqlServerAsync(ElevationScenarios.Support_access_is_read_only_bounded_by_the_ceiling_and_confined_to_one_tenant);
    [SqlServerFact] public Task Emergency_access_never_alters_an_effective_record() => ProviderRunner.SqlServerAsync(ElevationScenarios.Emergency_access_adds_only_account_recovery_and_can_never_alter_an_effective_record);
    [SqlServerFact] public Task Access_ends_on_expiry_or_when_ended() => ProviderRunner.SqlServerAsync(ElevationScenarios.Access_ends_on_expiry_or_when_ended_and_the_tenants_administrators_see_all_of_it);
    [SqlServerFact] public Task Emergency_access_waits_for_a_second_administrator() => ProviderRunner.SqlServerAsync(ElevationScenarios.Emergency_access_waits_for_a_second_installation_administrator_where_the_installation_requires_it);
    [SqlServerFact] public Task Every_action_carries_the_elevation_identifier() => ProviderRunner.SqlServerAsync(ElevationScenarios.Every_action_under_an_elevation_carries_its_identifier_in_the_audit_trail);
}
