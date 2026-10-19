using Ecp.DataMigration.Domain;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

public sealed record MaskedDeclarationInput(Guid OrganisationId, string DatasetOwner, string SourceSystem, string MaskingMethod, string FieldsMasked, bool AttachmentsIncluded, bool PersonalDataIncluded,
    bool ConfidentialDataIncluded, string ApprovalReference, string ApprovedBy, DateTime ApprovedUtc, DateTime ReviewByUtc, string? PermittedUsers);

public sealed record MaskedDeclarationDto(Guid Id, Guid OrganisationId, string DatasetOwner, string SourceSystem, string MaskingMethod, string FieldsMasked, bool AttachmentsIncluded, bool PersonalDataIncluded,
    bool ConfidentialDataIncluded, string ApprovalReference, string ApprovedBy, DateTime ApprovedUtc, DateTime ReviewByUtc, string PermittedEnvironment, string PermittedUsers, string DeclaredBy, DateTime DeclaredUtc,
    DateTime? RevokedUtc, string? RevokedReason, bool Current, int BatchCount);

public sealed record BatchClassificationDto(Guid BatchId, string DataClass, string DataClassLabel, MaskedDeclarationDto? Declaration, string LinkedBy, DateTime LinkedUtc);

/// <summary>The data-handling checkpoint (G-35, D14.12). The customer masks their data; this records the declaration, ties it to the batch and refuses to commit a batch of masked company data in Test without a current declaration that permits it.</summary>
public interface IMaskedDataDeclarations
{
    Task<MaskedDeclarationDto> DeclareAsync(MaskedDeclarationInput input, CancellationToken ct = default);
    Task<IReadOnlyList<MaskedDeclarationDto>> ListAsync(Guid organisationId, CancellationToken ct = default);
    Task RevokeAsync(Guid declarationId, string reason, CancellationToken ct = default);
    Task<BatchClassificationDto?> ClassificationAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>Marks a batch as Synthetic, or as Masked Company Data covered by <paramref name="declarationId"/>. Allowed until the batch starts committing.</summary>
    Task ClassifyBatchAsync(Guid batchId, string dataClass, Guid? declarationId, CancellationToken ct = default);
    /// <summary>Throws, with a stable code and a plain sentence, when the batch may not be committed in this environment for want of a declaration.</summary>
    Task EnsureCommitAllowedAsync(Guid batchId, CancellationToken ct = default);
}

internal sealed class MaskedDataDeclarationService(MigrationKernel k, IMalwareScanner scanner, IUserService users, IActorContext actor, IMockScannerAcceptance? mockAcceptance = null) : IMaskedDataDeclarations
{
    private static MaskedDeclarationDto Map(MaskedDataDeclaration d, DateTime now, int batches) => new(d.Id, d.OrganisationId, d.DatasetOwner, d.SourceSystem, d.MaskingMethod, d.FieldsMasked, d.AttachmentsIncluded,
        d.PersonalDataIncluded, d.ConfidentialDataIncluded, d.ApprovalReference, d.ApprovedBy, d.ApprovedUtc, d.ReviewByUtc, d.PermittedEnvironment, d.PermittedUsers, d.DeclaredBy, d.DeclaredUtc, d.RevokedUtc, d.RevokedReason,
        d.IsCurrentAt(now), batches);

    private Task RequireOrgAsync(string permission, Guid organisationId, CancellationToken ct)
        => k.Access.RequireAsync(permission, MigrationKernel.Describe(k.TenantId, organisationId, "masked-data"), ct);

