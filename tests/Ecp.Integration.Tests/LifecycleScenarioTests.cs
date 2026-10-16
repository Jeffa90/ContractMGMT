using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Lifecycle")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class LifecycleScenarioTests_Sqlite
{
    [Fact] public Task A_contract_goes_from_draft_to_effective() => ProviderRunner.SqliteAsync(LifecycleScenarios.A_contract_goes_from_draft_to_effective_through_every_stage, LifecycleScenarios.Scanner);
    [Fact] public Task A_locked_contract_refuses_crafted_steps() => ProviderRunner.SqliteAsync(LifecycleScenarios.A_locked_contract_refuses_every_crafted_step_and_edit_whoever_asks, LifecycleScenarios.Scanner);
    [Fact] public Task Workflow_events_notify_once() => ProviderRunner.SqliteAsync(NotificationScenarios.A_workflow_event_tells_the_right_people_once_and_a_second_run_changes_nothing, LifecycleScenarios.Scanner);
    [Fact] public Task Privileged_content_does_not_leak() => ProviderRunner.SqliteAsync(NotificationScenarios.Notification_content_never_leaks_a_classified_record_to_people_who_could_not_open_it, LifecycleScenarios.Scanner);
    [Fact] public Task Restricted_titles_are_withheld() => ProviderRunner.SqliteAsync(NotificationScenarios.A_title_is_withheld_from_the_message_at_the_restricted_level_but_the_reference_remains, LifecycleScenarios.Scanner);
    [Fact] public Task Reminders_once_per_window_and_follow_ownership() => ProviderRunner.SqliteAsync(NotificationScenarios.Reminders_are_sent_once_per_window_and_follow_current_ownership, LifecycleScenarios.Scanner);
    [Fact] public Task Evergreen_reviews_not_end_dates() => ProviderRunner.SqliteAsync(NotificationScenarios.Evergreen_agreements_are_reminded_of_their_review_date_but_never_an_end_date, LifecycleScenarios.Scanner);
    [Fact] public Task Critical_notice_earlier_alerts() => ProviderRunner.SqliteAsync(NotificationScenarios.Critical_notice_agreements_get_earlier_alerts_against_their_notice_deadline, LifecycleScenarios.Scanner);
    [Fact] public Task Reminder_acknowledgement() => ProviderRunner.SqliteAsync(NotificationScenarios.A_reminder_is_acknowledged_by_a_current_owner_and_the_dashboard_follows, LifecycleScenarios.Scanner);
    [Fact] public Task Failed_email_retry_and_dead_letter() => ProviderRunner.SqliteAsync(NotificationScenarios.Failed_email_is_retried_with_a_back_off_then_dead_lettered_where_administrators_can_see_and_retry_it, LifecycleScenarios.Scanner);
    [Fact] public Task Lost_access_redacts_the_inbox() => ProviderRunner.SqliteAsync(NotificationScenarios.A_person_who_loses_access_to_a_record_sees_nothing_about_it_in_their_inbox, LifecycleScenarios.Scanner);
    [Fact] public Task Every_state_offers_its_transitions_and_refuses_the_rest() => ProviderRunner.SqliteAsync(LifecycleScenarios2.Every_state_offers_exactly_its_transitions_and_every_other_step_is_refused, LifecycleScenarios.Scanner);
    [Fact] public Task Plain_steps_keep_the_contract_locked_with_a_history() => ProviderRunner.SqliteAsync(LifecycleScenarios2.Plain_steps_after_effectiveness_move_the_contract_and_keep_it_locked_with_a_history, LifecycleScenarios.Scanner);
    [Fact] public Task Register_authorisation_rules() => ProviderRunner.SqliteAsync(LifecycleScenarios2.Register_authorisation_rules_self_approval_backup_rejection_and_registered_date, LifecycleScenarios.Scanner);
    [Fact] public Task Legal_rounds_kept_and_privileged_review_hidden() => ProviderRunner.SqliteAsync(LifecycleScenarios2.Legal_rounds_are_kept_and_a_privileged_review_is_invisible_to_those_without_the_grant, LifecycleScenarios.Scanner);
    [Fact] public Task Force_unlock_rules_and_audit() => ProviderRunner.SqliteAsync(LifecycleScenarios3.A_force_unlock_needs_permission_recent_sign_in_reason_and_a_time_limit_and_is_fully_audited, LifecycleScenarios.Scanner);
    [Fact] public Task Variation_applied_with_before_and_after() => ProviderRunner.SqliteAsync(LifecycleScenarios3.A_Variation_changes_an_effective_contract_only_when_approved_and_keeps_before_and_after, LifecycleScenarios.Scanner);
    [Fact] public Task Variation_cancel_and_reject_leave_parent() => ProviderRunner.SqliteAsync(LifecycleScenarios3.Cancelling_or_rejecting_a_Variation_leaves_the_parent_exactly_as_it_was, LifecycleScenarios.Scanner);
    [Fact] public Task Variation_with_Legal_rounds() => ProviderRunner.SqliteAsync(LifecycleScenarios3.A_Variation_that_needs_Legal_goes_through_Legal_rounds_before_it_can_be_signed, LifecycleScenarios.Scanner);
    [Fact] public Task Renewal_links_and_copy_rules() => ProviderRunner.SqliteAsync(LifecycleScenarios3.A_renewal_copies_only_what_is_configured_links_both_ways_and_never_alters_the_predecessor, LifecycleScenarios.Scanner);
    [Fact] public Task Renewal_cancel() => ProviderRunner.SqliteAsync(LifecycleScenarios3.Cancelling_a_renewal_withdraws_the_draft_successor_and_restores_the_predecessor, LifecycleScenarios.Scanner);
    [Fact] public Task Cross_tenant_lifecycle_attempts() => ProviderRunner.SqliteAsync(LifecycleScenarios3.Another_tenants_records_do_not_exist_for_any_lifecycle_call, LifecycleScenarios.Scanner);
    [Fact] public Task Pinned_workflows() => ProviderRunner.SqliteAsync(LifecycleScenarios3.A_published_workflow_applies_to_new_contracts_while_existing_ones_stay_pinned_and_an_invalid_one_is_refused, LifecycleScenarios.Scanner);
    [Fact] public Task Ownership_events() => ProviderRunner.SqliteAsync(NotificationScenarios2.Ownership_changes_tell_only_the_person_affected, LifecycleScenarios.Scanner);
    [Fact] public Task Disposition_events() => ProviderRunner.SqliteAsync(NotificationScenarios2.A_disposition_request_reaches_the_approver_group_and_no_other_group, LifecycleScenarios.Scanner);
    [Fact] public Task Admin_scope() => ProviderRunner.SqliteAsync(NotificationScenarios2.Notification_administrators_see_only_their_own_organisations_deliveries, LifecycleScenarios.Scanner);
    [Fact] public Task Job_runs() => ProviderRunner.SqliteAsync(NotificationScenarios2.Background_job_runs_are_recorded_and_visible_only_to_administrators, LifecycleScenarios.Scanner);
    [Fact] public Task Dashboard_security() => ProviderRunner.SqliteAsync(NotificationScenarios2.Dashboards_never_show_or_count_a_record_the_reader_could_not_open, LifecycleScenarios.Scanner);
    [Fact] public Task Signature_boundary() => ProviderRunner.SqliteAsync(LifecycleScenarios4.The_signature_boundary_stores_envelope_status_and_a_completed_envelope_only_supplies_the_executed_copy, LifecycleScenarios.Scanner);
}

