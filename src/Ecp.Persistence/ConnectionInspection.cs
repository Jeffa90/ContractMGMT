using Microsoft.Data.SqlClient;

namespace Ecp.Persistence;

public enum ConnectionAuthentication
{
    /// <summary>Not a SQL Server connection string, or one that could not be read.</summary>
    Unknown,

    /// <summary>Windows integrated authentication: the process (for IIS, the application-pool identity) is the database login. The string holds no credential.</summary>
    IntegratedWindows,

    /// <summary>A managed or integrated directory identity (Azure SQL). The string holds no credential.</summary>
    DirectoryIdentity,

    /// <summary>A SQL login with a user name and password: the string is a secret and must come from a secret reference.</summary>
    SqlLogin
}

/// <summary>What a connection string says about how the application signs in, with nothing secret in it (server and database names are not credentials).</summary>
public sealed record ConnectionFacts(ConnectionAuthentication Authentication, string Server, string Database, bool Encrypt, bool TrustServerCertificate)
{
    /// <summary>True when the string contains no user name or password, so it may be placed in ordinary configuration.</summary>
    public bool CredentialFree => Authentication is ConnectionAuthentication.IntegratedWindows or ConnectionAuthentication.DirectoryIdentity;

    /// <summary>A line safe to log or show: names the server, database and sign-in method, never a credential.</summary>
    public string Describe() => $"server {(Server.Length == 0 ? "(none)" : Server)}, database {(Database.Length == 0 ? "(none)" : Database)}, authentication {Authentication}";

    public static readonly ConnectionFacts None = new(ConnectionAuthentication.Unknown, "", "", false, false);
}

public static class SqlConnectionInspector
{
    public static ConnectionFacts Inspect(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return ConnectionFacts.None;
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            var hasCredential = !string.IsNullOrEmpty(b.UserID) || !string.IsNullOrEmpty(b.Password);
            var auth = b.Authentication switch
            {
                SqlAuthenticationMethod.ActiveDirectoryIntegrated or SqlAuthenticationMethod.ActiveDirectoryManagedIdentity
                    or SqlAuthenticationMethod.ActiveDirectoryMSI or SqlAuthenticationMethod.ActiveDirectoryDefault when !hasCredential => ConnectionAuthentication.DirectoryIdentity,
                _ when hasCredential => ConnectionAuthentication.SqlLogin,
                _ when b.IntegratedSecurity => ConnectionAuthentication.IntegratedWindows,
                _ => ConnectionAuthentication.Unknown
            };
            return new ConnectionFacts(auth, b.DataSource, b.InitialCatalog, b.Encrypt != SqlConnectionEncryptOption.Optional, b.TrustServerCertificate);
        }
        catch (Exception)
        {
            return ConnectionFacts.None; // an unreadable string is treated as carrying credentials by the guard (it is not "credential free")
        }
    }
}

/// <summary>The database login the application actually runs as and the privileges it holds. Read from the server, never assumed.</summary>
public sealed record RuntimePrivilege(string Login, bool IsSysAdmin, bool IsDbOwner, bool IsRuntimeRoleMember);

public static class RuntimePrivilegeProbe
{
    /// <summary>Asks SQL Server who the connection is and which of the powerful roles it holds. Returns null if the server cannot be reached.</summary>
    public static async Task<RuntimePrivilege?> ProbeAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT SUSER_SNAME(), IS_SRVROLEMEMBER('sysadmin'), IS_MEMBER('db_owner'), IS_MEMBER(@role)";
            cmd.Parameters.AddWithValue("@role", RowLevelSecurity.RuntimeRole);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            static bool Yes(object v) => v is not DBNull && Convert.ToInt32(v) == 1;
            return new RuntimePrivilege(reader.IsDBNull(0) ? "" : reader.GetString(0), Yes(reader.GetValue(1)), Yes(reader.GetValue(2)), Yes(reader.GetValue(3)));
        }
        catch (Exception) { return null; }
    }
}
