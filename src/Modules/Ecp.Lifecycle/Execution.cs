using System.Text;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Lifecycle;

public sealed record EnvelopeRequest(Guid ContractId, string Reference, string Title, IReadOnlyList<string> DocumentHashes);
public sealed record EnvelopeResult(string EnvelopeId, ExecutionStatus Status);
public sealed record ExecutedContent(byte[] Content, string FileName, string MimeType);

/// <summary>
/// The boundary with an external signature service. The service owns the Delegation of Authority and the collection of signatures; the platform stores the envelope id and the status and receives
/// the executed copy. A provider never approves, registers or locks anything: a completed envelope only lets the executed-copy check pass.
/// </summary>
public interface IESignatureProvider
{
    string Key { get; }
    string Label { get; }
    /// <summary>True when the provider sends nothing to a real service (the manual provider and the mock).</summary>
    bool IsSimulated { get; }
    Task<EnvelopeResult?> CreateEnvelopeAsync(EnvelopeRequest request, CancellationToken ct = default);
    Task<ExecutionStatus?> GetStatusAsync(string envelopeId, CancellationToken ct = default);
    Task<ExecutedContent?> FetchExecutedAsync(string envelopeId, CancellationToken ct = default);
    Task VoidAsync(string envelopeId, string reason, CancellationToken ct = default);
}

public sealed record ProviderInfo(string Key, string Label, string Status, bool Available, string Note);

public interface IESignatureProviderFactory
{
    IESignatureProvider Resolve(string key, EcpMode mode);
    IReadOnlyList<ProviderInfo> Describe(EcpMode mode);
}

