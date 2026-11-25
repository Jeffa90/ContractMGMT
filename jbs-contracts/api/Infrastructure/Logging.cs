using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace JbsContracts.Api.Infrastructure;

public static class Logging
{
    /// <summary>
    /// Console, plus daily JSON files in the logs folder when one is set (log-YYYYMMDD.txt, as the deploy's failure diagnostics expect).
    /// Never log secrets or contract content: log ids and codes, not what people typed.
    /// </summary>
    public static void AddAppLogging(this WebApplicationBuilder builder, AppSettings settings)
    {
        builder.Services.AddSerilog(config =>
        {
            config.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console();
            if (!string.IsNullOrWhiteSpace(settings.LogDirectory))
            {
                config.WriteTo.File(new CompactJsonFormatter(), Path.Combine(settings.LogDirectory, settings.LogFileName),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: settings.LogRetainedFiles, shared: true);
            }
        });
    }
}
