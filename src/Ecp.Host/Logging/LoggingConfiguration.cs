using Ecp.SharedKernel.Secrets;
using Serilog;
using Serilog.Events;

namespace Ecp.Host.Logging;

public static class LoggingConfiguration
{
    /// <summary>
    /// The one place the application's logging is described: structured events, the shared context (application, version, environment, process, thread, database, correlation, login),
    /// a durable rolling file that survives an application-pool recycle (shared writes, so the old and new worker can overlap), optional console for containers, and the redaction
    /// of secrets from every line before it is written.
    /// </summary>
    public static LoggerConfiguration Apply(LoggerConfiguration logger, LoggingSettings settings, string version, string environment, string databaseIdentifier,
        IHttpContextAccessor? accessor, ISecretProvider secrets, Func<DateTime> utcNow)
    {
        var level = Enum.TryParse<LogEventLevel>(settings.MinimumLevel, true, out var parsed) ? parsed : LogEventLevel.Information;
        logger
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.With(new EcpLogEnricher(version, environment, databaseIdentifier, accessor))
            .WriteTo.File(new RedactingJsonFormatter(), Path.Combine(settings.Directory, "ecp-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: settings.RetainedFiles, fileSizeLimitBytes: settings.FileSizeLimitBytes, rollOnFileSizeLimit: true, shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1));
        if (settings.Console) logger.WriteTo.Console(new RedactingJsonFormatter());
        if (settings.FatalEmail.Enabled && settings.Problems().Count == 0)
            logger.WriteTo.Sink(new FatalEmailSink(new SmtpFatalEmailSender(settings.FatalEmail, secrets), TimeSpan.FromMinutes(settings.FatalEmail.MinimumMinutesBetween), utcNow), LogEventLevel.Fatal);
        return logger;
    }
}
