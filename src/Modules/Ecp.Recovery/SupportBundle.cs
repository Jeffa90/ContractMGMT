using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Ecp.Configuration;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

public sealed record LogLine(DateTime Utc, string Level, string Category, string Message, string? ExceptionType);

/// <summary>
/// Keeps the last few warnings and errors in memory so a support bundle can include them. Exception messages and stack traces are not kept (they can carry data); only the exception type and the message the code
/// wrote. Every line is redacted again when the bundle is built.
/// </summary>
public sealed class RecentLogBuffer : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly Queue<LogLine> _lines = new();
    private const int Capacity = 200;

    public ILogger CreateLogger(string categoryName) => new Buffered(this, categoryName);
    public void Dispose() { }

    public IReadOnlyList<LogLine> Snapshot() { lock (_gate) return _lines.ToList(); }

    private sealed class Buffered(RecentLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var cleaned = Redaction.Clean(formatter(state, null));
            var line = new LogLine(DateTime.UtcNow, logLevel.ToString(), category, cleaned.Length > 300 ? cleaned[..300] : cleaned, exception?.GetType().Name);
            lock (owner._gate) { owner._lines.Enqueue(line); while (owner._lines.Count > Capacity) owner._lines.Dequeue(); }
        }
    }
}

public interface ISupportBundle
{
    /// <summary>A zip holding bundle.json (counts, versions, statuses, redacted log lines) and a README. Never secrets, connection strings, keys, contract or document content, or personal data.</summary>
    Task<byte[]> BuildAsync(CancellationToken ct = default);
}

