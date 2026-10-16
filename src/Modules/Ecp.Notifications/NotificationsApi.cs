using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.Notifications.Policy;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecp.Notifications;

public sealed class NotificationsOptions
{
    /// <summary>The installation default solution pack, used only for an organisation that has none of its own (<c>Ecp:DefaultPackId</c>); not set unless the installer sets it. An organisation that has published no policy gets the reference policy of its own pack.</summary>
    public string? DefaultPackId { get; set; }
    /// <summary>Installation mode. The demonstration log and the development capture are refused in Production.</summary>
    public EcpMode Mode { get; set; } = EcpMode.Production;
    /// <summary>Email provider: demo-log, dev-capture, smtp or graph. Production accepts only smtp or graph.</summary>
    public string EmailProvider { get; set; } = "dev-capture";
    /// <summary>Prefixed to links in email so they open the right site (for example https://contracts.example.test). Empty keeps links relative.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>Non-secret SMTP settings; the user and password are secret references. Used by the smtp provider.</summary>
    public EmailSettings? Smtp { get; set; }
    public int BatchSize { get; set; } = 100;
    /// <summary>Seconds between background runs in the host (0 turns the timer off; the work can still be run from the administration page). The first run waits one interval.</summary>
    public int JobIntervalSeconds { get; set; } = 60;
}

public sealed record InboxItem(Guid Id, string EventType, string Kind, string Title, string Body, string? LinkPath, string Classification, bool Mandatory, bool RequiresAcknowledgement, DateTime CreatedUtc,
    DateTime? ReadUtc, DateTime? AcknowledgedUtc, string? AcknowledgedBy, Guid? ContractId, string? ReminderKind, int? WindowDays);

public sealed record InboxFilter(bool UnreadOnly = false, bool NeedsAcknowledgement = false, string? Kind = null, int Page = 1, int PageSize = 25);

/// <summary>The signed-in person's notifications. Each is read as the recipient only; nobody reads another person's inbox.</summary>
public interface INotificationInbox
{
    Task<PagedResult<InboxItem>> ListAsync(InboxFilter filter, CancellationToken ct = default);
    Task<int> UnreadCountAsync(CancellationToken ct = default);
    Task MarkReadAsync(Guid notificationId, CancellationToken ct = default);
    Task MarkAllReadAsync(CancellationToken ct = default);
    /// <summary>Acknowledges a reminder. The acknowledgement is recorded against the contract's reminder state and kept with who gave it and when.</summary>
    Task AcknowledgeAsync(Guid notificationId, CancellationToken ct = default);
    /// <summary>Acknowledges the contract's latest reminder on behalf of a current owner of the contract, from the contract page.</summary>
    Task AcknowledgeContractAsync(Guid contractId, CancellationToken ct = default);
}

public sealed record DeliveryRow(Guid DeliveryId, Guid NotificationId, string RecipientName, string Channel, string Provider, string Status, int Attempts, DateTime? LastAttemptUtc, DateTime? NextAttemptUtc,
    string? LastError, string Title, string EventType, string CorrelationId, DateTime CreatedUtc);

public sealed record AttemptRow(int Number, string Provider, DateTime StartedUtc, int DurationMs, bool Succeeded, string? Error, string CorrelationId);

public sealed record JobRow(string JobName, string Status, DateTime StartedUtc, DateTime? FinishedUtc, int Processed, int Failures, string? Detail);

public sealed record CapturedEmailRow(Guid Id, string To, string Subject, string Body, DateTime CapturedUtc, string CorrelationId);

public sealed record NotificationSummary(int Pending, int Failed, int DeadLetter, int SentLast24Hours, int ReminderUnacknowledged, int EventsWaiting, string EmailProvider, bool EmailIsSimulated);

