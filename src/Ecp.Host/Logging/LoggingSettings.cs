using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;

namespace Ecp.Host.Logging;

/// <summary>
/// Where and how the application writes its log (docs/DEPLOYMENT.md, "Logging"). A durable rolling file is always written; the console is for containers. Application Insights and
/// fatal-error email are settings only in this build: they are accepted and validated but nothing is sent to them, and readiness says so (NOT VERIFIED).
/// </summary>
public sealed record LoggingSettings(
    string Directory, string MinimumLevel, int RetainedFiles, long FileSizeLimitBytes, bool Console,
    string ApplicationInsightsConnectionRef, FatalEmailSettings FatalEmail)
{
    public const string ApplicationName = "EnterpriseContractPlatform";

    public static LoggingSettings From(IConfiguration config, string dataDirectory)
    {
        var section = config.GetSection("Ecp:Logging");
        var configured = section["Directory"];
        var email = section.GetSection("FatalEmail");
        return new LoggingSettings(
            string.IsNullOrWhiteSpace(configured) ? Path.Combine(dataDirectory, "logs") : configured.Trim(),
            section["MinimumLevel"] is { Length: > 0 } level ? level : "Information",
            Math.Clamp(section.GetValue("RetainedFiles", 31), 1, 3650),
            Math.Clamp(section.GetValue("FileSizeLimitBytes", 100L * 1024 * 1024), 1024 * 1024, 2L * 1024 * 1024 * 1024),
            section.GetValue("Console", true),
            section["ApplicationInsightsConnectionRef"] ?? "",
            new FatalEmailSettings(email.GetValue("Enabled", false), email["To"] ?? "", email["From"] ?? "", email["SmtpHost"] ?? "", email.GetValue("SmtpPort", 25),
                email.GetValue("UseTls", true), email["CredentialRef"] ?? "", Math.Clamp(email.GetValue("MinimumMinutesBetween", 15), 1, 1440)));
    }

    public bool LevelIsValid => Enum.TryParse<Serilog.Events.LogEventLevel>(MinimumLevel, true, out _);

    /// <summary>Problems that make the logging settings unusable. A setting that names a secret must be a reference, never the secret.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!LevelIsValid) problems.Add("Ecp:Logging:MinimumLevel must be Verbose, Debug, Information, Warning, Error or Fatal.");
        if (ApplicationInsightsConnectionRef.Length > 0 && !SecretReference.TryParse(ApplicationInsightsConnectionRef, out _))
            problems.Add("Ecp:Logging:ApplicationInsightsConnectionRef must be a secret reference (env:NAME or file:PATH), never the connection string itself.");
        if (FatalEmail.Enabled)
        {
            if (!System.Net.Mail.MailAddress.TryCreate(FatalEmail.To, out _)) problems.Add("Ecp:Logging:FatalEmail:To must be an email address.");
            if (!System.Net.Mail.MailAddress.TryCreate(FatalEmail.From, out _)) problems.Add("Ecp:Logging:FatalEmail:From must be an email address.");
            if (FatalEmail.SmtpHost.Length == 0) problems.Add("Ecp:Logging:FatalEmail:SmtpHost is required when fatal-error email is enabled.");
            if (FatalEmail.CredentialRef.Length > 0 && !SecretReference.TryParse(FatalEmail.CredentialRef, out _))
                problems.Add("Ecp:Logging:FatalEmail:CredentialRef must be a secret reference, never a password.");
        }
        return problems;
    }
}

public sealed record FatalEmailSettings(bool Enabled, string To, string From, string SmtpHost, int SmtpPort, bool UseTls, string CredentialRef, int MinimumMinutesBetween);
