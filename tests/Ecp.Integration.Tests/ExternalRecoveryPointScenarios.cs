using Ecp.Audit.Persistence;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Recovery owned by the database administrators (Ecp:Recovery:Mode = External). The platform records what they declare and says, everywhere, that it is declared and not verified here.
/// </summary>
internal static class ExternalRecoveryPointScenarios
{
    private static readonly OperatorIdentity Dba = new("cli:dba.operator", "cli:dba.operator");

    /// <summary>A fresh folder for every test host, so declared points never leak from one scenario to the next.</summary>
    public static Action<IServiceCollection> Configure => services =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "ecp-ext-points-" + Guid.NewGuid().ToString("N"));
        var database = (DatabaseOptions)services.Last(d => d.ServiceType == typeof(DatabaseOptions)).ImplementationInstance!;
        services.AddRecoveryModule(o => { o.Mode = "External"; o.ExternalRecoveryPointsDirectory = directory; }, database);
        services.AddSingleton<IRestoreTargetFactory, OperationsFoundationScenarios.NoTargets>();
    };

    private static ExternalRecoveryPointRequest Request(TestApp app, string id = "Nightly full 2026-11-12", string environment = "Test", string version = "1.0.0", DateTime? taken = null, string reason = "Pre-upgrade recovery point")
        => new(id, "SQL Server native backup, copy-only", environment, "DBA team (rehearsed restore on 2026-11-11)", taken ?? app.Clock.UtcNow.AddHours(-2), version, reason);

    private static async Task<T> InstallationAsync<T>(TestApp app, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public static async Task A_declared_recovery_point_is_recorded_audited_and_never_called_verified(TestApp app)
    {
        var point = await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().DeclareAsync(Request(app), Dba));
        Assert.StartsWith("ext-", point.PointId);
        Assert.Equal("Declared by DBA; not verified by this application", point.Statement);
        Assert.False(point.VerifiedByApplication);
        Assert.Equal("Test", point.Environment);
        Assert.Equal("1.0.0", point.DatabaseSchemaVersion);
        Assert.False(string.IsNullOrEmpty(point.CorrelationId));
        Assert.Equal(64, point.RecordSha256.Length);
        Assert.Equal("cli:dba.operator", point.DeclaredBy);

        var again = await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().FindAsync(point.PointId));
        Assert.NotNull(again);
        Assert.Equal(point.Identifier, again!.Identifier); Assert.Equal(point.Source, again.Source); Assert.Equal(point.Verifier, again.Verifier);
        Assert.Equal(point.TakenUtc, again.TakenUtc); Assert.Equal(point.RecordSha256, again.RecordSha256);

        await using var scope = app.ScopeFor(SystemTenant.Id);
        var events = await scope.Get<AuditDbContext>().Events.Where(e => e.Action == "operations.recovery_point.declared").ToListAsync();
        var evt = Assert.Single(events);
        Assert.Equal(point.PointId, evt.EntityId);
        Assert.Contains("Declared by DBA; not verified by this application", evt.NewValuesJson);
        Assert.Contains(point.RecordSha256, evt.NewValuesJson);
        Assert.Contains("\"verifiedByApplication\":false", evt.NewValuesJson);
        Assert.False(string.IsNullOrEmpty(evt.CorrelationId));
    }

    public static async Task A_declaration_is_complete_plausible_for_this_environment_and_append_only(TestApp app)
    {
        async Task<DomainException> Refused(ExternalRecoveryPointRequest r) => await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().DeclareAsync(r, Dba)));
        Assert.Equal("environment_mismatch", (await Refused(Request(app, environment: "Production"))).Code);
        Assert.Equal("recovery_point_time", (await Refused(Request(app, taken: app.Clock.UtcNow.AddHours(3)))).Code);
        Assert.Equal("recovery_point_time", (await Refused(Request(app, taken: app.Clock.UtcNow.AddDays(-400)))).Code);
        Assert.Equal("recovery_point_version", (await Refused(Request(app, version: "latest"))).Code);
        Assert.Equal("recovery_point_field", (await Refused(Request(app, id: ""))).Code);
        Assert.Equal("recovery_point_field", (await Refused(Request(app, reason: "  "))).Code);
        Assert.Equal("recovery_point_field", (await Refused(Request(app, id: new string('x', 300)))).Code);
        Assert.Equal("recovery_point_field", (await Refused(Request(app, id: "line one\nline two"))).Code);

        // Declaring twice in the same instant is refused rather than overwriting the first record.
        var first = await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().DeclareAsync(Request(app, id: "Same"), Dba));
        var ex = await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().DeclareAsync(Request(app, id: "Same"), Dba)));
        Assert.Equal("recovery_point_exists", ex.Code);
        var kept = await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().FindAsync(first.PointId));
        Assert.Equal(first.RecordSha256, kept!.RecordSha256);
    }

    public static async Task A_secret_pasted_into_a_declaration_is_redacted_and_an_identifier_is_never_used_as_a_path(TestApp app)
    {
        var point = await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().DeclareAsync(
            Request(app, reason: "Server=x;Database=y;User Id=u;Password=SuperSecretValue123 restore point"), Dba));
        Assert.DoesNotContain("SuperSecretValue123", point.Reason);
        var directory = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RecoveryOptions>>().Value.ExternalRecoveryPointsDirectory;
        var file = File.ReadAllText(Path.Combine(directory, point.PointId + ".json"));
        Assert.DoesNotContain("SuperSecretValue123", file);
        foreach (var hostile in new[] { "../../etc/passwd", "ext-..\\..\\x", "ext-20260101T000000Z-../x", point.PointId + "/../x", "bk-20260101T000000Z-abcdef12" })
            Assert.Null(await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().FindAsync(hostile)));
    }

    public static async Task The_upgrade_preflight_treats_a_declared_point_as_NOT_VERIFIED_and_checks_its_version_environment_and_age(TestApp app)
    {
        var point = await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().DeclareAsync(Request(app), Dba));
        PreflightReport report = await InstallationAsync(app, s => s.GetRequiredService<IUpgradePreflight>().RunAsync(
            new UpgradeTarget(app.Services, app.ConnectionString, "", true, "test"), new PreflightOptions(point.PointId, PreflightPhase.Apply)));
        var check = Assert.Single(report.Checks, c => c.Code == "UPF-020");
        Assert.Equal(CheckOutcome.NotVerified, check.Outcome);                      // never Passed: the platform did not verify it
        Assert.Contains("Declared by DBA; not verified by this application", check.Description);
        Assert.Contains(point.PointId, check.Description);
        Assert.Contains(point.RecordSha256, check.TechnicalDetail);
        Assert.False(report.CanApply);                                              // and an unaccepted NOT VERIFIED check blocks the upgrade

        var unknown = await InstallationAsync(app, s => s.GetRequiredService<IUpgradePreflight>().RunAsync(
            new UpgradeTarget(app.Services, app.ConnectionString, "", true, "test"), new PreflightOptions("ext-20260101T000000Z-not-declared", PreflightPhase.Apply)));
        Assert.Contains(unknown.Checks, c => c.Code == "UPF-020" && c.Outcome == CheckOutcome.Failed);

        var unnamed = await InstallationAsync(app, s => s.GetRequiredService<IUpgradePreflight>().RunAsync(
            new UpgradeTarget(app.Services, app.ConnectionString, "", true, "test"), new PreflightOptions(null, PreflightPhase.Apply)));
        Assert.Contains(unnamed.Checks, c => c.Code == "UPF-020" && c.Outcome == CheckOutcome.Failed);
    }

    public static async Task Rehearsal_and_rollback_evidence_refuse_a_declared_point_because_this_application_cannot_restore_it(TestApp app)
    {
        var point = await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().DeclareAsync(Request(app), Dba));
        var rehearsal = await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app, s => s.GetRequiredService<IUpgradeRehearsal>().RehearseAsync(
            new RehearsalRequest(point.PointId, "ecp_rehearsal", "try", "x", Dba, []))));
        Assert.Equal("external_recovery_point", rehearsal.Code);
        var rollback = await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app, s => s.GetRequiredService<IRollbackEvidence>().ProveAsync(
            new RollbackEvidenceRequest(point.PointId, "ecp_rollback", "try", "x", Dba))));
        Assert.Equal("external_recovery_point", rollback.Code);
    }

    public static async Task Readiness_says_recovery_is_owned_externally_and_never_that_the_application_made_a_backup(TestApp app)
    {
        var before = await InstallationAsync(app, s => s.GetServices<IReadinessCheck>().Single(c => c.Id == "R-BKP-01").RunAsync(default));
        Assert.Contains("owned by the database administrators", before.Message);
        Assert.Contains("does not create or verify backups", before.Message);
        Assert.Contains("No recovery point has been declared", before.Message);
        var point = await InstallationAsync(app, s => s.GetRequiredService<IExternalRecoveryPoints>().DeclareAsync(Request(app), Dba));
        var after = await InstallationAsync(app, s => s.GetServices<IReadinessCheck>().Single(c => c.Id == "R-BKP-01").RunAsync(default));
        Assert.Contains(point.PointId, after.Message);
        Assert.Contains("Declared by DBA; not verified by this application", after.Message);
        Assert.DoesNotContain("recent verified backup exists", after.Message);
    }
}

