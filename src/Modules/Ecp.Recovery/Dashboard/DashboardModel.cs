using Ecp.Operations;

namespace Ecp.Recovery;

/// <summary>
/// The four states the operations dashboard shows, always distinct. <see cref="NotVerified"/> means "nobody has looked, or it cannot be looked at here": it is never a pass, and it is never merged into one.
/// </summary>
public enum OpsStatus { Verified, Warning, Failed, NotVerified }

public static class OpsStatuses
{
    /// <summary>Failed wins, then Not Verified, then Warning, then Verified: the same order as the upgrade pre-flight. An empty set is Not Verified.</summary>
    public static OpsStatus Worst(IEnumerable<OpsStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Count == 0) return OpsStatus.NotVerified;
        if (list.Contains(OpsStatus.Failed)) return OpsStatus.Failed;
        if (list.Contains(OpsStatus.NotVerified)) return OpsStatus.NotVerified;
        return list.Contains(OpsStatus.Warning) ? OpsStatus.Warning : OpsStatus.Verified;
    }

    public static string Label(OpsStatus s) => s switch { OpsStatus.Verified => "Verified", OpsStatus.Warning => "Warning", OpsStatus.Failed => "Failed", _ => "Not Verified" };
    public static string Css(OpsStatus s) => s switch { OpsStatus.Verified => "ok", OpsStatus.Warning => "warn", OpsStatus.Failed => "bad", _ => "" };
}

/// <summary>One line on a panel. <see cref="Code"/> is stable. <see cref="Detail"/> is technical and empty unless the caller holds <c>operations.validate</c>.</summary>
public sealed record DashboardItem(string Code, string Title, OpsStatus Status, string Explanation, string Detail = "", DateTime? LastUtc = null, string? CorrelationId = null, string? NextStep = null, string? Link = null);

public sealed record DashboardPanel(string Key, string Title, string Summary, OpsStatus Status, IReadOnlyList<DashboardItem> Items);

/// <summary>A recent failure, or a state that looks stuck, with what the operator may do. <see cref="RetryValid"/> is true only where starting a fresh attempt is valid and safe; restores and upgrades are never retried.</summary>
public sealed record FailureEntry(string Code, string Kind, string Status, string Summary, DateTime? WhenUtc, string CorrelationId, string NextStep, string? Link, bool RetryValid, string? RetryLabel);

public sealed record ModuleVersion(string Module, string Applied, string Compiled, int Pending);

/// <summary>How many of the readiness catalogue's checks the platform can run on the dashboard today. The rest are listed as Not Verified.</summary>
public sealed record CatalogueCoverage(int Total, int RunHere, int RunElsewhere, int NotBuilt);

public sealed record OperationsDashboard(DateTime GeneratedUtc, string CorrelationId, OpsStatus Overall, string PlatformVersion, string EnvironmentMode, string DatabaseEngine, MaintenanceStatus Maintenance,
    IReadOnlyList<DashboardPanel> Panels, IReadOnlyList<FailureEntry> Failures, CatalogueCoverage Coverage, IReadOnlyList<LeaseInfo> Leases, IReadOnlyList<ModuleVersion> Modules, bool DetailVisible,
    IReadOnlyDictionary<string, long> Metrics, string Governance = "")
{
    public DashboardPanel? Panel(string key) => Panels.FirstOrDefault(p => p.Key == key);
}

/// <summary>One entry of the readiness catalogue (requirements/12-operations/SETUP_AND_READINESS_SPEC.md section 4).</summary>
public sealed record CatalogueEntry(string Id, string Title, string Severity, bool Startup);

public static class ReadinessCatalogue
{
    public static readonly IReadOnlyList<CatalogueEntry> Entries =
    [
        new("R-ID-01", "Demo identity disabled and unreachable", "C", true), new("R-ID-02", "OIDC discovery reachable; token validation works", "C", false),
        new("R-ID-03", "Step-up authentication configured for sensitive actions", "W", false), new("R-DB-01", "Database supported, reachable, all migrations applied", "C", true),
        new("R-DB-02", "Connection uses TLS; at-rest encryption attested", "C / W", false), new("R-DB-03", "Runtime principal is least-privilege", "C", false),
        new("R-DB-04", "Backup evidence and last successful restore test recorded", "W", false), new("R-ST-01", "Document storage is persistent", "C", true),
        new("R-ST-02", "Storage round trip under tenant prefix succeeds", "C", false), new("R-ST-03", "Storage encryption attested by the operator", "W", false),
        new("R-SEC-01", "No default or sample credentials present", "C", true), new("R-SEC-02", "All secrets resolve from the secret provider", "C", true),
        new("R-SEC-03", "HTTPS enforced with HSTS; certificate valid", "C", false), new("R-SEC-04", "Data Protection key ring persisted and protected", "C", true),
        new("R-SEC-05", "Setup routes removed", "C", true), new("R-SEC-06", "Security headers and CSP present on a probe", "C", false),
        new("R-SEC-07", "Malware scanner configured and healthy, or risk accepted", "C / W", false), new("R-NOT-01", "Test email delivered", "C", false),
        new("R-CFG-01", "A validated configuration release is published", "C", false), new("R-CFG-02", "Administrators exist; last-administrator protection active", "C", false),
        new("R-OPS-01", "Health endpoints respond; logs flow to the configured sink", "W", false), new("R-OPS-02", "ICU and time-zone data present", "C", true),
        new("R-OPS-03", "Background worker running; job lease acquired; outbox draining", "C", false), new("R-OPS-04", "Maintenance mode off; clock skew within tolerance", "W", false),
        new("R-DEP-D1", "Docker: volumes, image digest, non-root user", "C", false), new("R-DEP-I1", "IIS: hosting bundle, application pool, ACLs", "C", false),
        new("R-DEP-A1", "Azure: managed identity, private storage, references only", "C", false), new("R-DEMO-01", "No demonstration data present", "C", false),
        new("R-AI-01", "AI disabled, or provider approved", "W", false), new("R-MIG-01", "Migration limits and quarantine area configured", "I", false),
    ];

    /// <summary>Catalogue checks that exist but run only in the setup wizard or as the start-up guard, not on the dashboard. The dashboard says so rather than guess a result.</summary>
    public static readonly IReadOnlySet<string> RunElsewhere = new HashSet<string>(StringComparer.Ordinal)
    {
        "R-ID-01", "R-ID-02", "R-ST-01", "R-SEC-01", "R-SEC-02", "R-CFG-01", "R-NOT-01", "R-DEMO-01", "R-OPS-01"
    };
}
