using System.Diagnostics;
using Ecp.Identity;
using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.Notifications.Policy;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecp.Notifications;

public sealed record EmailDelivery(Guid DeliveryId, Guid NotificationId, string To, string Subject, string Body, string CorrelationId);

/// <summary>An email channel. Every provider receives only classification-safe text that the policy produced; none of them is trusted with more.</summary>
public interface IEmailDeliveryProvider
{
    string Key { get; }
    string Label { get; }
    /// <summary>True when nothing is delivered to a real mailbox (the demonstration log and the development capture).</summary>
    bool IsSimulated { get; }
    Task SendAsync(EmailDelivery message, CancellationToken ct = default);
}

public sealed record EmailProviderInfo(string Key, string Label, string Status, bool Available, string Note);

public interface IEmailDeliveryProviderFactory
{
    IReadOnlyList<EmailProviderInfo> Describe(EcpMode mode);
    bool IsSimulated(string key);
}

/// <summary>Demonstration log: writes that a message would have been sent (recipient masked, subject only) and delivers nothing. Demonstration and Test only.</summary>
internal sealed class DemoLogProvider(ILogger<DemoLogProvider> log) : IEmailDeliveryProvider
{
    public string Key => "demo-log";
    public string Label => "Demonstration log (delivers nothing)";
    public bool IsSimulated => true;
    public Task SendAsync(EmailDelivery m, CancellationToken ct = default)
    {
        log.LogInformation("DEMO email not sent. To={To} Subject={Subject} Correlation={Correlation}", Mask(m.To), m.Subject, m.CorrelationId);
        return Task.CompletedTask;
    }
    internal static string Mask(string address) { var at = address.IndexOf('@'); return at <= 1 ? "***" : address[0] + "***" + address[at..]; }
}

