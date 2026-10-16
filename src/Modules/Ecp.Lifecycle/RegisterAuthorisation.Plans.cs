using System.Text.Json;
using Ecp.Approvals;
using Ecp.Approvals.Domain;
using Ecp.Approvals.Resolution;
using Ecp.Audit;
using Ecp.Identity.Access;
using Ecp.Lifecycle.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

// Approval under an approval matrix. A workflow that names no matrix never reaches this file: the single approver group of the reference model decides in RegisterAuthorisation.cs, unchanged.
internal sealed partial class RegisterAuthorisationService
{
    private static StoredPlan ReadPlan(ApprovalRequest r) => JsonSerializer.Deserialize<StoredPlan>(r.PlanJson!, Json) ?? throw new DomainException("plan_unreadable", "The stored plan of this request cannot be read.");

    private async Task<List<PlanResponse>> ResponsesAsync(Guid requestId, CancellationToken ct)
        => (await db.StepResponses.AsNoTracking().Where(x => x.RequestId == requestId).OrderBy(x => x.Sequence).ToListAsync(ct))
            .Select(x => new PlanResponse(x.StepKey, x.UserId, x.UserName, x.Role, x.OnBehalfOfUserId, x.OnBehalfOfName, x.Approved, x.Comment, x.RespondedUtc)).ToList();

    /// <summary>The people who can act on each open step today, and the one place (if any) where the signed-in person's response would count.</summary>
    private async Task<(string StepKey, Placement Placement)?> FindMyPlacementAsync(ApprovalRequest request, ApprovalPlan plan, PlanState state, IReadOnlyList<PlanResponse> responses, ResourceDescriptor descriptor, Guid me,
        Action<string, string>? firstError, CancellationToken ct)
    {
        foreach (var open in state.Open)
        {
            var participation = await approvalResolver.ParticipantsAsync(plan, open.Step.Key, descriptor, request.SubmittedByUserId, clock.UtcNow, ct);
            var capacities = participation.Participants.Where(p => p.UserId == me).ToList();
            if (capacities.Count == 0) continue;
            var placement = ApprovalPlans.Place(plan, responses, open.Step.Key, me, capacities);
            if (placement.Allowed) return (open.Step.Key, placement);
            firstError?.Invoke(placement.ErrorCode!, placement.Message!);
        }
        return null;
    }

    private async Task DecideWithPlanAsync(ApprovalRequest request, bool approve, string? comment, string? clientRequestId, CancellationToken ct)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("A signed-in person is required.");
        var plan = ReadPlan(request).Plan;
        var responses = await ResponsesAsync(request.Id, ct);
        var state = ApprovalPlans.Evaluate(plan, responses);
        if (state.Status != PlanStatus.InProgress) throw new DomainException("plan_settled", "Every step of this request has already been settled.");

        var subject = await engine.LoadSubjectAsync(request.SubjectKind, request.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var role = approve ? "approve" : "reject";
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == role)
            ?? throw new DomainException("invalid_transition", $"This {subject.Noun} is {resolved.Definition.DisplayOf(subject.Status)}, so the request can no longer be decided. Reload it to see where it stands.");
        var descriptor = Approving(await engine.DescribeAsync(subject, null, ct), request);

        string? errorCode = null, errorMessage = null;
        var mine = await FindMyPlacementAsync(request, plan, state, responses, descriptor, me, (c, m) => { errorCode ??= c; errorMessage ??= m; }, ct);
        if (mine is null)
        {
            if (errorCode is not null) throw new DomainException(errorCode, errorMessage!);
            throw new ForbiddenException("Only an assigned approver of a step that is open can respond to this request.");
        }
        var (stepKey, placement) = mine.Value;
        var name = await directory.DisplayNameAsync(me, ct) ?? engine.ActorLabel;
        var sequence = responses.Count + 1;
        var asRole = placement.Role!;
        var response = ApprovalStepResponse.Record(tenant.RequireTenantId(), request.Id, sequence, stepKey, me, name, asRole, placement.OnBehalfOfUserId, placement.OnBehalfOfName, approve, comment, clock.UtcNow);
        var after = ApprovalPlans.Evaluate(plan, [.. responses, new PlanResponse(stepKey, me, name, asRole, placement.OnBehalfOfUserId, placement.OnBehalfOfName, approve, comment, response.RespondedUtc)]);

