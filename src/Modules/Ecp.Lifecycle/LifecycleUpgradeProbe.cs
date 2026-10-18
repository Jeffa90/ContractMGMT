using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

/// <summary>
/// Upgrade pre-flight for in-flight lifecycle work (docs/UPGRADES.md). Contracts stay pinned to the workflow release they started under and are never moved by an upgrade; this reports what is mid-way so the owner can
/// choose a quiet time. Counts only: no contract, person or document is named.
/// </summary>
internal sealed class LifecycleUpgradeProbe(LifecycleDbContext db) : IUpgradeProbe
{
    public string Area => "Lifecycle";

    public async Task<IReadOnlyList<UpgradeFinding>> ProbeAsync(CancellationToken ct = default)
    {
        var findings = new List<UpgradeFinding>();
        var signatures = await db.SignatureRequests.AsNoTracking().CountAsync(r => r.Status == SignatureRequestStatus.Sent || r.Status == SignatureRequestStatus.Delivered, ct);
        if (signatures > 0)
            findings.Add(new("Lifecycle", "UPG-LIF-001", UpgradeFindingSeverity.Warning, "Signature requests are waiting for a response",
                "Some contracts have been sent for signature and are waiting. Provider callbacks that arrive while the installation is in maintenance mode are not processed until it is released; they are not lost by the platform.",
                $"{signatures} open signature request(s).", "Prefer a time when no request is waiting, or accept that the replies are processed after the upgrade.", signatures));
        var approvals = await db.Approvals.AsNoTracking().CountAsync(a => a.Status == ApprovalStatus.Pending, ct);
        if (approvals > 0)
            findings.Add(new("Lifecycle", "UPG-LIF-002", UpgradeFindingSeverity.Info, "Approvals are pending",
                "Some approval requests are waiting for a decision. They stay pinned to the approval matrix release they started under and are not changed by an upgrade.", $"{approvals} pending approval(s).", "", approvals));
        var reviews = await db.LegalReviews.AsNoTracking().CountAsync(r => r.Status == LegalReviewStatus.AwaitingLegal || r.Status == LegalReviewStatus.InReview || r.Status == LegalReviewStatus.ReworkRequired, ct);
        var variations = await db.Variations.AsNoTracking().CountAsync(v => v.IsOpen, ct);
        var renewals = await db.Renewals.AsNoTracking().CountAsync(r => r.Status == RenewalStatus.InProgress, ct);
        if (reviews + variations + renewals > 0)
            findings.Add(new("Lifecycle", "UPG-LIF-003", UpgradeFindingSeverity.Info, "Lifecycle work is in progress",
                "Legal reviews, variations or renewals are open. They keep the workflow release they started under (pinned) and are not moved to a newer one by an upgrade.", $"{reviews} legal review(s), {variations} variation(s), {renewals} renewal(s).", "", reviews + variations + renewals));
        return findings;
    }
}
