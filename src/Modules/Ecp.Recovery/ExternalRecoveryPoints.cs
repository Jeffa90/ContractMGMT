using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Operations;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

/// <summary>Who owns backup and recovery. <see cref="Application"/>: the platform takes and verifies its own backups. <see cref="External"/>: the database administrators and infrastructure team own them; the platform only records what they declare.</summary>
public enum RecoveryMode { Application, External }

public static class ExternalRecoveryStatements
{
    /// <summary>Shown wherever a declared recovery point appears. The platform did not create it and cannot prove it can be restored.</summary>
    public const string DeclaredNotVerified = "Declared by DBA; not verified by this application";

    public const string IdPrefix = "ext-";
    public static bool IsExternalId(string? id) => id is not null && id.StartsWith(IdPrefix, StringComparison.Ordinal);
}

/// <summary>What the database administrator declares about a recovery point they took with their own tools. The platform checks it is complete and plausible; it does not restore or inspect it.</summary>
public sealed record ExternalRecoveryPointRequest(
    string Identifier, string Source, string Environment, string Verifier, DateTime TakenUtc, string DatabaseSchemaVersion, string Reason);

public sealed record ExternalRecoveryPoint(
    string PointId, string Identifier, string Source, string Environment, string Verifier, DateTime TakenUtc, string DatabaseSchemaVersion, string Reason,
    DateTime DeclaredUtc, string DeclaredBy, string CorrelationId, string Statement, bool VerifiedByApplication, string RecordSha256);

public interface IExternalRecoveryPoints
{
    /// <summary>Records the declaration (append-only: a declared point is never edited or replaced) and audits it. Never contacts the database server and never claims the point is verified.</summary>
    Task<ExternalRecoveryPoint> DeclareAsync(ExternalRecoveryPointRequest request, OperatorIdentity declaredBy, CancellationToken ct = default);

    Task<IReadOnlyList<ExternalRecoveryPoint>> ListAsync(CancellationToken ct = default);
    Task<ExternalRecoveryPoint?> FindAsync(string pointId, CancellationToken ct = default);
}

