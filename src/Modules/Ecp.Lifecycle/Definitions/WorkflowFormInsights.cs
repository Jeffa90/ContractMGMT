using System.Text.Json.Nodes;
using Ecp.Approvals.Definitions;
using Ecp.Configuration;
using Ecp.Configuration.Forms;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle.Definitions;

/// <summary>
/// What a workflow draft would do, in words: every move with who may make it and what must be true first; what differs from the release in force; and what already exists that it would or would not touch. Contracts are pinned
/// to the release they were created under, so a new release never changes them.
/// </summary>
internal sealed class WorkflowFormInsight(string kind, LifecycleDbContext db) : IFormInsight
{
    public string Kind => kind;

    public async Task<FormInsight> AnalyseAsync(Guid organisationId, JsonObject draft, JsonObject? inForce, CancellationToken ct = default)
    {
        var (definition, _) = WorkflowDefinitionParser.Parse(draft.ToJsonString());
        if (definition is null) return new FormInsight([], ["The draft cannot be read yet. Fix the fields marked above."]);

        var moves = definition.Transitions.Select(t => (IReadOnlyList<string>)
        [
            string.Join(", ", t.From.Select(definition.DisplayOf)), t.Display, definition.DisplayOf(t.To), t.Permission,
            t.Guards.Count == 0 ? "-" : string.Join(", ", t.Guards), t.Role == "plain" ? "" : t.Role,
        ]).ToList();
        var previews = new List<InsightTable> { new("What the workflow does", "Read each line as: from these stages, this move, goes to this stage, needing this permission and these checks.", ["From", "Move", "To", "Permission", "Checks", "Special"], moves) };

        var impact = new List<string>();
        WorkflowDefinition? current = null;
        if (inForce is not null) current = WorkflowDefinitionParser.Parse(inForce.ToJsonString()).Definition;
        if (current is null) impact.Add("Nothing is in force yet. Publishing makes this the workflow for every new contract.");
        else
        {
            impact.Add("Contracts that already exist keep the release they were created under; this change applies to contracts created after it is published.");
            var added = definition.States.Select(s => s.Code).Except(current.States.Select(s => s.Code)).ToList();
            var removed = current.States.Select(s => s.Code).Except(definition.States.Select(s => s.Code)).ToList();
            if (added.Count > 0) impact.Add("New stages: " + string.Join(", ", added.Select(definition.DisplayOf)) + ".");
            if (removed.Count > 0) impact.Add("Stages no longer in the workflow: " + string.Join(", ", removed.Select(current.DisplayOf)) + ". Contracts already in them stay as they are and finish under the earlier release.");
            var newMoves = definition.Transitions.Select(t => t.Code).Except(current.Transitions.Select(t => t.Code)).ToList();
            var goneMoves = current.Transitions.Select(t => t.Code).Except(definition.Transitions.Select(t => t.Code)).ToList();
            if (newMoves.Count > 0) impact.Add("New moves: " + string.Join(", ", newMoves) + ".");
            if (goneMoves.Count > 0) impact.Add("Moves removed: " + string.Join(", ", goneMoves) + ".");
            foreach (var t in definition.Transitions)
                if (current.Transition(t.Code) is { } before && (before.Permission != t.Permission || before.To != t.To || !before.Guards.SequenceEqual(t.Guards)))
                    impact.Add($"The move '{t.Display}' changes: " + string.Join("; ", new[]
                    {
                        before.Permission != t.Permission ? $"permission {before.Permission} to {t.Permission}" : null,
                        before.To != t.To ? $"goes to {definition.DisplayOf(t.To)} instead of {current.DisplayOf(before.To)}" : null,
                        !before.Guards.SequenceEqual(t.Guards) ? "its checks change" : null,
                    }.Where(x => x is not null)) + ".");
            if (added.Count + removed.Count + newMoves.Count + goneMoves.Count == 0 && impact.Count == 1) impact.Add("The stages and moves are the same as the release in force.");
        }

        if (kind == WorkflowKinds.ContractLifecycle)
        {
            var approvals = await db.Approvals.AsNoTracking().CountAsync(a => a.OrganisationId == organisationId && a.Status == ApprovalStatus.Pending, ct);
            if (approvals > 0) impact.Add($"{approvals} approval(s) are waiting now. They are decided under the rules in force when they were submitted.");
            var legal = await db.LegalReviews.AsNoTracking().CountAsync(r => r.OrganisationId == organisationId && (r.Status == LegalReviewStatus.AwaitingLegal || r.Status == LegalReviewStatus.InReview || r.Status == LegalReviewStatus.ReworkRequired), ct);
            if (legal > 0) impact.Add($"{legal} Legal review(s) are under way and continue as they are.");
        }
        else
        {
            var open = await db.Variations.AsNoTracking().CountAsync(v => v.OrganisationId == organisationId && v.IsOpen, ct);
            if (open > 0) impact.Add($"{open} variation(s) are open and continue under the release they were raised under.");
        }
        return new FormInsight(previews, impact);
    }
}

/// <summary>What an approval matrix draft would do: each row as a sentence, what differs from the matrix in force, and which workflows and waiting approvals depend on it. The simulator tries a sample submission against the draft.</summary>
internal sealed class MatrixFormInsight(LifecycleDbContext db, IConfigurationReleases releases) : IFormInsight
{
    public string Kind => Ecp.Approvals.ApprovalsModule.MatrixKind;

