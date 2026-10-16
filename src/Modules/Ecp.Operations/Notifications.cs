using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Operations;

public sealed record EmailMessage(string To, string Subject, string Body);

public enum EmailProviderKind { Disabled, Mock, Smtp }

/// <summary>Non-secret email settings plus secret <em>references</em> for the SMTP user and password (never the values).</summary>
public sealed record EmailSettings(EmailProviderKind Provider, string? From = null, string? Host = null, int Port = 587, bool UseTls = true, string? UserRef = null, string? PasswordRef = null);

public sealed record EmailIssue(string Code, string Message);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public interface IEmailSenderFactory
{
    IReadOnlyList<EmailIssue> Validate(EmailSettings settings, EcpMode mode);
    IEmailSender Create(EmailSettings settings);
}

/// <summary>Demo and Test only: writes each message as a JSON file in the data directory's outbox instead of sending it. Never delivers anything.</summary>
internal sealed class MockEmailSender(string outbox) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outbox);
        var path = Path.Combine(outbox, $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(message), ct);
    }
}

internal sealed class DisabledEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default) => throw new DomainException("email_disabled", "Email is disabled; nothing was sent.");
}

/// <summary>SMTP delivery. Credentials come from secret references resolved at send time and are never logged or stored.</summary>
internal sealed class SmtpEmailSender(EmailSettings settings, ISecretProvider secrets) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.UseTls, Timeout = 15000 };
        if (SecretReference.TryParse(settings.UserRef, out var user) && SecretReference.TryParse(settings.PasswordRef, out var password))
            client.Credentials = new NetworkCredential(secrets.Resolve(user), secrets.Resolve(password));
        using var mail = new MailMessage(settings.From!, message.To, message.Subject, message.Body);
        try { await client.SendMailAsync(mail, ct); }
        catch (SmtpException) { throw new DomainException("email_failed", "The mail server did not accept the message."); }
    }
}

internal sealed class EmailSenderFactory(ISecretProvider secrets, DataDirectoryOptions data) : IEmailSenderFactory
{
    public IReadOnlyList<EmailIssue> Validate(EmailSettings s, EcpMode mode)
    {
        var issues = new List<EmailIssue>();
        if (s.Provider == EmailProviderKind.Mock && mode == EcpMode.Production) issues.Add(new("mock_in_production", "The mock email provider never delivers mail and is refused in Production."));
        if (s.Provider == EmailProviderKind.Disabled && mode == EcpMode.Production) issues.Add(new("email_required", "Production requires a delivering email provider (R-NOT-01)."));
        if (s.Provider == EmailProviderKind.Disabled) return issues;
        if (string.IsNullOrWhiteSpace(s.From) || !System.Net.Mail.MailAddress.TryCreate(s.From, out _)) issues.Add(new("from", "A valid From address is required."));
        if (s.Provider == EmailProviderKind.Smtp)
        {
            if (string.IsNullOrWhiteSpace(s.Host) || s.Host.Contains('/') || s.Host.Contains(' ')) issues.Add(new("host", "Enter the SMTP host name (no scheme or path)."));
            if (s.Port is < 1 or > 65535) issues.Add(new("port", "The SMTP port must be between 1 and 65535."));
            if (!s.UseTls && mode == EcpMode.Production) issues.Add(new("tls", "Production requires TLS for SMTP."));
            foreach (var (label, value) in new[] { ("user", s.UserRef), ("password", s.PasswordRef) })
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!SecretReference.TryParse(value, out var reference)) issues.Add(new($"{label}_ref", $"The SMTP {label} must be a secret reference (env:, file: or keyvault:), never the value."));
                else if (secrets.Resolve(reference) is null && mode == EcpMode.Production) issues.Add(new($"{label}_unresolved", $"The SMTP {label} secret reference does not resolve."));
            }
            if (string.IsNullOrWhiteSpace(s.UserRef) != string.IsNullOrWhiteSpace(s.PasswordRef)) issues.Add(new("credentials", "Set both the SMTP user and password references, or neither."));
        }
        return issues;
    }

    public IEmailSender Create(EmailSettings s) => s.Provider switch
    {
        EmailProviderKind.Mock => new MockEmailSender(Path.Combine(data.Path, "outbox")),
        EmailProviderKind.Smtp => new SmtpEmailSender(s, secrets),
        _ => new DisabledEmailSender()
    };
}
