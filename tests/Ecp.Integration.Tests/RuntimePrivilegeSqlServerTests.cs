using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// The runtime login is read from the server, not assumed. Uses a SQL login only because CI's disposable Linux server has no Windows authentication;
/// the check itself is independent of how the login signs in. Windows integrated authentication is NOT VERIFIED here (needs a Windows host and the JBS domain).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class RuntimePrivilegeSqlServerTests
{
    private static StartupFacts Facts(EcpMode mode) => new(mode, DatabaseProvider.SqlServer, false, false, false, false, "/var/lib/ecp");

    [SqlServerFact]
    public async Task A_least_privilege_runtime_login_is_reported_healthy_and_the_administrator_is_flagged()
    {
        await using var app = await TestApp.CreateSqlServerAsync();
        var admin = SqlServerEnvironment.ConnectionString!;
        var database = new SqlConnectionStringBuilder(app.ConnectionString).InitialCatalog;
        var login = "ecp_rt_" + Guid.NewGuid().ToString("N")[..12];
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        await using (var c = new SqlConnection(admin))
        {
            await c.OpenAsync();
            foreach (var sql in new[]
            {
                $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF",
                $"USE [{database}]; CREATE USER [{login}] FOR LOGIN [{login}]",
                $"USE [{database}]; ALTER ROLE [{RowLevelSecurity.RuntimeRole}] ADD MEMBER [{login}]"
            })
            {
                await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync();
            }
        }
        try
        {
            var runtime = new SqlConnectionStringBuilder(app.ConnectionString) { UserID = login, Password = password }.ConnectionString;
            var healthy = await new DatabaseAccessReadinessCheck(new DatabaseOptions { Provider = DatabaseProvider.SqlServer, ConnectionString = runtime },
                Facts(EcpMode.Test)).RunAsync(default);
            Assert.Equal(ReadinessStatus.Healthy, healthy.Status);
            Assert.Contains(login, healthy.Message);
            Assert.DoesNotContain(password, healthy.Message);

            var privilege = await RuntimePrivilegeProbe.ProbeAsync(runtime);
            Assert.NotNull(privilege);
            Assert.True(privilege!.IsRuntimeRoleMember); Assert.False(privilege.IsSysAdmin); Assert.False(privilege.IsDbOwner);

            var flagged = await new DatabaseAccessReadinessCheck(new DatabaseOptions { Provider = DatabaseProvider.SqlServer, ConnectionString = app.ConnectionString },
                Facts(EcpMode.Test)).RunAsync(default);
            Assert.Equal(ReadinessStatus.Degraded, flagged.Status);
            Assert.Contains("sysadmin", flagged.Message);
        }
        finally
        {
            await using var c = new SqlConnection(admin); await c.OpenAsync();
            await using var cmd = c.CreateCommand(); cmd.CommandText = $"DROP LOGIN [{login}]"; await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task The_lightweight_provider_reports_not_applicable_rather_than_pretending()
    {
        var result = await new DatabaseAccessReadinessCheck(new DatabaseOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" }, Facts(EcpMode.Demo)).RunAsync(default);
        Assert.Equal(ReadinessStatus.Healthy, result.Status);
        Assert.Contains("not applicable", result.Message);
    }
}