internal sealed partial class ExternalRecoveryPointStore(IOptions<RecoveryOptions> options, StartupFactsAccessor facts, OperationAudit audit, IActorContext actor, IClock clock) : IExternalRecoveryPoints
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[A-Za-z0-9.]+)?$", RegexOptions.NonBacktracking)] private static partial Regex VersionPattern();
    [GeneratedRegex(@"^ext-\d{8}T\d{6}Z-[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.NonBacktracking)] private static partial Regex PointIdPattern();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string Directory_ => options.Value.ExternalRecoveryPointsDirectory;

    private static string Bounded(string? value, string label, int max)
    {
        if (value is not null && value.Any(char.IsControl)) throw new DomainException("recovery_point_field", $"{label} must be a single line.");
        var text = Redaction.Clean(value)?.Trim() ?? "";
        if (text.Length == 0) throw new DomainException("recovery_point_field", $"{label} is required.");
        if (text.Length > max) throw new DomainException("recovery_point_field", $"{label} must be {max} characters or fewer.");
        return text;
    }

    public async Task<ExternalRecoveryPoint> DeclareAsync(ExternalRecoveryPointRequest request, OperatorIdentity declaredBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Directory_)) throw new DomainException("recovery_points_not_configured", "No folder is configured for declared recovery points (Ecp:Recovery:ExternalRecoveryPointsDirectory).");
        var identifier = Bounded(request.Identifier, "The recovery point identifier", 100);
        var source = Bounded(request.Source, "The source (how it was taken)", 200);
        var verifier = Bounded(request.Verifier, "The verifier (who confirmed it can be restored)", 200);
        var reason = Bounded(request.Reason, "The reason", 500);
        var environment = Bounded(request.Environment, "The environment", 40);
        var installation = facts.Mode.ToString();
        if (!string.Equals(environment, installation, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("environment_mismatch", $"The recovery point is declared for {environment} but this installation runs in {installation} mode. A recovery point from another environment is refused.");
        var version = (request.DatabaseSchemaVersion ?? "").Trim();
        if (!VersionPattern().IsMatch(version)) throw new DomainException("recovery_point_version", "The database schema version must look like 1.2.3 (the platform version the database held when the recovery point was taken).");
        var now = clock.UtcNow;
        var taken = request.TakenUtc.Kind == DateTimeKind.Local ? request.TakenUtc.ToUniversalTime() : DateTime.SpecifyKind(request.TakenUtc, DateTimeKind.Utc);
        if (taken > now.AddMinutes(5)) throw new DomainException("recovery_point_time", "The recovery point cannot have been taken in the future.");
        if (taken < now.AddDays(-365)) throw new DomainException("recovery_point_time", "A recovery point older than a year is not accepted. Take a fresh one.");

        var slug = Regex.Replace(identifier.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        slug = slug.Length == 0 ? "point" : slug[..Math.Min(40, slug.Length)].Trim('-');
        var pointId = $"{ExternalRecoveryStatements.IdPrefix}{now:yyyyMMddTHHmmss}Z-{slug}";
        var correlation = actor.CorrelationId;
        var core = new
        {
            pointId, identifier, source, environment = installation, verifier, takenUtc = taken.ToString("O"), databaseSchemaVersion = version, reason,
            declaredUtc = now.ToString("O"), declaredBy = Redaction.Clean(declaredBy.Id), correlationId = correlation,
            statement = ExternalRecoveryStatements.DeclaredNotVerified, verifiedByApplication = false
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(core, Json);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        System.IO.Directory.CreateDirectory(Directory_);
        var path = Path.Combine(Directory_, pointId + ".json");
        try { await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await stream.WriteAsync(bytes, ct); }
        catch (IOException) when (File.Exists(path)) { throw new DomainException("recovery_point_exists", "A recovery point with that identifier was already declared in the same second. Declared points are never replaced."); }

        // The audit event carries the hash of the exact record, so a later change to the file is detectable against the tamper-evident audit chain.
        await audit.RecordAsync(new AuditEntry("operations.recovery_point.declared", "RecoveryPoint", pointId, Reason: reason,
            NewValuesJson: JsonSerializer.Serialize(new
            {
                pointId, identifier, source, environment = installation, verifier, takenUtc = taken, databaseSchemaVersion = version, recordSha256 = sha,
                statement = ExternalRecoveryStatements.DeclaredNotVerified, verifiedByApplication = false, declaredBy = Redaction.Clean(declaredBy.Id)
            })), ct);
        return To(core.pointId, identifier, source, installation, verifier, taken, version, reason, now, Redaction.Clean(declaredBy.Id), correlation, sha);
    }

    private static ExternalRecoveryPoint To(string pointId, string identifier, string source, string environment, string verifier, DateTime taken, string version, string reason, DateTime declared, string by, string correlation, string sha)
        => new(pointId, identifier, source, environment, verifier, taken, version, reason, declared, by, correlation, ExternalRecoveryStatements.DeclaredNotVerified, false, sha);

    public async Task<ExternalRecoveryPoint?> FindAsync(string pointId, CancellationToken ct = default)
    {
        if (!PointIdPattern().IsMatch(pointId ?? "") || string.IsNullOrWhiteSpace(Directory_)) return null;   // an identifier is never used as a path unless it has exactly the generated shape
        var path = Path.Combine(Directory_, pointId + ".json");
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var r = doc.RootElement;
            string S(string n) => r.GetProperty(n).GetString() ?? "";
            return To(S("pointId"), S("identifier"), S("source"), S("environment"), S("verifier"), r.GetProperty("takenUtc").GetDateTime().ToUniversalTime(), S("databaseSchemaVersion"), S("reason"),
                r.GetProperty("declaredUtc").GetDateTime().ToUniversalTime(), S("declaredBy"), S("correlationId"), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    public async Task<IReadOnlyList<ExternalRecoveryPoint>> ListAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Directory_) || !System.IO.Directory.Exists(Directory_)) return [];
        var list = new List<ExternalRecoveryPoint>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory_, "ext-*.json").OrderByDescending(f => f, StringComparer.Ordinal))
            if (await FindAsync(Path.GetFileNameWithoutExtension(file), ct) is { } point) list.Add(point);
        return list;
    }
}

/// <summary>The installation mode, as the recovery module needs it. Registered by the host (the same facts the production guard judges).</summary>
internal sealed class StartupFactsAccessor(StartupFacts facts)
{
    public EcpMode Mode => facts.Mode;
}
