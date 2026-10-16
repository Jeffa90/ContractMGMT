using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

[Trait("Category", "Migration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class MigrationScenarioTests_Sqlite
{
    private static async Task RunAsync(Func<TestApp, Task> scenario)
    {
        await using var app = await TestApp.CreateSqliteAsync();
        await scenario(app);
    }

    [Fact] public Task The_sample_contracts_manifest_and_archive_load_end_to_end() => RunAsync(MigrationScenarios.The_sample_contracts_manifest_and_archive_load_end_to_end);
}
