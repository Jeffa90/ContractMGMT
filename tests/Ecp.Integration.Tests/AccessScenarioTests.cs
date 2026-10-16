using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

public abstract class WorldFixtureBase : IAsyncLifetime
{
    private TestApp? _app;
    internal AccessWorld Shared { get; private set; } = null!;
    internal AccessWorld Other { get; private set; } = null!;
    protected abstract bool Available { get; }
    protected abstract Task<TestApp> CreateAppAsync();

    public async Task InitializeAsync()
    {
        if (!Available) return; // tests are skipped and reported NOT VERIFIED
        _app = await CreateAppAsync();
        Shared = await AccessWorld.CreateAsync(_app, "TenantWorldA");
        Other = await AccessWorld.CreateAsync(_app, "TenantWorldB");
    }

    internal Task<AccessWorld> FreshAsync() => AccessWorld.CreateAsync(_app!, "TenantW" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());

    public async Task DisposeAsync() { if (_app is not null) await _app.DisposeAsync(); }
}

public sealed class SqliteWorldFixture : WorldFixtureBase
{
    protected override bool Available => true;
    protected override Task<TestApp> CreateAppAsync() => TestApp.CreateSqliteAsync();
}

public sealed class SqlServerWorldFixture : WorldFixtureBase
{
    protected override bool Available => SqlServerEnvironment.IsAvailable;
    protected override Task<TestApp> CreateAppAsync() => TestApp.CreateSqlServerAsync();
}

