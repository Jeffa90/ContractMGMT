using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Infrastructure;

/// <summary>
/// Writes append-only audit events. Each event's hash covers its content and the previous event's hash, so a changed,
/// removed or reordered event breaks the chain (<see cref="VerifyAsync"/>). Always written inside the same transaction
/// as the change it records, so a change is never saved without its audit event.
/// </summary>
public sealed class AuditLog(TimeProvider clock)
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public sealed record Actor(int? PersonId, string Name)
    {
        public static readonly Actor System = new(null, "JBS Contracts");
    }

    public async Task WriteAsync(SqlConnection connection, SqlTransaction tx, Actor actor, string action, string subjectType, int? subjectId,
        string summary, object? details = null, int? contractId = null)
    {
        // One writer at a time extends the chain (released when the transaction ends).
        await connection.ExecuteAsync("EXEC sp_getapplock @Resource = N'contracts.audit', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000",
            transaction: tx);
        var previous = await connection.QuerySingleOrDefaultAsync<string>("SELECT TOP (1) Hash FROM contracts.AuditEvent ORDER BY Id DESC", transaction: tx) ?? Genesis;
        // Stored to the millisecond, so hash the millisecond value.
        var now = clock.GetUtcNow().UtcDateTime;
        var occurred = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var detailsJson = details is null ? null : JsonSerializer.Serialize(details);
        var hash = Hash(previous, occurred, actor.PersonId, actor.Name, action, subjectType, subjectId, contractId, summary, detailsJson);
        await connection.ExecuteAsync("""
            INSERT INTO contracts.AuditEvent (OccurredUtc, PersonId, ActorName, Action, SubjectType, SubjectId, ContractId, Summary, DetailsJson, PreviousHash, Hash)
            VALUES (@occurred, @personId, @actorName, @action, @subjectType, @subjectId, @contractId, @summary, @detailsJson, @previous, @hash)
            """,
            new { occurred, personId = actor.PersonId, actorName = actor.Name, action, subjectType, subjectId, contractId, summary, detailsJson, previous, hash }, tx);
    }

    public static string Hash(string previous, DateTime occurredUtc, int? personId, string actorName, string action, string subjectType, int? subjectId,
        int? contractId, string summary, string? detailsJson)
    {
        var text = string.Join('\u001f', previous, occurredUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
            personId?.ToString(CultureInfo.InvariantCulture) ?? "", actorName, action, subjectType, subjectId?.ToString(CultureInfo.InvariantCulture) ?? "",
            contractId?.ToString(CultureInfo.InvariantCulture) ?? "", summary, detailsJson ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public DateTime OccurredUtc { get; set; }
        public int? PersonId { get; set; }
        public string ActorName { get; set; } = "";
        public string Action { get; set; } = "";
        public string SubjectType { get; set; } = "";
        public int? SubjectId { get; set; }
        public int? ContractId { get; set; }
        public string Summary { get; set; } = "";
        public string? DetailsJson { get; set; }
        public string PreviousHash { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    /// <summary>Checks the whole chain. Returns the id of the first broken event, or null when it is intact.</summary>
    public static async Task<long?> VerifyAsync(SqlConnection connection)
    {
        var previous = Genesis;
        foreach (var r in await connection.QueryAsync<Row>("SELECT * FROM contracts.AuditEvent ORDER BY Id"))
        {
            var expected = Hash(previous, DateTime.SpecifyKind(r.OccurredUtc, DateTimeKind.Utc), r.PersonId, r.ActorName, r.Action, r.SubjectType,
                r.SubjectId, r.ContractId, r.Summary, r.DetailsJson);
            if (r.PreviousHash != previous || r.Hash != expected) return r.Id;
            previous = r.Hash;
        }
        return null;
    }
}
