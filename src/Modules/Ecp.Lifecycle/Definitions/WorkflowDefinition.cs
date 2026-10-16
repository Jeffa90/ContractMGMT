using System.Text.Json;
using System.Text.Json.Serialization;
using Ecp.Configuration;
using Ecp.Identity.Access;
using Ecp.SharedKernel;

namespace Ecp.Lifecycle.Definitions;

/// <summary>
/// The closed vocabularies a workflow definition may use. A definition is data: it can name only these guards, roles and effects, never code, SQL or expressions.
/// The meaning of each lives in the engine and the services that own the step; the states, their names and which transitions exist are configuration.
/// </summary>
public static class WorkflowVocabulary
{
    public static readonly IReadOnlyList<string> Guards =
    [
        "LegalRequired", "LegalNotRequired", "CommentRequired", "ReasonRequired", "FinalDocumentExists", "ExecutedCopyExists", "NoExecutedCopy", "AuthorisedApprover", "SubmitterNotApprover",
        "ParentRemainsLocked", "NoOtherOpenVariation", "NoOpenRenewal", "RenewalSuccessorEffective", "Authorised", "RetentionChecksPass", "NoLegalHold", "ControlledFieldChanged",
        "VariationOutcomeApplied", "VariationOutcomeNotApplied", "SignatureVoidedOrDeclined", "RequiredFieldsPresent", "RequiredDocumentsPresent", "ClassificationClearance"
    ];

    /// <summary>What a transition does beyond changing state. "plain" has no side effect; the others are carried out by the service that owns the step and cannot be run through the generic transition call.</summary>
    public static readonly IReadOnlyList<string> TransitionRoles =
    [
        "plain", "submitLegal", "recallLegal", "beginLegal", "returnLegal", "clearLegal", "resubmitLegal", "returnToLegal", "sendSignature", "returnToDrafting", "submitRegister", "approve", "reject",
        "openVariation", "applyVariation", "rejectVariation", "cancelVariation", "startRenewal", "completeRenewal", "cancelRenewal", "withdraw", "flag", "archive", "cancel"
    ];

    /// <summary>Transitions the generic call refuses because their step has its own data and rules (a Legal round, an approval request, a Variation, a renewal, an envelope).</summary>
    public static readonly IReadOnlySet<string> ManagedRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "submitLegal", "recallLegal", "beginLegal", "returnLegal", "clearLegal", "resubmitLegal", "returnToLegal", "sendSignature", "submitRegister", "approve", "reject",
        "openVariation", "applyVariation", "rejectVariation", "cancelVariation", "startRenewal", "completeRenewal", "cancelRenewal", "cancel"
    };

    public static readonly IReadOnlyList<string> StateRoles =
    [
        "initial", "legalQueue", "legalReview", "legalRework", "legalCleared", "drafting", "signature", "approval", "effective", "variation", "renewal", "suspended", "terminated", "ended",
        "flagged", "archived", "withdrawn", "applied", "rejected", "cancelled"
    ];

    public static readonly IReadOnlyList<string> RequiredFieldNames =
        ["title", "description", "supplier", "category", "currencyCode", "estimatedValue", "startDate", "effectiveDate", "reviewDate", "endDate", "extensionType", "noticePeriodDays"];
}

public sealed record StateDefinition(string Code, string Display, string? Role, bool Locked, bool FreezeContent, string DocumentPolicy, string? LockReason, bool Terminal, bool Initial);

public sealed record TransitionDefinition(
    string Code, string Display, IReadOnlyList<string> From, string To, string Permission, string Role, IReadOnlyList<string> Guards, bool CommentRequired, bool ReasonRequired,
    IReadOnlyList<string> RequiredFields, IReadOnlyList<string> RequiredDocumentTypes, string? Description)
{
    public bool Managed => WorkflowVocabulary.ManagedRoles.Contains(Role);
}

