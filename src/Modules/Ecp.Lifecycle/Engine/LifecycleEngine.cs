using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

/// <summary>What a step is acting on: a contract, or one of its Variations (whose parent contract supplies the access facts).</summary>
internal sealed record SubjectView(string Kind, Guid Id, Guid ContractId, Guid OrganisationId, string Status, string Classification, string? WorkflowRef, Guid? ReleaseId, ContractFacts Contract, Variation? Variation)
{
    public bool IsVariation => Kind == SubjectKinds.Variation;
    public bool LegalRequired => IsVariation ? Variation!.LegalReviewRequired : Contract.LegalReviewRequired;
    public string Noun => IsVariation ? "Variation" : "contract";
}

/// <summary>Values supplied by the service that owns a step, which the guards read (the outcome of a Variation, for example). Never taken from request input.</summary>
internal sealed record GuardInputs(VariationOutcome? VariationOutcome = null);

internal sealed record TransitionCommand(string SubjectKind, Guid SubjectId, string TransitionCode, string? Comment = null, string? Reason = null, string? ExpectedStatus = null, string? ClientRequestId = null,
    bool AllowManaged = false, GuardInputs? Inputs = null, ApprovalRequest? Approval = null);

/// <summary>Handed to the owning service while the step's transaction is open, so its own records commit atomically with the state change.</summary>
internal sealed record TransitionScope(SubjectView Subject, ResolvedDefinition Definition, TransitionDefinition Transition, string Actor, Guid? ActorUserId, DateTime NowUtc, StateEffects Effects);

