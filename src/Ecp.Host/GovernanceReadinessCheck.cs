using Ecp.Operations;
using Ecp.SharedKernel;

namespace Ecp.Host;

/// <summary>
/// R-GOV-01: which configuration governance the installation runs under. It never fails, because simplified governance is a legitimate choice for a trial, but it always says plainly whether the second-person rule is
/// in force so that nobody mistakes a simplified Test environment for a Production-equivalent one (ADR-0038).
/// </summary>
public sealed class GovernanceReadinessCheck(GovernanceMode governance) : IReadinessCheck
{
    public string Id => "R-GOV-01";

    public Task<ReadinessResult> RunAsync(CancellationToken ct)
        => Task.FromResult(new ReadinessResult(Id, "Configuration governance mode", ReadinessStatus.Healthy,
            $"{governance.Name}: {governance.Label}. {(governance.SecondPersonRequired ? "The second-person rule is in force." : "The second-person rule is NOT in force; this is not Production-equivalent.")}"));
}
