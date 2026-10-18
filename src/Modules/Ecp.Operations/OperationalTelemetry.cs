using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Ecp.Operations.Domain;
using Microsoft.Extensions.Logging;

namespace Ecp.Operations;

/// <summary>
/// The platform's operational metrics (docs/OPERATIONS_DASHBOARD.md, "Observability"). They use the standard .NET <see cref="Meter"/>, which any OpenTelemetry or other collector can subscribe to without a package in
/// the platform; the local snapshot below is the in-process reader used by the dashboard, the support bundle and the tests. <b>No exporter is configured or verified here</b>: OpenTelemetry, syslog and SIEM sinks are INT-04
/// and are Not Verified. Metric tags are fixed words (kind, status, outcome); no tenant, person, record or error text is ever a tag.
/// </summary>
public static class OperationalTelemetry
{
    public const string MeterName = "Ecp.Operations";
    public static readonly Meter Meter = new(MeterName, "1.0");

    /// <summary>Backups, restores, validations, upgrades and rehearsals, by kind and final status.</summary>
    public static readonly Counter<long> OperationsFinished = Meter.CreateCounter<long>("ecp.operations.finished", description: "Operations finished, by kind and status.");
    /// <summary>One increment per background-job cycle, by outcome (Completed, Paused, Failed).</summary>
    public static readonly Counter<long> JobCycles = Meter.CreateCounter<long>("ecp.jobs.cycles", description: "Background-job cycles, by outcome.");
    public static readonly Counter<long> DashboardBuilds = Meter.CreateCounter<long>("ecp.dashboard.builds", description: "Operations dashboard builds, by overall status.");

    private static readonly ConcurrentDictionary<(string Name, string Tag), long> Gauges = new();
    static OperationalTelemetry()
    {
        Meter.CreateObservableGauge("ecp.outbox.items", () => Read("outbox"), description: "Outbox deliveries by status, from the last dashboard build.");
        Meter.CreateObservableGauge("ecp.jobs.runs", () => Read("jobs"), description: "Background-job runs by state, from the last dashboard build.");
        Meter.CreateObservableGauge("ecp.leases", () => Read("leases"), description: "Operation leases by state, from the last dashboard build.");
        Meter.CreateObservableGauge("ecp.health.panels", () => Read("health"), description: "Dashboard panels by status, from the last dashboard build.");
    }

    private static IEnumerable<Measurement<long>> Read(string name)
        => Gauges.Where(g => g.Key.Name == name).OrderBy(g => g.Key.Tag, StringComparer.Ordinal).Select(g => new Measurement<long>(g.Value, new KeyValuePair<string, object?>("state", g.Key.Tag))).ToList();

    /// <summary>Records the latest value of a gauge. Called when the dashboard is built.</summary>
    public static void SetGauge(string name, string tag, long value) => Gauges[(name, tag)] = value;

    /// <summary>The latest gauge values, for the dashboard, the bundle and tests. Deterministic order.</summary>
    public static IReadOnlyDictionary<string, long> GaugeSnapshot()
        => Gauges.OrderBy(g => g.Key.Name, StringComparer.Ordinal).ThenBy(g => g.Key.Tag, StringComparer.Ordinal).ToDictionary(g => g.Key.Name + "." + g.Key.Tag, g => g.Value);
}

/// <summary>
/// Structured operational events. Every event carries the correlation id so a line in the log, a row on the dashboard and an audit event can be matched. Parameters are fixed words, counts and ids only. Failures are logged
/// as warnings so they reach the support bundle's redacted recent-lines buffer.
/// </summary>
public static class OperationalLog
{
    public static readonly EventId OperationFinishedId = new(7001, "ecp.operation.finished");
    public static readonly EventId BackgroundCycleId = new(7002, "ecp.jobs.cycle");
    public static readonly EventId DashboardBuiltId = new(7003, "ecp.dashboard.built");
    public static readonly EventId SupportBundleId = new(7004, "ecp.supportbundle.generated");

    private static readonly Action<ILogger, string, string, string, Exception?> OperationOk = LoggerMessage.Define<string, string, string>(LogLevel.Information, OperationFinishedId,
        "Operation {Kind} finished: {Status} (correlation {CorrelationId}).");
    private static readonly Action<ILogger, string, string, string, Exception?> OperationBad = LoggerMessage.Define<string, string, string>(LogLevel.Warning, OperationFinishedId,
        "Operation {Kind} finished: {Status} (correlation {CorrelationId}).");
    private static readonly Action<ILogger, int, int, bool, Exception?> Cycle = LoggerMessage.Define<int, int, bool>(LogLevel.Information, BackgroundCycleId,
        "Background cycle: {Tenants} tenant(s) run, {Failed} failed, paused for maintenance: {Paused}.");
    private static readonly Action<ILogger, int, int, bool, Exception?> CycleBad = LoggerMessage.Define<int, int, bool>(LogLevel.Warning, BackgroundCycleId,
        "Background cycle: {Tenants} tenant(s) run, {Failed} failed, paused for maintenance: {Paused}.");
    private static readonly Action<ILogger, string, int, int, string, Exception?> Dashboard = LoggerMessage.Define<string, int, int, string>(LogLevel.Information, DashboardBuiltId,
        "Operations dashboard built: overall {Overall}, {Failed} failed and {NotVerified} not verified panel(s) (correlation {CorrelationId}).");
    private static readonly Action<ILogger, int, string, Exception?> Bundle = LoggerMessage.Define<int, string>(LogLevel.Information, SupportBundleId,
        "Support bundle generated: {Entries} entries (correlation {CorrelationId}).");

    public static void OperationFinished(ILogger log, string kind, string status, string correlationId)
    {
        OperationalTelemetry.OperationsFinished.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("status", status));
        if (status is OperationStatuses.Failed or OperationStatuses.Incomplete or OperationStatuses.NotVerified) OperationBad(log, kind, status, correlationId, null);
        else OperationOk(log, kind, status, correlationId, null);
    }

    public static void BackgroundCycle(ILogger log, int tenants, int failed, bool paused)
    {
        OperationalTelemetry.JobCycles.Add(1, new KeyValuePair<string, object?>("outcome", paused ? "Paused" : failed > 0 ? "Failed" : "Completed"));
        if (failed > 0) CycleBad(log, tenants, failed, paused, null); else Cycle(log, tenants, failed, paused, null);
    }

    public static void DashboardBuilt(ILogger log, string overall, int failedPanels, int notVerifiedPanels, string correlationId)
    {
        OperationalTelemetry.DashboardBuilds.Add(1, new KeyValuePair<string, object?>("overall", overall));
        Dashboard(log, overall, failedPanels, notVerifiedPanels, correlationId, null);
    }

    public static void SupportBundleGenerated(ILogger log, int entries, string correlationId) => Bundle(log, entries, correlationId, null);
}