    public async Task<FormInsight> AnalyseAsync(Guid organisationId, JsonObject draft, JsonObject? inForce, CancellationToken ct = default)
    {
        var set = MatrixParser.Parse(draft.ToJsonString()).Set;
        if (set is null) return new FormInsight([], ["The draft cannot be read yet. Fix the fields marked above."]);
        var previews = new List<InsightTable>();
        foreach (var m in set.Matrices)
        {
            var rows = m.Rows.OrderByDescending(r => r.Priority).Select(r => (IReadOnlyList<string>)
            [
                r.Code, r.Kind == RowKinds.Exception ? $"Exception ({r.Effect})" : "Base", r.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture), When(r.When, m.Currency),
                string.Join(r.Flow == RowFlows.Parallel ? " and, at the same time, " : " then ", r.Steps.Select(s => $"{s.Display} ({s.ApproverGroup}{(s.Mode == StepModes.AllRequired ? ", everyone must respond" : "")}{(s.TimeoutDays is { } d ? $", escalates after {d} days" : "")})")),
            ]).ToList();
            previews.Add(new InsightTable($"{m.Name} ({m.Code})", $"Values are in {m.Currency}. The highest priority row that fits decides.", ["Row", "Kind", "Priority", "Applies when", "Who approves"], rows));
        }

        var impact = new List<string>();
        var current = inForce is null ? null : MatrixParser.Parse(inForce.ToJsonString()).Set;
        if (current is null) impact.Add("No matrix is in force yet. Publishing it lets a workflow name one of these matrices.");
        else
        {
            var gone = current.Matrices.Select(m => m.Code).Except(set.Matrices.Select(m => m.Code), StringComparer.OrdinalIgnoreCase).ToList();
            var fresh = set.Matrices.Select(m => m.Code).Except(current.Matrices.Select(m => m.Code), StringComparer.OrdinalIgnoreCase).ToList();
            if (fresh.Count > 0) impact.Add("New matrices: " + string.Join(", ", fresh) + ".");
            if (gone.Count > 0) impact.Add("Matrices removed: " + string.Join(", ", gone) + ".");
            foreach (var m in set.Matrices)
                if (current.Find(m.Code) is { } before)
                {
                    var addedRows = m.Rows.Select(r => r.Code).Except(before.Rows.Select(r => r.Code)).Count();
                    var removedRows = before.Rows.Select(r => r.Code).Except(m.Rows.Select(r => r.Code)).Count();
                    var changedRows = m.Rows.Count(r => before.Rows.FirstOrDefault(b => b.Code == r.Code) is { } b && System.Text.Json.JsonSerializer.Serialize(b) != System.Text.Json.JsonSerializer.Serialize(r));
                    if (addedRows + removedRows + changedRows > 0) impact.Add($"{m.Name}: {addedRows} row(s) added, {removedRows} removed, {changedRows} changed.");
                }
            // A workflow that names a matrix this draft no longer holds would block every submission.
            foreach (var wk in new[] { WorkflowKinds.ContractLifecycle, WorkflowKinds.Variation })
                if (await releases.LatestAsync(wk, organisationId, ct) is { } wf && WorkflowDefinitionParser.Parse(wf.ContentJson).Definition?.Settings.Approval.Matrix is { Length: > 0 } named && set.Find(named) is null)
                    impact.Add($"The published {(wk == WorkflowKinds.Variation ? "variation" : "contract")} workflow names the matrix '{named}', which this draft does not contain, so submissions would be blocked.");
        }
        var pending = await db.Approvals.AsNoTracking().CountAsync(a => a.OrganisationId == organisationId && a.Status == ApprovalStatus.Pending, ct);
        impact.Add(pending > 0 ? $"{pending} approval(s) are waiting now. Each keeps the plan it was given when it was submitted; this change affects submissions made after it is published."
                              : "No approval is waiting. This change affects submissions made after it is published.");
        return new FormInsight(previews, impact, $"/Admin/Approvals/Simulate?organisationId={organisationId}&source=draft");
    }

    private static string When(RowConditions w, string currency)
    {
        var parts = new List<string>();
        if (w.ValueMin is not null || w.ValueMax is not null)
            parts.Add((w.ValueMin, w.ValueMax) switch
            {
                ({ } lo, { } hi) => $"value from {lo:0.##} up to {hi:0.##} {currency}", ({ } lo, null) => $"value of {lo:0.##} {currency} or more", (null, { } hi) => $"value under {hi:0.##} {currency}", _ => ""
            });
        void List(string label, IReadOnlyList<string> items) { if (items.Count > 0) parts.Add($"{label} {string.Join(" or ", items)}"); }
        List("category", w.Categories); List("type", w.ContractTypes); List("legal entity", w.LegalEntities); List("business unit", w.BusinessUnits); List("site", w.Sites);
        List("classified", w.Classifications); List("risk", w.RiskLevels); List("counterparty type", w.SupplierTypes);
        foreach (var c in w.Custom) parts.Add($"{c.Field} {c.Operator} {string.Join(", ", c.Values)}");
        return parts.Count == 0 ? "always" : string.Join("; ", parts);
    }
}
