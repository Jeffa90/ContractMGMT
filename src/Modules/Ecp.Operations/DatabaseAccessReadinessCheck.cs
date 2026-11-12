using Ecp.Persistence;
using Ecp.SharedKernel;

namespace Ecp.Operations;

/// <summary>
/// R-DB-02: how the application signs in to SQL Server and what that login can do. It reads the login and its roles from the server rather than trusting
/// the configuration, never prints a credential, and flags an over-privileged runtime account (sysadmin or db_owner) because the runtime should only hold
/// the least-privilege runtime role; schema changes belong to a separate, deliberate migration identity (docs/DEPLOYMENT.md).
/// It cannot judge whether the database administrator's grants are correct; that stays a database-administrator answer.
/// </summary>
public sealed class DatabaseAccessReadinessCheck(DatabaseOptions database, StartupFacts facts) : IReadinessCheck
{
    public string Id => "R-DB-02";
    private const string Name = "Database sign-in and runtime privileges";

    public async Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        if (database.Provider != DatabaseProvider.SqlServer)
            return new ReadinessResult(Id, Name, ReadinessStatus.Healthy, "Lightweight provider: not applicable. Production requires SQL Server.");

        var connection = SqlConnectionInspector.Inspect(database.ConnectionString);
        var privilege = await RuntimePrivilegeProbe.ProbeAsync(database.ConnectionString, ct);
        var issues = new List<string>();
        var detail = $"The application connects to {connection.Describe()}.";
        if (privilege is null)
            return new ReadinessResult(Id, Name, ReadinessStatus.Degraded, detail + " The login and its roles could not be read.");
        detail += $" Database login: {privilege.Login}.";
        if (privilege.IsSysAdmin) issues.Add("the runtime login is a SQL Server sysadmin");
        if (privilege.IsDbOwner) issues.Add("the runtime login is db_owner in this database");
        if (!privilege.IsRuntimeRoleMember && !privilege.IsDbOwner) issues.Add($"the runtime login is not a member of the {RowLevelSecurity.RuntimeRole} role");
        if (facts.Mode == EcpMode.Production && !connection.Encrypt) issues.Add("the connection does not require encryption");
        if (facts.Mode == EcpMode.Production && connection.TrustServerCertificate) issues.Add("the server certificate is trusted without validation (TrustServerCertificate=True): an infrastructure answer is needed to remove it");
        return issues.Count == 0
            ? new ReadinessResult(Id, Name, ReadinessStatus.Healthy, detail + " The runtime login holds the runtime role only.")
            : new ReadinessResult(Id, Name, ReadinessStatus.Degraded, detail + " Attention: " + string.Join("; ", issues) + ".");
    }
}
