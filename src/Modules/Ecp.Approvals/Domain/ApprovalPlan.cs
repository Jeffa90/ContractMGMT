using Ecp.Approvals.Definitions;

namespace Ecp.Approvals.Domain;

public static class ParticipantRoles
{
    public const string Primary = "Primary";
    public const string Backup = "Backup";
    public const string Delegate = "Delegate";
}

/// <summary>One person who may respond to a step, and why. A delegate acts on behalf of the member who delegated; a backup stands in for the group's primary approvers.</summary>
public sealed record PlanApprover(Guid UserId, string Name, string Role, Guid? OnBehalfOfUserId, string? OnBehalfOfName, string Why);

/// <summary>A step of a resolved plan. <see cref="Stage"/> orders the steps: every step of the lowest stage that is not yet satisfied is open at the same time.</summary>
public sealed record PlanStep(
    string Key, string RowCode, string Display, int Stage, string ApproverGroup, string? BackupGroup, string Mode, int? TimeoutDays, string? EscalateTo, bool AllowBackup, IReadOnlyList<PlanApprover> Approvers)
{
    public IReadOnlyList<PlanApprover> Primaries => Approvers.Where(a => a.Role == ParticipantRoles.Primary).ToList();
}

/// <summary>
/// The outcome of resolving a matrix for one submission, stored with the submission. Later matrix versions never change it. The people named were eligible on the date of resolution; who may respond
/// when the time comes is judged again against the group as it then is (the first-response rule of the register authorisation), while an all-required step keeps the members it named.
/// </summary>
public sealed record ApprovalPlan(
    int Version, string MatrixCode, string MatrixName, int? ReleaseNumber, Guid? ReleaseId, string ContentHash, DateTime ResolvedUtc, DateOnly EffectiveDate, string MatrixCurrency, string? FactCurrency,
    decimal? FactValue, decimal? ConvertedValue, IReadOnlyList<string> RowCodes, IReadOnlyList<PlanStep> Steps)
{
    public IEnumerable<string> GroupKeys => Steps.SelectMany(s => new[] { s.ApproverGroup, s.BackupGroup! }).Where(g => g is not null).Distinct(StringComparer.Ordinal);
    public PlanStep? Step(string key) => Steps.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
}

public sealed record PlanResponse(string StepKey, Guid UserId, string UserName, string Role, Guid? OnBehalfOfUserId, string? OnBehalfOfName, bool Approved, string? Comment, DateTime RespondedUtc);

public enum PlanStatus { InProgress, Approved, Rejected }
public enum StepStatus { Waiting, Open, Satisfied, Rejected }

public sealed record StepState(PlanStep Step, StepStatus Status, IReadOnlyList<PlanResponse> Responses, IReadOnlyList<PlanApprover> Outstanding);

public sealed record PlanState(PlanStatus Status, int? ActiveStage, IReadOnlyList<StepState> Steps)
{
    public IEnumerable<StepState> Open => Steps.Where(s => s.Status == StepStatus.Open);
    /// <summary>The approver groups (and backup groups) that currently have something to decide: the groups of the open steps.</summary>
    public IReadOnlyList<string> ActiveGroupKeys => Open.SelectMany(s => new[] { s.Step.ApproverGroup, s.Step.BackupGroup! }).Where(g => g is not null).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>Where a response goes in a plan, or why it cannot.</summary>
public sealed record Placement(bool Allowed, string? Role, Guid? OnBehalfOfUserId, string? OnBehalfOfName, string? ErrorCode, string? Message)
{
    public static Placement Ok(string role, Guid? onBehalf = null, string? name = null) => new(true, role, onBehalf, name, null, null);
    public static Placement No(string code, string message) => new(false, null, null, null, code, message);
}

/// <summary>Pure rules for how a plan progresses. No I/O, so every combination of sequential, parallel, first-response and all-required steps is unit-testable.</summary>
public static class ApprovalPlans
{
    public static PlanState Evaluate(ApprovalPlan plan, IReadOnlyList<PlanResponse> responses)
    {
        var anyReject = responses.Any(r => !r.Approved);
        var states = new List<StepState>();
        int? active = null;
        foreach (var stage in plan.Steps.Select(s => s.Stage).Distinct().OrderBy(x => x))
        {
            var inStage = plan.Steps.Where(s => s.Stage == stage).ToList();
            var satisfiedAll = inStage.All(s => IsSatisfied(s, responses));
            if (!satisfiedAll && active is null) active = stage;
        }
        foreach (var step in plan.Steps.OrderBy(s => s.Stage))
        {
            var mine = responses.Where(r => r.StepKey == step.Key).ToList();
            StepStatus status;
            if (mine.Any(r => !r.Approved)) status = StepStatus.Rejected;
            else if (IsSatisfied(step, responses)) status = StepStatus.Satisfied;
            else if (active == step.Stage) status = StepStatus.Open;
            else status = StepStatus.Waiting;
            states.Add(new StepState(step, status, mine, status is StepStatus.Open or StepStatus.Waiting ? Outstanding(step, responses) : []));
        }
        var overall = anyReject ? PlanStatus.Rejected : active is null ? PlanStatus.Approved : PlanStatus.InProgress;
        return new PlanState(overall, overall == PlanStatus.InProgress ? active : null, states);
    }