public sealed record DocumentSettings(string ExecutedCopyType, IReadOnlyList<string> FinalTypes, string LegalMarkupType, string ApprovalEvidenceType, IReadOnlyList<string>? UploadTypes = null);
/// <summary>The approval step of a workflow. Without a <see cref="Matrix"/> the single <see cref="ApproverGroupKey"/> decides (the reference model); with one, the named approval matrix resolves the steps for each submission.</summary>
public sealed record ApprovalSettings(string ApproverGroupKey, bool RequireExecutedCopy, string? Matrix = null);
public sealed record LegalSettings(bool ReworkReasonRequired);
public sealed record UnlockSettings(int DefaultMinutes, int MaxMinutes, bool RequireSecondPerson, bool Enabled = true);
public sealed record RenewalSettings(string SuccessorInitialState, IReadOnlyList<string> CopyFields, IReadOnlyList<string> CopyDocumentTypes, string StartDateRule);
public sealed record VariationSettings(string? Workflow, string LegalReview, IReadOnlyList<string> ChangeableFields);

public sealed record WorkflowSettings(DocumentSettings Documents, ApprovalSettings Approval, LegalSettings Legal, UnlockSettings Unlock, RenewalSettings Renewal, VariationSettings Variation);

public sealed class WorkflowDefinition
{
    public required string Workflow { get; init; }
    public required string Subject { get; init; }
    public required int SchemaVersion { get; init; }
    public string? Description { get; init; }
    public required WorkflowSettings Settings { get; init; }
    public required IReadOnlyList<StateDefinition> States { get; init; }
    public required IReadOnlyList<TransitionDefinition> Transitions { get; init; }

    public StateDefinition? State(string? code) => States.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.Ordinal));
    public StateDefinition? StateWithRole(string role) => States.FirstOrDefault(s => s.Role == role);
    public StateDefinition Initial => States.First(s => s.Initial);
    public TransitionDefinition? Transition(string? code) => Transitions.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.Ordinal));
    public IEnumerable<TransitionDefinition> From(string status) => Transitions.Where(t => t.From.Contains(status, StringComparer.Ordinal));
    public IEnumerable<TransitionDefinition> WithRole(string role) => Transitions.Where(t => t.Role == role);
    public string DisplayOf(string status) => State(status)?.Display ?? status;
    public StateEffectsView EffectsOf(string status) => State(status) is { } s ? new(s.Locked, s.FreezeContent, s.DocumentPolicy, s.LockReason) : new(false, false, "Open", null);
}

public sealed record StateEffectsView(bool Lock, bool FreezeContent, string DocumentPolicy, string? LockReason);