    public async Task<MaskedDeclarationDto> DeclareAsync(MaskedDeclarationInput i, CancellationToken ct = default)
    {
        if (k.Options.Mode == EcpMode.Production) throw new DomainException("MDD-010", "Masked-data declarations are for Test environments. Production data follows Production governance and is not declared this way.");
        await RequireOrgAsync(MigrationPermissions.Stage, i.OrganisationId, ct);
        var label = await k.ActorLabelAsync(ct);
        var row = MaskedDataDeclaration.Create(k.TenantId, i.OrganisationId, i.DatasetOwner, i.SourceSystem, i.MaskingMethod, i.FieldsMasked, i.AttachmentsIncluded, i.PersonalDataIncluded, i.ConfidentialDataIncluded,
            i.ApprovalReference, i.ApprovedBy, i.ApprovedUtc, i.ReviewByUtc, NormaliseUsers(i.PermittedUsers), label, k.UserId, k.Clock.UtcNow, actor.CorrelationId);
        await k.Uow.ExecuteAsync(async c =>
        {
            k.Db.MaskedDeclarations.Add(row);
            await k.Db.SaveChangesAsync(c);
            // The audit entry names the declaration and its facts. It holds no data and no field values: only that a declaration was made, by whom, under which approval reference.
            k.Record("migration.masked.declared", nameof(MaskedDataDeclaration), row.Id.ToString(), row.OrganisationId, null,
                new { row.DatasetOwner, row.SourceSystem, row.ApprovalReference, row.ApprovedBy, row.ReviewByUtc, row.AttachmentsIncluded, row.PersonalDataIncluded, row.ConfidentialDataIncluded, permittedUsers = row.PermittedUserList.Count, row.CorrelationId });
        }, ct);
        return Map(row, k.Clock.UtcNow, 0);
    }

