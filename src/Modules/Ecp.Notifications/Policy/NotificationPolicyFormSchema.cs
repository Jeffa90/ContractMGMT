using System.Text.Json.Nodes;
using Ecp.Configuration.Forms;
using static Ecp.Configuration.Forms.FormFields;

namespace Ecp.Notifications.Policy;

/// <summary>
/// The notification policy form: who is told about what, how they are told, the wording of each message and the reminder rules. Recipients, channels, events and placeholders come from the closed lists in
/// <see cref="PolicyVocabulary"/>; message wording is plain text with {{placeholders}} from the list shown beside the box, and nothing else.
/// </summary>
internal sealed class NotificationPolicyFormSchemas : IFormSchemaSource
{
    private static readonly IReadOnlyDictionary<string, string> RecipientMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CategoryOwners"] = "The owners of the contract's category", ["Submitter"] = "The person who submitted it", ["LegalPool"] = "Everyone in the Legal pool", ["AssignedLegal"] = "The Legal reviewer assigned to it",
        ["ApproverGroup"] = "The approver group that decides it", ["Counterpart"] = "The other party to the step (for example the submitter, when Legal responds)", ["AffectedUser"] = "The person the event is about",
        ["Actor"] = "The person who did it", ["OrganisationAdministrators"] = "The organisation's administrators",
    };

    private static readonly IReadOnlyDictionary<string, string> EventMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["legal.submitted"] = "Sent to Legal", ["legal.resubmitted"] = "Sent back to Legal", ["legal.assigned"] = "A Legal reviewer was assigned", ["legal.started"] = "Legal started the review",
        ["legal.returned"] = "Legal returned it for rework", ["legal.cleared"] = "Legal cleared it", ["legal.withdrawn"] = "Withdrawn from Legal", ["legal.commented"] = "Legal commented",
        ["approval.submitted"] = "Submitted for approval", ["approval.approved"] = "Approved", ["approval.rejected"] = "Rejected", ["contract.registered"] = "Registered",
        ["variation.raised"] = "A variation was raised", ["variation.applied"] = "A variation was applied", ["variation.rejected"] = "A variation was rejected", ["variation.cancelled"] = "A variation was cancelled",
        ["renewal.started"] = "A renewal started", ["renewal.completed"] = "A renewal completed", ["renewal.cancelled"] = "A renewal was cancelled", ["disposition.requested"] = "Disposal was requested",
        ["unlock.opened"] = "A record was unlocked", ["unlock.closed"] = "An unlock ended", ["ownership.assigned"] = "Ownership was assigned", ["ownership.ended"] = "Ownership ended",
        ["reminder.reviewDate"] = "Reminder: review date approaching", ["reminder.endDate"] = "Reminder: end date approaching", ["reminder.criticalNotice"] = "Reminder: notice deadline approaching",
        ["reminder.acknowledged"] = "A reminder was acknowledged",
    };

    private static IEnumerable<FormOption> Meaning(IEnumerable<string> codes, IReadOnlyDictionary<string, string> meanings)
        => codes.Select(c => new FormOption(c, meanings.TryGetValue(c, out var m) ? m + " (" + c + ")" : c));

    private static IReadOnlyList<FormOption> Templates(JsonObject document)
        => (document["templates"] as JsonObject)?.Select(kv => new FormOption(kv.Key, kv.Key)).ToList() ?? [];

    private static readonly string PlaceholderHelp = "Plain text. Insert details with these placeholders only: " + string.Join(", ", PolicyVocabulary.Placeholders.Select(p => "{{" + p + "}}")) + ".";

    public IEnumerable<FormSchema> Schemas() =>
    [
        new FormSchema
        {
            Kind = NotificationPolicyKinds.Policy,
            Title = "Notification policy",
            Intro = "Who is told about what, how, and in what words; and when reminders are sent. A message is plain text with the placeholders listed beside the box. Check the draft to see the wording as it will be sent, then submit it for a second person to publish.",
            Sections =
            [
                new FormSection("about", "About this policy", null, [LongText("description", "Description", "Free text for your own records.", maxLength: 1000)]),
                new FormSection("reminders", "Reminders", "When people are reminded about review dates, end dates and notice deadlines.",
                [
                    Group("reminders", "Reminders",
                    [
                        Integer("defaultLeadDays", "Default warning time (days)", "How many days before a date the reminder is sent unless a contract chooses another. Must be one of the allowed times.", "90", @default: "90"),
                        Numbers("allowedLeadDays", "Warning times a contract may choose (days)", "Whole numbers separated by commas, each between 1 and 3650, each once.", "30, 60, 90, 120"),
                        Boolean("reviewDate", "Remind about review dates", @default: true),
                        Boolean("endDate", "Remind about end dates", @default: true),
                        Group("criticalNotice", "Notice deadlines",
                        [
                            Boolean("enabled", "Send notice-deadline reminders", @default: true),
                            Numbers("offsetsDays", "Remind this many days before the deadline", "Whole numbers separated by commas. Example: 30, 14, 7, 1.", "30, 14, 7, 1"),
                            Integer("earlierLeadDays", "Extra warning time for critical contracts (days)", "0 to 3650.", "30", @default: "30"),
                        ]),
                        Group("evergreen", "Open-ended contracts",
                        [
                            Boolean("suppressEndDate", "Do not remind about an end date", "An open-ended contract has none.", @default: true),
                            Boolean("keepReviewDate", "Still remind about the review date", @default: true),
                        ]),
                        Integer("escalateUnacknowledgedAfterDays", "Keep escalation details for (days)", "0 to 365.", "3", @default: "3"),
                        Many("unownedFallback", "Who is told when nobody owns the category", Meaning(PolicyVocabulary.Recipients, RecipientMeaning),
                            "If nobody is chosen the reminder is not sent and the scan tries again later. A policy that said nothing is shown with the organisation's administrators ticked, which is what it did.",
                            whenAbsent: ReminderRules.LegacyUnownedFallback),
                    ]),
                ]),
                new FormSection("content", "What a message may reveal", "Classification levels at which a message hides the contract's title, or everything.",
                [
                    Group("content", "Content",
                    [
                        SelectFrom("withholdTitleFromLevel", "Hide the title from this level upwards", "levels", "Messages about contracts at or above this level do not show the title."),
                        SelectFrom("withholdEverythingFromLevel", "Say nothing from this level upwards", "levels", "Must be at or above the level that hides titles."),
                    ]),
                ]),
                new FormSection("delivery", "Delivery", "How often a failed delivery is retried.",
                [
                    Group("delivery", "Delivery",
                    [
                        Integer("maxAttempts", "Attempts", "1 to 20.", "5", @default: "5"),
                        Numbers("backoffMinutes", "Wait between attempts (minutes)", "Whole numbers separated by commas, each between 1 and 10080. Example: 1, 5, 30, 120, 720.", "1, 5, 30, 120, 720"),
                    ]),
                ]),
                new FormSection("events", "Who is told about what", "One line for each event. An event with no line sends nothing.",
                [
                    Rows("events", "Events", "event", "event",
                    [
                        Select("event", "Event", Meaning(PolicyVocabulary.Events, EventMeaning), required: true),
                        Many("recipients", "Who is told", Meaning(PolicyVocabulary.Recipients, RecipientMeaning), "At least one."),
                        Many("channels", "How", PolicyVocabulary.Channels, "InApp is the inbox; Email needs the mail settings to be working. A mandatory message must include InApp."),
                        SelectDerived("template", "Wording", Templates, "One of the messages below.", required: true),
                        Boolean("mandatory", "Mandatory", "People cannot switch this message off for themselves."),
                    ]),
                ]),
                new FormSection("templates", "Wording of messages", "Each message has a name, a subject and a body.",
                [
                    Named("templates", "Messages", "message", "Message name",
                    [
                        Text("subject", "Subject", PlaceholderHelp, "Contract {{contract.reference}} needs your attention", required: true, maxLength: 300),
                        LongText("body", "Body", PlaceholderHelp, required: true, maxLength: 3000),
                    ], "Names are letters and digits. Events choose a message by name."),
                ]),
            ],
        },
    ];
}
