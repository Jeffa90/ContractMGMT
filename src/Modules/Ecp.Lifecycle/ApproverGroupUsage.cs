using Ecp.Approvals;
using Ecp.Identity;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

/// <summary>
/// Says where an approver group is relied on, so that a group cannot be emptied or retired from under work that needs it: the approval matrices in force (a step, its backup or its escalation names the group), a workflow that
/// names it as its single approver group, and approvals that are waiting for it now. Reads published documents and counts only; it changes nothing.
/// </summary>
internal sealed class LifecycleApproverGroupUsage(IApprovalMatrixStore matrices, IOrganisationStructureService organisations, IWorkflowDefinitions workflows, LifecycleDbContext db) : IApproverGroupUsageSource
{
    public async Task<IReadOnlyList<GroupUsageLine>> UsageAsync(string groupKey, CancellationToken ct = default)
    {
        var lines = new List<GroupUsageLine>();
        foreach (var org in await organisations.ListOrganisationsAsync(ct))
        {
            if (await matrices.TryLoadPublishedAsync(org.Id, ct) is { } loaded)
                foreach (var matrix in loaded.Set.Matrices)
                {
                    var steps = matrix.Rows.SelectMany(r => r.Steps)
                        .Count(s => string.Equals(s.ApproverGroup, groupKey, StringComparison.Ordinal) || string.Equals(s.BackupGroup, groupKey, StringComparison.Ordinal) || string.Equals(s.EscalateTo, groupKey, StringComparison.Ordinal));
                    if (steps > 0) lines.Add(new GroupUsageLine("Matrix", $"Approval matrix “{matrix.Name}” for {org.Name}", steps));
                }
            try
            {
                var current = await workflows.CurrentAsync(WorkflowKinds.ContractLifecycle, org.Id, ct);
                if (string.Equals(current.Definition.Settings.Approval.ApproverGroupKey, groupKey, StringComparison.Ordinal))
                    lines.Add(new GroupUsageLine("Workflow", $"The contract workflow for {org.Name} names it as its approver group", 1));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* an organisation without a workflow relies on nothing here */ }
        }
        var pending = await db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Pending).Select(a => new { a.ApproverGroupKey, a.ActiveGroupKeys }).ToListAsync(ct);
        var waiting = pending.Count(a => a.ActiveGroupKeys is { Length: > 0 } g ? g.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(groupKey, StringComparer.Ordinal) : string.Equals(a.ApproverGroupKey, groupKey, StringComparison.Ordinal));
        if (waiting > 0) lines.Add(new GroupUsageLine("InFlight", "Approvals waiting for a decision from this group", waiting));
        return lines;
    }
}