// The scenario bodies are shared (AccessScenarios.cs); these two classes run them on each provider. Generated wrappers keep one test per scenario
// per provider, so a missing SQL Server shows up as one NOT VERIFIED per scenario instead of silently shrinking the suite.
[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class AccessScenarioTests_Sqlite(SqliteWorldFixture fx) : IClassFixture<SqliteWorldFixture>
{
    private WorldFixtureBase Fx => fx;
    private async Task RunFreshAsync(Func<AccessWorld, Task> scenario) => await scenario(await fx.FreshAsync());

    [Fact] public Task Viewer_all_contracts_sees_its_organisation_within_its_ceiling_and_nothing_else() => AccessScenarios.Viewer_all_contracts_sees_its_organisation_within_its_ceiling_and_nothing_else(fx.Shared);
    [Fact] public Task Category_viewer_sees_its_category_tree_only() => AccessScenarios.Category_viewer_sees_its_category_tree_only(fx.Shared);
    [Fact] public Task Business_unit_viewer_sees_its_business_unit_only() => AccessScenarios.Business_unit_viewer_sees_its_business_unit_only(fx.Shared);
    [Fact] public Task Site_viewer_sees_its_site_only_and_classification_still_applies() => AccessScenarios.Site_viewer_sees_its_site_only_and_classification_still_applies(fx.Shared);
    [Fact] public Task Restricted_viewer_is_the_intersection_of_site_category_and_ceiling() => AccessScenarios.Restricted_viewer_is_the_intersection_of_site_category_and_ceiling(fx.Shared);
    [Fact] public Task Category_owner_reads_all_in_organisation_within_ceiling() => AccessScenarios.Category_owner_reads_all_in_organisation_within_ceiling(fx.Shared);
    [Fact] public Task Legal_and_procurement_administrator_read_up_to_their_ceilings() => AccessScenarios.Legal_and_procurement_administrator_read_up_to_their_ceilings(fx.Shared);
    [Fact] public Task Tenant_wide_auditor_reads_every_organisation_in_the_tenant() => AccessScenarios.Tenant_wide_auditor_reads_every_organisation_in_the_tenant(fx.Shared);
    [Fact] public Task Administrators_and_approvers_without_a_relationship_see_no_contracts() => AccessScenarios.Administrators_and_approvers_without_a_relationship_see_no_contracts(fx.Shared);
    [Fact] public Task A_direct_read_outside_scope_or_above_the_ceiling_is_not_found_not_forbidden() => AccessScenarios.A_direct_read_outside_scope_or_above_the_ceiling_is_not_found_not_forbidden(fx.Shared);
    [Fact] public Task An_in_scope_user_without_the_permission_is_forbidden() => AccessScenarios.An_in_scope_user_without_the_permission_is_forbidden(fx.Shared);
    [Fact] public Task Category_owner_edits_only_current_categories_and_never_a_locked_record() => RunFreshAsync(AccessScenarios.Category_owner_edits_only_current_categories_and_never_a_locked_record);
    [Fact] public Task Ending_an_ownership_removes_edit_rights_immediately() => RunFreshAsync(AccessScenarios.Ending_an_ownership_removes_edit_rights_immediately);
    [Fact] public Task Creating_a_contract_follows_ownership_scope_and_allocates_the_organisation_reference() => RunFreshAsync(AccessScenarios.Creating_a_contract_follows_ownership_scope_and_allocates_the_organisation_reference);
    [Fact] public Task A_failed_creation_does_not_consume_a_reference_number() => RunFreshAsync(AccessScenarios.A_failed_creation_does_not_consume_a_reference_number);
    [Fact] public Task Supplier_visibility_follows_visible_contracts_for_narrowed_viewers() => AccessScenarios.Supplier_visibility_follows_visible_contracts_for_narrowed_viewers(fx.Shared);
    [Fact] public Task Category_lists_are_narrowed_by_category_scope() => AccessScenarios.Category_lists_are_narrowed_by_category_scope(fx.Shared);
    [Fact] public Task Category_administration_needs_the_taxonomy_permissions() => RunFreshAsync(AccessScenarios.Category_administration_needs_the_taxonomy_permissions);
    [Fact] public Task List_visibility_matches_the_single_record_decision_for_every_user_and_contract() => AccessScenarios.List_visibility_matches_the_single_record_decision_for_every_user_and_contract(fx.Shared);
    [Fact] public Task Denials_are_audited_and_the_audit_chain_stays_valid() => RunFreshAsync(AccessScenarios.Denials_are_audited_and_the_audit_chain_stays_valid);
    [Fact] public Task A_disabled_user_sees_nothing() => RunFreshAsync(AccessScenarios.A_disabled_user_sees_nothing);
    [Fact] public Task Delegated_administration_grants_within_scope_and_refuses_the_rest() => RunFreshAsync(AccessScenarios.Delegated_administration_grants_within_scope_and_refuses_the_rest);
    [Fact] public Task Revocation_ends_authority_keeps_history_and_protects_the_last_administrator() => RunFreshAsync(AccessScenarios.Revocation_ends_authority_keeps_history_and_protects_the_last_administrator);
    [Fact] public Task Identities_link_by_issuer_and_subject_invite_only_and_never_by_email_alone() => RunFreshAsync(AccessScenarios.Identities_link_by_issuer_and_subject_invite_only_and_never_by_email_alone);
    [Fact] public Task Group_mappings_grant_while_the_user_is_in_the_group_and_end_when_they_leave() => RunFreshAsync(AccessScenarios.Group_mappings_grant_while_the_user_is_in_the_group_and_end_when_they_leave);
    [Fact] public Task Identity_data_is_tenant_scoped_and_another_tenant_cannot_see_it() => AccessScenarios.Identity_data_is_tenant_scoped_and_another_tenant_cannot_see_it(fx.Shared, fx.Other);
    [Fact] public Task Provider_settings_are_validated_and_stored_without_secret_values() => RunFreshAsync(AccessScenarios.Provider_settings_are_validated_and_stored_without_secret_values);
}

[Trait("Category", "SqlServer")]
public sealed class AccessScenarioTests_SqlServer(SqlServerWorldFixture fx) : IClassFixture<SqlServerWorldFixture>
{
    private WorldFixtureBase Fx => fx;
    private async Task RunFreshAsync(Func<AccessWorld, Task> scenario) => await scenario(await fx.FreshAsync());

    [SqlServerFact] public Task Viewer_all_contracts_sees_its_organisation_within_its_ceiling_and_nothing_else() => AccessScenarios.Viewer_all_contracts_sees_its_organisation_within_its_ceiling_and_nothing_else(fx.Shared);
    [SqlServerFact] public Task Category_viewer_sees_its_category_tree_only() => AccessScenarios.Category_viewer_sees_its_category_tree_only(fx.Shared);
    [SqlServerFact] public Task Business_unit_viewer_sees_its_business_unit_only() => AccessScenarios.Business_unit_viewer_sees_its_business_unit_only(fx.Shared);
    [SqlServerFact] public Task Site_viewer_sees_its_site_only_and_classification_still_applies() => AccessScenarios.Site_viewer_sees_its_site_only_and_classification_still_applies(fx.Shared);
    [SqlServerFact] public Task Restricted_viewer_is_the_intersection_of_site_category_and_ceiling() => AccessScenarios.Restricted_viewer_is_the_intersection_of_site_category_and_ceiling(fx.Shared);
    [SqlServerFact] public Task Category_owner_reads_all_in_organisation_within_ceiling() => AccessScenarios.Category_owner_reads_all_in_organisation_within_ceiling(fx.Shared);
    [SqlServerFact] public Task Legal_and_procurement_administrator_read_up_to_their_ceilings() => AccessScenarios.Legal_and_procurement_administrator_read_up_to_their_ceilings(fx.Shared);
    [SqlServerFact] public Task Tenant_wide_auditor_reads_every_organisation_in_the_tenant() => AccessScenarios.Tenant_wide_auditor_reads_every_organisation_in_the_tenant(fx.Shared);
    [SqlServerFact] public Task Administrators_and_approvers_without_a_relationship_see_no_contracts() => AccessScenarios.Administrators_and_approvers_without_a_relationship_see_no_contracts(fx.Shared);
    [SqlServerFact] public Task A_direct_read_outside_scope_or_above_the_ceiling_is_not_found_not_forbidden() => AccessScenarios.A_direct_read_outside_scope_or_above_the_ceiling_is_not_found_not_forbidden(fx.Shared);
    [SqlServerFact] public Task An_in_scope_user_without_the_permission_is_forbidden() => AccessScenarios.An_in_scope_user_without_the_permission_is_forbidden(fx.Shared);
    [SqlServerFact] public Task Category_owner_edits_only_current_categories_and_never_a_locked_record() => RunFreshAsync(AccessScenarios.Category_owner_edits_only_current_categories_and_never_a_locked_record);
    [SqlServerFact] public Task Ending_an_ownership_removes_edit_rights_immediately() => RunFreshAsync(AccessScenarios.Ending_an_ownership_removes_edit_rights_immediately);
    [SqlServerFact] public Task Creating_a_contract_follows_ownership_scope_and_allocates_the_organisation_reference() => RunFreshAsync(AccessScenarios.Creating_a_contract_follows_ownership_scope_and_allocates_the_organisation_reference);
    [SqlServerFact] public Task A_failed_creation_does_not_consume_a_reference_number() => RunFreshAsync(AccessScenarios.A_failed_creation_does_not_consume_a_reference_number);
    [SqlServerFact] public Task Supplier_visibility_follows_visible_contracts_for_narrowed_viewers() => AccessScenarios.Supplier_visibility_follows_visible_contracts_for_narrowed_viewers(fx.Shared);
    [SqlServerFact] public Task Category_lists_are_narrowed_by_category_scope() => AccessScenarios.Category_lists_are_narrowed_by_category_scope(fx.Shared);
    [SqlServerFact] public Task Category_administration_needs_the_taxonomy_permissions() => RunFreshAsync(AccessScenarios.Category_administration_needs_the_taxonomy_permissions);
    [SqlServerFact] public Task List_visibility_matches_the_single_record_decision_for_every_user_and_contract() => AccessScenarios.List_visibility_matches_the_single_record_decision_for_every_user_and_contract(fx.Shared);
    [SqlServerFact] public Task Denials_are_audited_and_the_audit_chain_stays_valid() => RunFreshAsync(AccessScenarios.Denials_are_audited_and_the_audit_chain_stays_valid);
    [SqlServerFact] public Task A_disabled_user_sees_nothing() => RunFreshAsync(AccessScenarios.A_disabled_user_sees_nothing);
    [SqlServerFact] public Task Delegated_administration_grants_within_scope_and_refuses_the_rest() => RunFreshAsync(AccessScenarios.Delegated_administration_grants_within_scope_and_refuses_the_rest);
    [SqlServerFact] public Task Revocation_ends_authority_keeps_history_and_protects_the_last_administrator() => RunFreshAsync(AccessScenarios.Revocation_ends_authority_keeps_history_and_protects_the_last_administrator);
    [SqlServerFact] public Task Identities_link_by_issuer_and_subject_invite_only_and_never_by_email_alone() => RunFreshAsync(AccessScenarios.Identities_link_by_issuer_and_subject_invite_only_and_never_by_email_alone);
    [SqlServerFact] public Task Group_mappings_grant_while_the_user_is_in_the_group_and_end_when_they_leave() => RunFreshAsync(AccessScenarios.Group_mappings_grant_while_the_user_is_in_the_group_and_end_when_they_leave);
    [SqlServerFact] public Task Identity_data_is_tenant_scoped_and_another_tenant_cannot_see_it() => AccessScenarios.Identity_data_is_tenant_scoped_and_another_tenant_cannot_see_it(fx.Shared, fx.Other);
    [SqlServerFact] public Task Provider_settings_are_validated_and_stored_without_secret_values() => RunFreshAsync(AccessScenarios.Provider_settings_are_validated_and_stored_without_secret_values);
}
