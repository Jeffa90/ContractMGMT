using System.Text.Json;
using Ecp.Audit;
using Ecp.Operations.Domain;
using Ecp.Operations.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Operations;

public sealed record AcceptedExceptionDto(Guid Id, string Kind, string Code, string Scope, string Reason, string AcceptedByName, DateTime AcceptedUtc, DateTime ExpiresUtc, bool Active, DateTime? RevokedUtc, string CorrelationId);

public static class AcceptedExceptionKinds
{
    public const string PreflightWarning = "preflight.warning";
    public const string ReadinessWarning = "readiness.warning";
    public const string MockScanner = "scanner.mock";
}

/// <summary>
/// Explicit acceptance of a warning by a named person, with a reason and an expiry (D14.13). It is the only thing that lets "Ready with warnings" count as Passed in evidence. Accepting never changes the original outcome:
/// the pre-flight stays "Ready with warnings" and the acceptance is shown beside it.
/// </summary>
public interface IAcceptedExceptions
{
    Task<AcceptedExceptionDto> AcceptAsync(string kind, string code, string scope, string reason, OperatorIdentity by, TimeSpan? validFor = null, CancellationToken ct = default);
    Task RevokeAsync(Guid id, OperatorIdentity by, CancellationToken ct = default);
    Task<IReadOnlyList<AcceptedExceptionDto>> ListAsync(bool activeOnly, CancellationToken ct = default);
    /// <summary>The codes that are accepted right now for <paramref name="kind"/>.</summary>
    Task<IReadOnlySet<string>> ActiveCodesAsync(string kind, CancellationToken ct = default);
}

internal sealed class AcceptedExceptions(OperationsDbContext db, IUnitOfWork uow, IAuditWriter audit, TenantContext tenant, IClock clock, IActorContext actor) : IAcceptedExceptions
{
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaximumValidity = TimeSpan.FromDays(90);

    private static AcceptedExceptionDto Map(AcceptedException e, DateTime now) => new(e.Id, e.Kind, e.Code, e.Scope, e.Reason, e.AcceptedByName, e.AcceptedUtc, e.ExpiresUtc, e.IsActiveAt(now), e.RevokedUtc, e.CorrelationId);

    public async Task<AcceptedExceptionDto> AcceptAsync(string kind, string code, string scope, string reason, OperatorIdentity by, TimeSpan? validFor = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10) throw new DomainException("reason_required", "Give the reason for accepting this warning (at least a short sentence). It is kept on the record.");
        var span = validFor ?? DefaultValidity;
        if (span <= TimeSpan.Zero || span > MaximumValidity) throw new DomainException("validity", $"An acceptance can last at most {(int)MaximumValidity.TotalDays} days; review it again after that.");
        using var _ = tenant.UseInstallationScope();
        var now = clock.UtcNow;
        return await uow.ExecuteAsync(async c =>
        {
            var row = AcceptedException.Accept(kind, code.Trim().ToUpperInvariant(), string.IsNullOrWhiteSpace(scope) ? "installation" : scope.Trim(), reason.Trim(), by.Id, by.Name, now, now + span, actor.CorrelationId);
            db.AcceptedExceptions.Add(row);
            audit.Record(new AuditEntry("operations.exception.accepted", "AcceptedException", row.Id.ToString(), Reason: row.Reason,
                NewValuesJson: JsonSerializer.Serialize(new { row.Kind, row.Code, row.Scope, row.ExpiresUtc, by = by.Id })));
            await db.SaveChangesAsync(c);
            return Map(row, now);
        }, ct);
    }

    public async Task RevokeAsync(Guid id, OperatorIdentity by, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        await uow.ExecuteAsync(async c =>
        {
            var row = await db.AcceptedExceptions.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new ResourceNotFoundException("That acceptance was not found.");
            if (row.RevokedUtc is not null) return;
            row.Revoke(by.Id, clock.UtcNow);
            audit.Record(new AuditEntry("operations.exception.revoked", "AcceptedException", row.Id.ToString(), NewValuesJson: JsonSerializer.Serialize(new { row.Kind, row.Code, by = by.Id })));
            await db.SaveChangesAsync(c);
        }, ct);
    }

    public async Task<IReadOnlyList<AcceptedExceptionDto>> ListAsync(bool activeOnly, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        var now = clock.UtcNow;
        var rows = await db.AcceptedExceptions.AsNoTracking().OrderByDescending(x => x.AcceptedUtc).Take(200).ToListAsync(ct);
        return [.. rows.Select(r => Map(r, now)).Where(r => !activeOnly || r.Active)];
    }

    public async Task<IReadOnlySet<string>> ActiveCodesAsync(string kind, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        var now = clock.UtcNow;
        var rows = await db.AcceptedExceptions.AsNoTracking().Where(x => x.Kind == kind && x.RevokedUtc == null && x.ExpiresUtc > now).Select(x => x.Code).ToListAsync(ct);
        return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class MockScannerAcceptance(IAcceptedExceptions exceptions) : IMockScannerAcceptance
{
    public const string Code = "MOCK-SCANNER";

    public async Task<MockScannerAcceptanceInfo?> CurrentAsync(CancellationToken ct = default)
    {
        var current = (await exceptions.ListAsync(activeOnly: true, ct)).FirstOrDefault(e => e.Kind == AcceptedExceptionKinds.MockScanner);
        return current is null ? null : new MockScannerAcceptanceInfo(current.AcceptedByName, current.Reason, current.Scope, current.AcceptedUtc, current.ExpiresUtc);
    }
}
