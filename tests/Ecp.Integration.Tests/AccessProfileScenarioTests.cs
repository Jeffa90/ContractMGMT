using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class AccessProfileScenarioTests_Sqlite(SqliteWorldFixture fx) : IClassFixture<SqliteWorldFixture>
{
    private async Task RunFreshAsync(Func<AccessWorld, Task> scenario) => await scenario(await fx.FreshAsync());

    [Fact] public Task Owning_all_categories_means_current_and_future_ones() => RunFreshAsync(AccessProfileScenarios.Owning_all_categories_means_current_and_future_ones_and_ending_it_leaves_other_ownership_alone);
    [Fact] public async Task A_standing_policy_leaves_existing_ownership_untouched_and_is_tenant_isolated() => await AccessProfileScenarios.A_standing_ownership_policy_leaves_existing_ownership_untouched_and_is_not_visible_to_another_tenant(await fx.FreshAsync(), fx.Other);
    [Fact] public Task The_policy_needs_authority_and_a_recent_sign_in() => RunFreshAsync(AccessProfileScenarios.The_policy_needs_the_right_to_assign_ownership_and_a_recent_sign_in);
    [Fact] public Task The_review_is_plain_English_with_codes_only_in_advanced_details() => RunFreshAsync(AccessProfileScenarios.The_review_says_in_plain_English_what_saving_would_give_and_shows_codes_only_in_advanced_details);
    [Fact] public Task Approval_authorities_are_set_up_by_name() => RunFreshAsync(AccessProfileScenarios.Approval_authorities_are_set_up_by_name_and_membership_changes_go_through_the_approver_group_rules);
    [Fact] public Task A_contract_manager_profile_becomes_an_ordinary_role_grant_and_category_ownership() => RunFreshAsync(AccessProfileScenarios.A_contract_manager_profile_becomes_an_ordinary_role_grant_and_category_ownership);
    [Fact] public Task A_manager_who_owns_nothing_is_told_why_they_cannot_create_yet() => RunFreshAsync(AccessProfileScenarios.A_manager_who_owns_nothing_is_told_why_they_cannot_create_yet);
    [Fact] public Task A_failure_part_way_through_leaves_nothing_behind() => RunFreshAsync(AccessProfileScenarios.A_failure_part_way_through_leaves_nothing_behind);
    [Fact] public Task Previewing_shows_what_saving_would_do_and_changes_nothing() => RunFreshAsync(AccessProfileScenarios.Previewing_shows_what_saving_would_do_and_changes_nothing);
    [Fact] public Task The_delegation_rules_still_decide_who_may_give_what() => RunFreshAsync(AccessProfileScenarios.The_delegation_rules_still_decide_who_may_give_what);
    [Fact] public Task A_person_cannot_give_themselves_access_through_a_profile() => RunFreshAsync(AccessProfileScenarios.A_person_cannot_give_themselves_access_through_a_profile);
    [Fact] public Task Changing_access_needs_a_recent_sign_in() => RunFreshAsync(AccessProfileScenarios.Changing_access_needs_a_recent_sign_in);
    [Fact] public Task An_approval_authority_needs_an_approver_group_and_a_reviewer_needs_the_step_switched_on() => RunFreshAsync(AccessProfileScenarios.An_approval_authority_needs_an_approver_group_and_a_reviewer_needs_the_step_switched_on);
    [Fact] public Task Deleting_every_provenance_row_changes_nobodys_permissions() => RunFreshAsync(AccessProfileScenarios.Deleting_every_provenance_row_changes_nobodys_permissions);
    [Fact] public Task Ending_an_application_ends_only_what_it_created() => RunFreshAsync(AccessProfileScenarios.Ending_an_application_ends_only_what_it_created);
    [Fact] public async Task Another_tenant_cannot_see_or_change_a_person() => await AccessProfileScenarios.Another_tenant_cannot_see_or_change_a_person(await fx.FreshAsync(), fx.Other);
    [Fact] public Task A_person_and_their_access_are_added_in_one_transaction_or_not_at_all() => RunFreshAsync(AccessProfileScenarios.A_person_and_their_access_are_added_in_one_transaction_or_not_at_all);
    [Fact] public Task A_test_person_signs_in_through_the_demonstration_provider_and_gets_no_access_from_that() => RunFreshAsync(AccessProfileScenarios.A_test_person_signs_in_through_the_demonstration_provider_and_gets_no_access_from_that);
    [Fact] public Task Set_up_choices_become_ordinary_grants_through_the_trusted_path_and_administer_only_grants_nothing() => RunFreshAsync(AccessProfileScenarios.Set_up_choices_become_ordinary_grants_through_the_trusted_path_and_administer_only_grants_nothing);
    [Fact] public Task Full_test_mode_access_is_bounded_audited_reversible_and_never_grants_system_authority() => RunFreshAsync(AccessProfileScenarios.Full_test_mode_access_is_bounded_audited_reversible_and_never_grants_system_authority);
    [Fact] public Task Full_test_mode_access_needs_an_administrator_a_recent_sign_in_and_a_reason() => RunFreshAsync(AccessProfileScenarios.Full_test_mode_access_needs_an_administrator_a_recent_sign_in_and_a_reason);
    [Fact] public Task Mappings_need_the_right_to_manage_them_a_recent_sign_in_and_a_closed_safe_catalogue() => RunFreshAsync(AccessProfileScenarios.Mappings_need_the_right_to_manage_them_a_recent_sign_in_and_a_closed_safe_catalogue);
    [Fact] public Task A_mapping_is_narrow_by_construction_no_high_ceiling_no_unchosen_places_and_viewer_kind_follows_the_places() => RunFreshAsync(AccessProfileScenarios.A_mapping_is_narrow_by_construction_no_high_ceiling_no_unchosen_places_and_viewer_kind_follows_the_places);
    [Fact] public Task Preview_starts_for_a_person_the_administrator_may_manage_and_is_audited() => RunFreshAsync(AccessProfileScenarios.Preview_starts_for_a_person_the_administrator_may_manage_and_is_audited);
    [Fact] public Task Preview_needs_a_reason_a_recent_sign_in_and_the_right_to_manage_the_person() => RunFreshAsync(AccessProfileScenarios.Preview_needs_a_reason_a_recent_sign_in_and_the_right_to_manage_the_person);
    [Fact] public Task You_may_preview_only_what_you_could_give_and_never_yourself_a_disabled_person_or_the_installation() => RunFreshAsync(AccessProfileScenarios.You_may_preview_only_what_you_could_give_and_never_yourself_a_disabled_person_or_the_installation);
    [Fact] public Task A_preview_runs_with_the_previewed_persons_real_access_not_the_administrators() => RunFreshAsync(AccessProfileScenarios.A_preview_runs_with_the_previewed_persons_real_access_not_the_administrators);
    [Fact] public Task A_preview_cannot_write_anything_but_its_own_record_and_the_audit_trail() => RunFreshAsync(AccessProfileScenarios.A_preview_cannot_write_anything_but_its_own_record_and_the_audit_trail);
    [Fact] public Task A_preview_ends_when_it_expires_or_the_previewer_loses_the_right_or_the_person_is_disabled() => RunFreshAsync(AccessProfileScenarios.A_preview_ends_when_it_expires_or_the_previewer_loses_the_right_or_the_person_is_disabled);
    [Fact] public async Task A_preview_cannot_cross_tenants() => await AccessProfileScenarios.A_preview_cannot_cross_tenants(await fx.FreshAsync(), fx.Other);
    [Fact] public Task An_invited_person_is_previewed_as_they_will_be_once_active_without_changing_the_account() => RunFreshAsync(AccessProfileScenarios.An_invited_person_is_previewed_as_they_will_be_once_active_without_changing_the_account);
    [Fact] public Task Effective_access_agrees_with_the_evaluator_for_every_person_and_permission() => RunFreshAsync(AccessProfileScenarios.Effective_access_agrees_with_the_evaluator_for_every_person_and_permission);
    [Fact] public Task A_person_can_read_their_own_effective_access_and_nobody_elses() => RunFreshAsync(AccessProfileScenarios.A_person_can_read_their_own_effective_access_and_nobody_elses);
}

