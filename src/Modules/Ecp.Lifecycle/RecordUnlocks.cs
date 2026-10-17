using System.Text.Json;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Identity;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Lifecycle;

public sealed record UnlockRequest(Guid ContractId, string Reason, int Minutes, IReadOnlyList<string> Fields);

public sealed record UnlockInfo(Guid Id, Guid ContractId, string OpenedByName, string Reason, IReadOnlyList<string> Fields, DateTime OpenedUtc, DateTime ExpiresUtc, DateTime? ClosedUtc, string? ClosedBy,
    string? CloseKind, int ChangeCount, bool IsOpen);

/// <summary>What the unlock form offers: the fields an unlock may name, the longest it may last and whether the organisation allows it at all.</summary>
public sealed record UnlockPolicyInfo(bool Enabled, int DefaultMinutes, int MaxMinutes, IReadOnlyList<string> Fields);

/// <summary>
/// The audited emergency unlock of a locked record. It needs the permission, a recent sign-in, a written reason and a time limit; it opens only the fields it names; every change made under it
/// carries the unlock id in the audit; it closes by itself at expiry. It never changes status, Registered Date, the approval or its snapshot.
/// </summary>
public interface IRecordUnlockService
{
    Task<UnlockPolicyInfo> PolicyAsync(Guid contractId, CancellationToken ct = default);
    Task<UnlockInfo> OpenAsync(UnlockRequest request, CancellationToken ct = default);
    /// <summary>Applies changes to the fields the open unlock names. Only the person who opened the unlock may make them.</summary>
    Task<IReadOnlyList<FieldChange>> ApplyChangesAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default);
    Task CloseAsync(Guid contractId, CancellationToken ct = default);
    Task<UnlockInfo?> CurrentAsync(Guid contractId, CancellationToken ct = default);
    Task<IReadOnlyList<UnlockInfo>> HistoryAsync(Guid contractId, CancellationToken ct = default);
    /// <summary>Closes every unlock past its time limit (a scheduled job). Returns how many were closed.</summary>
    Task<int> CloseExpiredAsync(CancellationToken ct = default);
}

