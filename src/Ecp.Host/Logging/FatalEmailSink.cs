using Ecp.SharedKernel.Secrets;
using Serilog.Core;
using Serilog.Events;

namespace Ecp.Host.Logging;

/// <summary>Sends one message. A seam so the sink can be tested without a mail server; the production sender uses the configured SMTP host.</summary>
public interface IFatalEmailSender
{
    void Send(string subject, string body);
}

/// <summary>
/// Emails fatal events to the configured address, at most once per interval, with the message redacted. It is configurable and unit-tested against a fake sender; it has NOT been
/// exercised against a real mail server, and the status is reported as NOT VERIFIED until an operator proves delivery (docs/DEPLOYMENT.md).
/// </summary>
public sealed class FatalEmailSink(IFatalEmailSender sender, TimeSpan minimumInterval, Func<DateTime> utcNow) : ILogEventSink
{
    private readonly object _gate = new();
    private DateTime _lastSent = DateTime.MinValue;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level != LogEventLevel.Fatal) return;
        lock (_gate)
        {
            var now = utcNow();
            if (now - _lastSent < minimumInterval) return;
            _lastSent = now;
        }
        try
        {
            var message = LogRedactor.Redact(logEvent.RenderMessage());
            var exception = logEvent.Exception is null ? "" : "\n\n" + LogRedactor.Redact(logEvent.Exception.GetType().FullName + ": " + logEvent.Exception.Message);
            var correlation = logEvent.Properties.TryGetValue("CorrelationId", out var c) ? c.ToString().Trim('"') : "";
            sender.Send($"[{LoggingSettings.ApplicationName}] Fatal error", $"{logEvent.Timestamp:u}\n{message}{exception}\nCorrelation: {correlation}");
        }
        catch (Exception) { /* a failing mail server must never take the application down; the file sink still has the event */ }
    }
}

internal sealed class SmtpFatalEmailSender(FatalEmailSettings settings, ISecretProvider secrets) : IFatalEmailSender
{
    public void Send(string subject, string body)
    {
        using var client = new System.Net.Mail.SmtpClient(settings.SmtpHost, settings.SmtpPort) { EnableSsl = settings.UseTls, Timeout = 15000 };
        if (SecretReference.TryParse(settings.CredentialRef, out var reference) && secrets.Resolve(reference) is { Length: > 0 } credential && credential.Split(':', 2) is [var user, var password])
            client.Credentials = new System.Net.NetworkCredential(user, password);   // "user:password" from the secret store; never logged
        client.Send(new System.Net.Mail.MailMessage(settings.From, settings.To, subject, body));
    }
}