/// <summary>
/// The one place a workflow state changes. For every step: the definition the record is pinned to decides what is possible; the caller's permission and scope are checked on the server; the guards run;
/// then the state, the status history, the audit event and the outbound event are written in a single transaction. Nothing outside this class changes a status.
/// </summary>
internal sealed class LifecycleEngine(
    LifecycleDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, ActorContext actor, CurrentUser currentUser, IAccessService access, IContractLifecyclePort port,
    ISecuredContractService contracts, IWorkflowDefinitions definitions, GuardEvaluator guards, LifecycleEvents events)
{
    public string ActorLabel => actor.ActorId ?? currentUser.UserId?.ToString() ?? "system";
    public Guid? ActorUserId => currentUser.UserId;

    public async Task<SubjectView> LoadSubjectAsync(string kind, Guid id, CancellationToken ct)
    {
        if (kind == SubjectKinds.Variation)
        {
            var v = await db.Variations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ResourceNotFoundException();
            var parent = await port.FindAsync(v.ContractId, ct) ?? throw new ResourceNotFoundException();
            return new SubjectView(kind, v.Id, v.ContractId, v.OrganisationId, v.Status, parent.Classification, v.WorkflowRef, v.ConfigReleaseId, parent, v);
        }
        var c = await port.FindAsync(id, ct) ?? throw new ResourceNotFoundException();
        return new SubjectView(SubjectKinds.Contract, c.Id, c.Id, c.OrganisationId, c.Status, c.Classification, c.WorkflowRef, c.ConfigReleaseId, c, null);
    }

    public Task<ResolvedDefinition> DefinitionForAsync(SubjectView s, CancellationToken ct)
        => definitions.ResolveAsync(s.IsVariation ? WorkflowKinds.Variation : WorkflowKinds.ContractLifecycle, s.OrganisationId, s.ReleaseId, ct);

    /// <summary>The contract's access facts, with the approval facts the evaluator needs when a request is waiting (the approver group and who submitted it).</summary>
    public async Task<ResourceDescriptor> DescribeAsync(SubjectView s, ApprovalRequest? pending, CancellationToken ct)
    {
        var descriptor = await contracts.DescribeAsync(s.ContractId, ct);
        if (pending is { Status: ApprovalStatus.Pending })
            descriptor = descriptor with { PendingApprovalGroupKeys = new HashSet<string>(pending.PendingGroups), SubmittedByUserId = pending.SubmittedByUserId };
        return descriptor;
    }

    /// <summary>Reading a record is what lets a person see its steps, history and reviews; outside their scope or classification it is a 404.</summary>
    public Task RequireReadAsync(ResourceDescriptor descriptor, CancellationToken ct) => access.RequireAsync("contracts.read", descriptor, ct);

    /// <summary>
    /// A disposition decision belongs to the approver group named in the workflow, and cannot be taken by the person who flagged the contract. The facts the evaluator needs for that are added here for
    /// the transition's permission; the rule itself stays in the access evaluator.
    /// </summary>
    public async Task<ResourceDescriptor> ForPermissionAsync(ResourceDescriptor descriptor, SubjectView s, ResolvedDefinition def, string permission, CancellationToken ct)
    {
        if (permission != "disposition.approve" || s.IsVariation) return descriptor;
        var flagged = def.Definition.States.Where(x => x.Role == "flagged").Select(x => x.Code).ToArray();
        var flagger = await db.History.AsNoTracking().Where(h => h.SubjectId == s.Id && flagged.Contains(h.ToStatus)).OrderByDescending(h => h.Sequence).Select(h => h.ActorUserId).FirstOrDefaultAsync(ct);
        return descriptor with { PendingApprovalGroupKeys = new HashSet<string>([def.Definition.Settings.Approval.ApproverGroupKey]), SubmittedByUserId = flagger };
    }

    public async Task<ApprovalRequest?> PendingApprovalAsync(string kind, Guid subjectId, CancellationToken ct)
        => await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.SubjectKind == kind && a.SubjectId == subjectId && a.Status == ApprovalStatus.Pending, ct);

    public async Task<IReadOnlyList<AvailableTransition>> AvailableAsync(SubjectView s, ResolvedDefinition def, CancellationToken ct)
    {
        var pending = await PendingApprovalAsync(s.Kind, s.Id, ct);
        var descriptor = await DescribeAsync(s, pending, ct);
        var list = new List<AvailableTransition>();
        foreach (var t in def.Definition.From(s.Status))
        {
            string? blocked = null;
            var permitted = await access.CheckSilentAsync(t.Permission, await ForPermissionAsync(descriptor, s, def, t.Permission, ct), ct);
            if (!permitted.Allowed) blocked = permitted.Deny == DenyKind.NotFound ? "You do not have access to this record." : "You do not have permission to take this step.";
            else
            {
                var failures = await guards.EvaluateAsync(new GuardContext(s, def, t, new TransitionCommand(s.Kind, s.Id, t.Code, Approval: pending), ActorUserId, pending), forAvailability: true, ct);
                if (failures.Count > 0) blocked = failures[0];
            }
            list.Add(new AvailableTransition(t.Code, t.Display, t.To, def.Definition.DisplayOf(t.To), blocked is null, blocked, t.CommentRequired || t.Guards.Contains("CommentRequired"),
                t.ReasonRequired || t.Guards.Contains("ReasonRequired"), t.Managed, t.Role));
        }
        return list;
    }

    /// <summary>Takes the step. <paramref name="within"/> runs inside the same transaction, after the state change, so the owning service's own records commit with it or not at all.</summary>
    public async Task<TransitionResult> ExecuteAsync(TransitionCommand cmd, Func<TransitionScope, CancellationToken, Task>? within, CancellationToken ct)
    {
        var subject = await LoadSubjectAsync(cmd.SubjectKind, cmd.SubjectId, ct);
        var resolved = await DefinitionForAsync(subject, ct);
        var def = resolved.Definition;
        var transition = def.Transition(cmd.TransitionCode) ?? throw new DomainException("unknown_transition", "That step is not part of this workflow.");

        // Who may take the step is decided first, and a person with no access to the record learns nothing about it (404).
        var pending = cmd.Approval ?? await PendingApprovalAsync(subject.Kind, subject.Id, ct);
        var descriptor = await DescribeAsync(subject, pending, ct);
        await access.RequireAsync(transition.Permission, await ForPermissionAsync(descriptor, subject, resolved, transition.Permission, ct), ct);

        // A repeat of a step the caller already took (same client request id) returns the first outcome and writes nothing.
        if (!string.IsNullOrWhiteSpace(cmd.ClientRequestId))
        {
            var prior = await db.History.AsNoTracking().FirstOrDefaultAsync(h => h.SubjectId == subject.Id && h.ClientRequestId == cmd.ClientRequestId, ct);
            if (prior is not null)
            {
                if (prior.TransitionCode != transition.Code) throw new DomainException("idempotency_conflict", "That request id was already used for a different step.");
                return new TransitionResult(subject.Id, prior.FromStatus, prior.ToStatus, prior.TransitionCode, Replayed: true);
            }
        }

        if (!transition.From.Contains(subject.Status, StringComparer.Ordinal))
        {
            var options = def.From(subject.Status).Select(t => t.Display).Distinct().ToList();
            var hint = options.Count == 0 ? $"No further steps are available from {def.DisplayOf(subject.Status)}." : "From here you can: " + string.Join(", ", options) + ".";
            await RecordDeniedAsync(subject, transition, $"The {subject.Noun} is {def.DisplayOf(subject.Status)}.", ct);
            throw new DomainException("invalid_transition", $"'{transition.Display}' is not available while the {subject.Noun} is {def.DisplayOf(subject.Status)}. {hint}");
        }
        if (cmd.ExpectedStatus is not null && !string.Equals(cmd.ExpectedStatus, subject.Status, StringComparison.Ordinal))
            throw new DomainException("state_changed", $"The {subject.Noun} has moved on since you opened it: it is now {def.DisplayOf(subject.Status)}. Reload it to see its current state.");
        if (transition.Managed && !cmd.AllowManaged)
            throw new DomainException("managed_transition", $"'{transition.Display}' is taken from its own page (Legal review, approval, Variation or renewal), not directly.");

        var failures = await guards.EvaluateAsync(new GuardContext(subject, resolved, transition, cmd, ActorUserId, pending), forAvailability: false, ct);
        if (failures.Count > 0)
        {
            await RecordDeniedAsync(subject, transition, string.Join(" ", failures), ct);
            throw new TransitionBlockedException(failures.Count == 1 ? failures[0] : "This step cannot be taken yet: " + string.Join(" ", failures), failures);
        }

        var target = def.State(transition.To)!;
        var effects = new StateEffects(target.Locked, target.FreezeContent, target.DocumentPolicy, target.LockReason);
        var now = clock.UtcNow;
        var label = ActorLabel;
        var scope = new TransitionScope(subject, resolved, transition, label, ActorUserId, now, effects);

        await uow.ExecuteAsync(async c =>
        {
            if (subject.IsVariation)
            {
                var variation = await db.Variations.FirstAsync(v => v.Id == subject.Id, c);
                if (!string.Equals(variation.Status, subject.Status, StringComparison.Ordinal))
                    throw new DomainException("state_changed", "The Variation has moved on since you opened it. Reload it to see its current state.");
                if (target.Terminal) { /* the owning service completes the Variation with its outcome inside `within` */ }
                else variation.MoveTo(transition.To, now);
            }
            else
            {
                if (subject.WorkflowRef is null) await port.PinWorkflowAsync(subject.Id, resolved.WorkflowRef, resolved.ReleaseId, c);
                await port.ApplyTransitionAsync(subject.Id, subject.Status, transition.To, effects, label, transition.Code, cmd.Comment ?? cmd.Reason, c);
            }
            db.History.Add(StatusHistory.Record(tenant.RequireTenantId(), subject.OrganisationId, subject.Kind, subject.Id, subject.ContractId, subject.Status, transition.To, transition.Code, label, ActorUserId,
                cmd.Comment ?? cmd.Reason, resolved.WorkflowRef, resolved.ReleaseId, actor.CorrelationId, string.IsNullOrWhiteSpace(cmd.ClientRequestId) ? null : cmd.ClientRequestId, actor.ElevationId, now,
                await NextHistorySequenceAsync(subject.ContractId, c)));
            if (within is not null) await within(scope, c);
            events.Raise(LifecycleEventTypes.Transitioned, subject, new { from = subject.Status, to = transition.To, transition = transition.Code, role = transition.Role });
            await SaveAsync(c);
            if (subject.IsVariation)
                audit.Record(new AuditEntry("variation.transitioned", nameof(Variation), subject.Id.ToString(), OrganisationId: subject.OrganisationId, Reason: cmd.Comment ?? cmd.Reason, Classification: subject.Classification,
                    OldValuesJson: AuditJson.Of(new { status = subject.Status }), NewValuesJson: AuditJson.Of(new { status = transition.To, transition = transition.Code, contractId = subject.ContractId })));
        }, ct);
        return new TransitionResult(subject.Id, subject.Status, transition.To, transition.Code, Replayed: false);
    }

    /// <summary>The next position in a contract's history, counting rows added in this unit of work that are not saved yet.</summary>
    public async Task<int> NextHistorySequenceAsync(Guid contractId, CancellationToken ct)
    {
        var saved = await db.History.Where(h => h.ContractId == contractId).Select(h => (int?)h.Sequence).MaxAsync(ct) ?? 0;
        var pending = db.ChangeTracker.Entries<StatusHistory>().Where(e => e.State == EntityState.Added && e.Entity.ContractId == contractId).Select(e => e.Entity.Sequence).DefaultIfEmpty(0).Max();
        return Math.Max(saved, pending) + 1;
    }

    /// <summary>The next position on a Legal review's timeline.</summary>
    public async Task<int> NextLegalSequenceAsync(Guid reviewId, CancellationToken ct)
    {
        var saved = await db.LegalEvents.Where(e => e.ReviewId == reviewId).Select(e => (int?)e.Sequence).MaxAsync(ct) ?? 0;
        var pending = db.ChangeTracker.Entries<LegalEvent>().Where(e => e.State == EntityState.Added && e.Entity.ReviewId == reviewId).Select(e => e.Entity.Sequence).DefaultIfEmpty(0).Max();
        return Math.Max(saved, pending) + 1;
    }

    /// <summary>Saves the lifecycle context, turning a concurrent change into a clear message.</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The record was changed by someone else at the same time. Reload it and try again."); }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("duplicate_request", "That action was already recorded by someone else at the same time. Reload to see where things stand."); }
    }

    /// <summary>A refused step is evidence too: it is audited as denied, in its own transaction so it survives the refusal.</summary>
    private async Task RecordDeniedAsync(SubjectView s, TransitionDefinition t, string why, CancellationToken ct)
    {
        try
        {
            await uow.ExecuteAsync(c =>
            {
                audit.Record(new AuditEntry("lifecycle.transition_denied", s.IsVariation ? nameof(Variation) : nameof(Contract), s.Id.ToString(), OrganisationId: s.OrganisationId, Outcome: AuditOutcome.Denied,
                    Reason: why, Classification: s.Classification, NewValuesJson: AuditJson.Of(new { transition = t.Code, status = s.Status })));
                return Task.CompletedTask;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the refusal itself is what the caller sees; a failure to audit it must not mask that */ }
    }
}

internal sealed class LifecycleService(LifecycleEngine engine, LifecycleDbContext db, IWorkflowDefinitions definitions, LegalReviewService legalReviews) : ILifecycleService
{
    public async Task<IReadOnlyList<AvailableTransition>> AvailableAsync(Guid contractId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        // Reading is what lets a person see the steps at all; a record outside their scope is a 404.
        var descriptor = await engine.DescribeAsync(subject, null, ct);
        await engine.RequireReadAsync(descriptor, ct);
        return await engine.AvailableAsync(subject, resolved, ct);
    }

    public async Task<TransitionResult> TransitionAsync(TransitionRequest r, CancellationToken ct = default)
        => await engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Contract, r.ContractId, r.TransitionCode, r.Comment, r.Reason, r.ExpectedStatus, r.ClientRequestId),
            // Withdrawing a contract that Legal still holds withdraws the open review with it (both are kept).
            (scope, c) => scope.Transition.Role == "withdraw" ? legalReviews.WithdrawOpenAsync(scope, r.Reason ?? r.Comment, c) : Task.CompletedTask, ct);

    public async Task<IReadOnlyList<HistoryItem>> HistoryAsync(Guid contractId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        var rows = await db.History.AsNoTracking().Where(h => h.ContractId == contractId).OrderBy(h => h.Sequence).ToListAsync(ct);
        var cache = new Dictionary<Guid, WorkflowDefinition>();
        var items = new List<HistoryItem>();
        foreach (var h in rows)
        {
            var releaseId = h.ConfigReleaseId;
            WorkflowDefinition? def = null;
            if (releaseId is { } id)
            {
                if (!cache.TryGetValue(id, out def))
                {
                    var kind = h.SubjectKind == SubjectKinds.Variation ? WorkflowKinds.Variation : WorkflowKinds.ContractLifecycle;
                    def = (await definitions.ResolveAsync(kind, h.OrganisationId, id, ct)).Definition;
                    cache[id] = def;
                }
            }
            items.Add(new HistoryItem(h.OccurredUtc, h.FromStatus, def?.DisplayOf(h.FromStatus) ?? h.FromStatus, h.ToStatus, def?.DisplayOf(h.ToStatus) ?? h.ToStatus, h.TransitionCode,
                def?.Transition(h.TransitionCode)?.Display ?? h.TransitionCode, h.Actor, h.Comment, h.SubjectKind, h.WorkflowRef));
        }
        return items;
    }

    public async Task<LifecycleState> StateAsync(Guid contractId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var def = resolved.Definition;
        var current = def.State(subject.Status);
        // The path is the states a contract normally passes through, in the order the definition reaches them from the initial state.
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(def.Initial.Code); seen.Add(def.Initial.Code);
        while (queue.Count > 0)
        {
            var code = queue.Dequeue(); order.Add(code);
            foreach (var t in def.From(code)) if (seen.Add(t.To)) queue.Enqueue(t.To);
        }
        return new LifecycleState(subject.Status, current?.Display ?? subject.Status, current?.Role, resolved.WorkflowRef, resolved.ReleaseNumber, subject.WorkflowRef is not null, subject.Contract.RecordLocked, subject.Contract.ContentFrozen,
            order.Select(c => new LifecycleStateStep(c, def.DisplayOf(c), c == subject.Status)).ToList(), def.Settings.Documents.UploadTypes ?? [.. def.Settings.Documents.FinalTypes, "Schedule", "SupportingDocument", "Correspondence"]);
    }
}
