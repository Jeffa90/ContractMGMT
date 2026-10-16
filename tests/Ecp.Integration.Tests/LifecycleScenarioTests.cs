using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Lifecycle")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class LifecycleScenarioTests_Sqlite
{
    [Fact] public Task A_contract_goes_from_draft_to_effective() => ProviderRunner.SqliteAsync(LifecycleScenarios.A_contract_goes_from_draft_to_effective_through_every_stage, LifecycleScenarios.Scanner);
}

[Trait("Category", "Lifecycle")]
[Trait("Category", "SqlServer")]
public sealed class LifecycleScenarioTests_SqlServer
{
    [SqlServerFact] public Task A_contract_goes_from_draft_to_effective() => ProviderRunner.SqlServerAsync(LifecycleScenarios.A_contract_goes_from_draft_to_effective_through_every_stage, LifecycleScenarios.Scanner);
}
