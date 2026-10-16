using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Classification")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ClassificationScenarioTests_Sqlite
{
    [Fact] public Task Legal_privileged_records_need_an_explicit_grant() => ProviderRunner.SqliteAsync(ClassificationScenarios.Legal_privileged_records_need_an_explicit_grant_and_a_role_or_ceiling_alone_never_opens_them);
    [Fact] public Task Only_a_tenant_administrator_can_grant_privileged_access() => ProviderRunner.SqliteAsync(ClassificationScenarios.Only_a_tenant_administrator_can_grant_privileged_access);
    [Fact] public Task A_category_owner_reads_up_to_the_ceiling() => ProviderRunner.SqliteAsync(ClassificationScenarios.A_category_owner_reads_everything_in_the_organisation_up_to_the_ceiling_and_nothing_above_it);
    [Fact] public Task A_higher_classified_document_stays_hidden() => ProviderRunner.SqliteAsync(ClassificationScenarios.A_document_classified_above_the_viewer_stays_hidden_even_though_its_contract_is_visible);
    [Fact] public Task Search_counts_and_exports_do_not_leak() => ProviderRunner.SqliteAsync(ClassificationScenarios.Search_counts_and_exports_never_leak_a_record_the_reader_could_not_open);
    [Fact] public Task Classification_changes_are_audited() => ProviderRunner.SqliteAsync(ClassificationScenarios.Classification_changes_are_audited_lowering_needs_authority_and_a_reason_and_documents_follow_a_raise);
    [Fact] public Task Audit_evidence_follows_the_record_classification() => ProviderRunner.SqliteAsync(ClassificationScenarios.Audit_evidence_about_a_classified_record_is_visible_only_where_the_record_is);
}

[Trait("Category", "Classification")]
[Trait("Category", "SqlServer")]
public sealed class ClassificationScenarioTests_SqlServer
{
    [SqlServerFact] public Task Legal_privileged_records_need_an_explicit_grant() => ProviderRunner.SqlServerAsync(ClassificationScenarios.Legal_privileged_records_need_an_explicit_grant_and_a_role_or_ceiling_alone_never_opens_them);
    [SqlServerFact] public Task Only_a_tenant_administrator_can_grant_privileged_access() => ProviderRunner.SqlServerAsync(ClassificationScenarios.Only_a_tenant_administrator_can_grant_privileged_access);
    [SqlServerFact] public Task A_category_owner_reads_up_to_the_ceiling() => ProviderRunner.SqlServerAsync(ClassificationScenarios.A_category_owner_reads_everything_in_the_organisation_up_to_the_ceiling_and_nothing_above_it);
    [SqlServerFact] public Task A_higher_classified_document_stays_hidden() => ProviderRunner.SqlServerAsync(ClassificationScenarios.A_document_classified_above_the_viewer_stays_hidden_even_though_its_contract_is_visible);
    [SqlServerFact] public Task Search_counts_and_exports_do_not_leak() => ProviderRunner.SqlServerAsync(ClassificationScenarios.Search_counts_and_exports_never_leak_a_record_the_reader_could_not_open);
    [SqlServerFact] public Task Classification_changes_are_audited() => ProviderRunner.SqlServerAsync(ClassificationScenarios.Classification_changes_are_audited_lowering_needs_authority_and_a_reason_and_documents_follow_a_raise);
    [SqlServerFact] public Task Audit_evidence_follows_the_record_classification() => ProviderRunner.SqlServerAsync(ClassificationScenarios.Audit_evidence_about_a_classified_record_is_visible_only_where_the_record_is);
}
