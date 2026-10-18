namespace Ecp.Recovery;

/// <summary>A flat, machine-readable shape of the dashboard for monitoring that polls it. It carries exactly what an authorised reader sees on the page (technical detail only for those who may see it) and nothing more.</summary>
public static class DashboardExport
{
    public static object Of(OperationsDashboard d) => new
    {
        generatedUtc = d.GeneratedUtc, correlationId = d.CorrelationId, overall = OpsStatuses.Label(d.Overall), platformVersion = d.PlatformVersion, environmentMode = d.EnvironmentMode,
        maintenance = new { d.Maintenance.Enabled, d.Maintenance.Cause },
        panels = d.Panels.Select(p => new
        {
            p.Key, status = OpsStatuses.Label(p.Status),
            items = p.Items.Select(i => new { i.Code, i.Title, status = OpsStatuses.Label(i.Status), i.Explanation, i.Detail, lastUtc = i.LastUtc, i.CorrelationId, i.NextStep }),
        }),
        failures = d.Failures.Select(f => new { f.Code, f.Kind, f.Status, f.Summary, whenUtc = f.WhenUtc, f.CorrelationId, f.NextStep, f.RetryValid }),
        coverage = d.Coverage, metrics = d.Metrics,
    };
}
