using Ecp.Contracts;
using Ecp.Lifecycle;
using Ecp.Notifications;
using static Ecp.Integration.Tests.PackResolutionScenarios;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S2 of the portability remediation (finding P-08): a migrated record that is not a legacy load starts in the initial state of <b>its organisation's</b> lifecycle, not in a state named in host
/// configuration. The JBS organisation's records still start in the JBS initial state; a Blank organisation's start in the Blank one; an organisation with no lifecycle at all is refused.
/// </summary>
internal static class MigrationInitialStatusScenarios
{
    public static async Task A_migrated_contract_starts_in_the_initial_state_of_its_own_organisations_lifecycle(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var jbs = await PackResolutionScenarios.AddOrganisationAsync(w, "Reference Migration Org", "MIJ", TestPacks.Reference);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Migration Org", "MIB", TestPacks.Blank);

        await using var s = w.W.AsScopeless();
        var port = s.Get<IContractMigrationPort>();
        Assert.Equal("Draft", await port.InitialStatusAsync(jbs.Org));
        Assert.Equal("Open", await port.InitialStatusAsync(blank.Org));

        // The record actually written carries it, whichever the host's own setting says.
        var batch = Guid.NewGuid();
        MigratedContract Migrated(PackOrg o, string reference, string status) => new(o.Org, batch, reference, null, "Migrated " + reference, null, o.Supplier, o.Category, status, false, "Source", "src-" + reference, null, null,
            "Internal", "AUD", 1000m, null, false, null, null, null, null, null, null, false, "tester");
        var jId = await port.CreateContractAsync(Migrated(jbs, "MIJ-900001", "Draft"));
        var bId = await port.CreateContractAsync(Migrated(blank, "MIB-900001", "Open"));
        Assert.Equal("Draft", (await s.Get<IContractService>().GetAsync(jId))!.Status);
        Assert.Equal("Open", (await s.Get<IContractService>().GetAsync(bId))!.Status);
    }

    public static async Task An_organisation_with_no_lifecycle_cannot_take_an_initial_state_from_host_configuration(TestApp _)
    {
        // A host with no installation default (the shipped configuration) and an organisation without a pack: there is no initial state to take, and nothing falls back to a configured status code.
        await using var undefaulted = await TestApp.CreateSqliteAsync(configure: svc =>
        {
            svc.AddLifecycleModule(o => { o.Mode = EcpMode.Test; o.DefaultPackId = null; });
            svc.AddNotificationsModule(o => { o.Mode = EcpMode.Test; o.DefaultPackId = null; });
        });
        await using var bootstrap = undefaulted.Services.CreateAsyncScope();
        var provisioned = await bootstrap.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
            .ProvisionAsync(new ProvisionTenantRequest("NoDefaultMigration", TenancyMode.MultiOrganisation, EcpMode.Test, "Orphan Org", "Australia/Sydney", "en-AU", "AUD", "ORP"));
        await using var scope = undefaulted.ScopeFor(provisioned.TenantId);
        var refused = await Assert.ThrowsAsync<DomainException>(() => scope.Get<IContractMigrationPort>().InitialStatusAsync(provisioned.OrganisationId));
        Assert.Equal("no_solution_pack", refused.Code);
    }
}

public sealed class MigrationInitialStatusScenarioTests_Sqlite
{
    [Fact] public Task Own_lifecycle() => ProviderRunner.SqliteAsync(MigrationInitialStatusScenarios.A_migrated_contract_starts_in_the_initial_state_of_its_own_organisations_lifecycle);
    [Fact] public Task No_lifecycle() => ProviderRunner.SqliteAsync(MigrationInitialStatusScenarios.An_organisation_with_no_lifecycle_cannot_take_an_initial_state_from_host_configuration);
}

[Trait("Category", "SqlServer")]
public sealed class MigrationInitialStatusScenarioTests_SqlServer
{
    [SqlServerFact] public Task Own_lifecycle() => ProviderRunner.SqlServerAsync(MigrationInitialStatusScenarios.A_migrated_contract_starts_in_the_initial_state_of_its_own_organisations_lifecycle);
    [SqlServerFact] public Task No_lifecycle() => ProviderRunner.SqlServerAsync(MigrationInitialStatusScenarios.An_organisation_with_no_lifecycle_cannot_take_an_initial_state_from_host_configuration);
}
