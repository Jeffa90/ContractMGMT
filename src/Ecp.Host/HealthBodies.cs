using Ecp.Operations;
using Ecp.SharedKernel;

namespace Ecp.Host;

/// <summary>
/// What the anonymous health endpoints may say. In Production the readiness answer is the overall status only: no check id, no message, no dependency name (SETUP_AND_READINESS_SPEC section 6). In Demo and Test, which are never
/// exposed to the internet by design, the per-check list is included because the container smoke test and developers rely on it; docs/OPERATIONS_DASHBOARD.md records this as a deliberate, labelled difference.
/// </summary>
public static class HealthBodies
{
    public static object Ready(ReadinessReport report, EcpMode mode)
        => mode == EcpMode.Production
            ? new { status = report.Overall.ToString() }
            : new { status = report.Overall.ToString(), checks = report.Results.Select(r => new { id = r.Id, status = r.Status.ToString(), message = r.Message }).ToList() };
}