[Trait("Category", "SqlServer")]
public sealed class AccessProfileScenarioTests_SqlServer(SqlServerWorldFixture fx) : IClassFixture<SqlServerWorldFixture>
{
    private async Task RunFreshAsync(Func<AccessWorld, Task> scenario) => await scenario(await fx.FreshAsync());

    [SqlServerFact] public Task Owning_all_categories_means_current_and_future_ones() => RunFreshAsync(AccessProfileScenarios.Owning_all_categories_means_current_and_future_ones_and_ending_it_leaves_other_ownership_alone);
    [SqlServerFact] public async Task A_standing_policy_leaves_existing_ownership_untouched_and_is_tenant_isolated() => await AccessProfileScenarios.A_standing_ownership_policy_leaves_existing_ownership_untouched_and_is_not_visible_to_another_tenant(await fx.FreshAsync(), fx.Other);
    [SqlServerFact] public Task The_policy_needs_authority_and_a_recent_sign_in() => RunFreshAsync(AccessProfileScenarios.The_policy_needs_the_right_to_assign_ownership_and_a_recent_sign_in);
    [SqlServerFact] public Task The_review_is_plain_English_with_codes_only_in_advanced_details() => RunFreshAsync(AccessProfileScenarios.The_review_says_in_plain_English_what_saving_would_give_and_shows_codes_only_in_advanced_details);
    [SqlServerFact] public Task Approval_authorities_are_set_up_by_name() => RunFreshAsync(AccessProfileScenarios.Approval_authorities_are_set_up_by_name_and_membership_changes_go_through_the_approver_group_rules);
    [SqlServerFact] public Task A_contract_manager_profile_becomes_an_ordinary_role_grant_and_category_ownership() => RunFreshAsync(AccessProfileScenarios.A_contract_manager_profile_becomes_an_ordinary_role_grant_and_category_ownership);
    [SqlServerFact] public Task A_manager_who_owns_nothing_is_told_why_they_cannot_create_yet() => RunFreshAsync(AccessProfileScenarios.A_manager_who_owns_nothing_is_told_why_they_cannot_create_yet);
    [SqlServerFact] public Task A_failure_part_way_through_leaves_nothing_behind() => RunFreshAsync(AccessProfileScenarios.A_failure_part_way_through_leaves_nothing_behind);
    [SqlServerFact] public Task Previewing_shows_what_saving_would_do_and_changes_nothing() => RunFreshAsync(AccessProfileScenarios.Previewing_shows_what_saving_would_do_and_changes_nothing);
    [SqlServerFact] public Task The_delegation_rules_still_decide_who_may_give_what() => RunFreshAsync(AccessProfileScenarios.The_delegation_rules_still_decide_who_may_give_what);
    [SqlServerFact] public Task A_person_cannot_give_themselves_access_through_a_profile() => RunFreshAsync(AccessProfileScenarios.A_person_cannot_give_themselves_access_through_a_profile);
    [SqlServerFact] public Task Changing_access_needs_a_recent_sign_in() => RunFreshAsync(AccessProfileScenarios.Changing_access_needs_a_recent_sign_in);
    [SqlServerFact] public Task An_approval_authority_needs_an_approver_group_and_a_reviewer_needs_the_step_switched_on() => RunFreshAsync(AccessProfileScenarios.An_approval_authority_needs_an_approver_group_and_a_reviewer_needs_the_step_switched_on);
    [SqlServerFact] public Task Deleting_every_provenance_row_changes_nobodys_permissions() => RunFreshAsync(AccessProfileScenarios.Deleting_every_provenance_row_changes_nobodys_permissions);
    [SqlServerFact] public Task Ending_an_application_ends_only_what_it_created() => RunFreshAsync(AccessProfileScenarios.Ending_an_application_ends_only_what_it_created);
    [SqlServerFact] public async Task Another_tenant_cannot_see_or_change_a_person() => await AccessProfileScenarios.Another_tenant_cannot_see_or_change_a_person(await fx.FreshAsync(), fx.Other);
    [SqlServerFact] public Task A_person_and_their_access_are_added_in_one_transaction_or_not_at_all() => RunFreshAsync(AccessProfileScenarios.A_person_and_their_access_are_added_in_one_transaction_or_not_at_all);
    [SqlServerFact] public Task A_test_person_signs_in_through_the_demonstration_provider_and_gets_no_access_from_that() => RunFreshAsync(AccessProfileScenarios.A_test_person_signs_in_through_the_demonstration_provider_and_gets_no_access_from_that);
    [SqlServerFact] public Task Set_up_choices_become_ordinary_grants_through_the_trusted_path_and_administer_only_grants_nothing() => RunFreshAsync(AccessProfileScenarios.Set_up_choices_become_ordinary_grants_through_the_trusted_path_and_administer_only_grants_nothing);
    [SqlServerFact] public Task Full_test_mode_access_is_bounded_audited_reversible_and_never_grants_system_authority() => RunFreshAsync(AccessProfileScenarios.Full_test_mode_access_is_bounded_audited_reversible_and_never_grants_system_authority);
    [SqlServerFact] public Task Full_test_mode_access_needs_an_administrator_a_recent_sign_in_and_a_reason() => RunFreshAsync(AccessProfileScenarios.Full_test_mode_access_needs_an_administrator_a_recent_sign_in_and_a_reason);
    [SqlServerFact] public Task Mappings_need_the_right_to_manage_them_a_recent_sign_in_and_a_closed_safe_catalogue() => RunFreshAsync(AccessProfileScenarios.Mappings_need_the_right_to_manage_them_a_recent_sign_in_and_a_closed_safe_catalogue);
    [SqlServerFact] public Task A_mapping_is_narrow_by_construction_no_high_ceiling_no_unchosen_places_and_viewer_kind_follows_the_places() => RunFreshAsync(AccessProfileScenarios.A_mapping_is_narrow_by_construction_no_high_ceiling_no_unchosen_places_and_viewer_kind_follows_the_places);
    [SqlServerFact] public Task Preview_starts_for_a_person_the_administrator_may_manage_and_is_audited() => RunFreshAsync(AccessProfileScenarios.Preview_starts_for_a_person_the_administrator_may_manage_and_is_audited);
    [SqlServerFact] public Task Preview_needs_a_reason_a_recent_sign_in_and_the_right_to_manage_the_person() => RunFreshAsync(AccessProfileScenarios.Preview_needs_a_reason_a_recent_sign_in_and_the_right_to_manage_the_person);
    [SqlServerFact] public Task You_may_preview_only_what_you_could_give_and_never_yourself_a_disabled_person_or_the_installation() => RunFreshAsync(AccessProfileScenarios.You_may_preview_only_what_you_could_give_and_never_yourself_a_disabled_person_or_the_installation);
    [SqlServerFact] public Task A_preview_runs_with_the_previewed_persons_real_access_not_the_administrators() => RunFreshAsync(AccessProfileScenarios.A_preview_runs_with_the_previewed_persons_real_access_not_the_administrators);
    [SqlServerFact] public Task A_preview_cannot_write_anything_but_its_own_record_and_the_audit_trail() => RunFreshAsync(AccessProfileScenarios.A_preview_cannot_write_anything_but_its_own_record_and_the_audit_trail);
    [SqlServerFact] public Task A_preview_ends_when_it_expires_or_the_previewer_loses_the_right_or_the_person_is_disabled() => RunFreshAsync(AccessProfileScenarios.A_preview_ends_when_it_expires_or_the_previewer_loses_the_right_or_the_person_is_disabled);
    [SqlServerFact] public async Task A_preview_cannot_cross_tenants() => await AccessProfileScenarios.A_preview_cannot_cross_tenants(await fx.FreshAsync(), fx.Other);
    [SqlServerFact] public Task An_invited_person_is_previewed_as_they_will_be_once_active_without_changing_the_account() => RunFreshAsync(AccessProfileScenarios.An_invited_person_is_previewed_as_they_will_be_once_active_without_changing_the_account);
    [SqlServerFact] public Task Effective_access_agrees_with_the_evaluator_for_every_person_and_permission() => RunFreshAsync(AccessProfileScenarios.Effective_access_agrees_with_the_evaluator_for_every_person_and_permission);
    [SqlServerFact] public Task A_person_can_read_their_own_effective_access_and_nobody_elses() => RunFreshAsync(AccessProfileScenarios.A_person_can_read_their_own_effective_access_and_nobody_elses);
}

