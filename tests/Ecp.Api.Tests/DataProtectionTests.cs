using Ecp.Host;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// Keys live outside the application folder and are reused by every new process: a recycled application pool, a redeploy and a restart do not sign anyone out.
/// The machine-scope Windows protection is exercised only when these tests run on Windows; elsewhere the key file is unprotected and the tests say so.
/// </summary>
[Trait("Category", "Api")]
public class DataProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ecp-dp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static IConfiguration Config(Dictionary<string, string?> v) => new ConfigurationBuilder().AddInMemoryCollection(v).Build();

    private DataProtectionSettings Settings() => DataProtectionSettings.From(Config(new() { ["Ecp:DataProtection:KeyDirectory"] = Path.Combine(_root, "keys") }), _root);

    private static IDataProtectionProvider NewProcess(DataProtectionSettings settings)
    {
        Directory.CreateDirectory(settings.KeyDirectory);
        var services = new ServiceCollection();
        settings.Apply(services.AddDataProtection());
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    [Fact]
    public void A_new_process_reads_what_the_previous_process_protected()
    {
        var settings = Settings();
        var protectedText = NewProcess(settings).CreateProtector("ecp.session").Protect("signed-in-person");
        // A second provider over the same directory is what an application-pool recycle, a redeploy or a server restart looks like: nothing in memory survives.
        var afterRecycle = NewProcess(settings).CreateProtector("ecp.session");
        Assert.Equal("signed-in-person", afterRecycle.Unprotect(protectedText));
        Assert.NotEmpty(Directory.GetFiles(settings.KeyDirectory, "key-*.xml"));
    }

    [Fact]
    public void Keys_are_not_stored_with_the_application_or_with_secrets()
    {
        var settings = DataProtectionSettings.From(Config(new()), "/var/lib/ecp");
        Assert.Equal(Path.Combine("/var/lib/ecp", "keyring"), settings.KeyDirectory);
        var csproj = File.ReadAllText(RepositoryFile("src/Ecp.Host/Ecp.Host.csproj"));
        Assert.DoesNotContain("keyring", csproj);
        var ignore = File.ReadAllText(RepositoryFile(".gitignore"));
        Assert.Contains("keyring", ignore);
    }

    private static string RepositoryFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative)) || Directory.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void The_default_protection_is_machine_scope_on_Windows_and_none_elsewhere()
    {
        var expected = OperatingSystem.IsWindows() ? KeyProtection.DpapiMachine : KeyProtection.None;
        Assert.Equal(expected, Settings().Protection);
        Assert.Empty(Settings().Validate());
    }

    [Theory]
    [InlineData("Rot13")]
    [InlineData("Certificate")]   // no thumbprint
    public void Unusable_protection_settings_are_refused_before_the_application_starts(string protection)
    {
        var settings = DataProtectionSettings.From(Config(new() { ["Ecp:DataProtection:Protection"] = protection }), _root);
        Assert.Contains(settings.Validate(), v => v.CheckId == "R-KEY-01");
    }

    [Fact]
    public void Windows_only_protection_is_refused_elsewhere_with_a_clear_message()
    {
        if (OperatingSystem.IsWindows()) return;   // on Windows this combination is valid
        var settings = DataProtectionSettings.From(Config(new() { ["Ecp:DataProtection:Protection"] = "DpapiMachine" }), _root);
        Assert.Contains("only available on Windows", Assert.Single(settings.Validate()).Message);
    }

    [Fact]
    public void Production_refuses_a_key_directory_inside_the_application_folder_or_in_temporary_storage()
    {
        var name = "ECP_DP_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "Server=db.example;Database=ecp;User Id=app;Password=not-a-real-secret");
        try
        {
            foreach (var keys in new[] { "/app/keys", Path.Combine(Path.GetTempPath(), "keys") })
            {
                var ex = Assert.Throws<StartupRefusedException>(() => HostConfiguration.Resolve(Config(new()
                {
                    ["Ecp:Mode"] = "Production", ["Ecp:DataDirectory"] = "/var/lib/ecp", ["Ecp:DataProtection:KeyDirectory"] = keys,
                    ["Database:Provider"] = "SqlServer", ["Database:ConnectionStringRef"] = "env:" + name,
                }), new EnvironmentAndFileSecretProvider(), "/app"));
                Assert.Contains("[R-ST-02]", ex.Message);
            }
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    private sealed class FakeRing(KeyRingState state) : IKeyRingInspector { public KeyRingState Inspect(DateTime nowUtc) => state; }

    private static StartupFacts Facts(EcpMode mode) => new(mode, DatabaseProvider.SqlServer, true, false, false, false, "/var/lib/ecp");

    [Fact]
    public async Task Readiness_reports_a_working_ring_and_names_where_it_is_and_how_it_is_protected()
    {
        var settings = Settings(); Directory.CreateDirectory(settings.KeyDirectory);
        var check = new DataProtectionReadinessCheck(settings, Facts(EcpMode.Production), new FakeRing(new KeyRingState(true, 1, 0, DateTime.UtcNow.AddDays(30), "")), TimeProvider.System);
        var result = await check.RunAsync(default);
        Assert.Equal(ReadinessStatus.Healthy, result.Status);
        Assert.Contains(settings.KeyDirectory, result.Message);
        Assert.Contains(settings.DescribeProtection(), result.Message);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_in_Production_when_the_key_directory_is_missing_and_degraded_elsewhere()
    {
        var settings = Settings();   // directory not created
        var ring = new FakeRing(new KeyRingState(true, 0, 0, null, ""));
        var production = await new DataProtectionReadinessCheck(settings, Facts(EcpMode.Production), ring, TimeProvider.System).RunAsync(default);
        var demo = await new DataProtectionReadinessCheck(settings, Facts(EcpMode.Demo), ring, TimeProvider.System).RunAsync(default);
        Assert.Equal(ReadinessStatus.Unhealthy, production.Status);
        Assert.Contains("does not exist", production.Message);
        Assert.Equal(ReadinessStatus.Degraded, demo.Status);
    }

    [Fact]
    public async Task Readiness_never_prints_key_material()
    {
        var settings = Settings();
        var provider = NewProcess(settings); provider.CreateProtector("x").Protect("y");
        var result = await new DataProtectionReadinessCheck(settings, Facts(EcpMode.Test), new FakeRing(new KeyRingState(true, 1, 0, null, "")), TimeProvider.System).RunAsync(default);
        var keyFile = File.ReadAllText(Directory.GetFiles(settings.KeyDirectory, "key-*.xml")[0]);
        var secretLooking = System.Text.RegularExpressions.Regex.Match(keyFile, "<value>([^<]{20,})</value>");
        if (secretLooking.Success) Assert.DoesNotContain(secretLooking.Groups[1].Value, result.Message);
    }
}