[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ExternalRecoveryPointTests_Sqlite
{
    [Fact] public Task Declare() => ProviderRunner.SqliteAsync(ExternalRecoveryPointScenarios.A_declared_recovery_point_is_recorded_audited_and_never_called_verified, ExternalRecoveryPointScenarios.Configure);
    [Fact] public Task Rules() => ProviderRunner.SqliteAsync(ExternalRecoveryPointScenarios.A_declaration_is_complete_plausible_for_this_environment_and_append_only, ExternalRecoveryPointScenarios.Configure);
    [Fact] public Task Redaction() => ProviderRunner.SqliteAsync(ExternalRecoveryPointScenarios.A_secret_pasted_into_a_declaration_is_redacted_and_an_identifier_is_never_used_as_a_path, ExternalRecoveryPointScenarios.Configure);
    [Fact] public Task Preflight() => ProviderRunner.SqliteAsync(ExternalRecoveryPointScenarios.The_upgrade_preflight_treats_a_declared_point_as_NOT_VERIFIED_and_checks_its_version_environment_and_age, ExternalRecoveryPointScenarios.Configure);
    [Fact] public Task Rehearsal() => ProviderRunner.SqliteAsync(ExternalRecoveryPointScenarios.Rehearsal_and_rollback_evidence_refuse_a_declared_point_because_this_application_cannot_restore_it, ExternalRecoveryPointScenarios.Configure);
    [Fact] public Task Readiness() => ProviderRunner.SqliteAsync(ExternalRecoveryPointScenarios.Readiness_says_recovery_is_owned_externally_and_never_that_the_application_made_a_backup, ExternalRecoveryPointScenarios.Configure);
}

[Trait("Category", "SqlServer")]
public sealed class ExternalRecoveryPointTests_SqlServer
{
    [SqlServerFact] public Task Declare() => ProviderRunner.SqlServerAsync(ExternalRecoveryPointScenarios.A_declared_recovery_point_is_recorded_audited_and_never_called_verified, ExternalRecoveryPointScenarios.Configure);
    [SqlServerFact] public Task Preflight() => ProviderRunner.SqlServerAsync(ExternalRecoveryPointScenarios.The_upgrade_preflight_treats_a_declared_point_as_NOT_VERIFIED_and_checks_its_version_environment_and_age, ExternalRecoveryPointScenarios.Configure);
    [SqlServerFact] public Task Readiness() => ProviderRunner.SqlServerAsync(ExternalRecoveryPointScenarios.Readiness_says_recovery_is_owned_externally_and_never_that_the_application_made_a_backup, ExternalRecoveryPointScenarios.Configure);
}