/// <summary>Manual execution: signing happens outside the platform and the owner uploads the executed copy. Implemented.</summary>
internal sealed class ManualSignatureProvider : IESignatureProvider
{
    public string Key => "manual";
    public string Label => "Manual (upload the executed copy)";
    public bool IsSimulated => true;
    public Task<EnvelopeResult?> CreateEnvelopeAsync(EnvelopeRequest request, CancellationToken ct = default) => Task.FromResult<EnvelopeResult?>(null);
    public Task<ExecutionStatus?> GetStatusAsync(string envelopeId, CancellationToken ct = default) => Task.FromResult<ExecutionStatus?>(null);
    public Task<ExecutedContent?> FetchExecutedAsync(string envelopeId, CancellationToken ct = default) => Task.FromResult<ExecutedContent?>(null);
    public Task VoidAsync(string envelopeId, string reason, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Mock provider for Demo and Test: simulates sending, delivery, completion (with a synthetic executed copy), decline and void. Delivers nothing anywhere. Refused in Production.</summary>
internal sealed class MockSignatureProvider : IESignatureProvider
{
    public string Key => "mock";
    public string Label => "Mock signature service (Demo and Test only)";
    public bool IsSimulated => true;
    public Task<EnvelopeResult?> CreateEnvelopeAsync(EnvelopeRequest request, CancellationToken ct = default)
        => Task.FromResult<EnvelopeResult?>(new EnvelopeResult("MOCK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), ExecutionStatus.Sent));
    public Task<ExecutionStatus?> GetStatusAsync(string envelopeId, CancellationToken ct = default) => Task.FromResult<ExecutionStatus?>(null);
    public Task<ExecutedContent?> FetchExecutedAsync(string envelopeId, CancellationToken ct = default)
    {
        // A minimal synthetic document; it stands in for the signed copy and is labelled as such.
        var text = $"%PDF-1.4\n% SYNTHETIC EXECUTED COPY (mock signature service) envelope {envelopeId}\n%%EOF\n";
        return Task.FromResult<ExecutedContent?>(new ExecutedContent(Encoding.ASCII.GetBytes(text), $"executed-{envelopeId}.pdf", "application/pdf"));
    }
    public Task VoidAsync(string envelopeId, string reason, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Adapter for an external signature service. Scaffolded, Not Verified: there is no live integration and no credentials in this release, so every call fails closed with a clear message.
/// Callback authenticity (a signature over the raw body, timestamp freshness, replay protection) belongs here when the adapter is built.
/// </summary>
internal sealed class ExternalEnvelopeProvider : IESignatureProvider
{
    public string Key => "external";
    public string Label => "External signature service (scaffold; not connected)";
    public bool IsSimulated => false;
    private static DomainException NotConnected() => new("provider_not_configured", "The external signature service is not connected in this release. Use manual execution and upload the executed copy.");
    public Task<EnvelopeResult?> CreateEnvelopeAsync(EnvelopeRequest request, CancellationToken ct = default) => throw NotConnected();
    public Task<ExecutionStatus?> GetStatusAsync(string envelopeId, CancellationToken ct = default) => throw NotConnected();
    public Task<ExecutedContent?> FetchExecutedAsync(string envelopeId, CancellationToken ct = default) => throw NotConnected();
    public Task VoidAsync(string envelopeId, string reason, CancellationToken ct = default) => throw NotConnected();
}

internal sealed class ESignatureProviderFactory : IESignatureProviderFactory
{
    private readonly ManualSignatureProvider _manual = new();
    private readonly MockSignatureProvider _mock = new();
    private readonly ExternalEnvelopeProvider _external = new();

    public IESignatureProvider Resolve(string key, EcpMode mode) => key switch
    {
        "manual" => _manual,
        "mock" when mode != EcpMode.Production => _mock,
        "mock" => throw new DomainException("mock_in_production", "The mock signature service is refused in Production."),
        "external" => _external,
        _ => throw new DomainException("unknown_provider", "That signature provider is not recognised.")
    };

    public IReadOnlyList<ProviderInfo> Describe(EcpMode mode) =>
    [
        new("manual", _manual.Label, "Implemented", true, "Signing happens outside the platform. Upload the executed copy when it is ready."),
        new("mock", _mock.Label, mode == EcpMode.Production ? "Disabled in Production" : "Mocked", mode != EcpMode.Production, "Simulates sending, completion, decline and void. Sends nothing."),
        new("external", _external.Label, "Scaffolded, Not Verified", false, "No live credentials or connection in this release.")
    ];
}

public sealed record ExecutionInfo(Guid Id, string SubjectKind, Guid SubjectId, string Provider, string ProviderLabel, string? EnvelopeId, ExecutionStatus Status, Guid? ExecutedDocumentId, DateTime? ExecutedUtc,
    string StartedBy, DateTime StartedUtc, DateTime StatusUpdatedUtc, string? Detail, bool IsSimulated);

public sealed record RecordExecutedCopyRequest(string SubjectKind, Guid SubjectId, string FileName, string MimeType, Stream Content, string? Classification = null);

public interface IExecutionService
{
    IReadOnlyList<ProviderInfo> Providers();
    Task<ExecutionInfo?> CurrentAsync(string subjectKind, Guid subjectId, CancellationToken ct = default);
    /// <summary>Sends the final document for signature (or records that signing is handled outside the platform) and moves the contract to the signature stage.</summary>
    Task<ExecutionInfo> SendForSignatureAsync(string subjectKind, Guid subjectId, string providerKey, string? clientRequestId = null, CancellationToken ct = default);
    /// <summary>Manual execution: attaches the executed copy. It is scanned like any upload and satisfies the executed-copy check only when it is clean.</summary>
    Task<ExecutionInfo> RecordExecutedCopyAsync(RecordExecutedCopyRequest request, CancellationToken ct = default);
    /// <summary>Mock provider only: reports a signing event (delivered, completed, declined, voided) as the service would.</summary>
    Task<ExecutionInfo> SimulateEventAsync(string subjectKind, Guid subjectId, ExecutionStatus status, CancellationToken ct = default);
}

internal sealed class ExecutionService(
    LifecycleDbContext db, LifecycleEngine engine, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IAccessService access, IESignatureProviderFactory providers,
    IDocumentService documentService, IDocumentQuery documentQuery, IOptions<LifecycleOptions> options, LifecycleEvents events) : IExecutionService
{
    private EcpMode Mode => options.Value.Mode;

    public IReadOnlyList<ProviderInfo> Providers() => providers.Describe(Mode);

    private string OwnerPermission(SubjectView s) => s.IsVariation ? "variations.create" : "contracts.submit_approval";

    public async Task<ExecutionInfo?> CurrentAsync(string subjectKind, Guid subjectId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        var e = await db.Executions.AsNoTracking().FirstOrDefaultAsync(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent, ct);
        return e is null ? null : ToInfo(e);
    }

    private ExecutionInfo ToInfo(ExecutionRecord e)
    {
        var p = providers.Describe(Mode).FirstOrDefault(x => x.Key == e.Provider);
        return new ExecutionInfo(e.Id, e.SubjectKind, e.SubjectId, e.Provider, p?.Label ?? e.Provider, e.EnvelopeId, e.Status, e.ExecutedDocumentId, e.ExecutedUtc, e.StartedBy, e.StartedUtc, e.StatusUpdatedUtc, e.Detail,
            e.Provider is "manual" or "mock");
    }

    public async Task<ExecutionInfo> SendForSignatureAsync(string subjectKind, Guid subjectId, string providerKey, string? clientRequestId = null, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "sendSignature")
            ?? throw new DomainException("invalid_transition", $"This {subject.Noun} cannot be sent for signature while it is {resolved.Definition.DisplayOf(subject.Status)}.");
        var provider = providers.Resolve(providerKey, Mode);
        ExecutionRecord created = null!;
        await engine.ExecuteAsync(new TransitionCommand(subjectKind, subjectId, transition.Code, ExpectedStatus: subject.Status, ClientRequestId: clientRequestId, AllowManaged: true), async (scope, c) =>
        {
            var documents = (await documentQuery.ListForContractAsync(scope.Subject.ContractId, c)).Where(d => resolved.Definition.Settings.Documents.FinalTypes.Contains(d.DocumentTypeCode) && d.Downloadable).ToList();
            var title = scope.Subject.IsVariation ? scope.Subject.Variation!.Title : scope.Subject.Contract.Title;
            var reference = scope.Subject.IsVariation ? scope.Subject.Variation!.Reference : scope.Subject.Contract.AgreementReference;
            var envelope = await provider.CreateEnvelopeAsync(new EnvelopeRequest(scope.Subject.ContractId, reference, title, documents.Select(d => d.Sha256 ?? "").ToList()), c);
            foreach (var prior in await db.Executions.Where(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent).ToListAsync(c)) prior.Supersede();
            created = ExecutionRecord.Start(tenant.RequireTenantId(), scope.Subject.OrganisationId, subjectKind, subjectId, scope.Subject.ContractId, provider.Key, envelope?.EnvelopeId,
                envelope?.Status ?? ExecutionStatus.Sent, engine.ActorLabel, scope.NowUtc);
            db.Executions.Add(created);
            await db.SaveChangesAsync(c);
            events.Raise(LifecycleEventTypes.ExecutionChanged, scope.Subject, new { executionId = created.Id, provider = provider.Key, status = created.Status.ToString() });
            audit.Record(new AuditEntry("execution.started", nameof(ExecutionRecord), created.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Classification: scope.Subject.Classification,
                NewValuesJson: AuditJson.Of(new { provider = provider.Key, envelopeId = created.EnvelopeId, status = created.Status.ToString() })));
        }, ct);
        return ToInfo(created);
    }

    public async Task<ExecutionInfo> RecordExecutedCopyAsync(RecordExecutedCopyRequest r, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(r.SubjectKind, r.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var descriptor = await engine.DescribeAsync(subject, null, ct);
        await access.RequireAsync(OwnerPermission(subject), descriptor, ct);
        var current = await db.Executions.FirstOrDefaultAsync(x => x.SubjectKind == r.SubjectKind && x.SubjectId == r.SubjectId && x.IsCurrent, ct);
        if (current is null || resolved.Definition.State(subject.Status)?.Role != "signature")
            throw new DomainException("not_out_for_signature", $"The executed copy is attached while the {subject.Noun} is out for signature.");
        var scale = await access.ClassificationScaleAsync(ct);
        var requested = scale.Canonical(r.Classification) ?? (string.IsNullOrWhiteSpace(r.Classification) ? null : throw new DomainException("invalid_classification", $"Classification '{r.Classification}' is not recognised."));
        var level = requested is not null && scale.Rank(requested) >= scale.Rank(subject.Classification) ? requested : subject.Classification;
        if (!(await access.CheckSilentAsync("contracts.read", descriptor with { Classification = level }, ct)).Allowed) throw new ForbiddenException("You cannot classify a document above your own access.");

        var title = (subject.IsVariation ? subject.Variation!.Reference : subject.Contract.AgreementReference) + " executed copy";
        var dto = await documentService.AddAsync(new AddDocumentRequest(subject.OrganisationId, subject.ContractId, resolved.Definition.Settings.Documents.ExecutedCopyType, title, r.FileName, r.MimeType, r.Content, level, IsExecutedCopy: true), ct);
        return await CompleteAsync(current.Id, subject, dto.DocumentId, "Executed copy uploaded", ct);
    }

    private async Task<ExecutionInfo> CompleteAsync(Guid executionId, SubjectView subject, Guid documentId, string detail, CancellationToken ct)
    {
        ExecutionRecord record = null!;
        await uow.ExecuteAsync(async c =>
        {
            record = await db.Executions.FirstAsync(x => x.Id == executionId, c);
            record.Complete(documentId, clock.UtcNow);
            if (subject.IsVariation) db.SubjectDocuments.Add(SubjectDocument.Link(tenant.RequireTenantId(), SubjectKinds.Variation, subject.Id, subject.ContractId, documentId, "Executed", engine.ActorLabel, clock.UtcNow));
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.ExecutionChanged, subject, new { executionId, status = "Completed" });
            audit.Record(new AuditEntry("execution.completed", nameof(ExecutionRecord), executionId.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification, Reason: detail,
                NewValuesJson: AuditJson.Of(new { documentId, note = "An executed copy is not register authorisation" })));
            await db.SaveChangesAsync(c);
        }, ct);
        return ToInfo(record);
    }

    public async Task<ExecutionInfo> SimulateEventAsync(string subjectKind, Guid subjectId, ExecutionStatus status, CancellationToken ct = default)
    {
        if (Mode == EcpMode.Production) throw new DomainException("mock_in_production", "Simulated signing events are refused in Production.");
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        await access.RequireAsync(OwnerPermission(subject), await engine.DescribeAsync(subject, null, ct), ct);
        var current = await db.Executions.FirstOrDefaultAsync(x => x.SubjectKind == subjectKind && x.SubjectId == subjectId && x.IsCurrent, ct)
            ?? throw new DomainException("not_out_for_signature", "Nothing has been sent for signature.");
        if (current.Provider != "mock") throw new DomainException("not_mock", "Only the mock signature service can be driven by hand.");
        if (status == ExecutionStatus.Completed)
        {
            var provider = providers.Resolve("mock", Mode);
            var content = await provider.FetchExecutedAsync(current.EnvelopeId!, ct) ?? throw new DomainException("no_content", "The service returned no executed copy.");
            await using var stream = new MemoryStream(content.Content);
            var title = (subject.IsVariation ? subject.Variation!.Reference : subject.Contract.AgreementReference) + " executed copy (mock)";
            var dto = await documentService.AddAsync(new AddDocumentRequest(subject.OrganisationId, subject.ContractId, resolved.Definition.Settings.Documents.ExecutedCopyType, title, content.FileName, content.MimeType, stream,
                subject.Classification, IsExecutedCopy: true), ct);
            return await CompleteAsync(current.Id, subject, dto.DocumentId, "Completed by the mock signature service", ct);
        }
        if (status is not (ExecutionStatus.Delivered or ExecutionStatus.Declined or ExecutionStatus.Voided or ExecutionStatus.Sent)) throw new DomainException("invalid_event", "That event is not recognised.");
        await uow.ExecuteAsync(async c =>
        {
            current.SetStatus(status, $"{status} (mock)", clock.UtcNow);
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.ExecutionChanged, subject, new { executionId = current.Id, status = status.ToString() });
            audit.Record(new AuditEntry("execution.status_changed", nameof(ExecutionRecord), current.Id.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification,
                NewValuesJson: AuditJson.Of(new { status = status.ToString(), provider = "mock" })));
            await db.SaveChangesAsync(c);
        }, ct);
        return ToInfo(current);
    }
}
