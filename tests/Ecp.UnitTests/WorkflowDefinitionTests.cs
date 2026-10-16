using Ecp.Configuration;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Lifecycle.Definitions;
using Ecp.Notifications.Policy;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

internal static class PackFiles
{
    public static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", "jbs-procurement", relative));
    public static WorkflowDefinition Lifecycle() => Parse(Read("workflows/contract-lifecycle.json"));
    public static WorkflowDefinition Variation() => Parse(Read("workflows/variation.json"));

    public static WorkflowDefinition Parse(string json)
    {
        var (def, messages) = WorkflowDefinitionParser.Parse(json);
        Assert.NotNull(def);
        Assert.Empty(messages);
        return def!;
    }

    public static WorkflowDefinition With(this WorkflowDefinition d, Func<TransitionDefinition, TransitionDefinition>? transition = null, Func<StateDefinition, StateDefinition>? state = null, Func<WorkflowSettings, WorkflowSettings>? settings = null,
        IEnumerable<TransitionDefinition>? extraTransitions = null)
        => new()
        {
            Workflow = d.Workflow, Subject = d.Subject, SchemaVersion = d.SchemaVersion, Description = d.Description,
            Settings = settings?.Invoke(d.Settings) ?? d.Settings,
            States = state is null ? d.States : d.States.Select(state).ToList(),
            Transitions = (transition is null ? d.Transitions : d.Transitions.Select(transition)).Concat(extraTransitions ?? []).ToList()
        };

    public static IEnumerable<string> Errors(IEnumerable<ValidationMessage> m) => m.Where(x => x.Severity == ValidationSeverity.Error).Select(x => x.Code);
}

[Trait("Category", "Unit")]
public sealed class WorkflowDefinitionTests
{
    private static IEnumerable<string> ErrorsOf(WorkflowDefinition d, string subject = "Contract") => PackFiles.Errors(WorkflowDefinitionParser.Validate(d, subject, PermissionMatrix.Instance));

    [Fact]
    public void The_JBS_reference_workflows_are_valid_and_complete()
    {
        var c = PackFiles.Lifecycle();
        Assert.Empty(ErrorsOf(c));
        Assert.Equal(17, c.States.Count);
        Assert.Equal("Draft", c.Initial.Code);
        Assert.False(c.Initial.Locked);
        Assert.Empty(PackFiles.Errors(WorkflowDefinitionParser.Validate(PackFiles.Variation(), "Variation", PermissionMatrix.Instance)));
        // The register authorisation is the only way into a locked Effective state, and it is guarded.
        var approve = Assert.Single(c.WithRole("approve"));
        Assert.True(c.State(approve.To)!.Locked);
        Assert.Contains("AuthorisedApprover", approve.Guards);
        Assert.Contains("SubmitterNotApprover", approve.Guards);
        Assert.Contains("ExecutedCopyExists", approve.Guards);
    }

