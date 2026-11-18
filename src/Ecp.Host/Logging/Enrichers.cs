using Ecp.Host.Security;
using Ecp.Persistence;
using Serilog.Core;
using Serilog.Events;

namespace Ecp.Host.Logging;

/// <summary>
/// Adds the same context to every log event: application name, version, environment (mode), process and thread, and the database as an identifier (server and database name,
/// never a credential). The correlation id and the signed-in person's identifier are added per request. A person is identified by their account id and provider, not their name or email.
/// </summary>
public sealed class EcpLogEnricher(string version, string environment, string databaseIdentifier, IHttpContextAccessor? accessor = null) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        void Add(string name, object? value) { if (value is not null) logEvent.AddPropertyIfAbsent(factory.CreateProperty(name, value)); }
        Add("Application", LoggingSettings.ApplicationName);
        Add("Version", version);
        Add("Environment", environment);
        Add("ProcessId", Environment.ProcessId);
        Add("ThreadId", Environment.CurrentManagedThreadId);
        Add("Database", databaseIdentifier);
        var context = accessor?.HttpContext;
        if (context is null) return;
        Add("CorrelationId", context.TraceIdentifier);
        if (context.User.FindFirst(EcpClaims.UserId)?.Value is { Length: > 0 } login) Add("Login", login);
        if (context.User.FindFirst(EcpClaims.Provider)?.Value is { Length: > 0 } provider) Add("LoginProvider", provider);
    }

    /// <summary>"server/database" or "(none)": enough to tell which database a line is about, never how to sign in to it.</summary>
    public static string DatabaseIdentifier(DatabaseProvider provider, string connectionString)
    {
        if (provider != DatabaseProvider.SqlServer) return provider.ToString().ToLowerInvariant();
        var facts = SqlConnectionInspector.Inspect(connectionString);
        return facts.Server.Length == 0 ? "(none)" : facts.Server + "/" + (facts.Database.Length == 0 ? "(default)" : facts.Database);
    }
}
