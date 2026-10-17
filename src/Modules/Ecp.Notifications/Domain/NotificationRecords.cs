using Ecp.SharedKernel;

namespace Ecp.Notifications.Domain;

public enum DeliveryChannel { InApp, Email }

public enum DeliveryStatus { Pending, Sent, Failed, DeadLetter }

/// <summary>
/// What a person is told. The content was made safe for the record's classification when it was written, and the recipient was checked against the record's access rules then; a notification never
/// names a record its recipient could not open.
/// </summary>
public sealed class Notification : ITenantOwned
{
    private Notification() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public string EventType { get; private set; } = "";
    /// <summary>Event or Reminder.</summary>
    public string Kind { get; private set; } = "";
    public Guid? SourceEventId { get; private set; }
    public Guid? ContractId { get; private set; }
    public string? SubjectKind { get; private set; }
    public Guid? SubjectId { get; private set; }
    public string Classification { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string? LinkPath { get; private set; }
    public bool Mandatory { get; private set; }
    public bool RequiresAcknowledgement { get; private set; }
    /// <summary>One message per event, recipient and channel set; a retry, restart or re-run finds this key and writes nothing.</summary>
    public string DedupeKey { get; private set; } = "";
    public string CorrelationId { get; private set; } = "";
    public string? ReminderKind { get; private set; }
    public int? WindowDays { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public DateTime? ReadUtc { get; private set; }
    public DateTime? AcknowledgedUtc { get; private set; }
    public string? AcknowledgedBy { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Notification Create(Guid tenantId, Guid organisationId, Guid recipient, string eventType, string kind, Guid? sourceEventId, Guid? contractId, string? subjectKind, Guid? subjectId,
        string classification, string title, string body, string? linkPath, bool mandatory, bool requiresAck, string dedupeKey, string correlationId, string? reminderKind, int? windowDays, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = organisationId, RecipientUserId = Guard.NotEmpty(recipient, nameof(recipient)),
        EventType = Guard.NotBlank(eventType, "Event type", 100), Kind = kind, SourceEventId = sourceEventId, ContractId = contractId, SubjectKind = subjectKind, SubjectId = subjectId,
        Classification = Guard.NotBlank(classification, "Classification", 30), Title = Guard.NotBlank(title, "Title", 300), Body = Guard.NotBlank(body, "Body", 4000), LinkPath = Guard.OptionalBounded(linkPath, "Link", 300),
        Mandatory = mandatory, RequiresAcknowledgement = requiresAck, DedupeKey = Guard.NotBlank(dedupeKey, "Dedupe key", 300), CorrelationId = Guard.NotBlank(correlationId, "Correlation id", 64),
        ReminderKind = reminderKind, WindowDays = windowDays, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void MarkRead(DateTime nowUtc) { ReadUtc ??= nowUtc; }

    public void Acknowledge(string by, DateTime nowUtc)
    {
        if (!RequiresAcknowledgement) throw new DomainException("not_acknowledgeable", "This [[notification|lower]] does not need acknowledging.");
        if (AcknowledgedUtc is not null) return;
        AcknowledgedUtc = nowUtc; AcknowledgedBy = Guard.NotBlank(by, "Acknowledged by", 200); ReadUtc ??= nowUtc;
    }
}

/// <summary>One channel's delivery of a notification, with its state. In-app delivery is complete when the notification is stored; email goes through a provider with attempts, retry and dead-lettering.</summary>
public sealed class NotificationDelivery : ITenantOwned
{
    private NotificationDelivery() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid NotificationId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public DeliveryChannel Channel { get; private set; }
    public string Provider { get; private set; } = "";
    public DeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public DateTime? NextAttemptUtc { get; private set; }
    public DateTime? LastAttemptUtc { get; private set; }
    public DateTime? SentUtc { get; private set; }
    public string? LastError { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public byte[] RowVersion { get; private set; } = [];

    public static NotificationDelivery Create(Guid tenantId, Guid notificationId, Guid recipient, DeliveryChannel channel, string provider, string correlationId, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, NotificationId = notificationId, RecipientUserId = recipient, Channel = channel, Provider = Guard.NotBlank(provider, "Provider", 40),
        Status = channel == DeliveryChannel.InApp ? DeliveryStatus.Sent : DeliveryStatus.Pending, CorrelationId = correlationId, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)),
        NextAttemptUtc = channel == DeliveryChannel.InApp ? null : nowUtc, SentUtc = channel == DeliveryChannel.InApp ? nowUtc : null
    };

    /// <summary>A retry uses the email provider configured now, so fixing the configuration and retrying works.</summary>
    public void UseProvider(string provider) { Provider = Guard.NotBlank(provider, "Provider", 40); }

    public void Succeeded(DateTime nowUtc) { Attempts++; LastAttemptUtc = nowUtc; SentUtc = nowUtc; Status = DeliveryStatus.Sent; NextAttemptUtc = null; LastError = null; }

    public void Failed(string error, DateTime nowUtc, int maxAttempts, TimeSpan backoff)
    {
        Attempts++; LastAttemptUtc = nowUtc; LastError = Guard.OptionalBounded(error, "Error", 500);
        if (Attempts >= maxAttempts) { Status = DeliveryStatus.DeadLetter; NextAttemptUtc = null; }
        else { Status = DeliveryStatus.Failed; NextAttemptUtc = nowUtc + backoff; }
    }

    /// <summary>An administrator's retry: a dead-lettered or failed delivery is tried again at once, with its history kept.</summary>
    public void Requeue(DateTime nowUtc)
    {
        if (Status is not (DeliveryStatus.DeadLetter or DeliveryStatus.Failed)) throw new DomainException("not_retryable", "Only a failed delivery can be retried.");
        Status = DeliveryStatus.Pending; NextAttemptUtc = nowUtc;
    }
}

public sealed class DeliveryAttempt : ITenantOwned
{
    private DeliveryAttempt() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid DeliveryId { get; private set; }
    public int Number { get; private set; }
    public string Provider { get; private set; } = "";
    public DateTime StartedUtc { get; private set; }
    public int DurationMs { get; private set; }
    public bool Succeeded { get; private set; }
    public string? Error { get; private set; }
    public string CorrelationId { get; private set; } = "";

    public static DeliveryAttempt Record(Guid tenantId, Guid deliveryId, int number, string provider, DateTime startedUtc, int durationMs, bool succeeded, string? error, string correlationId) => new()
    {
        Id = Ids.New(), TenantId = tenantId, DeliveryId = deliveryId, Number = number, Provider = provider, StartedUtc = Guard.Utc(startedUtc, nameof(startedUtc)), DurationMs = durationMs, Succeeded = succeeded,
        Error = Guard.OptionalBounded(error, "Error", 500), CorrelationId = correlationId
    };
}

/// <summary>A reminder window for one contract date. Unique per contract, kind, window and date, so a window is acted on once; a changed date makes a new window and leaves the old one unsent.</summary>
public sealed class ReminderWindow : ITenantOwned
{
    private ReminderWindow() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid ContractId { get; private set; }
    public string Kind { get; private set; } = "";
    public int WindowDays { get; private set; }
    public DateOnly DueDate { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public DateTime? SentUtc { get; private set; }
    /// <summary>The window had already passed when the contract was first scanned, so a later window covers it and nothing is sent for this one.</summary>
    public bool Suppressed { get; private set; }
    public int RecipientCount { get; private set; }

    public static ReminderWindow Open(Guid tenantId, Guid organisationId, Guid contractId, string kind, int windowDays, DateOnly dueDate, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, OrganisationId = organisationId, ContractId = contractId, Kind = Guard.NotBlank(kind, "Kind", 30), WindowDays = windowDays, DueDate = dueDate, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Sent(int recipients, DateTime nowUtc) { SentUtc = nowUtc; RecipientCount = recipients; }
    public void Suppress() { Suppressed = true; }
}

/// <summary>Where a contract stands on reminders: when the last one was sent and whether it has been acknowledged. Escalation data is held for external reporting; nothing is escalated automatically.</summary>
public sealed class ReminderTracking : ITenantOwned
{
    private ReminderTracking() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid ContractId { get; private set; }
    public string Classification { get; private set; } = "";
    public DateTime LastReminderSentUtc { get; private set; }
    public string LastReminderKind { get; private set; } = "";
    public int LastWindowDays { get; private set; }
    public DateOnly LastDueDate { get; private set; }
    public DateTime? AcknowledgedUtc { get; private set; }
    public string? AcknowledgedBy { get; private set; }
    public DateTime? EscalationDueUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsAcknowledged => AcknowledgedUtc is { } a && a >= LastReminderSentUtc;

    public static ReminderTracking Start(Guid tenantId, Guid organisationId, Guid contractId, string classification, string kind, int window, DateOnly due, DateTime sentUtc, int? escalateAfterDays) => new()
    {
        Id = Ids.New(), TenantId = tenantId, OrganisationId = organisationId, ContractId = contractId, Classification = classification, LastReminderSentUtc = Guard.Utc(sentUtc, nameof(sentUtc)),
        LastReminderKind = kind, LastWindowDays = window, LastDueDate = due, EscalationDueUtc = escalateAfterDays is { } d ? sentUtc.AddDays(d) : null
    };

    public void Reminded(string classification, string kind, int window, DateOnly due, DateTime sentUtc, int? escalateAfterDays)
    {
        Classification = classification; LastReminderSentUtc = Guard.Utc(sentUtc, nameof(sentUtc)); LastReminderKind = kind; LastWindowDays = window; LastDueDate = due;
        AcknowledgedUtc = null; AcknowledgedBy = null; EscalationDueUtc = escalateAfterDays is { } d ? sentUtc.AddDays(d) : null;
    }

    public void Acknowledge(string by, DateTime nowUtc)
    {
        if (IsAcknowledged) return;
        AcknowledgedUtc = nowUtc; AcknowledgedBy = Guard.NotBlank(by, "Acknowledged by", 200); EscalationDueUtc = null;
    }

    public void SetClassification(string classification) => Classification = classification;
}

/// <summary>An email held by the development capture provider instead of being sent. Demonstration and Test only.</summary>
public sealed class CapturedEmail : ITenantOwned
{
    private CapturedEmail() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? NotificationId { get; private set; }
    public Guid? DeliveryId { get; private set; }
    public string ToAddress { get; private set; } = "";
    public string Subject { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string CorrelationId { get; private set; } = "";
    public DateTime CapturedUtc { get; private set; }

    public static CapturedEmail Capture(Guid tenantId, Guid? notificationId, Guid? deliveryId, string to, string subject, string body, string correlationId, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = tenantId, NotificationId = notificationId, DeliveryId = deliveryId, ToAddress = Guard.NotBlank(to, "To", 320), Subject = Guard.NotBlank(subject, "Subject", 300),
        Body = Guard.NotBlank(body, "Body", 4000), CorrelationId = correlationId, CapturedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

public enum JobStatus { Running, Succeeded, Failed }

/// <summary>A run of a background job for a tenant, kept so administrators can see that the work is happening and what it did.</summary>
public sealed class JobRun : ITenantOwned
{
    private JobRun() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string JobName { get; private set; } = "";
    public JobStatus Status { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public DateTime? FinishedUtc { get; private set; }
    public int Processed { get; private set; }
    public int Failures { get; private set; }
    public string? Detail { get; private set; }

    public static JobRun Start(Guid tenantId, string name, DateTime nowUtc) => new() { Id = Ids.New(), TenantId = tenantId, JobName = Guard.NotBlank(name, "Job", 60), Status = JobStatus.Running, StartedUtc = Guard.Utc(nowUtc, nameof(nowUtc)) };

    public void Finish(int processed, int failures, string? detail, DateTime nowUtc)
    {
        Processed = processed; Failures = failures; Detail = Guard.OptionalBounded(detail, "Detail", 1000); FinishedUtc = nowUtc; Status = failures > 0 && processed == 0 ? JobStatus.Failed : JobStatus.Succeeded;
    }

    public void Fail(string error, DateTime nowUtc) { Status = JobStatus.Failed; Detail = Guard.OptionalBounded(error, "Error", 1000); FinishedUtc = nowUtc; Failures++; }
}

/// <summary>How far an event source has been read, so a restart resumes where it stopped and nothing is handled twice.</summary>
public sealed class DispatchCursor : ITenantOwned
{
    private DispatchCursor() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Source { get; private set; } = "";
    public long LastSequence { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DispatchCursor Start(Guid tenantId, string source, long sequence) => new() { Id = Ids.New(), TenantId = tenantId, Source = Guard.NotBlank(source, "Source", 40), LastSequence = sequence };
    public void Advance(long sequence) { if (sequence > LastSequence) LastSequence = sequence; }
}
