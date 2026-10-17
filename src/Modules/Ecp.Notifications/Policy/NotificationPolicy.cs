using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;

namespace Ecp.Notifications.Policy;

/// <summary>The closed vocabularies a notification policy may use. A policy is data: it names these recipients, channels and placeholders, never code or expressions.</summary>
public static class PolicyVocabulary
{
    public static readonly IReadOnlyList<string> Recipients = ["CategoryOwners", "Submitter", "LegalPool", "AssignedLegal", "ApproverGroup", "Counterpart", "AffectedUser", "Actor", "OrganisationAdministrators"];
    public static readonly IReadOnlyList<string> Channels = ["InApp", "Email"];

    /// <summary>The only variables a template may use. Every substituted value is encoded for plain text and bounded; there are no expressions, loops, conditions or includes.</summary>
    public static readonly IReadOnlyList<string> Placeholders = ["contract.reference", "contract.title", "actor.name", "link", "due.date", "days.remaining", "window.days", "step.name"];

    /// <summary>Every event a policy may configure. Workflow events come from the lifecycle's outbox; ownership events from the audit trail; reminders from the scan.</summary>
    public static readonly IReadOnlyList<string> Events =
    [
        "legal.submitted", "legal.resubmitted", "legal.assigned", "legal.started", "legal.returned", "legal.cleared", "legal.withdrawn", "legal.commented",
        "approval.submitted", "approval.approved", "approval.rejected", "contract.registered", "variation.raised", "variation.applied", "variation.rejected", "variation.cancelled",
        "renewal.started", "renewal.completed", "renewal.cancelled", "disposition.requested", "unlock.opened", "unlock.closed", "ownership.assigned", "ownership.ended",
        "reminder.reviewDate", "reminder.endDate", "reminder.criticalNotice", "reminder.acknowledged"
    ];
}

public sealed record EventRule(string Event, IReadOnlyList<string> Recipients, string Template, IReadOnlyList<string> Channels, bool Mandatory);
public sealed record MessageTemplate(string Subject, string Body);
public sealed record CriticalNoticeRule(bool Enabled, IReadOnlyList<int> OffsetsDays, int EarlierLeadDays);
public sealed record EvergreenRule(bool SuppressEndDate, bool KeepReviewDate);
/// <summary>
/// The reminder windows and who is told when nobody currently owns the contract's category (<paramref name="UnownedFallback"/>: recipient rules from the closed vocabulary; empty means the reminder is not sent
/// and the scan tries again later). A policy published before the field existed has no entry for it; it reads as <see cref="ReminderRules.LegacyUnownedFallback"/>, which is what the platform did then.
/// </summary>
public sealed record ReminderRules(int DefaultLeadDays, IReadOnlyList<int> AllowedLeadDays, bool ReviewDate, bool EndDate, CriticalNoticeRule CriticalNotice, EvergreenRule Evergreen, int EscalateUnacknowledgedAfterDays,
    IReadOnlyList<string> UnownedFallback)
{
    /// <summary>The fall-back of a policy that does not state one (every release published before this field existed). New packs state theirs, including "none".</summary>
    public static readonly IReadOnlyList<string> LegacyUnownedFallback = ["OrganisationAdministrators"];
}
public sealed record ContentRules(string WithholdTitleFromLevel, string WithholdEverythingFromLevel);
public sealed record DeliveryRules(int MaxAttempts, IReadOnlyList<int> BackoffMinutes);

public sealed class NotificationPolicy
{
    public required int SchemaVersion { get; init; }
    public required ReminderRules Reminders { get; init; }
    public required ContentRules Content { get; init; }
    public required DeliveryRules Delivery { get; init; }
    public required IReadOnlyList<EventRule> Events { get; init; }
    public required IReadOnlyDictionary<string, MessageTemplate> Templates { get; init; }

