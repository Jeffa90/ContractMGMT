using Ecp.Operations;
using Ecp.SharedKernel;

namespace Ecp.Host.Logging;

/// <summary>R-LOG-01: the durable log file can be written, is outside the application folder, and says where. Never prints a credential.</summary>
public sealed class LoggingReadinessCheck(LoggingSettings settings, StartupFacts facts) : IReadinessCheck
{
    public string Id => "R-LOG-01";
    private const string Name = "Durable application log";

    public Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        var issues = new List<string>(settings.Problems());
        try
        {
            Directory.CreateDirectory(settings.Directory);
            var probe = Path.Combine(settings.Directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x"); File.Delete(probe);
        }
        catch (Exception) { issues.Add("the log folder cannot be written"); }
        if (facts.Mode == EcpMode.Production && facts.ApplicationDirectory.Length > 0
            && Path.GetFullPath(settings.Directory).StartsWith(Path.GetFullPath(facts.ApplicationDirectory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            issues.Add("the log folder is inside the application folder, which a redeploy replaces");
        var where = $"Rolling daily file log in {settings.Directory} (keeps {settings.RetainedFiles} files, {settings.FileSizeLimitBytes / (1024 * 1024)} MB each).";
        return Task.FromResult(issues.Count == 0
            ? new ReadinessResult(Id, Name, ReadinessStatus.Healthy, where)
            : new ReadinessResult(Id, Name, facts.Mode == EcpMode.Production ? ReadinessStatus.Unhealthy : ReadinessStatus.Degraded, where + " Attention: " + string.Join("; ", issues) + "."));
    }
}

/// <summary>R-LOG-02: the optional log destinations. Configured is not delivered: both are NOT VERIFIED here, and Application Insights has no sink in this build.</summary>
public sealed class LogDestinationsReadinessCheck(LoggingSettings settings) : IReadinessCheck
{
    public string Id => "R-LOG-02";

    public Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        if (settings.ApplicationInsightsConnectionRef.Length > 0) notes.Add("Application Insights is configured but this build has no telemetry sink for it: NOT VERIFIED, nothing is sent");
        if (settings.FatalEmail.Enabled) notes.Add("fatal-error email is configured: NOT VERIFIED until delivery has been proved to a real mailbox");
        return Task.FromResult(new ReadinessResult(Id, "Optional log destinations", ReadinessStatus.Healthy,
            notes.Count == 0 ? "No optional destination is configured; the durable file log is the record." : string.Join("; ", notes) + "."));
    }
}