/// <summary>Reads and checks workflow definitions. Unknown properties are errors, so a typo in a definition is found at validation and never silently ignored.</summary>
public static class WorkflowDefinitionParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
        NumberHandling = JsonNumberHandling.Strict
    };

    private sealed class RawDefinition
    {
        public int SchemaVersion { get; set; }
        public string? Workflow { get; set; }
        public string? Subject { get; set; }
        public string? Description { get; set; }
        public RawSettings? Settings { get; set; }
        public List<RawState>? States { get; set; }
        public List<RawTransition>? Transitions { get; set; }
    }

    private sealed class RawSettings
    {
        public RawDocuments? Documents { get; set; }
        public RawApproval? Approval { get; set; }
        public RawLegal? Legal { get; set; }
        public RawUnlock? Unlock { get; set; }
        public RawRenewal? Renewal { get; set; }
        public RawVariation? Variation { get; set; }
    }

    private sealed class RawDocuments { public string? ExecutedCopyType { get; set; } public List<string>? FinalTypes { get; set; } public string? LegalMarkupType { get; set; } public string? ApprovalEvidenceType { get; set; } public List<string>? UploadTypes { get; set; } }
    private sealed class RawApproval { public string? ApproverGroupKey { get; set; } public bool RequireExecutedCopy { get; set; } = true; public string? Matrix { get; set; } }
    private sealed class RawLegal { public bool ReworkReasonRequired { get; set; } = true; }
    private sealed class RawUnlock { public int DefaultMinutes { get; set; } = 60; public int MaxMinutes { get; set; } = 240; public bool RequireSecondPerson { get; set; } public bool Enabled { get; set; } = true; }
    private sealed class RawRenewal { public string? SuccessorInitialState { get; set; } public List<string>? CopyFields { get; set; } public List<string>? CopyDocumentTypes { get; set; } public string? StartDateRule { get; set; } }
    private sealed class RawVariation { public string? Workflow { get; set; } public string? LegalReview { get; set; } public List<string>? ChangeableFields { get; set; } }

    private sealed class RawState
    {
        public string? Code { get; set; } public string? Display { get; set; } public string? Role { get; set; }
        public bool Locked { get; set; } public bool FreezeContent { get; set; } public string? DocumentPolicy { get; set; } public string? LockReason { get; set; }
        public bool Terminal { get; set; } public bool Initial { get; set; }
    }

    private sealed class RawTransition
    {
        public string? Code { get; set; } public string? Display { get; set; } public List<string>? From { get; set; } public string? To { get; set; } public string? Permission { get; set; }
        public string? Role { get; set; } public List<string>? Guards { get; set; } public bool CommentRequired { get; set; } public bool ReasonRequired { get; set; }
        public List<string>? RequiredFields { get; set; } public List<string>? RequiredDocumentTypes { get; set; } public string? Description { get; set; }
    }

    /// <summary>Parses a definition. Structural problems are returned as messages; a null definition means it could not be read at all.</summary>
    public static (WorkflowDefinition? Definition, List<ValidationMessage> Messages) Parse(string json)
    {
        var messages = new List<ValidationMessage>();
        RawDefinition? raw;
        try { raw = JsonSerializer.Deserialize<RawDefinition>(json, Options); }
        catch (JsonException ex)
        {
            messages.Add(Error("$", "invalid_json", "The workflow definition could not be read: " + ex.Message.Split('\n')[0]));
            return (null, messages);
        }
        if (raw is null) { messages.Add(Error("$", "invalid_json", "The workflow definition is empty.")); return (null, messages); }

        var docs = raw.Settings?.Documents;
        var settings = new WorkflowSettings(
            new DocumentSettings(docs?.ExecutedCopyType ?? "", docs?.FinalTypes ?? [], docs?.LegalMarkupType ?? "", docs?.ApprovalEvidenceType ?? "", docs?.UploadTypes),
            new ApprovalSettings(raw.Settings?.Approval?.ApproverGroupKey ?? "", raw.Settings?.Approval?.RequireExecutedCopy ?? true, string.IsNullOrWhiteSpace(raw.Settings?.Approval?.Matrix) ? null : raw.Settings!.Approval!.Matrix!.Trim()),
            new LegalSettings(raw.Settings?.Legal?.ReworkReasonRequired ?? true),
            new UnlockSettings(raw.Settings?.Unlock?.DefaultMinutes ?? 60, raw.Settings?.Unlock?.MaxMinutes ?? 240, raw.Settings?.Unlock?.RequireSecondPerson ?? false, raw.Settings?.Unlock?.Enabled ?? true),
            new RenewalSettings(raw.Settings?.Renewal?.SuccessorInitialState ?? "", raw.Settings?.Renewal?.CopyFields ?? [], raw.Settings?.Renewal?.CopyDocumentTypes ?? [], raw.Settings?.Renewal?.StartDateRule ?? "None"),
            new VariationSettings(raw.Settings?.Variation?.Workflow, raw.Settings?.Variation?.LegalReview ?? "Optional", raw.Settings?.Variation?.ChangeableFields ?? []));
        var states = (raw.States ?? []).Select(s => new StateDefinition(s.Code?.Trim() ?? "", s.Display?.Trim() ?? s.Code?.Trim() ?? "", s.Role, s.Locked, s.FreezeContent, s.DocumentPolicy ?? "Open", s.LockReason, s.Terminal, s.Initial)).ToList();
        var transitions = (raw.Transitions ?? []).Select(t => new TransitionDefinition(t.Code?.Trim() ?? "", t.Display?.Trim() ?? t.Code?.Trim() ?? "", t.From ?? [], t.To?.Trim() ?? "", t.Permission?.Trim() ?? "",
            t.Role ?? "plain", t.Guards ?? [], t.CommentRequired, t.ReasonRequired, t.RequiredFields ?? [], t.RequiredDocumentTypes ?? [], t.Description)).ToList();
        var definition = new WorkflowDefinition
        {
            Workflow = raw.Workflow?.Trim() ?? "", Subject = raw.Subject?.Trim() ?? "", SchemaVersion = raw.SchemaVersion, Description = raw.Description,
            Settings = settings, States = states, Transitions = transitions
        };
        return (definition, messages);
    }

    private static ValidationMessage Error(string path, string code, string message) => new(ValidationSeverity.Error, path, code, message);
    private static ValidationMessage Warn(string path, string code, string message) => new(ValidationSeverity.Warning, path, code, message);

    /// <summary>Checks a parsed definition for consistency: references resolve, vocabularies are respected, every non-terminal state has a way out and the protections cannot be loosened by accident.</summary>
    public static IReadOnlyList<ValidationMessage> Validate(WorkflowDefinition d, string expectedSubject, PermissionMatrix matrix)
    {
        var m = new List<ValidationMessage>();
        if (d.SchemaVersion != 1) m.Add(Error("$.schemaVersion", "schema_version", "Only schema version 1 is supported."));
        if (string.IsNullOrWhiteSpace(d.Workflow)) m.Add(Error("$.workflow", "workflow_required", "The workflow needs a code."));
        if (!string.Equals(d.Subject, expectedSubject, StringComparison.Ordinal)) m.Add(Error("$.subject", "subject", $"This definition must describe a {expectedSubject}."));

        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < d.States.Count; i++)
        {
            var s = d.States[i];
            var path = $"$.states[{i}]";
            if (string.IsNullOrWhiteSpace(s.Code) || s.Code.Length > 100 || !s.Code.All(char.IsLetterOrDigit)) m.Add(Error(path + ".code", "state_code", "A state code is letters and digits only (up to 100)."));
            else if (!codes.Add(s.Code)) m.Add(Error(path + ".code", "state_duplicate", $"State '{s.Code}' is defined twice."));
            if (s.Display.Length is 0 or > 100) m.Add(Error(path + ".display", "state_display", "A state needs a display name of up to 100 characters."));
            if (s.Role is not null && !WorkflowVocabulary.StateRoles.Contains(s.Role)) m.Add(Error(path + ".role", "state_role", $"State role '{s.Role}' is not recognised."));
            if (!Ecp.Contracts.Domain.DocumentPolicies.IsKnown(s.DocumentPolicy)) m.Add(Error(path + ".documentPolicy", "document_policy", "The document policy must be Open, LegalOnly or Closed."));
            if (s.LockReason is { Length: > 200 }) m.Add(Error(path + ".lockReason", "lock_reason", "A lock reason is at most 200 characters."));
        }
        if (d.States.Count(s => s.Initial) != 1) m.Add(Error("$.states", "initial_state", "Exactly one state must be marked initial."));
        else if (d.States.First(s => s.Initial).Locked) m.Add(Error("$.states", "initial_locked", "The initial state cannot be locked: a new record must be editable."));
        foreach (var role in d.States.Where(s => s.Role is not null).GroupBy(s => s.Role!).Where(g => g.Count() > 1))
            m.Add(Error("$.states", "role_duplicate", $"Role '{role.Key}' is given to more than one state."));

        var tcodes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < d.Transitions.Count; i++)
        {
            var t = d.Transitions[i];
            var path = $"$.transitions[{i}]";
            if (string.IsNullOrWhiteSpace(t.Code) || !t.Code.All(char.IsLetterOrDigit)) m.Add(Error(path + ".code", "transition_code", "A transition code is letters and digits only."));
            else if (!tcodes.Add(t.Code)) m.Add(Error(path + ".code", "transition_duplicate", $"Transition '{t.Code}' is defined twice."));
            if (t.From.Count == 0) m.Add(Error(path + ".from", "transition_from", "A transition needs at least one source state."));
            foreach (var f in t.From) if (!codes.Contains(f)) m.Add(Error(path + ".from", "transition_state", $"Source state '{f}' is not defined."));
            if (!codes.Contains(t.To)) m.Add(Error(path + ".to", "transition_state", $"Target state '{t.To}' is not defined."));
            if (!matrix.IsKnown(t.Permission)) m.Add(Error(path + ".permission", "transition_permission", $"Permission '{t.Permission}' is not in the permission catalogue."));
            if (!WorkflowVocabulary.TransitionRoles.Contains(t.Role)) m.Add(Error(path + ".role", "transition_role", $"Transition role '{t.Role}' is not recognised."));
            foreach (var g in t.Guards) if (!WorkflowVocabulary.Guards.Contains(g)) m.Add(Error(path + ".guards", "guard_unknown", $"Guard '{g}' is not in the guard vocabulary."));
            foreach (var f in t.RequiredFields) if (!WorkflowVocabulary.RequiredFieldNames.Contains(f)) m.Add(Error(path + ".requiredFields", "field_unknown", $"Field '{f}' cannot be required."));
            if (t.To is { Length: > 0 } to && d.State(to) is { } target && t.From.Any(f => d.State(f) is { Locked: true }) && !target.Locked)
                m.Add(Error(path, "unlocks", $"Transition '{t.Code}' would move a locked record to the unlocked state '{target.Display}'. A transition cannot unlock a record."));
            if (t.Role is "approve" or "applyVariation" && d.State(t.To) is { } approved && d.Subject == "Contract" && !approved.Locked)
                m.Add(Error(path, "approve_must_lock", "Register authorisation must land in a locked state."));
            if (t.Role == "approve" && !t.Guards.Contains("AuthorisedApprover")) m.Add(Error(path + ".guards", "approve_guard", "An approving transition must carry the AuthorisedApprover guard."));
            if (t.Role == "approve" && !t.Guards.Contains("SubmitterNotApprover")) m.Add(Error(path + ".guards", "approve_guard", "An approving transition must carry the SubmitterNotApprover guard."));
            if ((t.Role is "approve" or "submitRegister") && !t.Guards.Contains("ExecutedCopyExists")) m.Add(Error(path + ".guards", "executed_copy_guard", "Submitting for, or giving, register authorisation requires the ExecutedCopyExists guard."));
            if (t.Role == "reject" && !t.Guards.Contains("CommentRequired") && !t.CommentRequired) m.Add(Error(path, "reject_comment", "A rejection needs a comment."));
            if (t.Role == "returnLegal" && !t.Guards.Contains("CommentRequired") && !t.CommentRequired) m.Add(Error(path, "rework_reason", "Returning for rework needs a reason."));
        }

        // Every state that is not terminal needs a way out (removes dead ends, CON-04), and every state should be reachable from the initial state.
        foreach (var s in d.States.Where(s => !s.Terminal && s.Code.Length > 0))
            if (!d.Transitions.Any(t => t.From.Contains(s.Code, StringComparer.Ordinal))) m.Add(Error("$.states", "dead_end", $"State '{s.Display}' is not terminal but has no way out."));
        foreach (var s in d.States.Where(s => s.Terminal))
            if (d.Transitions.Any(t => t.From.Contains(s.Code, StringComparer.Ordinal))) m.Add(Error("$.states", "terminal_exit", $"State '{s.Display}' is terminal but has a transition out of it."));
        if (d.States.Any(s => s.Initial))
        {
            var reached = new HashSet<string>(StringComparer.Ordinal) { d.Initial.Code };
            var queue = new Queue<string>(reached);
            while (queue.Count > 0)
                foreach (var t in d.From(queue.Dequeue()))
                    if (reached.Add(t.To)) queue.Enqueue(t.To);
            foreach (var s in d.States.Where(s => !reached.Contains(s.Code))) m.Add(Warn("$.states", "unreachable", $"State '{s.Display}' cannot be reached from the initial state."));
        }

        var doc = d.Settings.Documents;
        if (string.IsNullOrWhiteSpace(doc.ExecutedCopyType)) m.Add(Error("$.settings.documents.executedCopyType", "document_type", "Name the document type used for the executed copy."));
        if (doc.FinalTypes.Count == 0) m.Add(Error("$.settings.documents.finalTypes", "document_type", "Name at least one document type that counts as the final document."));
        if (doc.UploadTypes is { } upload)
        {
            if (upload.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 50)) m.Add(Error("$.settings.documents.uploadTypes", "document_type", "Document types are up to 50 characters."));
            if (upload.Contains(doc.ExecutedCopyType, StringComparer.Ordinal)) m.Add(Error("$.settings.documents.uploadTypes", "document_type", "The executed-copy type is attached by the signing step, not uploaded directly."));
        }
        if (string.IsNullOrWhiteSpace(doc.LegalMarkupType)) m.Add(Error("$.settings.documents.legalMarkupType", "document_type", "Name the document type used for Legal mark-ups."));
        if (string.IsNullOrWhiteSpace(d.Settings.Approval.ApproverGroupKey)) m.Add(Error("$.settings.approval.approverGroupKey", "approver_group", "Name the approver group that gives register authorisation."));
        if (d.Settings.Approval.Matrix is { } matrixCode && !System.Text.RegularExpressions.Regex.IsMatch(matrixCode, "^[a-z0-9][a-z0-9-]{0,49}$", System.Text.RegularExpressions.RegexOptions.NonBacktracking))
            m.Add(Error("$.settings.approval.matrix", "approval_matrix", "The approval matrix is named by its code: lower-case letters, digits and hyphens (up to 50)."));
        var unlock = d.Settings.Unlock;
        if (unlock.MaxMinutes is < 5 or > 1440) m.Add(Error("$.settings.unlock.maxMinutes", "unlock_max", "The longest unlock must be between 5 minutes and 24 hours."));
        if (unlock.DefaultMinutes < 5 || unlock.DefaultMinutes > unlock.MaxMinutes) m.Add(Error("$.settings.unlock.defaultMinutes", "unlock_default", "The default unlock must be at least 5 minutes and no more than the longest unlock."));
        if (unlock.RequireSecondPerson) m.Add(Error("$.settings.unlock.requireSecondPerson", "second_person", "Second-person confirmation of an unlock is not supported yet. Leave it off; the unlock is reason-bound, time-limited, re-authenticated and audited."));

        if (d.Subject == "Contract")
        {
            foreach (var role in new[] { "approve", "submitRegister", "openVariation", "applyVariation", "rejectVariation", "cancelVariation", "startRenewal" })
                if (!d.WithRole(role).Any()) m.Add(Warn("$.transitions", "role_missing", $"No transition has the role '{role}'; the matching step is not available."));
            var ren = d.Settings.Renewal;
            if (d.WithRole("startRenewal").Any())
            {
                if (d.State(ren.SuccessorInitialState) is not { } initial) m.Add(Error("$.settings.renewal.successorInitialState", "renewal_state", "The successor's starting state is not a defined state."));
                else if (initial.Locked) m.Add(Error("$.settings.renewal.successorInitialState", "renewal_state", "A successor cannot start in a locked state."));
                foreach (var f in ren.CopyFields) if (!RenewalCopyFields.Contains(f)) m.Add(Error("$.settings.renewal.copyFields", "renewal_field", $"Field '{f}' cannot be copied to a successor."));
                if (ren.StartDateRule is not ("None" or "DayAfterPredecessorEnd")) m.Add(Error("$.settings.renewal.startDateRule", "renewal_start", "The start date rule must be None or DayAfterPredecessorEnd."));
            }
            if (d.Settings.Variation.Workflow is { Length: > 0 } && d.Settings.Variation.Workflow == d.Workflow) m.Add(Error("$.settings.variation.workflow", "variation_self", "The Variation workflow must be a different workflow."));
        }
        else
        {
            foreach (var f in d.Settings.Variation.ChangeableFields) if (!Ecp.Contracts.Domain.Contract.Editable.VariationFields.Contains(f)) m.Add(Error("$.settings.variation.changeableFields", "variation_field", $"Field '{f}' cannot be changed by a Variation. Allowed: {string.Join(", ", Ecp.Contracts.Domain.Contract.Editable.VariationFields)}."));
            if (d.Settings.Variation.ChangeableFields.Count == 0) m.Add(Error("$.settings.variation.changeableFields", "variation_field", "Name at least one field a Variation may change."));
            if (d.Settings.Variation.LegalReview is not ("None" or "Optional" or "Required")) m.Add(Error("$.settings.variation.legalReview", "variation_legal", "The Variation Legal review setting must be None, Optional or Required."));
            if (!d.WithRole("approve").Any()) m.Add(Error("$.transitions", "role_missing", "A Variation workflow needs an approving transition."));
            else if (d.State(d.WithRole("approve").First().To)?.Role != "applied") m.Add(Error("$.transitions", "approve_target", "A Variation's approval must land in the state with the 'applied' role."));
        }
        return m;
    }

    /// <summary>Fields of a predecessor that a renewal may copy to its successor (never dates, status, audit or provenance).</summary>
    public static readonly IReadOnlyList<string> RenewalCopyFields =
        ["description", "currencyCode", "estimatedValue", "extensionType", "noticePeriodDays", "criticalNotice", "reminderLeadDays", "legalReviewRequired"];
}
