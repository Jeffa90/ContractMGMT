using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Lifecycle")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class LifecycleScenarioTests_Sqlite
{
    [Fact] public Task A_contract_goes_from_draft_to_effective() => ProviderRunner.SqliteAsync(LifecycleScenarios.A_contract_goes_from_draft_to_effective_through_every_stage, LifecycleScenarios.Scanner);
    [Fact] public Task Workflow_events_notify_once() => ProviderRunner.SqliteAsync(NotificationScenarios.A_workflow_event_tells_the_right_people_once_and_a_second_run_changes_nothing, LifecycleScenarios.Scanner);
    [Fact] public Task Privileged_content_does_not_leak() => ProviderRunner.SqliteAsync(NotificationScenarios.Notification_content_never_leaks_a_classified_record_to_people_who_could_not_open_it, LifecycleScenarios.Scanner);
    [Fact] public Task Restricted_titles_are_withheld() => ProviderRunner.SqliteAsync(NotificationScenarios.A_title_is_withheld_from_the_message_at_the_restricted_level_but_the_reference_remains, LifecycleScenarios.Scanner);
    [Fact] public Task Reminders_once_per_window_and_follow_ownership() => ProviderRunner.SqliteAsync(NotificationScenarios.Reminders_are_sent_once_per_window_and_follow_current_ownership, LifecycleScenarios.Scanner);
    [Fact] public Task Evergreen_reviews_not_end_dates() => ProviderRunner.SqliteAsync(NotificationScenarios.Evergreen_agreements_are_reminded_of_their_review_date_but_never_an_end_date, LifecycleScenarios.Scanner);
    [Fact] public Task Critical_notice_earlier_alerts() => ProviderRunner.SqliteAsync(NotificationScenarios.Critical_notice_agreements_get_earlier_alerts_against_their_notice_deadline, LifecycleScenarios.Scanner);
    [Fact] public Task Reminder_acknowledgement() => ProviderRunner.SqliteAsync(NotificationScenarios.A_reminder_is_acknowledged_by_a_current_owner_and_the_dashboard_follows, LifecycleScenarios.Scanner);
    [Fact] public Task Failed_email_retry_and_dead_letter() => ProviderRunner.SqliteAsync(NotificationScenarios.Failed_email_is_retried_with_a_back_off_then_dead_lettered_where_administrators_can_see_and_retry_it, LifecycleScenarios.Scanner);
    [Fact] public Task Lost_access_redacts_the_inbox() => ProviderRunner.SqliteAsync(NotificationScenarios.A_person_who_loses_access_to_a_record_sees_nothing_about_it_in_their_inbox, LifecycleScenarios.Scanner);
}

[Trait("Category", "Lifecycle")]
[Trait("Category", "SqlServer")]
public sealed class LifecycleScenarioTests_SqlServer
{
    [SqlServerFact] public Task A_contract_goes_from_draft_to_effective() => ProviderRunner.SqlServerAsync(LifecycleScenarios.A_contract_goes_from_draft_to_effective_through_every_stage, LifecycleScenarios.Scanner);

    [SqlServerFact] public Task Workflow_events_notify_once() => ProviderRunner.SqlServerAsync(NotificationScenarios.A_workflow_event_tells_the_right_people_once_and_a_second_run_changes_nothing, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Privileged_content_does_not_leak() => ProviderRunner.SqlServerAsync(NotificationScenarios.Notification_content_never_leaks_a_classified_record_to_people_who_could_not_open_it, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Restricted_titles_are_withheld() => ProviderRunner.SqlServerAsync(NotificationScenarios.A_title_is_withheld_from_the_message_at_the_restricted_level_but_the_reference_remains, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Reminders_once_per_window_and_follow_ownership() => ProviderRunner.SqlServerAsync(NotificationScenarios.Reminders_are_sent_once_per_window_and_follow_current_ownership, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Evergreen_reviews_not_end_dates() => ProviderRunner.SqlServerAsync(NotificationScenarios.Evergreen_agreements_are_reminded_of_their_review_date_but_never_an_end_date, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Critical_notice_earlier_alerts() => ProviderRunner.SqlServerAsync(NotificationScenarios.Critical_notice_agreements_get_earlier_alerts_against_their_notice_deadline, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Reminder_acknowledgement() => ProviderRunner.SqlServerAsync(NotificationScenarios.A_reminder_is_acknowledged_by_a_current_owner_and_the_dashboard_follows, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Failed_email_retry_and_dead_letter() => ProviderRunner.SqlServerAsync(NotificationScenarios.Failed_email_is_retried_with_a_back_off_then_dead_lettered_where_administrators_can_see_and_retry_it, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Lost_access_redacts_the_inbox() => ProviderRunner.SqlServerAsync(NotificationScenarios.A_person_who_loses_access_to_a_record_sees_nothing_about_it_in_their_inbox, LifecycleScenarios.Scanner);
}
