using System.Security.Cryptography;
using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Identity;
using Ecp.Packs;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Configuration;

public sealed record TrustKeyView(string KeyId, string Algorithm, string Fingerprint, string Label, string Source, string Status, DateTime? NotBeforeUtc, DateTime? NotAfterUtc,
    string? RegisteredBy, DateTime? RegisteredUtc, string? RevokedBy, DateTime? RevokedUtc, string? RevokedReason, bool Editable);

/// <summary>
/// The tenant's trust store: the publishers whose signatures this tenant accepts. Only public keys are ever held. Registering and revoking are restricted to <c>packs.trust.manage</c> and audited; a revoked key is
/// kept, so earlier decisions stay explicable. Keys from host configuration (the platform operator's anchors) are shown read-only and cannot be shadowed by a tenant key of the same id.
/// </summary>
public interface IPackTrustKeys
{
    Task<IReadOnlyList<TrustKeyView>> ListAsync(CancellationToken ct = default);
    Task RegisterAsync(string keyId, string label, string publicKeyPem, DateTime? notBeforeUtc, DateTime? notAfterUtc, CancellationToken ct = default);
    Task RevokeAsync(string keyId, string? reason, CancellationToken ct = default);
}

internal sealed class PackTrustKeys(ConfigurationDbContext db, PlatformKeyResolver platform, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, Ecp.Identity.IAccessService access, CurrentUser currentUser,
    ActorContext actor, IClock clock, PackPackageCache cache) : IPackTrustKeys
{
    private string Who() => currentUser.UserId?.ToString() ?? actor.ActorId ?? "system";

    public async Task<IReadOnlyList<TrustKeyView>> ListAsync(CancellationToken ct = default)
    {
        await access.RequireAsync("packs.trust.manage", PackManager.TenantResource(tenant.RequireTenantId()), ct);
        var rows = await db.TrustedKeys.AsNoTracking().OrderBy(k => k.KeyId).ToListAsync(ct);
        var list = platform.All.OrderBy(k => k.KeyId).Select(k => new TrustKeyView(k.KeyId, k.Algorithm, PackKeys.Fingerprint(k.PublicKey), k.Label ?? "", k.Source, k.Status.ToString(), k.NotBeforeUtc, k.NotAfterUtc, null, null, null, null, null, false)).ToList();
        list.AddRange(rows.Select(r => new TrustKeyView(r.KeyId, r.Algorithm, r.Fingerprint, r.Label, "tenant trust store", r.Status, r.NotBeforeUtc, r.NotAfterUtc, r.RegisteredBy, r.RegisteredUtc, r.RevokedBy, r.RevokedUtc, r.RevokedReason, true)));
        return list;
    }

    public async Task RegisterAsync(string keyId, string label, string publicKeyPem, DateTime? notBeforeUtc, DateTime? notAfterUtc, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync("packs.trust.manage", PackManager.TenantResource(tenantId), ct);
        keyId = (keyId ?? "").Trim();
        if (!PackSignatureDocument.KeyIdPattern().IsMatch(keyId)) throw new DomainException("key_id_invalid", "The key id must be 2 to 80 letters, digits, '.', '_', ':' or '-', starting with a letter or digit.");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 120) throw new DomainException("key_label_invalid", "Give the key a label of up to 120 characters, such as the publisher's name.");
        if ((publicKeyPem ?? "").Contains("PRIVATE", StringComparison.OrdinalIgnoreCase)) throw new DomainException("key_is_private", "That looks like a private key. Paste the PUBLIC key only, and do not share the private key with anyone.");
        byte[] spki;
        try { spki = PackKeys.PublicKeyFromPem(publicKeyPem ?? ""); }
        catch (CryptographicException) { throw new DomainException("key_invalid", "That is not a valid ECDSA P-256 public key in PEM form (it should begin with -----BEGIN PUBLIC KEY-----)."); }
        if (notBeforeUtc is { } nb && notAfterUtc is { } na && na <= nb) throw new DomainException("key_dates_invalid", "The key's end date must be after its start date.");
        if (await platform.FindAsync(keyId, ct) is not null) throw new DomainException("key_id_reserved", "That key id is already used by a platform-configured key. Choose another id.");
        try
        {
            await uow.ExecuteAsync(async c =>
            {
                db.TrustedKeys.Add(TrustedPublisherKey.Create(tenantId, keyId, SignatureAlgorithms.Es256, spki, PackKeys.Fingerprint(spki), label.Trim(), notBeforeUtc, notAfterUtc, Who(), clock.UtcNow));
                await db.SaveChangesAsync(c);
                audit.Record(new AuditEntry("pack.trust.registered", "TrustedPublisherKey", keyId, NewValuesJson: JsonSerializer.Serialize(new { keyId, label = label.Trim(), fingerprint = PackKeys.Fingerprint(spki), notBeforeUtc, notAfterUtc })));
            }, ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); throw new DomainException("key_id_in_use", "A key with that id is already registered. Keys are revoked, never replaced; register the new key under a new id."); }
        cache.InvalidateTenant(tenantId);
    }

    public async Task RevokeAsync(string keyId, string? reason, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync("packs.trust.manage", PackManager.TenantResource(tenantId), ct);
        var row = await db.TrustedKeys.FirstOrDefaultAsync(k => k.KeyId == keyId, ct) ?? throw new ResourceNotFoundException();
        await uow.ExecuteAsync(async c =>
        {
            row.Revoke(Who(), reason, clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("pack.trust.revoked", "TrustedPublisherKey", keyId, Reason: reason, NewValuesJson: JsonSerializer.Serialize(new { keyId, fingerprint = row.Fingerprint })));
        }, ct);
        cache.InvalidateTenant(tenantId);
    }
}