        try
        {
            if (!approve || after.Status == PlanStatus.Approved)
            {
                // The last approval, or any rejection, decides the whole request through the same transition the reference model uses (register date, lock, history, audit).
                await engine.ExecuteAsync(new TransitionCommand(request.SubjectKind, request.SubjectId, transition.Code, comment, null, subject.Status, clientRequestId, AllowManaged: true, Approval: request), async (scope, c) =>
                {
                    if (scope.Subject.IsVariation)
                    {
                        if (approve) await variationOutcomes.ApplyApprovedAsync(scope, request, comment, c);
                        else await variationOutcomes.RejectAsync(scope, request, comment!, c);
                    }
                    else if (approve) await port.StampRegistrationAsync(scope.Subject.ContractId, scope.Actor, c);
                    db.StepResponses.Add(response);
                    request.Decide(approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected, me, name, asRole, placement.OnBehalfOfUserId, placement.OnBehalfOfName, comment, null, scope.NowUtc);
                    await engine.SaveAsync(c);
                    events.Raise(approve ? LifecycleEventTypes.ApprovalApproved : LifecycleEventTypes.ApprovalRejected, scope.Subject,
                        new { requestId = request.Id, decidedBy = me, asRole, submitterUserId = request.SubmittedByUserId, group = request.ApproverGroupKey });
                    if (approve && !scope.Subject.IsVariation) events.Raise(LifecycleEventTypes.ContractRegistered, scope.Subject, new { requestId = request.Id, submitterUserId = request.SubmittedByUserId });
                    audit.Record(new AuditEntry(approve ? "approval.approved" : "approval.rejected", nameof(ApprovalRequest), request.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: comment,
                        Classification: scope.Subject.Classification, OldValuesJson: AuditJson.Of(new { status = "Pending" }),
                        NewValuesJson: AuditJson.Of(new { status = request.Status.ToString(), decidedBy = me, asRole, onBehalfOf = placement.OnBehalfOfUserId, step = stepKey, matrix = request.MatrixCode, release = request.MatrixRelease, planSha256 = request.PlanSha256, snapshotSha256 = request.SnapshotSha256, subjectKind = request.SubjectKind })));
                    await db.SaveChangesAsync(c);
                }, ct);
                return;
            }

            // An earlier step of several: record the response and open whatever it unlocks. The request stays pending.
            var previousGroups = state.ActiveGroupKeys;
            await uow.ExecuteAsync(async c =>
            {
                db.StepResponses.Add(response);
                request.SetActiveGroups(after.ActiveGroupKeys);
                await db.SaveChangesAsync(c);
                foreach (var g in after.ActiveGroupKeys.Except(previousGroups, StringComparer.Ordinal))
                    events.Raise(LifecycleEventTypes.ApprovalSubmitted, subject, new { requestId = request.Id, group = g, submitterUserId = request.SubmittedByUserId });
                audit.Record(new AuditEntry("approval.step_approved", nameof(ApprovalRequest), request.Id.ToString(), OrganisationId: subject.OrganisationId, Reason: comment, Classification: subject.Classification,
                    NewValuesJson: AuditJson.Of(new { step = stepKey, by = me, asRole, onBehalfOf = placement.OnBehalfOfUserId, opened = after.Open.Select(x => x.Step.Key), matrix = request.MatrixCode, release = request.MatrixRelease })));
                await db.SaveChangesAsync(c);
            }, ct);
        }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); throw new DomainException("concurrency_conflict", "Someone else responded at the same moment. Reload the request to see where it stands."); }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); throw new DomainException("concurrency_conflict", "Someone else responded at the same moment. Reload the request to see where it stands."); }
        catch (DomainException ex) when (ex.Code is "concurrency_conflict" or "state_changed" or "duplicate_request")
        {
            db.ChangeTracker.Clear();
            var latest = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.Id, ct);
            if (latest is not null && latest.Status != ApprovalStatus.Pending) throw AlreadyDecided(latest);
            throw;
        }
    }

    private async Task<PlanView> BuildPlanViewAsync(ApprovalRequest request, ResourceDescriptor descriptor, CancellationToken ct)
    {
        var stored = ReadPlan(request);
        var plan = stored.Plan;
        var responses = await ResponsesAsync(request.Id, ct);
        var state = ApprovalPlans.Evaluate(plan, responses);
        var steps = state.Steps.Select(s => new PlanStepView(s.Step.Key, s.Step.Display, s.Step.RowCode, s.Step.Stage + 1, s.Step.ApproverGroup, s.Step.BackupGroup, s.Step.Mode, s.Status.ToString(), s.Step.TimeoutDays,
            s.Step.Approvers.Select(a => new PlanApproverView(a.UserId, a.Name, a.Role, a.OnBehalfOfName, a.Why)).ToList(),
            s.Responses.Select(r => new PlanResponseView(r.UserName, r.Role, r.OnBehalfOfName, r.Approved, r.Comment, r.RespondedUtc)).ToList(), s.Outstanding.Select(o => o.Name).Distinct().ToList())).ToList();

        string? part = null, note = null;
        if (request.Status == ApprovalStatus.Pending && currentUser.UserId is { } me)
        {
            string? why = null;
            var mine = await FindMyPlacementAsync(request, plan, state, responses, descriptor, me, (_, m) => why ??= m, ct);
            if (mine is { } m) part = state.Steps.First(x => x.Step.Key == m.StepKey).Step.Display + (m.Placement.OnBehalfOfName is { } ob ? $" (on behalf of {ob})" : "");
            else note = why ?? (request.SubmittedByUserId == me ? "You submitted this request, so someone else must decide it." : "You are not one of the people who can respond to an open step.");
        }
        // How the plan was reached, and why people were left out, is for the administrators who maintain the matrix (they hold approvals.matrix.read).
        IReadOnlyList<TraceEntry>? trace = null; IReadOnlyList<ApproverExplanation>? explanations = null;
        if (descriptor.OrganisationId is { } org && (await access.CheckSilentAsync("approvals.matrix.read", new ResourceDescriptor("ApprovalMatrix", descriptor.TenantId, org, Dimensions: ScopeDimensions.OrganisationOnly), ct)).Allowed)
        { trace = stored.Trace; explanations = stored.Explanations; }
        return new PlanView(plan.MatrixCode, plan.MatrixName, plan.ReleaseNumber, plan.EffectiveDate, state.Status.ToString(), state.ActiveStage is { } a ? a + 1 : null, steps, part, note, trace, explanations);
    }
}