[Trait("Category", "Lifecycle")]
[Trait("Category", "SqlServer")]
public sealed class LifecycleScenarioTests_SqlServer
{
    [SqlServerFact] public Task A_contract_goes_from_draft_to_effective() => ProviderRunner.SqlServerAsync(LifecycleScenarios.A_contract_goes_from_draft_to_effective_through_every_stage, LifecycleScenarios.Scanner);

    [SqlServerFact] public Task A_locked_contract_refuses_crafted_steps() => ProviderRunner.SqlServerAsync(LifecycleScenarios.A_locked_contract_refuses_every_crafted_step_and_edit_whoever_asks, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Workflow_events_notify_once() => ProviderRunner.SqlServerAsync(NotificationScenarios.A_workflow_event_tells_the_right_people_once_and_a_second_run_changes_nothing, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Privileged_content_does_not_leak() => ProviderRunner.SqlServerAsync(NotificationScenarios.Notification_content_never_leaks_a_classified_record_to_people_who_could_not_open_it, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Restricted_titles_are_withheld() => ProviderRunner.SqlServerAsync(NotificationScenarios.A_title_is_withheld_from_the_message_at_the_restricted_level_but_the_reference_remains, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Reminders_once_per_window_and_follow_ownership() => ProviderRunner.SqlServerAsync(NotificationScenarios.Reminders_are_sent_once_per_window_and_follow_current_ownership, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Evergreen_reviews_not_end_dates() => ProviderRunner.SqlServerAsync(NotificationScenarios.Evergreen_agreements_are_reminded_of_their_review_date_but_never_an_end_date, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Critical_notice_earlier_alerts() => ProviderRunner.SqlServerAsync(NotificationScenarios.Critical_notice_agreements_get_earlier_alerts_against_their_notice_deadline, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Reminder_acknowledgement() => ProviderRunner.SqlServerAsync(NotificationScenarios.A_reminder_is_acknowledged_by_a_current_owner_and_the_dashboard_follows, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Failed_email_retry_and_dead_letter() => ProviderRunner.SqlServerAsync(NotificationScenarios.Failed_email_is_retried_with_a_back_off_then_dead_lettered_where_administrators_can_see_and_retry_it, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Lost_access_redacts_the_inbox() => ProviderRunner.SqlServerAsync(NotificationScenarios.A_person_who_loses_access_to_a_record_sees_nothing_about_it_in_their_inbox, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Every_state_offers_its_transitions_and_refuses_the_rest() => ProviderRunner.SqlServerAsync(LifecycleScenarios2.Every_state_offers_exactly_its_transitions_and_every_other_step_is_refused, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Plain_steps_keep_the_contract_locked_with_a_history() => ProviderRunner.SqlServerAsync(LifecycleScenarios2.Plain_steps_after_effectiveness_move_the_contract_and_keep_it_locked_with_a_history, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Register_authorisation_rules() => ProviderRunner.SqlServerAsync(LifecycleScenarios2.Register_authorisation_rules_self_approval_backup_rejection_and_registered_date, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Legal_rounds_kept_and_privileged_review_hidden() => ProviderRunner.SqlServerAsync(LifecycleScenarios2.Legal_rounds_are_kept_and_a_privileged_review_is_invisible_to_those_without_the_grant, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Two_decisions_at_once_produce_one() => ProviderRunner.SqlServerAsync(LifecycleScenarios2.Two_people_deciding_at_once_produce_one_decision_and_one_registration, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task The_same_step_twice_happens_once() => ProviderRunner.SqlServerAsync(LifecycleScenarios2.The_same_step_taken_twice_at_once_happens_once, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Force_unlock_rules_and_audit() => ProviderRunner.SqlServerAsync(LifecycleScenarios3.A_force_unlock_needs_permission_recent_sign_in_reason_and_a_time_limit_and_is_fully_audited, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Variation_applied_with_before_and_after() => ProviderRunner.SqlServerAsync(LifecycleScenarios3.A_Variation_changes_an_effective_contract_only_when_approved_and_keeps_before_and_after, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Variation_cancel_and_reject_leave_parent() => ProviderRunner.SqlServerAsync(LifecycleScenarios3.Cancelling_or_rejecting_a_Variation_leaves_the_parent_exactly_as_it_was, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Variation_with_Legal_rounds() => ProviderRunner.SqlServerAsync(LifecycleScenarios3.A_Variation_that_needs_Legal_goes_through_Legal_rounds_before_it_can_be_signed, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Renewal_links_and_copy_rules() => ProviderRunner.SqlServerAsync(LifecycleScenarios3.A_renewal_copies_only_what_is_configured_links_both_ways_and_never_alters_the_predecessor, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Renewal_cancel() => ProviderRunner.SqlServerAsync(LifecycleScenarios3.Cancelling_a_renewal_withdraws_the_draft_successor_and_restores_the_predecessor, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Cross_tenant_lifecycle_attempts() => ProviderRunner.SqlServerAsync(LifecycleScenarios3.Another_tenants_records_do_not_exist_for_any_lifecycle_call, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Pinned_workflows() => ProviderRunner.SqlServerAsync(LifecycleScenarios3.A_published_workflow_applies_to_new_contracts_while_existing_ones_stay_pinned_and_an_invalid_one_is_refused, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Ownership_events() => ProviderRunner.SqlServerAsync(NotificationScenarios2.Ownership_changes_tell_only_the_person_affected, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Disposition_events() => ProviderRunner.SqlServerAsync(NotificationScenarios2.A_disposition_request_reaches_the_approver_group_and_no_other_group, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Admin_scope() => ProviderRunner.SqlServerAsync(NotificationScenarios2.Notification_administrators_see_only_their_own_organisations_deliveries, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Job_runs() => ProviderRunner.SqlServerAsync(NotificationScenarios2.Background_job_runs_are_recorded_and_visible_only_to_administrators, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Dashboard_security() => ProviderRunner.SqlServerAsync(NotificationScenarios2.Dashboards_never_show_or_count_a_record_the_reader_could_not_open, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Signature_boundary() => ProviderRunner.SqlServerAsync(LifecycleScenarios4.The_signature_boundary_stores_envelope_status_and_a_completed_envelope_only_supplies_the_executed_copy, LifecycleScenarios.Scanner);
}
