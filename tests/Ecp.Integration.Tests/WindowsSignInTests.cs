using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Always runs (lightweight provider: application behaviour, not SQL Server proof) and again on real SQL Server when one is available.</summary>
[Trait("Category", "Integration")]
public sealed class WindowsSignInTests
{
    private static async Task RunAsync(Func<TestApp, Task> scenario)
    {
        await using var app = await TestApp.CreateSqliteAsync();
        await scenario(app);
    }

    [Fact] public Task Invited_person_signs_in() => RunAsync(WindowsSignInScenarios.An_invited_active_person_with_access_signs_in_and_the_account_is_linked_to_their_SID);
    [Fact] public Task Uninvited_is_refused_and_audited() => RunAsync(WindowsSignInScenarios.An_uninvited_windows_account_is_refused_and_nothing_is_created);
    [Fact] public Task Email_alone_never_links() => RunAsync(WindowsSignInScenarios.Email_alone_never_links_a_windows_account);
    [Fact] public Task Without_access_is_refused() => RunAsync(WindowsSignInScenarios.An_invited_person_without_effective_access_is_refused_and_not_linked);
    [Fact] public Task Disabled_is_refused() => RunAsync(WindowsSignInScenarios.A_disabled_person_is_refused);
    [Fact] public Task Recreated_account_does_not_inherit() => RunAsync(WindowsSignInScenarios.A_linked_account_name_cannot_be_claimed_by_a_different_SID);
    [Fact] public Task Invitation_not_hijacked() => RunAsync(WindowsSignInScenarios.An_invitation_that_names_an_account_is_not_hijacked_before_the_first_sign_in);
    [Fact] public Task No_role_from_authentication() => RunAsync(WindowsSignInScenarios.Windows_authentication_grants_no_role_and_changes_no_existing_access);
    [Fact] public Task Domain_and_identity_rules() => RunAsync(WindowsSignInScenarios.Domain_restrictions_and_unusable_identities_are_refused);
    [Fact] public Task Not_enabled_and_tenant_isolated() => RunAsync(WindowsSignInScenarios.Windows_sign_in_is_refused_when_the_provider_is_not_enabled_and_is_tenant_isolated);
    [Fact] public Task First_administrator_signs_in() => RunAsync(WindowsSignInScenarios.The_first_administrator_named_at_setup_signs_in_with_their_windows_account);

    private static async Task RunSqlAsync(Func<TestApp, Task> scenario)
    {
        await using var app = await TestApp.CreateSqlServerAsync();
        await scenario(app);
    }

    [SqlServerFact] public Task Sql_invited_person_signs_in() => RunSqlAsync(WindowsSignInScenarios.An_invited_active_person_with_access_signs_in_and_the_account_is_linked_to_their_SID);
    [SqlServerFact] public Task Sql_uninvited_is_refused_and_audited() => RunSqlAsync(WindowsSignInScenarios.An_uninvited_windows_account_is_refused_and_nothing_is_created);
    [SqlServerFact] public Task Sql_without_access_is_refused() => RunSqlAsync(WindowsSignInScenarios.An_invited_person_without_effective_access_is_refused_and_not_linked);
    [SqlServerFact] public Task Sql_recreated_account_does_not_inherit() => RunSqlAsync(WindowsSignInScenarios.A_linked_account_name_cannot_be_claimed_by_a_different_SID);
    [SqlServerFact] public Task Sql_tenant_isolation() => RunSqlAsync(WindowsSignInScenarios.Windows_sign_in_is_refused_when_the_provider_is_not_enabled_and_is_tenant_isolated);
    [SqlServerFact] public Task Sql_first_administrator_signs_in() => RunSqlAsync(WindowsSignInScenarios.The_first_administrator_named_at_setup_signs_in_with_their_windows_account);
}