    public static bool IsSatisfied(PlanStep step, IReadOnlyList<PlanResponse> responses)
    {
        var approvals = responses.Where(r => r.StepKey == step.Key && r.Approved).ToList();
        if (step.Mode == StepModes.FirstResponse) return approvals.Count > 0;
        var slots = step.Primaries;
        return slots.Count > 0 && slots.All(p => Covers(approvals, p.UserId));
    }

    private static bool Covers(IEnumerable<PlanResponse> approvals, Guid slotOwner) => approvals.Any(r => r.UserId == slotOwner || r.OnBehalfOfUserId == slotOwner);

    /// <summary>The people who still have to respond: for an all-required step the members who have not been covered; for a first-response step everyone named.</summary>
    public static IReadOnlyList<PlanApprover> Outstanding(PlanStep step, IReadOnlyList<PlanResponse> responses)
    {
        var approvals = responses.Where(r => r.StepKey == step.Key && r.Approved).ToList();
        if (step.Mode == StepModes.FirstResponse) return approvals.Count > 0 ? [] : step.Approvers;
        return step.Primaries.Where(p => !Covers(approvals, p.UserId)).ToList();
    }

    /// <summary>
    /// Decides where an approval from <paramref name="userId"/> is recorded, given the capacities in which they may act on the step today (their own seat, the seat of a member who delegated to them, or as a backup).
    /// A person responds once to a step. For an all-required step each response covers one named member, tried in this order: their own seat, a seat delegated to them, then (where the step allows it) the first
    /// member still outstanding as a backup. For a first-response step any capacity settles the step; the response records who it was on behalf of when that is clear.
    /// </summary>
    public static Placement Place(ApprovalPlan plan, IReadOnlyList<PlanResponse> responses, string stepKey, Guid userId, IReadOnlyList<PlanApprover> capacities)
    {
        var step = plan.Step(stepKey);
        if (step is null) return Placement.No("step_unknown", "That step is not part of this plan.");
        var state = Evaluate(plan, responses);
        var s = state.Steps.First(x => x.Step.Key == stepKey);
        if (s.Status != StepStatus.Open) return Placement.No("step_not_open", s.Status == StepStatus.Waiting ? "An earlier step has to be completed first." : "This step has already been settled.");
        if (capacities.Count == 0) return Placement.No("not_a_participant", "You are not one of the people who can respond to this step.");
        if (responses.Any(r => r.StepKey == stepKey && r.UserId == userId)) return Placement.No("already_responded", "You have already responded to this step.");

        if (step.Mode == StepModes.FirstResponse)
        {
            var delegated = capacities.FirstOrDefault(c => c.Role == ParticipantRoles.Delegate);
            if (capacities.Any(c => c.Role == ParticipantRoles.Primary)) return Placement.Ok(ParticipantRoles.Primary);
            if (delegated is not null) return Placement.Ok(ParticipantRoles.Delegate, delegated.OnBehalfOfUserId, delegated.OnBehalfOfName);
            var p = step.Primaries;
            return p.Count == 1 ? Placement.Ok(ParticipantRoles.Backup, p[0].UserId, p[0].Name) : Placement.Ok(ParticipantRoles.Backup);
        }

        var outstanding = s.Outstanding;
        if (capacities.Any(c => c.Role == ParticipantRoles.Primary) && outstanding.Any(o => o.UserId == userId)) return Placement.Ok(ParticipantRoles.Primary);
        foreach (var d in capacities.Where(c => c.Role == ParticipantRoles.Delegate))
        {
            var owner = outstanding.FirstOrDefault(o => o.UserId == d.OnBehalfOfUserId);
            if (owner is not null) return Placement.Ok(ParticipantRoles.Delegate, owner.UserId, owner.Name);
        }
        if (capacities.Any(c => c.Role == ParticipantRoles.Backup))
        {
            if (!step.AllowBackup) return Placement.No("backup_not_allowed", "This step needs its named approvers to respond; a backup cannot stand in.");
            var first = outstanding.Where(o => o.UserId != userId).OrderBy(o => o.Name, StringComparer.Ordinal).FirstOrDefault();
            if (first is not null) return Placement.Ok(ParticipantRoles.Backup, first.UserId, first.Name);
        }
        return outstanding.Count == 0 ? Placement.No("slot_covered", "Every required approver of this step has been covered.") : Placement.No("not_a_required_approver", "This step is waiting on other named approvers.");
    }
}