    [Fact]
    public void Unknown_members_and_bad_json_are_errors_not_silently_ignored()
    {
        var json = PackFiles.Read("workflows/contract-lifecycle.json").Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"runScript\": \"x\",", StringComparison.Ordinal);
        var (def, messages) = WorkflowDefinitionParser.Parse(json);
        Assert.Null(def);
        Assert.Contains(messages, m => m.Code == "invalid_json" && m.Severity == ValidationSeverity.Error);
        Assert.Contains(WorkflowDefinitionParser.Parse("{ not json").Messages, m => m.Code == "invalid_json");
        Assert.Contains(WorkflowDefinitionParser.Parse("null").Messages, m => m.Code == "invalid_json");
    }

    [Fact]
    public void A_transition_cannot_unlock_a_locked_record()
    {
        var d = PackFiles.Lifecycle();
        var locked = d.States.First(s => s.Locked && !s.Terminal).Code;
        var open = d.Initial.Code;
        var broken = d.With(extraTransitions: [new TransitionDefinition("SneakUnlock", "Sneak", [locked], open, "contracts.update", "plain", [], false, false, [], [], null)]);
        Assert.Contains("unlocks", ErrorsOf(broken));
    }

    [Fact]
    public void Approval_must_keep_its_guards_and_the_executed_copy_requirement()
    {
        var d = PackFiles.Lifecycle();
        Assert.Contains("approve_guard", ErrorsOf(d.With(transition: t => t.Role == "approve" ? t with { Guards = t.Guards.Where(g => g != "SubmitterNotApprover").ToList() } : t)));
        Assert.Contains("approve_guard", ErrorsOf(d.With(transition: t => t.Role == "approve" ? t with { Guards = t.Guards.Where(g => g != "AuthorisedApprover").ToList() } : t)));
        Assert.Contains("executed_copy_guard", ErrorsOf(d.With(transition: t => t.Role == "submitRegister" ? t with { Guards = t.Guards.Where(g => g != "ExecutedCopyExists").ToList() } : t)));
        Assert.Contains("reject_comment", ErrorsOf(d.With(transition: t => t.Role == "reject" ? t with { Guards = t.Guards.Where(g => g != "CommentRequired").ToList(), CommentRequired = false } : t)));
    }

    [Fact]
    public void Approval_that_lands_in_an_unlocked_state_is_refused()
    {
        var d = PackFiles.Lifecycle();
        var approve = d.WithRole("approve").Single();
        var unlocked = d.With(state: s => s.Code == approve.To ? s with { Locked = false } : s);
        Assert.Contains("approve_must_lock", ErrorsOf(unlocked));
    }

    [Fact]
    public void Dead_ends_unknown_references_and_vocabulary_breaches_are_found()
    {
        var d = PackFiles.Lifecycle();
        Assert.Contains("dead_end", DeadEnd(d));
        Assert.Contains("guard_unknown", ErrorsOf(d.With(transition: t => t.Role == "plain" ? t with { Guards = [.. t.Guards, "RunSql"] } : t)));
        Assert.Contains("transition_state", ErrorsOf(d.With(transition: t => t.Role == "approve" ? t with { To = "Nowhere" } : t)));
        Assert.Contains("transition_permission", ErrorsOf(d.With(transition: t => t.Role == "approve" ? t with { Permission = "contracts.made_up" } : t)));
        Assert.Contains("transition_role", ErrorsOf(d.With(transition: t => t.Role == "approve" ? t with { Role = "superuser" } : t)));
        Assert.Contains("state_role", ErrorsOf(d.With(state: s => s.Initial ? s with { Role = "godmode" } : s)));
        Assert.Contains("field_unknown", ErrorsOf(d.With(transition: t => t.Code == d.Transitions[0].Code ? t with { RequiredFields = ["passwordHash"] } : t)));
    }

    private static IEnumerable<string> DeadEnd(WorkflowDefinition d)
    {
        // Remove every way out of one non-terminal state.
        var victim = d.States.First(s => !s.Terminal && !s.Initial).Code;
        var cut = d.With(transition: t => t.From.Contains(victim) ? t with { From = t.From.Where(f => f != victim).ToList().Count == 0 ? [d.Initial.Code] : t.From.Where(f => f != victim).ToList() } : t);
        return ErrorsOf(cut);
    }

    [Fact]
    public void Exactly_one_initial_state_and_it_cannot_be_locked()
    {
        var d = PackFiles.Lifecycle();
        Assert.Contains("initial_state", ErrorsOf(d.With(state: s => s with { Initial = false })));
        Assert.Contains("initial_state", ErrorsOf(d.With(state: s => s with { Initial = true })));
        Assert.Contains("initial_locked", ErrorsOf(d.With(state: s => s.Initial ? s with { Locked = true } : s)));
        Assert.Contains("state_duplicate", ErrorsOf(d.With(state: s => s.Code == d.States[1].Code ? s with { Code = d.Initial.Code, Initial = false } : s)));
    }

    [Fact]
    public void Unlock_settings_are_bounded_and_second_person_confirmation_is_not_offered_yet()
    {
        var d = PackFiles.Lifecycle();
        Assert.Contains("unlock_max", ErrorsOf(d.With(settings: s => s with { Unlock = s.Unlock with { MaxMinutes = 100000 } })));
        Assert.Contains("unlock_default", ErrorsOf(d.With(settings: s => s with { Unlock = s.Unlock with { DefaultMinutes = s.Unlock.MaxMinutes + 1 } })));
        Assert.Contains("second_person", ErrorsOf(d.With(settings: s => s with { Unlock = s.Unlock with { RequireSecondPerson = true } })));
    }

    [Fact]
    public void Renewal_settings_cannot_copy_dates_or_start_a_successor_locked()
    {
        var d = PackFiles.Lifecycle();
        Assert.Contains("renewal_field", ErrorsOf(d.With(settings: s => s with { Renewal = s.Renewal with { CopyFields = [.. s.Renewal.CopyFields, "endDate"] } })));
        Assert.Contains("renewal_field", ErrorsOf(d.With(settings: s => s with { Renewal = s.Renewal with { CopyFields = ["registeredDateUtc"] } })));
        var lockedState = d.States.First(x => x.Locked && !x.Terminal).Code;
        Assert.Contains("renewal_state", ErrorsOf(d.With(settings: s => s with { Renewal = s.Renewal with { SuccessorInitialState = lockedState } })));
        Assert.Contains("renewal_state", ErrorsOf(d.With(settings: s => s with { Renewal = s.Renewal with { SuccessorInitialState = "Nope" } })));
        Assert.Contains("renewal_start", ErrorsOf(d.With(settings: s => s with { Renewal = s.Renewal with { StartDateRule = "Whenever" } })));
    }

    [Fact]
    public void Document_settings_name_their_types_and_the_executed_copy_is_not_directly_uploadable()
    {
        var d = PackFiles.Lifecycle();
        Assert.Contains("document_type", ErrorsOf(d.With(settings: s => s with { Documents = s.Documents with { ExecutedCopyType = "" } })));
        Assert.Contains("document_type", ErrorsOf(d.With(settings: s => s with { Documents = s.Documents with { FinalTypes = [] } })));
        Assert.Contains("document_type", ErrorsOf(d.With(settings: s => s with { Documents = s.Documents with { UploadTypes = [s.Documents.ExecutedCopyType] } })));
        Assert.Contains("approver_group", ErrorsOf(d.With(settings: s => s with { Approval = s.Approval with { ApproverGroupKey = " " } })));
    }

    [Fact]
    public void A_Variation_workflow_names_only_allowed_fields_and_must_apply_on_approval()
    {
        var v = PackFiles.Variation();
        string[] Check(WorkflowDefinition x) => PackFiles.Errors(WorkflowDefinitionParser.Validate(x, "Variation", PermissionMatrix.Instance)).ToArray();
        Assert.Contains("variation_field", Check(v.With(settings: s => s with { Variation = s.Variation with { ChangeableFields = [.. s.Variation.ChangeableFields, "classification"] } })));
        Assert.Contains("variation_field", Check(v.With(settings: s => s with { Variation = s.Variation with { ChangeableFields = [] } })));
        Assert.Contains("variation_legal", Check(v.With(settings: s => s with { Variation = s.Variation with { LegalReview = "Maybe" } })));
        Assert.Contains("subject", ErrorsOf(v, "Contract"));
    }

    [Fact]
    public void The_closed_vocabularies_cover_every_guard_role_and_state_role_the_pack_uses()
    {
        foreach (var d in new[] { PackFiles.Lifecycle(), PackFiles.Variation() })
        {
            foreach (var t in d.Transitions)
            {
                Assert.Contains(t.Role, WorkflowVocabulary.TransitionRoles);
                Assert.All(t.Guards, g => Assert.Contains(g, WorkflowVocabulary.Guards));
            }
            foreach (var s in d.States.Where(s => s.Role is not null)) Assert.Contains(s.Role!, WorkflowVocabulary.StateRoles);
        }
        // Managed roles are only reachable through their own services, never through the generic transition call.
        foreach (var role in new[] { "approve", "submitRegister", "returnLegal", "clearLegal", "openVariation", "startRenewal" }) Assert.Contains(role, WorkflowVocabulary.ManagedRoles);
    }
}