internal sealed class RecordUnlockService(
    LifecycleDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, ActorContext actor, CurrentUser currentUser, IAccessService access, IAccessDirectory directory,
    IContractLifecyclePort port, ISecuredContractService contracts, IWorkflowDefinitions definitions, ReauthenticationContext reauth, IOptions<LifecycleOptions> options, LifecycleEvents events) : IRecordUnlockService
{
    /// <summary>Classification is changed through the classification path (which keeps documents at or above the record and records why), not as a free field.</summary>
    private static readonly string[] OfferedFields = [.. Contract.Editable.UnlockFields.Where(f => f != "classification")];

    private string Label => actor.ActorId ?? currentUser.UserId?.ToString() ?? "system";

    private async Task<(ContractFacts Facts, Ecp.Identity.Access.ResourceDescriptor Descriptor, UnlockSettings Settings)> LoadAsync(Guid contractId, CancellationToken ct)
    {
        var facts = await port.FindAsync(contractId, ct) ?? throw new ResourceNotFoundException();
        var def = await definitions.ResolveAsync(WorkflowKinds.ContractLifecycle, facts.OrganisationId, facts.ConfigReleaseId, ct);
        var descriptor = (await contracts.DescribeAsync(contractId, ct)) with { EmergencyUnlockAuthorised = def.Definition.Settings.Unlock.Enabled };
        return (facts, descriptor, def.Definition.Settings.Unlock);
    }

    public async Task<UnlockPolicyInfo> PolicyAsync(Guid contractId, CancellationToken ct = default)
    {
        var (_, descriptor, settings) = await LoadAsync(contractId, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        return new UnlockPolicyInfo(settings.Enabled, settings.DefaultMinutes, settings.MaxMinutes, OfferedFields);
    }

    public async Task<UnlockInfo> OpenAsync(UnlockRequest r, CancellationToken ct = default)
    {
        var (facts, descriptor, settings) = await LoadAsync(r.ContractId, ct);
        await access.RequireAsync("contracts.unlock_emergency", descriptor, ct);
        var now = clock.UtcNow;
        // Sign in again first: a stolen idle session must not be able to open a locked agreement.
        reauth.Require(now, options.Value.ReauthenticationWindow);
        var user = currentUser.UserId ?? throw new ForbiddenException("A signed-in person is required.");
        if (!settings.Enabled) throw new DomainException("unlock_disabled", "Emergency unlock is not enabled by this [[organisation|lower]]'s policy.");
        if (!facts.RecordLocked) throw new DomainException("not_locked", "This [[agreement|lower]] is not locked, so there is nothing to unlock.");
        if (r.Minutes < 5 || r.Minutes > settings.MaxMinutes) throw new DomainException("invalid_duration", $"An unlock lasts between 5 and {settings.MaxMinutes} minutes.");
        var fields = r.Fields.Distinct(StringComparer.Ordinal).ToList();
        if (fields.Count == 0) throw new DomainException("fields_required", "Choose at least one field to unlock.");
        var unknown = fields.Where(f => !OfferedFields.Contains(f)).ToList();
        if (unknown.Count > 0) throw new DomainException("field_not_changeable", $"These fields cannot be unlocked: {string.Join(", ", unknown)}.");

        var name = await directory.DisplayNameAsync(user, ct) ?? Label;
        var unlock = RecordUnlock.Open(tenant.RequireTenantId(), facts.OrganisationId, facts.Id, user, name, r.Reason, JsonSerializer.Serialize(fields), reauth.LastAuthenticationUtc ?? now, TimeSpan.FromMinutes(r.Minutes), now);
        await uow.ExecuteAsync(async c =>
        {
            // A previous unlock past its limit is closed first so the record never carries two.
            foreach (var stale in await db.Unlocks.Where(u => u.ContractId == facts.Id && u.ClosedUtc == null).ToListAsync(c))
                stale.Close("system", stale.ExpiresUtc <= now ? "Expired" : "Superseded", now);
            db.Unlocks.Add(unlock);
            await db.SaveChangesAsync(c);
            await port.OpenUnlockAsync(facts.Id, unlock.Id, unlock.ExpiresUtc, Label, c);
            audit.Record(new AuditEntry("unlock.opened", nameof(RecordUnlock), unlock.Id.ToString(), OrganisationId: facts.OrganisationId, Reason: unlock.Reason, ElevationId: unlock.Id, Classification: facts.Classification,
                NewValuesJson: AuditJson.Of(new { contractId = facts.Id, agreement = facts.AgreementReference, fields, expiresUtc = unlock.ExpiresUtc, reauthenticatedUtc = unlock.ReauthenticatedUtc, status = facts.Status })));
            events.Raise(LifecycleEventTypes.UnlockOpened, facts.OrganisationId, SubjectKinds.Contract, facts.Id, facts.Id, facts.Classification, new { unlockId = unlock.Id, expiresUtc = unlock.ExpiresUtc });
            await db.SaveChangesAsync(c);
        }, ct);
        return ToInfo(unlock, now);
    }

    public async Task<IReadOnlyList<FieldChange>> ApplyChangesAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default)
    {
        var (facts, descriptor, _) = await LoadAsync(contractId, ct);
        await access.RequireAsync("contracts.unlock_emergency", descriptor, ct);
        var user = currentUser.UserId ?? throw new ForbiddenException("A signed-in person is required.");
        var now = clock.UtcNow;
        var unlock = await db.Unlocks.FirstOrDefaultAsync(u => u.ContractId == contractId && u.ClosedUtc == null && u.ExpiresUtc > now, ct)
            ?? throw new DomainException("no_open_unlock", "There is no open unlock on this [[agreement|lower]], or its time limit has passed.");
        if (unlock.OpenedByUserId != user) throw new ForbiddenException("Only the person who opened the unlock can make changes under it.");
        if (changes.Count == 0) throw new DomainException("nothing_to_change", "No changes were given.");
        var allowed = JsonSerializer.Deserialize<List<string>>(unlock.FieldsJson) ?? [];
        var outside = changes.Keys.Where(k => !allowed.Contains(k)).ToList();
        if (outside.Count > 0) throw new DomainException("field_not_unlocked", $"This unlock does not include: {string.Join(", ", outside)}.");
        return await uow.ExecuteAsync<IReadOnlyList<FieldChange>>(async c =>
        {
            var applied = await port.ApplyControlledChangesAsync(contractId, changes, allowed, Label, "unlock", unlock.Id, unlock.Reason, c);
            if (applied.Count > 0)
            {
                unlock.CountChange();
                await db.SaveChangesAsync(c);
            }
            return applied;
        }, ct);
    }

    public async Task CloseAsync(Guid contractId, CancellationToken ct = default)
    {
        var (facts, descriptor, _) = await LoadAsync(contractId, ct);
        await access.RequireAsync("contracts.unlock_emergency", descriptor, ct);
        var now = clock.UtcNow;
        var open = await db.Unlocks.Where(u => u.ContractId == contractId && u.ClosedUtc == null).ToListAsync(ct);
        if (open.Count == 0 && facts.ActiveUnlockId is null) return;
        await uow.ExecuteAsync(async c =>
        {
            foreach (var u in open) u.Close(Label, "Early", now);
            await db.SaveChangesAsync(c);
            await port.CloseUnlockAsync(contractId, Label, null, c);
            foreach (var u in open)
            {
                audit.Record(new AuditEntry("unlock.closed", nameof(RecordUnlock), u.Id.ToString(), OrganisationId: facts.OrganisationId, ElevationId: u.Id, Classification: facts.Classification,
                    NewValuesJson: AuditJson.Of(new { contractId, kind = "Early", changes = u.ChangeCount })));
                events.Raise(LifecycleEventTypes.UnlockClosed, facts.OrganisationId, SubjectKinds.Contract, contractId, contractId, facts.Classification, new { unlockId = u.Id, kind = "Early" });
            }
            await db.SaveChangesAsync(c);
        }, ct);
    }

    public async Task<UnlockInfo?> CurrentAsync(Guid contractId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var (_, descriptor, _) = await LoadAsync(contractId, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        var u = await db.Unlocks.AsNoTracking().Where(x => x.ContractId == contractId && x.ClosedUtc == null && x.ExpiresUtc > now).OrderByDescending(x => x.OpenedUtc).FirstOrDefaultAsync(ct);
        return u is null ? null : ToInfo(u, now);
    }

    public async Task<IReadOnlyList<UnlockInfo>> HistoryAsync(Guid contractId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var (_, descriptor, _) = await LoadAsync(contractId, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        return (await db.Unlocks.AsNoTracking().Where(x => x.ContractId == contractId).OrderByDescending(x => x.OpenedUtc).ToListAsync(ct)).Select(u => ToInfo(u, now)).ToList();
    }

    public async Task<int> CloseExpiredAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var due = await db.Unlocks.Where(u => u.ClosedUtc == null && u.ExpiresUtc <= now).ToListAsync(ct);
        foreach (var u in due)
        {
            var facts = await port.FindAsync(u.ContractId, ct);
            await uow.ExecuteAsync(async c =>
            {
                u.Close("system", "Expired", now);
                await db.SaveChangesAsync(c);
                await port.CloseUnlockAsync(u.ContractId, "system", u.Id, c);
                audit.Record(new AuditEntry("unlock.closed", nameof(RecordUnlock), u.Id.ToString(), OrganisationId: u.OrganisationId, ElevationId: u.Id, Classification: facts?.Classification,
                    NewValuesJson: AuditJson.Of(new { contractId = u.ContractId, kind = "Expired", changes = u.ChangeCount })));
                events.Raise(LifecycleEventTypes.UnlockClosed, u.OrganisationId, SubjectKinds.Contract, u.ContractId, u.ContractId, facts?.Classification ?? Classifications.Internal, new { unlockId = u.Id, kind = "Expired" });
                await db.SaveChangesAsync(c);
            }, ct);
        }
        return due.Count;
    }

    private static UnlockInfo ToInfo(RecordUnlock u, DateTime now)
        => new(u.Id, u.ContractId, u.OpenedByName, u.Reason, JsonSerializer.Deserialize<List<string>>(u.FieldsJson) ?? [], u.OpenedUtc, u.ExpiresUtc, u.ClosedUtc, u.ClosedBy, u.CloseKind, u.ChangeCount, u.IsOpenAt(now));
}
