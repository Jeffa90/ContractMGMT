using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Documents;
using Ecp.Documents.Domain;
using Ecp.Identity;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

internal sealed record GuardContext(SubjectView Subject, ResolvedDefinition Resolved, TransitionDefinition Transition, TransitionCommand Command, Guid? ActorUserId, ApprovalRequest? Pending)
{
    public WorkflowDefinition Definition => Resolved.Definition;
}

/// <summary>
/// Evaluates the guards a transition names. The guard vocabulary is closed: each code means one fixed check implemented here, so a definition can combine guards but never invent a check.
/// A guard answers in plain words when it fails.
/// </summary>
internal sealed class GuardEvaluator(LifecycleDbContext db, IDocumentQuery documents, IContractLifecyclePort port, IDispositionChecks disposition, IContractCustomFieldWriter customFields)
{
    /// <summary>Guards whose answer depends on what the caller supplies with the request (a comment or reason). They do not make a step unavailable; the form asks for the input.</summary>
    private static readonly HashSet<string> InputGuards = new(StringComparer.Ordinal) { "CommentRequired", "ReasonRequired", "VariationOutcomeApplied", "VariationOutcomeNotApplied", "RenewalSuccessorEffective" };

    public async Task<List<string>> EvaluateAsync(GuardContext g, bool forAvailability, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var code in g.Transition.Guards.Distinct())
        {
            if (forAvailability && InputGuards.Contains(code)) continue;
            var why = await CheckAsync(code, g, ct);
            if (why is not null) failures.Add(why);
        }
        if (g.Transition.CommentRequired && string.IsNullOrWhiteSpace(g.Command.Comment) && !forAvailability) failures.Add("A comment is required.");
        if (g.Transition.ReasonRequired && string.IsNullOrWhiteSpace(g.Command.Reason ?? g.Command.Comment) && !forAvailability) failures.Add("A reason is required.");
        var missing = await MissingRequiredAsync(g, ct);
        if (missing.Count > 0 && !forAvailability) failures.Add("These are needed first: " + string.Join(", ", missing) + ".");
        return failures.Distinct().ToList();
    }

    private async Task<string?> CheckAsync(string code, GuardContext g, CancellationToken ct)
    {
        var s = g.Subject;
        switch (code)
        {
            case "LegalRequired": return s.LegalRequired ? null : $"Legal review is not required for this {s.Noun}.";
            case "LegalNotRequired": return s.LegalRequired ? $"This {s.Noun} requires Legal review before it can go ahead." : null;
            case "CommentRequired": return string.IsNullOrWhiteSpace(g.Command.Comment) ? "A comment is required." : null;
            case "ReasonRequired": return string.IsNullOrWhiteSpace(g.Command.Reason ?? g.Command.Comment) ? "A reason is required." : null;
            case "FinalDocumentExists":
                return await HasDocumentAsync(g, d => g.Definition.Settings.Documents.FinalTypes.Contains(d.DocumentTypeCode), ct)
                    ? null : $"A final document ({string.Join(" or ", g.Definition.Settings.Documents.FinalTypes)}) that has passed its scan must be attached first.";
            case "ExecutedCopyExists":
                return await HasDocumentAsync(g, d => d.IsExecutedCopy, ct) ? null : "The executed copy must be attached, and have passed its scan, first.";
            case "NoExecutedCopy":
                return await HasDocumentAsync(g, d => d.IsExecutedCopy, ct) ? "An executed copy has already been attached, so this can no longer be withdrawn." : null;
            case "AuthorisedApprover":
                if (g.Pending is null) return "There is no approval request waiting for a decision.";
                return g.ActorUserId is null ? "Only a signed-in approver can act." : null; // the approver group and relationship are decided by the access check on approvals.action
            case "SubmitterNotApprover":
                return g.Pending is { } p && g.ActorUserId == p.SubmittedByUserId ? "You submitted this request, so someone else must decide it." : null;
            case "ParentRemainsLocked":
                return s.Contract.RecordLocked ? null : "The agreement must be locked while a Variation is open.";
            case "NoOtherOpenVariation":
                return await db.Variations.AnyAsync(v => v.ContractId == s.ContractId && v.IsOpen && v.Id != s.Id, ct) ? "Another Variation is already open on this agreement. Finish or cancel it first." : null;
            case "NoOpenRenewal":
                return await db.Renewals.AnyAsync(r => r.PredecessorContractId == s.ContractId && r.Status == RenewalStatus.InProgress, ct) ? "A renewal is already in progress for this agreement." : null;
            case "RenewalSuccessorEffective":
                {
                    var renewal = await db.Renewals.AsNoTracking().FirstOrDefaultAsync(r => r.PredecessorContractId == s.ContractId && r.Status == RenewalStatus.InProgress, ct);
                    if (renewal is null) return "There is no renewal in progress.";
                    var successor = await port.FindAsync(renewal.SuccessorContractId, ct);
                    return successor?.RegisteredDateUtc is null ? "The successor agreement has not been registered yet." : null;
                }
            case "Authorised": return null; // the permission and scope were checked before any guard ran
            case "RetentionChecksPass":
            case "NoLegalHold":
                return await disposition.BlockReasonAsync(s.ContractId, ct);
            case "ControlledFieldChanged":
                {
                    var review = await db.LegalReviews.AsNoTracking().FirstOrDefaultAsync(r => r.SubjectKind == s.Kind && r.SubjectId == s.Id, ct);
                    return review?.ClearedContractStampUtc is { } stamp && s.Contract.UpdatedUtc > stamp ? null : "Nothing has changed since Legal cleared it, so it does not need to go back to Legal.";
                }
            case "VariationOutcomeApplied":
                return g.Command.Inputs?.VariationOutcome == VariationOutcome.Applied ? null : "The Variation has not been approved and applied.";
            case "VariationOutcomeNotApplied":
                return g.Command.Inputs?.VariationOutcome is VariationOutcome.Rejected or VariationOutcome.Cancelled ? null : "The Variation has not been rejected or cancelled.";
            case "SignatureVoidedOrDeclined":
                {
                    var exec = await db.Executions.AsNoTracking().FirstOrDefaultAsync(x => x.SubjectKind == s.Kind && x.SubjectId == s.Id && x.IsCurrent, ct);
                    return exec is { Status: ExecutionStatus.Declined or ExecutionStatus.Voided } ? null : "Signing has not been declined or voided, so the document cannot be taken back.";
                }
            case "RequiredFieldsPresent":
            case "RequiredDocumentsPresent":
            case "ClassificationClearance":
                return null; // required fields and documents are reported together below; classification is part of the access check
            default:
                return $"The guard '{code}' is not recognised.";
        }
    }

    /// <summary>Fields and document types the transition itself requires (data in the definition), reported together.</summary>
    private async Task<List<string>> MissingRequiredAsync(GuardContext g, CancellationToken ct)
    {
        var missing = new List<string>();
        var t = g.Transition;
        var c = g.Subject.Contract;
        foreach (var field in t.RequiredFields)
        {
            var present = field switch
            {
                "title" => !string.IsNullOrWhiteSpace(c.Title),
                "description" => !string.IsNullOrWhiteSpace(c.Description),
                "supplier" => c.SupplierId != Guid.Empty,
                "category" => c.CategoryId != Guid.Empty,
                "currencyCode" => !string.IsNullOrWhiteSpace(c.CurrencyCode),
                "estimatedValue" => c.EstimatedValue.HasValue,
                "startDate" => c.StartDate.HasValue,
                "effectiveDate" => c.EffectiveDate.HasValue,
                "reviewDate" => c.ReviewDate.HasValue,
                "endDate" => c.EndDate.HasValue,
                "extensionType" => !string.IsNullOrWhiteSpace(c.ExtensionType),
                "noticePeriodDays" => c.NoticePeriodDays.HasValue,
                "noticePeriodValue" => c.NoticePeriodValue.HasValue,
                "contractType" => !string.IsNullOrWhiteSpace(c.ContractType),
                "agreementType" => !string.IsNullOrWhiteSpace(c.AgreementType),
                "riskLevel" => !string.IsNullOrWhiteSpace(c.RiskLevel),
                "functionCode" => !string.IsNullOrWhiteSpace(c.FunctionCode),
                "legalEntity" => c.LegalEntityId.HasValue,
                "sapOutlineAgreementNumber" => !string.IsNullOrWhiteSpace(c.SapOutlineAgreementNumber),
                _ when field.StartsWith("cf.", StringComparison.Ordinal) => (await customFields.CurrentAsync(c.Id, [field[3..]], ct)).GetValueOrDefault(field[3..]) is { Length: > 0 },
                _ => true
            };
            if (!present) missing.Add(field);
        }
        if (t.RequiredDocumentTypes.Count > 0)
        {
            var docs = await ActiveDocumentsAsync(g, ct);
            foreach (var type in t.RequiredDocumentTypes)
                if (!docs.Any(d => d.DocumentTypeCode == type)) missing.Add($"a {type} document");
        }
        return missing;
    }

    private async Task<bool> HasDocumentAsync(GuardContext g, Func<DocumentSummary, bool> predicate, CancellationToken ct)
        => (await ActiveDocumentsAsync(g, ct)).Any(predicate);

    /// <summary>Documents that count: active, downloadable (so scanned), and for a Variation only those linked to it.</summary>
    private async Task<List<DocumentSummary>> ActiveDocumentsAsync(GuardContext g, CancellationToken ct)
    {
        var all = (await documents.ListForContractAsync(g.Subject.ContractId, ct)).Where(d => d.Status == DocumentStatus.Active && d.Downloadable).ToList();
        if (!g.Subject.IsVariation) return all;
        var linked = await db.SubjectDocuments.AsNoTracking().Where(l => l.SubjectKind == SubjectKinds.Variation && l.SubjectId == g.Subject.Id).Select(l => l.DocumentId).ToListAsync(ct);
        return all.Where(d => linked.Contains(d.DocumentId)).ToList();
    }
}
