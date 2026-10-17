using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Setup;
using Ecp.Setup.Stages;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public class SetupStageTests
{
    private static SetupEnvironment Env(EcpMode mode, bool demoIdentity = false) =>
        new(mode, Path.GetTempPath(), demoIdentity, false, mode == EcpMode.Production, "SqlServer", "test", false);

    private static StageContext Context(EcpMode mode, Dictionary<string, string> values, SetupDocument? doc = null, bool demoIdentity = false)
    {
        var services = new ServiceCollection();
        services.AddOperationsModule(Path.Combine(Path.GetTempPath(), "ecp-unit-" + Guid.NewGuid().ToString("N")));
        services.AddSingleton<ISecretProvider>(new EnvironmentAndFileSecretProvider());
        services.AddSingleton(new StartupFacts(mode, DatabaseProvider.Sqlite, false, false, false, false, Path.GetTempPath()));
        var provider = services.BuildServiceProvider();
        return new StageContext(doc ?? new SetupDocument { CreatedUtc = DateTime.UtcNow }, new StageRecord(), values, new SetupActor("unit"), Env(mode, demoIdentity), provider, default);
    }

    private static Dictionary<string, string> Email(string provider, params (string, string)[] more)
    {
        var d = new Dictionary<string, string> { ["provider"] = provider, ["from"] = "platform@example.invalid", ["testRecipient"] = "to@example.invalid" };
        foreach (var (k, v) in more) d[k] = v;
        return d;
    }

    [Fact]
    public async Task Production_refuses_the_demonstration_identity_provider_even_when_the_flag_is_set()
    {
        var result = await new IdentityStage().ExecuteAsync(Context(EcpMode.Production, new() { ["kind"] = "Demo" }, demoIdentity: true));
        Assert.False(result.Succeeded);
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_demonstration_identity_provider_needs_to_be_enabled_by_host_configuration_in_demo_and_test()
    {
        var result = await new IdentityStage().ExecuteAsync(Context(EcpMode.Test, new() { ["kind"] = "Demo" }, demoIdentity: false));
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("Mock")]
    [InlineData("Disabled")]
    public async Task Production_refuses_email_providers_that_deliver_nothing(string provider)
    {
        var result = await new NotificationsStage().ExecuteAsync(Context(EcpMode.Production, Email(provider)));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Production_requires_tls_for_smtp_and_references_for_credentials()
    {
        var noTls = await new NotificationsStage().ExecuteAsync(Context(EcpMode.Production, Email("Smtp", ("host", "smtp.example.invalid"), ("port", "587"))));
        Assert.False(noTls.Succeeded);
        Assert.Contains("TLS", string.Join(" ", noTls.Details));
        var literal = await new NotificationsStage().ExecuteAsync(Context(EcpMode.Production, Email("Smtp", ("host", "smtp.example.invalid"), ("useTls", "true"), ("userRef", "svc-user"), ("passwordRef", "hunter2-literal"))));
        Assert.False(literal.Succeeded);
        Assert.DoesNotContain("hunter2-literal", literal.Message + string.Join(" ", literal.Details));
    }

    [Fact]
    public async Task The_demonstration_data_stage_does_not_exist_in_production_and_refuses_to_run()
    {
        var stage = new DemoDataStage();
        Assert.False(stage.AppliesTo(Env(EcpMode.Production)));
        Assert.True(stage.AppliesTo(Env(EcpMode.Test)));
        var result = await stage.ExecuteAsync(Context(EcpMode.Production, []));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task The_mode_stage_applies_the_production_guard_to_the_host_configuration()
    {
        // The engine turns a DomainException into a failed stage; the stage itself raises it.
        var ex = await Assert.ThrowsAsync<DomainException>(() => new ModeStage().ExecuteAsync(
            Context(EcpMode.Production, new() { ["deployment"] = "Container", ["tenancyMode"] = "SingleOrganisation" })));
        Assert.Contains("R-DB-01", ex.Message); // the lightweight provider is refused in Production
        Assert.Contains("R-SEC-02", ex.Message);
    }

    [Fact]
    public async Task Non_production_modes_require_an_acknowledgement_that_the_installation_is_not_for_real_data()
    {
        var env = Env(EcpMode.Demo);
        var fields = new ModeStage().Fields(new SetupDocument { CreatedUtc = DateTime.UtcNow }, env, _ => null);
        Assert.Contains(fields, f => f.Name == "acknowledge" && f.Required);
        Assert.DoesNotContain(new ModeStage().Fields(new SetupDocument { CreatedUtc = DateTime.UtcNow }, Env(EcpMode.Production), _ => null), f => f.Name == "acknowledge");
        await Task.CompletedTask;
    }

    [Fact]
    public void Secret_hashing_matches_only_the_original_secret_and_never_stores_it()
    {
        var secret = SetupSecrets.NewSecret();
        Assert.True(secret.Length >= 43);                       // 256 bits, URL-safe
        var hash = SetupSecrets.Hash(secret);
        Assert.DoesNotContain(secret, hash);
        Assert.True(SetupSecrets.Matches(secret, hash));
        Assert.False(SetupSecrets.Matches(secret + "x", hash));
        Assert.False(SetupSecrets.Matches(null, hash));
        Assert.False(SetupSecrets.Matches(secret, null));
        Assert.NotEqual(secret, SetupSecrets.NewSecret());
    }

    [Fact]
    public void The_demonstration_identity_constants_used_by_setup_match_the_host()
    {
        Assert.Equal("demo", SetupDemo.ProviderKey);
        Assert.Equal("https://demo.ecp.invalid", SetupDemo.Issuer);
    }

    [Fact]
    public void Every_stage_and_check_has_a_unique_id_and_dependencies_that_exist()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Ecp.Configuration.ISolutionPackCatalogue>(new FakeCatalogue());
        services.AddSingleton(new Ecp.SharedKernel.Localisation.LocalisationOptions());
        foreach (var t in SetupStages.All()) services.AddSingleton(t);
        var sp = services.BuildServiceProvider();
        var stages = SetupStages.All().Select(t => (ISetupStage)sp.GetRequiredService(t)).ToList();
        Assert.Equal(stages.Count, stages.Select(s => s.Id).Distinct().Count());
        foreach (var s in stages) Assert.All(s.DependsOn, d => Assert.Contains(stages, x => x.Id == d));
        Assert.Equal(SetupChecks.All().Count, SetupChecks.All().Distinct().Count());
    }

    private sealed class FakeCatalogue : Ecp.Configuration.ISolutionPackCatalogue
    {
        public IReadOnlyList<Ecp.Configuration.SolutionPackInfo> List() => [];
        public Ecp.Configuration.SolutionPackInfo? Find(string packId) => null;
        public IReadOnlyList<Ecp.Configuration.PackCatalogueEntry> ListEntries() => [];
        public string? TerminologyOf(string packKey) => null;
    }
}
