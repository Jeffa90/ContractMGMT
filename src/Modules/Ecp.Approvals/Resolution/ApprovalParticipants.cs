using Ecp.Approvals.Definitions;
using Ecp.Approvals.Domain;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;

namespace Ecp.Approvals.Resolution;

/// <summary>
/// Works out who can act on one step: the members of the approver group, its backup approvers and backup group, and anyone who holds a delegation from an eligible member. Every candidate is run through the
/// same access evaluator that guards the record, so the submitter, a disabled account, someone whose assignment is not in force on the date, someone outside the organisation or scope and someone whose
/// classification ceiling is below the record's are each left out, with the reason kept for the explanation.
/// </summary>
internal sealed class ApprovalParticipants(IAccessDirectory directory, AccessEvaluator evaluator, IClassificationScaleProvider scales, IApprovalDelegations delegations)
{
    public sealed record StepSpec(string Key, string Display, string Group, string? BackupGroup, string Mode, bool AllowBackup);

    private sealed record Candidate(Guid UserId, string Name, string Role, string Why);

    public async Task<StepParticipation> ForStepAsync(StepSpec spec, Guid organisationId, ResourceDescriptor resource, Guid? submitter, DateTime asOfUtc, ResolutionTrace? trace, CancellationToken ct)
    {
        var scale = await scales.GetAsync(ct);
        var groups = new[] { spec.Group, spec.BackupGroup }.Where(g => g is not null).Select(g => g!).ToHashSet(StringComparer.Ordinal);
        var res = resource with { PendingApprovalGroupKeys = groups, SubmittedByUserId = submitter };

        var candidates = new Dictionary<Guid, Candidate>();
        void Offer(Candidate c) { if (!candidates.TryGetValue(c.UserId, out var have) || (have.Role != ParticipantRoles.Primary && c.Role == ParticipantRoles.Primary)) candidates[c.UserId] = c; }
        foreach (var a in await directory.ApproversAsync(spec.Group, asOfUtc, ct))
            Offer(a.RoleCode == RoleCodes.Approver
                ? new Candidate(a.UserId, a.DisplayName, ParticipantRoles.Primary, $"Approver of group {spec.Group}")
                : new Candidate(a.UserId, a.DisplayName, ParticipantRoles.Backup, $"Backup approver of group {spec.Group}"));
        if (spec.BackupGroup is not null)
            foreach (var a in await directory.ApproversAsync(spec.BackupGroup, asOfUtc, ct))
                Offer(new Candidate(a.UserId, a.DisplayName, ParticipantRoles.Backup, $"Member of backup group {spec.BackupGroup}"));

        var explanations = new List<ApproverExplanation>();
        var participants = new List<PlanApprover>();
        var eligibleSeats = new Dictionary<Guid, PlanApprover>();

        async Task<(bool Ok, IReadOnlyList<string> Reasons)> CheckAsync(Guid userId, string role)
        {
            if (submitter == userId) return (false, ["Submitted this request, and nobody approves their own submission (segregation of duties)."]);
            if (role == ParticipantRoles.Backup && !spec.AllowBackup) return (false, ["This step does not allow backup approvers to stand in."]);
            var subject = await directory.LoadSubjectAsync(userId, ct);
            if (subject is null) return (false, ["The account is disabled or no longer exists."]);
            var decision = evaluator.Evaluate(subject, scale, asOfUtc, "approvals.action", res);
            return decision.Allowed ? (true, []) : (false, [Describe(decision, res, subject, scale)]);
        }

        foreach (var c in candidates.Values.OrderBy(c => c.Role == ParticipantRoles.Primary ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            var (ok, reasons) = await CheckAsync(c.UserId, c.Role);
            if (ok)
            {
                var p = new PlanApprover(c.UserId, c.Name, c.Role, null, null, c.Why);
                participants.Add(p); eligibleSeats[c.UserId] = p;
                explanations.Add(new ApproverExplanation(spec.Key, spec.Display, c.UserId, c.Name, true, c.Role, null, null, c.Why + "; passes the access rules for this record.", []));
                trace?.Add(TraceStages.Approver, c.Name, TraceOutcomes.Included, $"{c.Name} is included as {c.Role.ToLowerInvariant()} approver for step '{spec.Display}': {c.Why}.");
            }
            else
            {
                explanations.Add(new ApproverExplanation(spec.Key, spec.Display, c.UserId, c.Name, false, c.Role, null, null, $"{c.Why}, but left out.", reasons));
                trace?.Add(TraceStages.Approver, c.Name, TraceOutcomes.Excluded, $"{c.Name} ({c.Why}) is left out of step '{spec.Display}': {string.Join(" ", reasons)}");
            }
        }

        // Delegations: only the authority of a person who is eligible today can be handed on, only one hop, and a loop is ignored altogether.
        var active = await delegations.ActiveAsync(organisationId, groups, asOfUtc, ct);
        var edges = active.Select(d => new DelegationGraph.Edge(d.FromUserId, d.ToUserId)).ToList();
        var inCycle = DelegationGraph.EdgesInCycles(edges);
        foreach (var d in active.OrderBy(d => d.FromName, StringComparer.Ordinal).ThenBy(d => d.ToName, StringComparer.Ordinal))
        {
            var edge = new DelegationGraph.Edge(d.FromUserId, d.ToUserId);
            if (inCycle.Contains(edge))
            {
                explanations.Add(new ApproverExplanation(spec.Key, spec.Display, d.ToUserId, d.ToName, false, ParticipantRoles.Delegate, d.FromUserId, d.FromName, $"Delegation from {d.FromName} ignored.", ["The delegation is part of a circular arrangement, so it is ignored."]));
                trace?.Add(TraceStages.Delegation, d.ToName, TraceOutcomes.Excluded, $"Delegation {d.FromName} to {d.ToName} is part of a circular arrangement and is ignored.");
                continue;
            }
            if (!eligibleSeats.ContainsKey(d.FromUserId))
            {
                explanations.Add(new ApproverExplanation(spec.Key, spec.Display, d.ToUserId, d.ToName, false, ParticipantRoles.Delegate, d.FromUserId, d.FromName, $"Delegation from {d.FromName} ignored.", [$"{d.FromName} cannot act on this step, so has no authority to delegate."]));
                trace?.Add(TraceStages.Delegation, d.ToName, TraceOutcomes.Excluded, $"Delegation {d.FromName} to {d.ToName} is ignored: {d.FromName} cannot act on this step.");
                continue;
            }
            var (ok, reasons) = await CheckAsync(d.ToUserId, ParticipantRoles.Delegate);
            var why = $"Acts on behalf of {d.FromName} until {d.ValidToUtc:yyyy-MM-dd HH:mm} UTC ({d.Reason})";
            if (ok)
            {
                participants.Add(new PlanApprover(d.ToUserId, d.ToName, ParticipantRoles.Delegate, d.FromUserId, d.FromName, why));
                explanations.Add(new ApproverExplanation(spec.Key, spec.Display, d.ToUserId, d.ToName, true, ParticipantRoles.Delegate, d.FromUserId, d.FromName, why + "; passes the access rules for this record.", []));
                trace?.Add(TraceStages.Delegation, d.ToName, TraceOutcomes.Included, $"{d.ToName} is included as delegate of {d.FromName} for step '{spec.Display}'.");
            }
            else
            {
                explanations.Add(new ApproverExplanation(spec.Key, spec.Display, d.ToUserId, d.ToName, false, ParticipantRoles.Delegate, d.FromUserId, d.FromName, why + ", but left out.", reasons));
                trace?.Add(TraceStages.Delegation, d.ToName, TraceOutcomes.Excluded, $"{d.ToName} (delegate of {d.FromName}) is left out of step '{spec.Display}': {string.Join(" ", reasons)}");
            }
        }
        return new StepParticipation(participants, explanations);
    }

    /// <summary>True when the participants make the step satisfiable: someone for a first-response step, at least one named primary approver for an all-required step.</summary>
    public static bool IsSatisfiable(string mode, IReadOnlyList<PlanApprover> participants)
        => mode == StepModes.AllRequired ? participants.Any(p => p.Role == ParticipantRoles.Primary) : participants.Count > 0;

    private static string Describe(AccessDecision d, ResourceDescriptor res, AccessSubject subject, ClassificationScale scale)
    {
        var text = $"Access rules refuse it: {d.Reason}";
        if (res.Classification is { } level && scale.IsPrivileged(level)) text += $" (the record is {level}, which needs an explicit privileged-access grant)";
        return text;
    }
}