internal sealed class SupportBundle(IServiceScopeFactory scopes, ReadinessService readiness, IBackupCatalogue catalogue, IOperationHistory history, IMaintenanceService maintenance, IOperationLeases leases,
    IOptions<RecoveryOptions> recovery, StartupFacts facts, IClock clock, IEnumerable<RecentLogBuffer> logs) : ISupportBundle
{
    public async Task<byte[]> BuildAsync(CancellationToken ct = default)
    {
        var report = await readiness.RunAsync(ct);
        var m = await maintenance.GetAsync(ct);
        var modules = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Ecp.", StringComparison.Ordinal) == true && !a.GetName().Name!.Contains("Tests", StringComparison.Ordinal))
            .Select(a => new { name = a.GetName().Name, version = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() }).OrderBy(a => a.name, StringComparer.Ordinal).ToList();

        var migrations = new List<object>(); var tenantsOut = new List<object>(); var queue = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long tenants = 0, organisations = 0;
        await using (var scope = scopes.CreateAsyncScope())
        {
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
            foreach (var s in (await SchemaInspector.ReadAsync(scope.ServiceProvider, ct)).Modules) migrations.Add(new { module = s.Module, applied = s.AppliedMigrations.Count, latest = s.AppliedMigrations.LastOrDefault() });
            var directory = (await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(ct)).Where(t => t.Id != SystemTenant.Id).OrderBy(t => t.Id).ToList();
            tenants = directory.Count;
            foreach (var t in directory)
            {
                await using var ts = scopes.CreateAsyncScope();
                using var __ = ts.ServiceProvider.GetRequiredService<TenantContext>().Use(t.Id);
                var packs = (await ts.ServiceProvider.GetRequiredService<IConfigurationInventory>().InstalledPacksAsync(ct)).Select(p => new { p.PackKey, p.Version, sha256 = p.PackageSha256, p.SignatureStatus, p.Status }).ToList();
                var releases = (await ts.ServiceProvider.GetRequiredService<IConfigurationInventory>().ReleasesAsync(ct)).GroupBy(r => r.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new { kind = g.Key, releases = g.Count(), highest = g.Max(r => r.Number) }).ToList();
                var connection = ts.ServiceProvider.GetRequiredService<IScopedConnection>();
                long orgCount = 0;
                var orgTable = InstallationInventory.Qualified(connection, "tenancy", "Organisation");
                await connection.ApplyTenantScopeAsync(ct);
                await using (var cmd = connection.Connection.CreateCommand()) { cmd.Transaction = connection.Transaction; cmd.CommandText = InstallationInventory.ForProvider(connection, $"SELECT COUNT_BIG(*) FROM {orgTable}"); orgCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)); }
                organisations += orgCount;
                var deliveries = InstallationInventory.Qualified(connection, "notifications", "NotificationDelivery");
                await using (var cmd = connection.Connection.CreateCommand())
                {
                    cmd.Transaction = connection.Transaction; cmd.CommandText = InstallationInventory.ForProvider(connection, $"SELECT Status, COUNT_BIG(*) FROM {deliveries} GROUP BY Status");
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) { var key = Convert.ToString(r.GetValue(0)) ?? "?"; queue[key] = queue.GetValueOrDefault(key) + r.GetInt64(1); }
                }
                tenantsOut.Add(new { tenantId = t.Id, organisations = orgCount, installedPacks = packs, configuration = releases });
            }
        }

        var failures = (await history.ListAsync(null, 100, ct)).Where(r => r.Status is OperationStatuses.Failed or OperationStatuses.Incomplete).Take(20)
            .Select(r => new { r.Kind, r.Status, reference = Redaction.Clean(r.Reference), summary = Redaction.Clean(r.Summary), r.StartedUtc, r.CorrelationId }).ToList();
        var correlations = (await history.ListAsync(null, 20, ct)).Select(r => r.CorrelationId).Distinct().ToList();
        var backups = (await catalogue.ListAsync(ct)).Take(10).Select(b => new { b.BackupId, state = b.State.ToString(), b.CreatedUtc, b.Outcome, b.Warnings, verifiedUtc = b.VerifiedUtc }).ToList();
        var lastRestore = (await history.ListAsync(OperationKinds.Restore, 3, ct)).Select(r => new { r.Status, reference = Redaction.Clean(r.Reference), r.StartedUtc, r.CompletedUtc, summary = Redaction.Clean(r.Summary) }).ToList();

        var bundle = new
        {
            generatedUtc = clock.UtcNow, environmentMode = facts.Mode.ToString(), databaseProvider = facts.Provider.ToString(), modules, migrations,
            readiness = new { overall = report.Overall.ToString(), checks = report.Results.Select(r => new { r.Id, status = r.Status.ToString(), message = Redaction.Clean(r.Message) }) },
            counts = new { tenants, organisations }, tenants = tenantsOut, notificationQueue = queue,
            backupAndRestore = new { backupConfigured = recovery.Value.BackupConfigured, encryptionRequested = recovery.Value.EncryptionRequested, maintenance = m.Enabled ? m.Cause : "Off", recentBackups = backups, recentRestores = lastRestore,
                leases = (await leases.ListAsync(ct)).Select(l => new { l.Name, l.ScopeKey, l.IsActive, l.AcquiredUtc, l.ExpiresUtc, l.Generation }) },
            recentFailures = failures, recentCorrelationIds = correlations,
            deploymentModels = DeploymentModels.All,
            recentLogLines = logs.SelectMany(l => l.Snapshot()).OrderByDescending(l => l.Utc).Take(100).Select(l => new { l.Utc, l.Level, l.Category, message = Redaction.Clean(l.Message), l.ExceptionType }),
            excluded = new[] { "secrets", "tokens", "passwords", "connection strings", "private keys", "contract content", "document content", "personal data", "provider callback payloads", "raw configuration content" },
        };
        var json = JsonSerializer.Serialize(bundle, ManifestJson.Options) + "\n";
        if (Redaction.ContainsSecret(json)) throw new DomainException("secret_in_bundle", "The support bundle was not produced because it appears to contain a secret.");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "bundle.json", json);
            Add(zip, "README.txt", "Support bundle\n\nThis bundle lists versions, statuses, counts and redacted log lines to help diagnose a problem. It contains no secrets, connection strings, private keys, contract content, document content or personal data.\nCheck it before you send it anywhere; you remain responsible for where it goes.\n");
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}