/// <summary>What administrators see: failures and retries, the background jobs and the development mailbox. Needs <c>notifications.administer</c>.</summary>
public interface INotificationAdmin
{
    Task<NotificationSummary> SummaryAsync(CancellationToken ct = default);
    Task<PagedResult<DeliveryRow>> DeliveriesAsync(string? status, int page = 1, int pageSize = 25, CancellationToken ct = default);
    Task<IReadOnlyList<AttemptRow>> AttemptsAsync(Guid deliveryId, CancellationToken ct = default);
    Task RetryAsync(Guid deliveryId, CancellationToken ct = default);
    Task<IReadOnlyList<JobRow>> JobsAsync(int take = 50, CancellationToken ct = default);
    Task<PagedResult<CapturedEmailRow>> CapturedEmailsAsync(int page = 1, int pageSize = 25, CancellationToken ct = default);
}

public sealed record ReminderScanResult(int ContractsScanned, int RemindersSent, int NotificationsCreated, int WindowsSuppressed, int SkippedNoRecipient);

public interface IReminderScanner
{
    /// <summary>Finds contracts whose Review Date, End Date or notice deadline has reached a reminder window and tells their current owners, once per window.</summary>
    Task<ReminderScanResult> ScanAsync(CancellationToken ct = default);
}

public interface INotificationDispatcher
{
    /// <summary>Turns lifecycle events and ownership changes into notifications for the people the policy names. Safe to run again: nothing is created twice.</summary>
    Task<int> DispatchAsync(CancellationToken ct = default);
}

public interface IDeliveryRunner
{
    /// <summary>Attempts every delivery that is due, recording each attempt. A failure waits for its back-off; after the last attempt the delivery is dead-lettered where administrators can see it.</summary>
    Task<int> DeliverDueAsync(CancellationToken ct = default);
}

/// <summary>Runs every background job once for the current tenant, recording a run for each (the host calls this for each tenant on a timer).</summary>
public interface INotificationJobs
{
    Task<IReadOnlyList<JobRow>> RunAllAsync(CancellationToken ct = default);
}

public static class NotificationsModule
{
    public const string Name = "Notifications";
    public const string Schema = "notifications";
    public const int Tier = 9;

    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, Action<NotificationsOptions>? configure = null)
    {
        services.AddOptions<NotificationsOptions>();
        if (configure is not null) services.Configure(configure);
        services.AddModuleDbContext<NotificationsDbContext>(Name, Tier, Schema);
        services.AddScoped<Ecp.Configuration.IConfigurationKind, NotificationPolicyKind>();
        services.AddScoped<INotificationPolicyStore, NotificationPolicyStore>();
        services.AddScoped<NotificationRenderer>();
        services.AddScoped<RecipientResolver>();
        services.AddScoped<NotificationWriter>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<IDeliveryRunner, DeliveryRunner>();
        services.AddScoped<IReminderScanner, ReminderScanner>();
        services.AddScoped<INotificationInbox, NotificationInbox>();
        services.AddScoped<INotificationAdmin, NotificationAdmin>();
        services.AddScoped<INotificationJobs, NotificationJobs>();
        services.AddScoped<IReminderSettings, ReminderSettings>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<Ecp.Contracts.Register.IContractSetProvider, RemindedUnacknowledgedSet>();
        services.TryAddSingleton<IEmailDeliveryProviderFactory, EmailDeliveryProviderFactory>();
        return services;
    }
}

public sealed record ReminderStatusInfo(
    int LeadDays, bool LeadIsDefault, IReadOnlyList<int> AllowedLeadDays, int? NoticePeriodDays, bool CriticalNotice, bool Evergreen, DateTime? LastReminderSentUtc, string? LastReminderKind, int? LastWindowDays,
    DateOnly? LastDueDate, bool Unacknowledged, string? AcknowledgedBy, DateTime? AcknowledgedUtc, DateTime? EscalationDueUtc, bool CanConfigure, bool CanAcknowledge, IReadOnlyList<string> Schedule);

/// <summary>A contract's reminder state and lead time, for its page. Reading needs the right to read the contract; changing the lead time needs <c>reminders.configure</c> and a lead time the policy allows.</summary>
public interface IReminderSettings
{
    Task<ReminderStatusInfo> StatusAsync(Guid contractId, CancellationToken ct = default);
    Task SetLeadDaysAsync(Guid contractId, int? leadDays, CancellationToken ct = default);
}