/// <summary>Development capture: keeps each email in a mailbox that administrators can read in the application. Demonstration and Test only; delivers nothing.</summary>
internal sealed class DevCaptureProvider(NotificationsDbContext db, ITenantContext tenant, IClock clock) : IEmailDeliveryProvider
{
    public string Key => "dev-capture";
    public string Label => "Development capture mailbox (delivers nothing)";
    public bool IsSimulated => true;
    public async Task SendAsync(EmailDelivery m, CancellationToken ct = default)
    {
        db.Captured.Add(CapturedEmail.Capture(tenant.RequireTenantId(), m.NotificationId, m.DeliveryId, m.To, m.Subject, m.Body, m.CorrelationId, clock.UtcNow));
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// SMTP delivery through the platform's email sender. Credentials are secret references resolved at send time. Implemented; Not Verified against a live mail server in this release.
/// </summary>
internal sealed class SmtpProvider(IEmailSenderFactory factory, IOptions<NotificationsOptions> options) : IEmailDeliveryProvider
{
    public string Key => "smtp";
    public string Label => "SMTP";
    public bool IsSimulated => false;
    public async Task SendAsync(EmailDelivery m, CancellationToken ct = default)
    {
        var settings = options.Value.Smtp;
        if (settings is null || settings.Provider != EmailProviderKind.Smtp) throw new DomainException("provider_not_configured", "SMTP settings are not configured.");
        var issues = factory.Validate(settings, options.Value.Mode);
        if (issues.Count > 0) throw new DomainException("provider_not_configured", "SMTP settings are not usable: " + string.Join(" ", issues.Select(i => i.Message)));
        await factory.Create(settings).SendAsync(new EmailMessage(m.To, m.Subject, m.Body), ct);
    }
}

/// <summary>Microsoft Graph delivery. Scaffolded, Not Verified: there is no tenant registration or credential in this release, so every send fails closed and is retried or dead-lettered visibly.</summary>
internal sealed class GraphProvider : IEmailDeliveryProvider
{
    public string Key => "graph";
    public string Label => "Microsoft Graph (scaffold; not connected)";
    public bool IsSimulated => false;
    public Task SendAsync(EmailDelivery m, CancellationToken ct = default) => throw new DomainException("provider_not_configured", "The Microsoft Graph email provider is not connected in this release.");
}

internal sealed class EmailDeliveryProviderFactory : IEmailDeliveryProviderFactory
{
    public bool IsSimulated(string key) => key is "demo-log" or "dev-capture";

    public IReadOnlyList<EmailProviderInfo> Describe(EcpMode mode)
    {
        var dev = mode != EcpMode.Production;
        return
        [
            new("demo-log", "Demonstration log", dev ? "Mocked" : "Disabled in Production", dev, "Writes a log line; delivers nothing."),
            new("dev-capture", "Development capture mailbox", dev ? "Mocked" : "Disabled in Production", dev, "Keeps each email in an in-application mailbox for administrators; delivers nothing."),
            new("smtp", "SMTP", "Implemented, Not Verified (no live mail server)", true, "Credentials are secret references; TLS is required in Production."),
            new("graph", "Microsoft Graph", "Scaffolded, Not Verified", false, "No tenant registration or credentials in this release.")
        ];
    }
}

internal sealed class DeliveryRunner(
    NotificationsDbContext db, IUnitOfWork uow, ITenantContext tenant, IClock clock, IUserService users, INotificationPolicyStore policies, IOptions<NotificationsOptions> options, IServiceProvider services) : IDeliveryRunner
{
    private IEmailDeliveryProvider Provider(string key)
    {
        if (options.Value.Mode == EcpMode.Production && key is "demo-log" or "dev-capture") throw new DomainException("provider_refused", "The demonstration email providers are refused in Production.");
        return key switch
        {
            "demo-log" => (IEmailDeliveryProvider)ActivatorUtilities.CreateInstance<DemoLogProvider>(services),
            "dev-capture" => ActivatorUtilities.CreateInstance<DevCaptureProvider>(services),
            "smtp" => ActivatorUtilities.CreateInstance<SmtpProvider>(services),
            "graph" => new GraphProvider(),
            _ => throw new DomainException("unknown_provider", $"Email provider '{key}' is not recognised.")
        };
    }

    public async Task<int> DeliverDueAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var due = await db.Deliveries.AsNoTracking().Where(d => d.Channel == DeliveryChannel.Email && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed) && d.NextAttemptUtc <= now)
            .OrderBy(d => d.NextAttemptUtc).Take(options.Value.BatchSize).Select(d => d.Id).ToListAsync(ct);
        var attempted = 0;
        foreach (var id in due)
        {
            await uow.ExecuteAsync(async c =>
            {
                var d = await db.Deliveries.FirstOrDefaultAsync(x => x.Id == id, c);
                if (d is null || d.Status is DeliveryStatus.Sent or DeliveryStatus.DeadLetter) return;
                var n = await db.Notifications.AsNoTracking().FirstAsync(x => x.Id == d.NotificationId, c);
                var policy = await policies.CurrentAsync(n.OrganisationId, c);
                if (options.Value.EmailProvider != d.Provider) d.UseProvider(options.Value.EmailProvider);
                var started = Stopwatch.StartNew();
                var startedUtc = clock.UtcNow;
                string? error = null;
                try
                {
                    var user = await users.GetAsync(d.RecipientUserId, c);
                    if (user is null || user.Status != UserStatus.Active) throw new DomainException("recipient_inactive", "The recipient has no active account.");
                    var body = options.Value.BaseUrl is { Length: > 0 } baseUrl && n.LinkPath is { Length: > 0 } link ? n.Body.Replace(link, baseUrl.TrimEnd('/') + link, StringComparison.Ordinal) : n.Body;
                    await Provider(d.Provider).SendAsync(new EmailDelivery(d.Id, n.Id, user.Email, n.Title, body, d.CorrelationId), c);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { error = ex is DomainException de ? $"{de.Code}: {de.Message}" : ex.GetType().Name + ": " + ex.Message; }
                started.Stop();
                var number = d.Attempts + 1;
                db.Attempts.Add(DeliveryAttempt.Record(tenant.RequireTenantId(), d.Id, number, d.Provider, startedUtc, (int)started.ElapsedMilliseconds, error is null, error, d.CorrelationId));
                if (error is null) d.Succeeded(clock.UtcNow);
                else d.Failed(error, clock.UtcNow, policy.Delivery.MaxAttempts, policy.BackoffFor(number));
                await db.SaveChangesAsync(c);
            }, ct);
            attempted++;
        }
        return attempted;
    }
}
