using Ecp.DataMigration.Domain;

namespace Ecp.DataMigration.Planning;

public enum DocAction
{
    /// <summary>Create a document with a first version.</summary>
    ImportNew,
    /// <summary>Add a version to a document that already exists in the target.</summary>
    AddVersionToExisting,
    /// <summary>Add a version to a document an earlier file of this batch creates.</summary>
    AddVersionToBatchDocument,
    /// <summary>The content is already attached to the same contract: no new document, the existing one is linked in the provenance.</summary>
    LinkExisting,
    LinkBatchDocument,
    /// <summary>Same document source id and same content as a document already imported: nothing to do (idempotent re-run).</summary>
    AlreadyImported,
    RejectDuplicate,
    Hold
}

public sealed record DocFile(Guid FileId, string Path, string Sha256, Guid ContractRowId, Guid? ContractId, string DocumentSourceId, bool Released);
public sealed record ExistingDoc(Guid DocumentId, Guid? ContractId, string? DocumentSourceId, string? Sha256);
public sealed record DocDecision(Guid FileId, DocAction Action, Guid? ExistingDocumentId, Guid? OriginalFileId, string? MessageCode, string? Message, bool IsError);

/// <summary>
/// Decides what happens to each matched file: import, add a version, link existing content, reject, or hold for a person (ATTACHMENT_INGESTION.md "Version and duplicate options").
/// The same function runs at validation (to show the result) and at commit (to act on it), in a fixed order, so the two always agree.
/// </summary>
public static class DocumentPlanner
{
    public static IReadOnlyList<DocDecision> Plan(IReadOnlyList<DocFile> files, DocumentDuplicateOption option, IReadOnlyList<ExistingDoc> existingBySource, IReadOnlyList<ExistingDoc> existingByHash)
    {
        var bySource = existingBySource.Where(d => d.DocumentSourceId is not null).GroupBy(d => d.DocumentSourceId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byHash = existingByHash.Where(d => d.Sha256 is not null).GroupBy(d => d.Sha256!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(d => d.DocumentId).First(), StringComparer.Ordinal);
        var firstInBatch = new Dictionary<string, DocFile>(StringComparer.Ordinal);
        var decisions = new List<DocDecision>();

        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (bySource.TryGetValue(file.DocumentSourceId, out var known))
            {
                if (string.Equals(known.Sha256, file.Sha256, StringComparison.Ordinal))
                    decisions.Add(new(file.FileId, DocAction.AlreadyImported, known.DocumentId, null, null, "The document was imported before with the same content; nothing is added.", false));
                else
                    decisions.Add(new(file.FileId, DocAction.AddVersionToExisting, known.DocumentId, null, null, "The document was imported before with different content; the new content is added as a new version.", false));
                firstInBatch.TryAdd(file.Sha256, file);
                continue;
            }

            DocFile? batchOriginal = null; ExistingDoc? targetOriginal = null;
            if (!file.Released)
            {
                if (firstInBatch.TryGetValue(file.Sha256, out var f)) batchOriginal = f;
                else if (byHash.TryGetValue(file.Sha256, out var d)) targetOriginal = d;
            }
            if (batchOriginal is null && targetOriginal is null)
            {
                decisions.Add(new(file.FileId, DocAction.ImportNew, null, null, file.Released ? "MIG-F007" : null, file.Released ? "Released after review: imported as a separate document." : null, false));
                firstInBatch.TryAdd(file.Sha256, file);
                continue;
            }

            var originalName = batchOriginal?.Path ?? "a document already in the target";
            var sameContract = batchOriginal is not null ? batchOriginal.ContractRowId == file.ContractRowId : targetOriginal!.ContractId is { } c && c == file.ContractId;
            var note = $"The content is identical to {originalName} (same SHA-256).";
            switch (option)
            {
                case DocumentDuplicateOption.RejectDuplicateHash:
                    decisions.Add(new(file.FileId, DocAction.RejectDuplicate, targetOriginal?.DocumentId, batchOriginal?.FileId, "MIG-F007", note + " The duplicate option is Reject, so it is not imported.", true));
                    break;
                case DocumentDuplicateOption.LinkExisting:
                    if (sameContract)
                        decisions.Add(new(file.FileId, batchOriginal is not null ? DocAction.LinkBatchDocument : DocAction.LinkExisting, targetOriginal?.DocumentId, batchOriginal?.FileId, "MIG-F007",
                            note + " It belongs to the same contract, so the existing content is linked instead of added again.", false));
                    else
                        decisions.Add(new(file.FileId, DocAction.ImportNew, null, batchOriginal?.FileId, "MIG-F007", note + " It belongs to a different contract, so it is imported as its own document (the stored content is shared).", false));
                    break;
                case DocumentDuplicateOption.AddVersion:
                    if (sameContract)
                        decisions.Add(new(file.FileId, batchOriginal is not null ? DocAction.AddVersionToBatchDocument : DocAction.AddVersionToExisting, targetOriginal?.DocumentId, batchOriginal?.FileId, "MIG-F007",
                            note + " It is added as a new version of that document.", false));
                    else
                        decisions.Add(new(file.FileId, DocAction.ImportNew, null, batchOriginal?.FileId, "MIG-F007", note + " It belongs to a different contract, so it is imported as its own document.", false));
                    break;
                case DocumentDuplicateOption.KeepSeparateWithWarning:
                    decisions.Add(new(file.FileId, DocAction.ImportNew, null, batchOriginal?.FileId, "MIG-F007", note + " It is kept as a separate document.", false));
                    break;
                default:
                    decisions.Add(new(file.FileId, DocAction.Hold, targetOriginal?.DocumentId, batchOriginal?.FileId, "MIG-F007", note + " It is held in the queue for a person to decide.", true));
                    break;
            }
        }
        return decisions;
    }
}