    private static string NormaliseUsers(string? list) => string.Join(", ", (list ?? "").Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).Distinct());

    public async Task<IReadOnlyList<MaskedDeclarationDto>> ListAsync(Guid organisationId, CancellationToken ct = default)
    {
        await RequireOrgAsync(MigrationPermissions.Stage, organisationId, ct);
        var now = k.Clock.UtcNow;
        var rows = await k.Db.MaskedDeclarations.AsNoTracking().Where(d => d.OrganisationId == organisationId).OrderByDescending(d => d.DeclaredUtc).ToListAsync(ct);
        var counts = await k.Db.BatchClassifications.AsNoTracking().Where(b => b.DeclarationId != null).GroupBy(b => b.DeclarationId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return [.. rows.Select(d => Map(d, now, counts.GetValueOrDefault(d.Id)))];
    }

    public async Task RevokeAsync(Guid declarationId, string reason, CancellationToken ct = default)
    {
        var row = await k.Db.MaskedDeclarations.AsTracking().FirstOrDefaultAsync(d => d.Id == declarationId, ct) ?? throw new ResourceNotFoundException();
        await RequireOrgAsync(MigrationPermissions.Stage, row.OrganisationId, ct);
        if (row.RevokedUtc is not null) return;
        row.Revoke(reason, k.Clock.UtcNow);
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.masked.revoked", nameof(MaskedDataDeclaration), row.Id.ToString(), row.OrganisationId, null, new { row.ApprovalReference }, reason);
        }, ct);
    }

    public async Task<BatchClassificationDto?> ClassificationAsync(Guid batchId, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var link = await k.Db.BatchClassifications.AsNoTracking().FirstOrDefaultAsync(b => b.BatchId == batch.Id, ct);
        if (link is null) return null;
        MaskedDeclarationDto? declaration = null;
        if (link.DeclarationId is { } id && await k.Db.MaskedDeclarations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) is { } d) declaration = Map(d, k.Clock.UtcNow, 1);
        return new BatchClassificationDto(batch.Id, link.DataClass, BatchDataClasses.Label(link.DataClass), declaration, link.LinkedBy, link.LinkedUtc);
    }

    public async Task ClassifyBatchAsync(Guid batchId, string dataClass, Guid? declarationId, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        if (batch.Status is BatchStatus.Committing or BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Accepted)
            throw new DomainException("MDD-011", "A batch that has started committing cannot be re-classified.");
        if (dataClass == BatchDataClasses.MaskedCompanyData)
        {
            if (k.Options.Mode == EcpMode.Production) throw new DomainException("MDD-010", "Masked company data is a Test-environment classification. Production data follows Production governance.");
            var decl = declarationId is { } id ? await k.Db.MaskedDeclarations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) : null;
            if (decl is null || decl.OrganisationId != batch.OrganisationId) throw new DomainException("MDD-001", "Choose a masked-data declaration made for this organisation.");
            if (!decl.IsCurrentAt(k.Clock.UtcNow)) throw new DomainException("MDD-002", "That declaration has expired or been revoked. Make a new declaration.");
        }
        var label = await k.ActorLabelAsync(ct);
        await k.Uow.ExecuteAsync(async c =>
        {
            var existing = await k.Db.BatchClassifications.FirstOrDefaultAsync(b => b.BatchId == batchId, c);
            if (existing is not null) k.Db.BatchClassifications.Remove(existing);
            k.Db.BatchClassifications.Add(BatchDataClassification.Create(batch.TenantId, batch.OrganisationId, batchId, dataClass, dataClass == BatchDataClasses.MaskedCompanyData ? declarationId : null, label, k.Clock.UtcNow));
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.batch.classified", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId, new { dataClass, declarationId });
        }, ct);
    }

    public async Task EnsureCommitAllowedAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await k.Db.Batches.AsNoTracking().FirstAsync(b => b.Id == batchId, ct);
        var link = await k.Db.BatchClassifications.AsNoTracking().FirstOrDefaultAsync(b => b.BatchId == batchId, ct);
        var mode = k.Options.Mode;
        if (mode == EcpMode.Demo) return;                                  // Demo carries synthetic data only and has no checkpoint
        if (mode == EcpMode.Production)
        {
            if (link is { DataClass: BatchDataClasses.MaskedCompanyData }) await Refuse(batch, "MDD-010", "This batch is marked as masked company data, which is a Test-environment classification. Production data follows Production governance; remove the classification.", ct);
            return;
        }
        // Test
        if (link is null)
        {
            if (k.Options.RequireDataClassification) await Refuse(batch, "MDD-003", "Say what this batch contains before it is committed: Synthetic, or Masked Company Data with a masked-data declaration.", ct);
            return;
        }
        if (link.DataClass == BatchDataClasses.Synthetic) return;

        var d = link.DeclarationId is { } id ? await k.Db.MaskedDeclarations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        if (d is null) { await Refuse(batch, "MDD-001", "This batch is marked as masked company data but its declaration cannot be found.", ct); return; }
        if (d.RevokedUtc is not null) await Refuse(batch, "MDD-002", "The masked-data declaration for this batch has been revoked.", ct);
        if (d.ReviewByUtc <= k.Clock.UtcNow) await Refuse(batch, "MDD-002", $"The masked-data declaration for this batch expired on {d.ReviewByUtc:yyyy-MM-dd}. Make a new declaration.", ct);
        if (!string.Equals(d.PermittedEnvironment, "Test", StringComparison.OrdinalIgnoreCase)) await Refuse(batch, "MDD-004", "The declaration does not permit this environment.", ct);
        var permitted = d.PermittedUserList;
        if (permitted.Count > 0)
        {
            var me = k.UserId is { } uid ? (await users.GetAsync(uid, ct))?.Email : null;
            if (me is null || !permitted.Contains(me, StringComparer.OrdinalIgnoreCase)) await Refuse(batch, "MDD-005", "The declaration limits who may load this data, and you are not on its list.", ct);
        }
        // A mock scanner checks nothing real, so masked company data may not be loaded behind it unless a named person accepted that for this rehearsal.
        if (scanner.IsDemonstrationOnly && (mockAcceptance is null || await mockAcceptance.CurrentAsync(ct) is null))
            await Refuse(batch, "MDD-006", "The only malware scanner connected is the demonstration scanner. Record an acceptance of the mock scanner for this rehearsal (Administration, Operations, Scanner), or connect a real scanner.", ct);
    }

    /// <summary>Records the refusal on the audit trail, then stops the commit with the code and sentence.</summary>
    private async Task Refuse(MigrationBatch batch, string code, string message, CancellationToken ct)
    {
        await k.Uow.ExecuteAsync(c => { k.Record("migration.masked.commit_refused", nameof(MigrationBatch), batch.Id.ToString(), batch.OrganisationId, batch.Id, new { code }); return Task.CompletedTask; }, ct);
        throw new DomainException(code, $"{code}: {message}");
    }
}
