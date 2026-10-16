using Ecp.Approvals.Definitions;
using Ecp.Approvals.Domain;
using Ecp.Identity.Access;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;

namespace Ecp.Approvals.Resolution;

internal sealed class ApprovalResolver(IApprovalMatrixStore store, IOrganisationStructureService structure, ApprovalParticipants participants) : IApprovalResolver
{
    public async Task<ApprovalResolution> ResolveAsync(ResolutionRequest r, CancellationToken ct = default)
    {
        var trace = new ResolutionTrace();
        var org = await structure.GetOrganisationAsync(r.OrganisationId, ct) ?? throw new ResourceNotFoundException();
        var date = TimeZones.LocalDate(r.AsOfUtc, TimeZones.Find(org.TimeZoneId));
        LoadedMatrices loaded;
        try { loaded = await store.LoadAsync(r.OrganisationId, r.Source, ct); }
        catch (DomainException ex) when (ex.Code is "matrix_not_published" or "matrix_unreadable")
        {
            trace.Add(TraceStages.Matrix, r.MatrixCode, TraceOutcomes.Blocked, ex.Message);
            return Blocked(r, date, trace, [], ex.Code, ex.Message, "none");
        }
        trace.Add(TraceStages.Matrix, "source", TraceOutcomes.Info, $"Reading {loaded.Label}, checksum {loaded.ContentHash[..12]}.");

        var selection = MatrixEvaluator.Select(loaded.Set, r.MatrixCode, r.Facts, date, trace);
        if (!selection.Success) return Blocked(r, date, trace, [], selection.BlockCode!, selection.BlockMessage!, loaded.Label, loaded.ReleaseNumber);

        var steps = new List<PlanStep>();
        var explanations = new List<ApproverExplanation>();
        var problems = new List<string>();
        foreach (var planned in selection.Steps)
        {
            var s = planned.Step;
            trace.Add(TraceStages.Step, planned.Key, TraceOutcomes.Info, $"Step '{s.Display}' (stage {planned.Stage + 1}, {s.Mode}): approver group {s.ApproverGroup}" + (s.BackupGroup is null ? "." : $", backup group {s.BackupGroup}."));
            var part = await participants.ForStepAsync(new ApprovalParticipants.StepSpec(planned.Key, s.Display, s.ApproverGroup, s.BackupGroup, s.Mode, s.AllowBackup), r.OrganisationId, r.Resource, r.SubmitterUserId, r.AsOfUtc, trace, ct);
            explanations.AddRange(part.Explanations);
            if (!ApprovalParticipants.IsSatisfiable(s.Mode, part.Participants))
            {
                var why = part.Explanations.Where(e => !e.Included && e.Role != ParticipantRoles.Delegate).Take(6).Select(e => $"{e.Name}: {string.Join(" ", e.Reasons)}").ToList();
                problems.Add(why.Count == 0
                    ? $"Step '{s.Display}': nobody holds the approver group '{s.ApproverGroup}'. An administrator must assign an approver and a backup."
                    : $"Step '{s.Display}': nobody who holds group '{s.ApproverGroup}' can approve{(s.Mode == StepModes.AllRequired ? " (this step needs its named approvers)" : "")}. {string.Join(" ", why)}");
                trace.Add(TraceStages.Step, planned.Key, TraceOutcomes.Blocked, problems[^1]);
            }
            steps.Add(new PlanStep(planned.Key, planned.RowCode, s.Display, planned.Stage, s.ApproverGroup, s.BackupGroup, s.Mode, s.TimeoutDays, s.EscalateTo, s.AllowBackup, part.Participants));
        }
        if (problems.Count > 0)
            return Blocked(r, date, trace, explanations, "missing_approvers", "Nobody can approve this submission as the matrix is set up. " + string.Join(" ", problems), loaded.Label, loaded.ReleaseNumber, selection.RowCodes);

        var plan = new ApprovalPlan(1, selection.Matrix!.Code, selection.Matrix.Name, loaded.ReleaseNumber, loaded.ReleaseId, loaded.ContentHash, r.AsOfUtc, date, selection.Matrix.Currency, r.Facts.Currency, r.Facts.Value,
            selection.ConvertedValue, selection.RowCodes, steps);
        trace.Add(TraceStages.Result, "resolved", TraceOutcomes.Selected, $"Resolved: {steps.Count} step(s) in {steps.Select(x => x.Stage).Distinct().Count()} stage(s) from {string.Join(" + ", selection.RowCodes)}.");
        return new ApprovalResolution(true, null, null, plan, trace.Entries, explanations, plan.MatrixCode, loaded.ReleaseNumber, loaded.Label, date, r.AsOfUtc, selection.RowCodes);
    }

    private static ApprovalResolution Blocked(ResolutionRequest r, DateOnly date, ResolutionTrace trace, IReadOnlyList<ApproverExplanation> explanations, string code, string message, string label, int? release = null, IReadOnlyList<string>? rows = null)
    {
        trace.Add(TraceStages.Result, code, TraceOutcomes.Blocked, message);
        return new ApprovalResolution(false, code, message, null, trace.Entries, explanations, r.MatrixCode, release, label, date, r.AsOfUtc, rows ?? []);
    }

    public async Task<StepParticipation> ParticipantsAsync(ApprovalPlan plan, string stepKey, ResourceDescriptor resource, Guid? submitterUserId, DateTime asOfUtc, CancellationToken ct = default)
    {
        var step = plan.Step(stepKey) ?? throw new DomainException("step_unknown", "That step is not part of this plan.");
        var org = resource.OrganisationId ?? throw new DomainException("organisation_required", "The record's organisation is required.");
        return await participants.ForStepAsync(new ApprovalParticipants.StepSpec(step.Key, step.Display, step.ApproverGroup, step.BackupGroup, step.Mode, step.AllowBackup), org, resource, submitterUserId, asOfUtc, null, ct);
    }
}
