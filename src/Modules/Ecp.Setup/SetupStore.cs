using System.Text.Json;
using System.Text.Json.Serialization;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Setup.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecp.Setup;

/// <summary>
/// Persists setup state. The protected bootstrap file exists from the first start (before any database); once the database is migrated the same
/// document is mirrored into it. On load the newer revision wins. The "setup.complete" marker file is deliberately not secret: it only lets the
/// host leave the setup pages unmapped at start-up.
/// </summary>
public interface ISetupStore
{
    Task<SetupDocument?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(SetupDocument document, CancellationToken ct = default);
    bool CompletionMarkerExists();
}

internal sealed class SetupStore(ISetupProtector protector, SetupEnvironment env, IServiceScopeFactory scopes, IClock clock, ILogger<SetupStore> logger) : ISetupStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private string BootstrapDirectory => Path.Combine(env.DataDirectory, "bootstrap");
    private string StatePath => Path.Combine(BootstrapDirectory, "setup.state");
    private string MarkerPath => Path.Combine(BootstrapDirectory, "setup.complete");

    public bool CompletionMarkerExists() => File.Exists(MarkerPath);

    public async Task<SetupDocument?> LoadAsync(CancellationToken ct = default)
    {
        var file = await ReadFileAsync(ct);
        var db = await ReadDatabaseAsync(ct);
        if (file is null) return db;
        if (db is null) return file;
        return db.Revision >= file.Revision ? db : file;
    }

    public async Task SaveAsync(SetupDocument document, CancellationToken ct = default)
    {
        document.Revision++;
        var json = JsonSerializer.Serialize(document, Json);
        await FileLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(BootstrapDirectory);
            var temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, protector.Protect(json), ct);
            File.Move(temp, StatePath, overwrite: true);
            if (document.IsComplete) await File.WriteAllTextAsync(MarkerPath, document.CompletedUtc!.Value.ToString("O"), ct);
            else if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        }
        finally { FileLock.Release(); }
        await WriteDatabaseAsync(document, json, ct);
    }

    private async Task<SetupDocument?> ReadFileAsync(CancellationToken ct)
    {
        await FileLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<SetupDocument>(protector.Unprotect(await File.ReadAllTextAsync(StatePath, ct)), Json);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            // An unreadable bootstrap file is never silently replaced: setup refuses to continue and the operator decides.
            throw new InvalidOperationException("The setup bootstrap file could not be read (damaged, or protected with a key that is no longer available).");
        }
        finally { FileLock.Release(); }
    }

    private async Task<SetupDocument?> ReadDatabaseAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
            var record = await scope.ServiceProvider.GetRequiredService<SetupDbContext>().Records.AsNoTracking().FirstOrDefaultAsync(ct);
            return record is null ? null : JsonSerializer.Deserialize<SetupDocument>(record.StateJson, Json);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException or DbUpdateException)
        {
            return null; // the database or the setup tables do not exist yet
        }
    }

    private async Task WriteDatabaseAsync(SetupDocument document, string json, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
            var db = scope.ServiceProvider.GetRequiredService<SetupDbContext>();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async c =>
            {
                var record = await db.Records.FirstOrDefaultAsync(c);
                if (record is null) db.Records.Add(SetupRecord.Create(document.Revision, json, document.CompletedUtc, clock.UtcNow));
                else record.Apply(document.Revision, json, document.CompletedUtc, clock.UtcNow);
                await db.SaveChangesAsync(c);
            }, ct);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException or DbUpdateException)
        {
            logger.LogDebug("Setup state was not mirrored to the database yet: {Reason}", ex.GetType().Name);
        }
    }
}