[Trait("Category", "Unit")]
public sealed class NotificationPolicyTests
{
    private static string Json => PackFiles.Read("notifications/notification-policy.json");
    private static NotificationPolicy Policy() { var (p, m) = NotificationPolicyParser.Parse(Json); Assert.NotNull(p); Assert.Empty(m); return p!; }
    private static IEnumerable<string> Errors(NotificationPolicy p) => PackFiles.Errors(NotificationPolicyParser.Validate(p, ClassificationScale.Default));

    private static NotificationPolicy WithTemplate(NotificationPolicy p, string name, MessageTemplate t)
        => new() { SchemaVersion = p.SchemaVersion, Reminders = p.Reminders, Content = p.Content, Delivery = p.Delivery, Events = p.Events, Templates = new Dictionary<string, MessageTemplate>(p.Templates) { [name] = t } };
    private static NotificationPolicy WithEvents(NotificationPolicy p, IEnumerable<EventRule> events)
        => new() { SchemaVersion = p.SchemaVersion, Reminders = p.Reminders, Content = p.Content, Delivery = p.Delivery, Events = events.ToList(), Templates = p.Templates };

    [Fact]
    public void The_JBS_policy_is_valid_and_defines_the_required_reminder_rules()
    {
        var p = Policy();
        Assert.Empty(Errors(p));
        Assert.Equal([30, 60, 90, 120], p.Reminders.AllowedLeadDays);
        Assert.NotNull(p.RuleFor("reminder.reviewDate"));
        Assert.NotNull(p.RuleFor("reminder.endDate"));
        Assert.NotNull(p.RuleFor("reminder.criticalNotice"));
        Assert.True(p.Reminders.Evergreen.SuppressEndDate);
        Assert.True(p.Reminders.Evergreen.KeepReviewDate);
        Assert.True(p.Reminders.CriticalNotice.Enabled);
        Assert.Equal("Restricted", p.Content.WithholdTitleFromLevel);
        Assert.Equal("Legal Privileged", p.Content.WithholdEverythingFromLevel);
    }