    public EventRule? RuleFor(string eventType) => Events.FirstOrDefault(e => string.Equals(e.Event, eventType, StringComparison.Ordinal));
    public TimeSpan BackoffFor(int attempts) => TimeSpan.FromMinutes(Delivery.BackoffMinutes.Count == 0 ? 5 : Delivery.BackoffMinutes[Math.Clamp(attempts - 1, 0, Delivery.BackoffMinutes.Count - 1)]);
}

public static partial class NotificationPolicyParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
        NumberHandling = JsonNumberHandling.Strict
    };

    private sealed class Raw
    {
        public int SchemaVersion { get; set; }
        public string? Description { get; set; }
        public RawReminders? Reminders { get; set; }
        public RawContent? Content { get; set; }
        public RawDelivery? Delivery { get; set; }
        public List<RawEvent>? Events { get; set; }
        public Dictionary<string, RawTemplate>? Templates { get; set; }
    }

    private sealed class RawReminders
    {
        public int DefaultLeadDays { get; set; } = 90;
        public List<int>? AllowedLeadDays { get; set; }
        public bool ReviewDate { get; set; } = true;
        public bool EndDate { get; set; } = true;
        public RawCritical? CriticalNotice { get; set; }
        public RawEvergreen? Evergreen { get; set; }
        public int EscalateUnacknowledgedAfterDays { get; set; } = 3;
        /// <summary>Null (absent) means the pre-field behaviour; an empty list means nobody.</summary>
        public List<string>? UnownedFallback { get; set; }
    }

    private sealed class RawCritical { public bool Enabled { get; set; } = true; public List<int>? OffsetsDays { get; set; } public int EarlierLeadDays { get; set; } = 30; }
    private sealed class RawEvergreen { public bool SuppressEndDate { get; set; } = true; public bool KeepReviewDate { get; set; } = true; }
    private sealed class RawContent { public string? WithholdTitleFromLevel { get; set; } public string? WithholdEverythingFromLevel { get; set; } }
    private sealed class RawDelivery { public int MaxAttempts { get; set; } = 5; public List<int>? BackoffMinutes { get; set; } }
    private sealed class RawEvent { public string? Event { get; set; } public List<string>? Recipients { get; set; } public string? Template { get; set; } public List<string>? Channels { get; set; } public bool Mandatory { get; set; } }
    private sealed class RawTemplate { public string? Subject { get; set; } public string? Body { get; set; } }

    public static (NotificationPolicy? Policy, List<ValidationMessage> Messages) Parse(string json)
    {
        var messages = new List<ValidationMessage>();
        Raw? raw;
        try { raw = JsonSerializer.Deserialize<Raw>(json, Options); }
        catch (JsonException ex)
        {
            messages.Add(new(ValidationSeverity.Error, "$", "invalid_json", "The notification policy could not be read: " + ex.Message.Split('\n')[0]));
            return (null, messages);
        }
        if (raw is null) { messages.Add(new(ValidationSeverity.Error, "$", "invalid_json", "The notification policy is empty.")); return (null, messages); }
        var r = raw.Reminders ?? new RawReminders();
        var policy = new NotificationPolicy
        {
            SchemaVersion = raw.SchemaVersion,
            Reminders = new ReminderRules(r.DefaultLeadDays, r.AllowedLeadDays ?? [30, 60, 90, 120], r.ReviewDate, r.EndDate,
                new CriticalNoticeRule(r.CriticalNotice?.Enabled ?? true, r.CriticalNotice?.OffsetsDays ?? [30, 14, 7, 1], r.CriticalNotice?.EarlierLeadDays ?? 30),
                new EvergreenRule(r.Evergreen?.SuppressEndDate ?? true, r.Evergreen?.KeepReviewDate ?? true), r.EscalateUnacknowledgedAfterDays,
                r.UnownedFallback ?? ReminderRules.LegacyUnownedFallback),
            Content = new ContentRules(raw.Content?.WithholdTitleFromLevel ?? "Restricted", raw.Content?.WithholdEverythingFromLevel ?? "Legal Privileged"),
            Delivery = new DeliveryRules(raw.Delivery?.MaxAttempts ?? 5, raw.Delivery?.BackoffMinutes ?? [1, 5, 30, 120, 720]),
            Events = (raw.Events ?? []).Select(e => new EventRule(e.Event ?? "", e.Recipients ?? [], e.Template ?? "", e.Channels ?? ["InApp"], e.Mandatory)).ToList(),
            Templates = (raw.Templates ?? []).ToDictionary(kv => kv.Key, kv => new MessageTemplate(kv.Value.Subject ?? "", kv.Value.Body ?? ""), StringComparer.Ordinal)
        };
        return (policy, messages);
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}", RegexOptions.NonBacktracking)]
    public static partial Regex PlaceholderPattern();

    public static IReadOnlyList<ValidationMessage> Validate(NotificationPolicy p, ClassificationScale scale)
    {
        var m = new List<ValidationMessage>();
        void Error(string path, string code, string message) => m.Add(new(ValidationSeverity.Error, path, code, message));
        if (p.SchemaVersion != 1) Error("$.schemaVersion", "schema_version", "Only schema version 1 is supported.");

        var r = p.Reminders;
        if (r.AllowedLeadDays.Count == 0 || r.AllowedLeadDays.Any(d => d is < 1 or > 3650) || r.AllowedLeadDays.Distinct().Count() != r.AllowedLeadDays.Count)
            Error("$.reminders.allowedLeadDays", "lead_days", "Give the lead times a contract may use, each between 1 and 3650 days, once each.");
        if (!r.AllowedLeadDays.Contains(r.DefaultLeadDays)) Error("$.reminders.defaultLeadDays", "lead_default", "The default lead time must be one of the allowed lead times.");
        if (r.CriticalNotice.OffsetsDays.Any(d => d is < 0 or > 3650) || r.CriticalNotice.OffsetsDays.Distinct().Count() != r.CriticalNotice.OffsetsDays.Count)
            Error("$.reminders.criticalNotice.offsetsDays", "critical_offsets", "Critical Notice offsets are days before the notice deadline, between 0 and 3650, once each.");
        if (r.CriticalNotice.EarlierLeadDays is < 0 or > 3650) Error("$.reminders.criticalNotice.earlierLeadDays", "critical_earlier", "The extra lead for Critical Notice contracts must be between 0 and 3650 days.");
        foreach (var fallback in r.UnownedFallback)
            if (!PolicyVocabulary.Recipients.Contains(fallback)) Error("$.reminders.unownedFallback", "recipient_unknown", $"Recipient rule '{fallback}' is not recognised.");
        if (r.UnownedFallback.Distinct().Count() != r.UnownedFallback.Count) Error("$.reminders.unownedFallback", "recipient_duplicate", "Name each fall-back recipient rule once.");
        if (r.EscalateUnacknowledgedAfterDays is < 0 or > 365) Error("$.reminders.escalateUnacknowledgedAfterDays", "escalate", "Escalation data is held after 0 to 365 days.");

        if (!scale.IsKnown(p.Content.WithholdTitleFromLevel)) Error("$.content.withholdTitleFromLevel", "level", $"'{p.Content.WithholdTitleFromLevel}' is not a classification level.");
        if (!scale.IsKnown(p.Content.WithholdEverythingFromLevel)) Error("$.content.withholdEverythingFromLevel", "level", $"'{p.Content.WithholdEverythingFromLevel}' is not a classification level.");
        else if (scale.IsKnown(p.Content.WithholdTitleFromLevel) && scale.Rank(p.Content.WithholdEverythingFromLevel) < scale.Rank(p.Content.WithholdTitleFromLevel))
            Error("$.content", "level_order", "Withholding everything must start at or above the level where titles are withheld.");

        if (p.Delivery.MaxAttempts is < 1 or > 20) Error("$.delivery.maxAttempts", "attempts", "Between 1 and 20 delivery attempts.");
        if (p.Delivery.BackoffMinutes.Any(x => x is < 1 or > 10080)) Error("$.delivery.backoffMinutes", "backoff", "Back-off intervals are between 1 minute and 7 days.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < p.Events.Count; i++)
        {
            var e = p.Events[i];
            var path = $"$.events[{i}]";
            if (!PolicyVocabulary.Events.Contains(e.Event)) Error(path + ".event", "event_unknown", $"Event '{e.Event}' is not recognised.");
            else if (!seen.Add(e.Event)) Error(path + ".event", "event_duplicate", $"Event '{e.Event}' is configured twice.");
            if (e.Recipients.Count == 0) Error(path + ".recipients", "recipients", "Name at least one recipient rule.");
            foreach (var rec in e.Recipients) if (!PolicyVocabulary.Recipients.Contains(rec)) Error(path + ".recipients", "recipient_unknown", $"Recipient rule '{rec}' is not recognised.");
            if (e.Channels.Count == 0) Error(path + ".channels", "channels", "Name at least one channel.");
            foreach (var ch in e.Channels) if (!PolicyVocabulary.Channels.Contains(ch)) Error(path + ".channels", "channel_unknown", $"Channel '{ch}' is not recognised.");
            if (e.Mandatory && !e.Channels.Contains("InApp")) Error(path + ".channels", "mandatory_inapp", "A mandatory notification must always reach the in-app inbox.");
            if (!p.Templates.ContainsKey(e.Template)) Error(path + ".template", "template_missing", $"Template '{e.Template}' is not defined.");
        }

        foreach (var (name, t) in p.Templates)
        {
            var path = $"$.templates.{name}";
            if (string.IsNullOrWhiteSpace(t.Subject) || t.Subject.Length > 300) Error(path + ".subject", "subject", "A template needs a subject of up to 300 characters.");
            if (string.IsNullOrWhiteSpace(t.Body) || t.Body.Length > 3000) Error(path + ".body", "body", "A template needs a body of up to 3000 characters.");
            foreach (var text in new[] { t.Subject, t.Body })
            {
                // Unknown placeholders and anything that looks like logic are found when the policy is published, never when a message is sent.
                foreach (Match match in PlaceholderPattern().Matches(text))
                    if (!PolicyVocabulary.Placeholders.Contains(match.Groups[1].Value)) Error(path, "placeholder_unknown", $"Placeholder '{{{{{match.Groups[1].Value}}}}}' is not allowed. Allowed: {string.Join(", ", PolicyVocabulary.Placeholders)}.");
                // [[term]] tokens are the only other thing a template may contain: a closed vocabulary of business terms, checked here so an unknown one fails at publication and never at send time.
                foreach (var problem in TermTokens.Problems(PlaceholderPattern().Replace(text, ""))) Error(path, "term_token", problem);
                var stripped = PlaceholderPattern().Replace(text, "");
                if (stripped.Contains("{{", StringComparison.Ordinal) || stripped.Contains("}}", StringComparison.Ordinal) || stripped.Contains("{%", StringComparison.Ordinal) || stripped.Contains("<script", StringComparison.OrdinalIgnoreCase))
                    Error(path, "template_syntax", "A template holds plain text and {{placeholders}} only: no expressions, conditions, loops, includes or markup.");
            }
            if (t.Subject.Any(char.IsControl)) Error(path + ".subject", "subject_control", "A subject cannot contain control characters.");
        }
        foreach (var required in new[] { "reminder.reviewDate", "reminder.endDate", "reminder.criticalNotice" })
            if (p.RuleFor(required) is null) m.Add(new(ValidationSeverity.Warning, "$.events", "reminder_event_missing", $"No rule for '{required}', so those reminders produce no notification."));
        return m;
    }
}