[Trait("Category", "Integration")]
public sealed class AccessProfileCatalogueTests
{
    [Fact] public void Every_permission_has_an_ability_and_every_ability_has_a_permission() => AccessProfileCatalogueTests_Shared.Every_permission_has_an_ability_and_every_ability_has_a_permission();
    [Fact] public void Profiles_use_only_existing_non_privileged_roles_and_are_unique() => AccessProfileCatalogueTests_Shared.Profiles_use_only_existing_non_privileged_roles_and_are_unique();
}

/// <summary>The Test/Demo-only tools do not exist when the installation's mode is Production.</summary>
[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class AccessProfileProductionTests
{
    [Fact]
    public async Task Demonstration_sign_in_and_synthetic_people_are_refused_in_production_at_the_service()
    {
        await using var app = await TestApp.CreateSqliteAsync(Ecp.SharedKernel.EcpMode.Production);
        await AccessProfileScenarios.Demonstration_sign_in_and_synthetic_people_are_refused_in_production_at_the_service(await AccessWorld.CreateAsync(app, "ProdWorldDemo"));
    }

    [Fact]
    public async Task Preview_does_not_exist_in_production()
    {
        await using var app = await TestApp.CreateSqliteAsync(Ecp.SharedKernel.EcpMode.Production);
        await AccessProfileScenarios.Preview_does_not_exist_in_production(await AccessWorld.CreateAsync(app, "ProdWorldPrev"));
    }

    [Fact]
    public async Task Test_mode_access_does_not_exist_in_production()
    {
        await using var app = await TestApp.CreateSqliteAsync(Ecp.SharedKernel.EcpMode.Production);
        await AccessProfileScenarios.Test_mode_access_does_not_exist_in_production(await AccessWorld.CreateAsync(app, "ProdWorldTest"));
    }
}