    [Fact]
    public void Templates_hold_plain_text_and_allowlisted_placeholders_only()
    {
        var p = Policy();
        var name = p.Templates.Keys.First();
        Assert.Contains("placeholder_unknown", Errors(WithTemplate(p, name, new("Hello {{user.password}}", "Body"))));
        Assert.Contains("template_syntax", Errors(WithTemplate(p, name, new("Hello", "{% if x %}secret{% endif %}"))));
        Assert.Contains("template_syntax", Errors(WithTemplate(p, name, new("Hello", "<script>alert(1)</script>"))));
        Assert.Contains("template_syntax", Errors(WithTemplate(p, name, new("Hello", "{{ contract.title | upper"))));
        Assert.Contains("subject_control", Errors(WithTemplate(p, name, new("Line\r\nBcc: attacker@example.test", "Body"))));
        Assert.Contains("subject", Errors(WithTemplate(p, name, new("", "Body"))));
        Assert.Contains("body", Errors(WithTemplate(p, name, new("Subject", new string('x', 3001)))));
        Assert.Empty(Errors(WithTemplate(p, name, new("{{contract.reference}} is due {{due.date}}", "{{actor.name}} {{ link }} in {{days.remaining}} days"))));
    }

    [Fact]
    public void Events_recipients_channels_and_templates_must_come_from_the_vocabulary()
    {
        var p = Policy();
        var first = p.Events[0];
        Assert.Contains("event_unknown", Errors(WithEvents(p, p.Events.Append(first with { Event = "made.up" }))));
        Assert.Contains("event_duplicate", Errors(WithEvents(p, p.Events.Append(first))));
        Assert.Contains("recipient_unknown", Errors(WithEvents(p, p.Events.Skip(1).Append(first with { Recipients = ["Everyone"] }))));
        Assert.Contains("recipients", Errors(WithEvents(p, p.Events.Skip(1).Append(first with { Recipients = [] }))));
        Assert.Contains("channel_unknown", Errors(WithEvents(p, p.Events.Skip(1).Append(first with { Channels = ["Sms"] }))));
        Assert.Contains("template_missing", Errors(WithEvents(p, p.Events.Skip(1).Append(first with { Template = "missing" }))));
        Assert.Contains("mandatory_inapp", Errors(WithEvents(p, p.Events.Skip(1).Append(first with { Mandatory = true, Channels = ["Email"] }))));
    }

    [Fact]
    public void Reminder_lead_times_content_thresholds_and_delivery_limits_are_bounded()
    {
        var p = Policy();
        NotificationPolicy With(ReminderRules? r = null, ContentRules? c = null, DeliveryRules? d = null, int? schema = null)
            => new() { SchemaVersion = schema ?? p.SchemaVersion, Reminders = r ?? p.Reminders, Content = c ?? p.Content, Delivery = d ?? p.Delivery, Events = p.Events, Templates = p.Templates };
        Assert.Contains("lead_days", Errors(With(r: p.Reminders with { AllowedLeadDays = [0, 30] })));
        Assert.Contains("lead_days", Errors(With(r: p.Reminders with { AllowedLeadDays = [30, 30] })));
        Assert.Contains("lead_default", Errors(With(r: p.Reminders with { DefaultLeadDays = 45 })));
        Assert.Contains("critical_offsets", Errors(With(r: p.Reminders with { CriticalNotice = p.Reminders.CriticalNotice with { OffsetsDays = [7, 7] } })));
        Assert.Contains("level", Errors(With(c: p.Content with { WithholdTitleFromLevel = "Top Secret" })));
        Assert.Contains("level_order", Errors(With(c: new ContentRules("Legal Privileged", "Internal"))));
        Assert.Contains("attempts", Errors(With(d: p.Delivery with { MaxAttempts = 0 })));
        Assert.Contains("backoff", Errors(With(d: p.Delivery with { BackoffMinutes = [0] })));
        Assert.Contains("schema_version", Errors(With(schema: 2)));
    }

    [Fact]
    public void Unknown_members_are_errors_and_backoff_grows_then_holds()
    {
        var (p, m) = NotificationPolicyParser.Parse(Json.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"sendHook\": \"http://x\",", StringComparison.Ordinal));
        Assert.Null(p);
        Assert.Contains(m, x => x.Code == "invalid_json");
        var policy = Policy();
        Assert.True(policy.BackoffFor(2) > policy.BackoffFor(1));
        Assert.Equal(policy.BackoffFor(policy.Delivery.BackoffMinutes.Count), policy.BackoffFor(99));
    }
}
