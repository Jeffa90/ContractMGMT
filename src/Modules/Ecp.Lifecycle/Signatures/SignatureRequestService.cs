using System.Text;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Lifecycle;

/// <summary>
/// External Execution: requests signatures from a provider, receives the provider's verified events and records the executed document. The provider's words are mapped to platform statuses by an explicit mapper;
/// a provider cannot move a contract's state, cannot make it Effective and cannot approve anything. Completed means only that an executed copy was stored through the normal document rules, which is what the
/// executed-copy guard looks for; register authorisation is a separate step by a separate person.
/// </summary>
internal sealed class SignatureRequestService(
    LifecycleDbContext db, LifecycleEngine engine, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IAccessService access, ISignatureProviderFactory providers,
    IDocumentService documentService, IDocumentQuery documentQuery, IOptions<LifecycleOptions> options, LifecycleEvents events, ActorContext actor) : ISignatureRequestService, ISignatureEventInbox
{
    private EcpMode Mode => options.Value.Mode;

    public IReadOnlyList<ProviderInfo> Providers() => providers.Describe(Mode);

    private static string OwnerPermission(SubjectView s) => s.IsVariation ? "variations.create" : "contracts.submit_approval";

    /// <summary>Text that came from outside is bounded and stripped of control characters before it is stored.</summary>
    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim()) if (!char.IsControl(ch)) sb.Append(ch);
        var text = sb.ToString();
        return text.Length <= max ? text : text[..max];
    }

    public async Task<SignatureRequestInfo?> CurrentAsync(string subjectKind, Guid subjectId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        var e = await db.SignatureRequests.AsNoTracking().FirstOrDefaultAsync(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent, ct);
        return e is null ? null : ToInfo(e);
    }

    private SignatureRequestInfo ToInfo(SignatureRequest e)
    {
        var p = providers.Describe(Mode).FirstOrDefault(x => x.Key == e.Provider);
        return new SignatureRequestInfo(e.Id, e.SubjectKind, e.SubjectId, e.Provider, p?.Label ?? e.Provider, e.ProviderRequestId, e.Status, e.ProviderStatus, e.ExecutedDocumentId, e.CompletedUtc, e.RequestedBy, e.RequestedUtc,
            e.StatusUpdatedUtc, e.LastProviderEventUtc, e.FailureCode, e.FailureMessage, e.Detail, p is null || p.Kind != SignatureProviderKind.Live);
    }

    public async Task<SignatureRequestInfo> RequestSignatureAsync(string subjectKind, Guid subjectId, string providerKey, string? clientRequestId = null, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var idempotencyKey = Clean(clientRequestId, 100);
        if (idempotencyKey is not null && await db.SignatureRequests.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct) is { } earlier)
        {
            // A repeat of a request the caller already made (same client request id) returns the first outcome and writes nothing new. The caller must still be allowed to make it.
            if (earlier.SubjectKind != subjectKind || earlier.SubjectId != subjectId) throw new DomainException("idempotency_conflict", "That request id was already used for a different request.");
            await access.RequireAsync(OwnerPermission(subject), await engine.DescribeAsync(subject, null, ct), ct);
            return ToInfo(earlier);
        }
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "sendSignature")
            ?? throw new DomainException("invalid_transition", $"This {subject.Noun} cannot be sent for signature while it is {resolved.Definition.DisplayOf(subject.Status)}.");
        var provider = providers.Resolve(providerKey, Mode);
        SignatureRequest? created = null;
        var result = await engine.ExecuteAsync(new TransitionCommand(subjectKind, subjectId, transition.Code, ExpectedStatus: subject.Status, ClientRequestId: clientRequestId, AllowManaged: true), async (scope, c) =>
        {
            var documents = (await documentQuery.ListForContractAsync(scope.Subject.ContractId, c)).Where(d => resolved.Definition.Settings.Documents.FinalTypes.Contains(d.DocumentTypeCode) && d.Downloadable).ToList();
            var title = scope.Subject.IsVariation ? scope.Subject.Variation!.Title : scope.Subject.Contract.Title;
            var reference = scope.Subject.IsVariation ? scope.Subject.Variation!.Reference : scope.Subject.Contract.AgreementReference;
            var key = idempotencyKey ?? Guid.NewGuid().ToString("N");
            var accepted = await provider.CreateRequestAsync(new SignatureRequestSpec(scope.Subject.ContractId, reference, title, documents.Select(d => d.Sha256 ?? "").ToList(), key, actor.CorrelationId), c);
            foreach (var prior in await db.SignatureRequests.Where(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent).ToListAsync(c)) prior.Supersede();
            created = SignatureRequest.Start(tenant.RequireTenantId(), scope.Subject.OrganisationId, subjectKind, subjectId, scope.Subject.ContractId, provider.Key, accepted?.ProviderRequestId,
                accepted?.Status ?? SignatureRequestStatus.Sent, accepted?.ProviderStatus, engine.ActorLabel, actor.CorrelationId, idempotencyKey, scope.NowUtc);
            db.SignatureRequests.Add(created);
            await db.SaveChangesAsync(c);
            events.Raise(LifecycleEventTypes.ExecutionChanged, scope.Subject, new { executionId = created.Id, provider = provider.Key, status = created.Status.ToString() });
            audit.Record(new AuditEntry("execution.started", nameof(SignatureRequest), created.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Classification: scope.Subject.Classification,
                NewValuesJson: AuditJson.Of(new { provider = provider.Key, providerRequestId = created.ProviderRequestId, status = created.Status.ToString() })));
        }, ct);
        if (created is not null) return ToInfo(created);
        // A repeat of a request the caller already made (same client request id) returns the first outcome and writes nothing.
        var current = await db.SignatureRequests.AsNoTracking().FirstOrDefaultAsync(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent, ct)
            ?? throw new DomainException("not_out_for_signature", result.Replayed ? "The earlier request was replaced." : "Nothing has been sent for signature.");
        return ToInfo(current);
    }

    public async Task<SignatureRequestInfo> RecordExecutedCopyAsync(RecordExecutedCopyRequest r, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(r.SubjectKind, r.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var descriptor = await engine.DescribeAsync(subject, null, ct);
        await access.RequireAsync(OwnerPermission(subject), descriptor, ct);
        var current = await db.SignatureRequests.FirstOrDefaultAsync(x => x.SubjectKind == r.SubjectKind && x.SubjectId == r.SubjectId && x.IsCurrent, ct);
        if (current is null || resolved.Definition.State(subject.Status)?.Role != "signature")
            throw new DomainException("not_out_for_signature", $"The executed copy is attached while the {subject.Noun} is out for signature.");
        var scale = await access.ClassificationScaleAsync(ct);
        var requested = scale.Canonical(r.Classification) ?? (string.IsNullOrWhiteSpace(r.Classification) ? null : throw new DomainException("invalid_classification", $"Classification '{r.Classification}' is not recognised."));
        var level = requested is not null && scale.Rank(requested) >= scale.Rank(subject.Classification) ? requested : subject.Classification;
        if (!(await access.CheckSilentAsync("contracts.read", descriptor with { Classification = level }, ct)).Allowed) throw new ForbiddenException("You cannot classify a document above your own access.");

        var title = (subject.IsVariation ? subject.Variation!.Reference : subject.Contract.AgreementReference) + " executed copy";
        var dto = await documentService.AddAsync(new AddDocumentRequest(subject.OrganisationId, subject.ContractId, resolved.Definition.Settings.Documents.ExecutedCopyType, title, r.FileName, r.MimeType, r.Content, level, IsExecutedCopy: true), ct);
        return ToInfo(await CompleteAsync(current.Id, subject, dto.DocumentId, "Executed copy uploaded", providerStatus: null, eventUtc: null, ct));
    }

    private async Task<SignatureRequest> CompleteAsync(Guid requestId, SubjectView subject, Guid documentId, string detail, string? providerStatus, DateTime? eventUtc, CancellationToken ct, Func<CancellationToken, Task>? alsoInTransaction = null)
    {
        SignatureRequest record = null!;
        await uow.ExecuteAsync(async c =>
        {
            record = await db.SignatureRequests.FirstAsync(x => x.Id == requestId, c);
            record.Complete(documentId, clock.UtcNow, providerStatus, eventUtc);
            if (subject.IsVariation) db.SubjectDocuments.Add(SubjectDocument.Link(tenant.RequireTenantId(), SubjectKinds.Variation, subject.Id, subject.ContractId, documentId, "Executed", engine.ActorLabel, clock.UtcNow));
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.ExecutionChanged, subject, new { executionId = requestId, status = "Completed" });
            audit.Record(new AuditEntry("execution.completed", nameof(SignatureRequest), requestId.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification, Reason: detail,
                NewValuesJson: AuditJson.Of(new { documentId, providerStatus, note = "An executed copy is not register authorisation" })));
            await db.SaveChangesAsync(c);
            if (alsoInTransaction is not null) await alsoInTransaction(c);
        }, ct);
        return record;
    }

    public async Task<SignatureRequestInfo> RefreshStatusAsync(string subjectKind, Guid subjectId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        await access.RequireAsync(OwnerPermission(subject), await engine.DescribeAsync(subject, null, ct), ct);
        var current = await db.SignatureRequests.AsNoTracking().FirstOrDefaultAsync(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent, ct)
            ?? throw new DomainException("not_out_for_signature", "Nothing has been sent for signature.");
        if (current.ProviderRequestId is null || current.Status.IsFinal()) return ToInfo(current);
        var report = await providers.Resolve(current.Provider, Mode).GetStatusAsync(current.ProviderRequestId, ct);
        if (report is not null)
        {
            // The same report is the same event: asking twice for an unchanged answer changes nothing.
            var status = Clean(report.ProviderStatus, 100) ?? "";
            await ReceiveAsync(new VerifiedProviderEvent(current.Provider, $"poll:{current.ProviderRequestId}:{status}", current.ProviderRequestId, status, report.OccurredUtc ?? clock.UtcNow), ct);
        }
        return ToInfo(await db.SignatureRequests.AsNoTracking().FirstAsync(x => x.Id == current.Id, ct));
    }

    public async Task<SignatureRequestInfo> CancelAsync(string subjectKind, Guid subjectId, string reason, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        await access.RequireAsync(OwnerPermission(subject), await engine.DescribeAsync(subject, null, ct), ct);
        var why = Guard.NotBlank(reason, "Reason", 500);
        var current = await db.SignatureRequests.FirstOrDefaultAsync(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent, ct)
            ?? throw new DomainException("not_out_for_signature", "Nothing has been sent for signature.");
        if (current.Status.IsFinal()) throw new DomainException("request_final", $"The signature request has already ended ({current.Status}).");
        var provider = providers.Resolve(current.Provider, Mode);
        if (current.ProviderRequestId is not null) await provider.CancelAsync(current.ProviderRequestId, why, ct);
        await uow.ExecuteAsync(async c =>
        {
            current.ApplyProviderEvent(SignatureRequestStatus.Voided, current.ProviderStatus ?? "cancelled by the platform", clock.UtcNow, "Cancelled: " + why, clock.UtcNow);
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.ExecutionChanged, subject, new { executionId = current.Id, status = "Voided" });
            audit.Record(new AuditEntry("execution.cancelled", nameof(SignatureRequest), current.Id.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification, Reason: why,
                NewValuesJson: AuditJson.Of(new { provider = provider.Key, providerRequestId = current.ProviderRequestId, status = "Voided" })));
            await db.SaveChangesAsync(c);
        }, ct);
        return ToInfo(current);
    }

    public async Task<SignatureRequestInfo> SimulateEventAsync(string subjectKind, Guid subjectId, SignatureRequestStatus status, CancellationToken ct = default)
    {
        if (Mode == EcpMode.Production) throw new DomainException("mock_in_production", "Simulated signing events are refused in Production.");
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        await access.RequireAsync(OwnerPermission(subject), await engine.DescribeAsync(subject, null, ct), ct);
        var current = await db.SignatureRequests.AsNoTracking().FirstOrDefaultAsync(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent, ct)
            ?? throw new DomainException("not_out_for_signature", "Nothing has been sent for signature.");
        if (current.Provider != "mock" || current.ProviderRequestId is null) throw new DomainException("not_mock", "Only the mock signature service can be driven by hand.");
        providers.Resolve("mock", Mode);
        // The same path a real provider's verified event takes: inbox, status mapper, document rules, audit.
        var result = await ReceiveAsync(new VerifiedProviderEvent("mock", "sim-" + Guid.NewGuid().ToString("N"), current.ProviderRequestId, MockStatusWords.For(status), clock.UtcNow), ct);
        if (result.Outcome is ProviderEventOutcome.Failed) throw new DomainException("event_failed", result.Detail ?? "The event could not be applied.");
        return ToInfo(await db.SignatureRequests.AsNoTracking().FirstAsync(x => x.Id == current.Id, ct));
    }

    // ---- the inbox ---------------------------------------------------------------------------------------------------------------------------------------------------------------------

    public async Task<ProviderEventResult> ReceiveAsync(VerifiedProviderEvent e, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var provider = Clean(e.ProviderKey, 40) ?? throw new DomainException("event_invalid", "The event names no provider.");
        var eventId = Clean(e.ProviderEventId, 200) ?? throw new DomainException("event_invalid", "The event has no identifier.");
        var requestRef = Clean(e.ProviderRequestId, 200);
        var nativeStatus = Clean(e.ProviderStatus, 100) ?? "";
        var occurred = e.OccurredUtc.Kind == DateTimeKind.Utc ? e.OccurredUtc : e.OccurredUtc.ToUniversalTime();

        // Each provider event id is processed once. A repeat that was applied, ignored or recorded as unmapped is acknowledged and changes nothing; one that failed or was interrupted is tried again.
        var row = await db.ProviderEvents.FirstOrDefaultAsync(x => x.Provider == provider && x.ProviderEventId == eventId, ct);
        if (row is not null && row.Outcome is not (ProviderEventOutcome.Failed or ProviderEventOutcome.Pending))
            return new ProviderEventResult(row.Outcome, true, row.SignatureRequestId, null, row.OutcomeDetail);
        if (row is null)
        {
            row = SignatureProviderEvent.Receive(tenantId, provider, eventId, requestRef, nativeStatus, occurred, actor.CorrelationId, clock.UtcNow);
            try { await uow.ExecuteAsync(async c => { db.ProviderEvents.Add(row); await db.SaveChangesAsync(c); }, ct); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
            {
                // Another delivery of the same event won the race: this one is a repeat.
                db.ChangeTracker.Clear();
                var winner = await db.ProviderEvents.AsNoTracking().FirstAsync(x => x.Provider == provider && x.ProviderEventId == eventId, ct);
                return new ProviderEventResult(winner.Outcome, true, winner.SignatureRequestId, null, winner.OutcomeDetail);
            }
        }

        var request = requestRef is null ? null : await db.SignatureRequests.Where(x => x.Provider == provider && x.ProviderRequestId == requestRef).OrderByDescending(x => x.RequestedUtc).FirstOrDefaultAsync(ct);
        if (request is null) return await ResolveAsync(row.Id, ProviderEventOutcome.UnknownRequest, "No request of this provider matches the event.", null, null, null, ct);

        var mapper = providers.StatusMapper(provider);
        var mapped = mapper?.Map(nativeStatus);
        if (mapped is null)
        {
            // Unknown words fail safely: the platform status is untouched, the word is kept on the request and in the inbox for an administrator.
            return await ResolveAsync(row.Id, ProviderEventOutcome.Unmapped, mapper is null ? "No status mapper is registered for this provider." : "The provider status is not mapped.", request, nativeStatus, occurred, ct);
        }
        var status = mapped.Value;
        if (!request.IsCurrent) return await ResolveAsync(row.Id, ProviderEventOutcome.Ignored, "The request has been replaced by a newer one.", request, null, null, ct);
        if (request.Status.IsFinal()) return await ResolveAsync(row.Id, ProviderEventOutcome.Ignored, "The request has already ended.", request, null, null, ct);
        if (status.Rank() < request.Status.Rank()) return await ResolveAsync(row.Id, ProviderEventOutcome.Ignored, "The event is older than what is already recorded.", request, null, null, ct);

        var subject = await engine.LoadSubjectAsync(request.SubjectKind, request.SubjectId, ct);

        if (status == SignatureRequestStatus.Completed) return await CompleteFromProviderAsync(row.Id, request, subject, provider, requestRef!, nativeStatus, occurred, ct);

        await uow.ExecuteAsync(async c =>
        {
            request.ApplyProviderEvent(status, nativeStatus, occurred, null, clock.UtcNow);
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.ExecutionChanged, subject, new { executionId = request.Id, status = status.ToString() });
            audit.Record(new AuditEntry("execution.status_changed", nameof(SignatureRequest), request.Id.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification,
                NewValuesJson: AuditJson.Of(new { status = status.ToString(), providerStatus = nativeStatus, provider, providerEventId = eventId })));
            await db.SaveChangesAsync(c);
            await MarkEventAsync(row.Id, ProviderEventOutcome.Applied, $"Status {status}.", request, c);
        }, ct);
        return new ProviderEventResult(ProviderEventOutcome.Applied, false, request.Id, status, null);
    }

    private async Task<ProviderEventResult> CompleteFromProviderAsync(Guid rowId, SignatureRequest request, SubjectView subject, string provider, string requestRef, string nativeStatus, DateTime occurred, CancellationToken ct)
    {
        async Task<ProviderEventResult> Failure(string code, string message)
        {
            await uow.ExecuteAsync(async c =>
            {
                var r = await db.SignatureRequests.FirstAsync(x => x.Id == request.Id, c);
                r.RecordFailure(code, message, clock.UtcNow);
                r.NoteProviderStatus(nativeStatus, occurred, clock.UtcNow);
                audit.Record(new AuditEntry("execution.provider_event_failed", nameof(SignatureRequest), r.Id.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification, Reason: message,
                    NewValuesJson: AuditJson.Of(new { code, provider, providerStatus = nativeStatus })));
                await db.SaveChangesAsync(c);
                await MarkEventAsync(rowId, ProviderEventOutcome.Failed, message, r, c);
            }, ct);
            return new ProviderEventResult(ProviderEventOutcome.Failed, false, request.Id, null, message);
        }

        var retriever = providers.Retriever(provider);
        if (retriever is null) return await Failure("executed_document_unsupported", "The provider reported completion but supplies no executed document. Upload the executed copy by hand.");
        ExecutedContent? content;
        try { content = await retriever.RetrieveAsync(requestRef, ct); }
        catch (DomainException ex) { return await Failure("executed_document_unavailable", ex.Message); }
        if (content is null) return await Failure("executed_document_unavailable", "The provider reported completion but returned no executed document.");

        var resolved = await engine.DefinitionForAsync(subject, ct);
        var title = (subject.IsVariation ? subject.Variation!.Reference : subject.Contract.AgreementReference) + " executed copy";
        DocumentVersionDto dto;
        try
        {
            await using var stream = new MemoryStream(content.Content);
            // The normal document rules apply: type, extension and size checks, malware scanning, hashing, classification and storage. A provider cannot bypass them.
            dto = await documentService.AddAsync(new AddDocumentRequest(subject.OrganisationId, subject.ContractId, resolved.Definition.Settings.Documents.ExecutedCopyType, title, content.FileName, content.MimeType, stream,
                subject.Classification, IsExecutedCopy: true), ct);
        }
        catch (DomainException ex) { return await Failure("executed_document_rejected", ex.Message); }

        await CompleteAsync(request.Id, subject, dto.DocumentId, "Completed by the signature provider", nativeStatus, occurred, ct,
            alsoInTransaction: c => MarkEventAsync(rowId, ProviderEventOutcome.Applied, "Status Completed; executed document stored.", request, c));
        return new ProviderEventResult(ProviderEventOutcome.Applied, false, request.Id, SignatureRequestStatus.Completed, null);
    }

    private async Task MarkEventAsync(Guid rowId, ProviderEventOutcome outcome, string? detail, SignatureRequest? request, CancellationToken c)
    {
        var row = await db.ProviderEvents.FirstAsync(x => x.Id == rowId, c);
        row.Resolve(outcome, detail, request?.Id, request?.OrganisationId, clock.UtcNow);
        await db.SaveChangesAsync(c);
    }

    private async Task<ProviderEventResult> ResolveAsync(Guid rowId, ProviderEventOutcome outcome, string detail, SignatureRequest? request, string? nativeStatusToKeep, DateTime? occurred, CancellationToken ct)
    {
        await uow.ExecuteAsync(async c =>
        {
            if (request is not null && nativeStatusToKeep is not null && !request.Status.IsFinal())
            {
                var r = await db.SignatureRequests.FirstAsync(x => x.Id == request.Id, c);
                r.NoteProviderStatus(nativeStatusToKeep, occurred ?? clock.UtcNow, clock.UtcNow);
            }
            if (outcome is ProviderEventOutcome.Unmapped or ProviderEventOutcome.UnknownRequest)
                audit.Record(new AuditEntry("execution.provider_event_unhandled", nameof(SignatureProviderEvent), rowId.ToString(), OrganisationId: request?.OrganisationId, Reason: detail,
                    NewValuesJson: AuditJson.Of(new { outcome = outcome.ToString() })));
            await db.SaveChangesAsync(c);
            await MarkEventAsync(rowId, outcome, detail, request, c);
        }, ct);
        return new ProviderEventResult(outcome, false, request?.Id, null, detail);
    }

    public async Task<IReadOnlyList<ProviderEventInfo>> ListNeedingAttentionAsync(int max = 100, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync("audit.read_admin", new ResourceDescriptor("SignatureProviderEvent", tenantId, IsTenantLevelDefinition: true, Dimensions: ScopeDimensions.None), ct);
        var rows = await db.ProviderEvents.AsNoTracking().Where(x => x.Outcome == ProviderEventOutcome.Unmapped || x.Outcome == ProviderEventOutcome.UnknownRequest || x.Outcome == ProviderEventOutcome.Failed || x.Outcome == ProviderEventOutcome.Pending)
            .OrderByDescending(x => x.ReceivedUtc).Take(Math.Clamp(max, 1, 500)).ToListAsync(ct);
        return rows.Select(x => new ProviderEventInfo(x.Id, x.Provider, x.ProviderEventId, x.ProviderRequestId, x.ProviderStatus, x.Outcome, x.OutcomeDetail, x.Attempts, x.OccurredUtc, x.ReceivedUtc, x.ProcessedUtc, x.SignatureRequestId)).ToList();
    }
}
