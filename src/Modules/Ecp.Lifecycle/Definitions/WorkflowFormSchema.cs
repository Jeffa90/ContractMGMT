using System.Text.Json.Nodes;
using Ecp.Configuration.Forms;
using static Ecp.Configuration.Forms.FormFields;

namespace Ecp.Lifecycle.Definitions;

/// <summary>
/// The workflow forms: the contract workflow and the Variation workflow share one shape, so one form describes both. Every choice is made from the closed vocabularies in <see cref="WorkflowVocabulary"/>; a
/// state, a transition, a guard or a role that is not on the lists cannot be entered. The words next to each choice say what it does, so that nobody has to read a definition to understand it.
/// </summary>
internal sealed class WorkflowFormSchemas : IFormSchemaSource
{
    private static readonly IReadOnlyDictionary<string, string> GuardMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LegalRequired"] = "Legal review is required for this record",
        ["LegalNotRequired"] = "Legal review is not required for this record",
        ["CommentRequired"] = "A comment is entered",
        ["ReasonRequired"] = "A reason is entered",
        ["FinalDocumentExists"] = "A final document is attached",
        ["ExecutedCopyExists"] = "A signed (executed) copy is attached",
        ["NoExecutedCopy"] = "No signed copy is attached yet",
        ["AuthorisedApprover"] = "The person is an authorised approver",
        ["SubmitterNotApprover"] = "The approver is not the person who submitted it",
        ["ParentRemainsLocked"] = "The agreement being varied stays locked",
        ["NoOtherOpenVariation"] = "No other variation is open on the agreement",
        ["NoOpenRenewal"] = "No renewal is already open",
        ["RenewalSuccessorEffective"] = "The renewal's successor is in force",
        ["Authorised"] = "The record has register authorisation",
        ["RetentionChecksPass"] = "The retention checks pass",
        ["NoLegalHold"] = "No legal hold applies",
        ["ControlledFieldChanged"] = "A controlled field was changed",
        ["VariationOutcomeApplied"] = "The variation was applied",
        ["VariationOutcomeNotApplied"] = "The variation was not applied",
        ["SignatureVoidedOrDeclined"] = "The signature request was voided or declined",
        ["RequiredFieldsPresent"] = "The required fields are filled in",
        ["RequiredDocumentsPresent"] = "The required documents are attached",
        ["ClassificationClearance"] = "The person is cleared for the record's classification",
    };

    private static readonly IReadOnlyDictionary<string, string> RoleMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["plain"] = "An ordinary step (no special handling)",
        ["submitLegal"] = "Send to Legal for review", ["recallLegal"] = "Recall from Legal", ["beginLegal"] = "Legal starts the review", ["returnLegal"] = "Legal returns it for rework",
        ["clearLegal"] = "Legal clears it", ["resubmitLegal"] = "Send back to Legal after rework", ["returnToLegal"] = "Return to Legal",
        ["sendSignature"] = "Send for signature", ["returnToDrafting"] = "Return to drafting", ["submitRegister"] = "Submit for register authorisation", ["approve"] = "Approve (register authorisation)",
        ["reject"] = "Reject", ["openVariation"] = "Raise a variation", ["applyVariation"] = "Apply the variation", ["rejectVariation"] = "Reject the variation", ["cancelVariation"] = "Cancel the variation",
        ["startRenewal"] = "Start a renewal", ["completeRenewal"] = "Complete the renewal", ["cancelRenewal"] = "Cancel the renewal", ["withdraw"] = "Withdraw", ["flag"] = "Flag for attention",
        ["archive"] = "Archive", ["cancel"] = "Cancel",
    };

    private static readonly IReadOnlyDictionary<string, string> StateRoleMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["initial"] = "Where new records start", ["legalQueue"] = "Waiting for Legal", ["legalReview"] = "With Legal", ["legalRework"] = "Returned by Legal for rework", ["legalCleared"] = "Cleared by Legal",
        ["drafting"] = "Being drafted", ["signature"] = "Out for signature", ["approval"] = "Awaiting register authorisation", ["effective"] = "In force", ["variation"] = "Being varied", ["renewal"] = "Being renewed",
        ["suspended"] = "Suspended", ["terminated"] = "Terminated", ["ended"] = "Ended", ["flagged"] = "Flagged", ["archived"] = "Archived", ["withdrawn"] = "Withdrawn", ["applied"] = "Applied",
        ["rejected"] = "Rejected", ["cancelled"] = "Cancelled",
    };

    private static IEnumerable<FormOption> Meaning(IEnumerable<string> codes, IReadOnlyDictionary<string, string> meanings)
        => codes.Select(c => new FormOption(c, meanings.TryGetValue(c, out var m) ? m + " (" + c + ")" : c));

    private static IReadOnlyList<FormOption> States(JsonObject document)
        => (document["states"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => (Code: FormBinder.Scalar(s["code"]), Display: FormBinder.Scalar(s["display"]))).Where(s => s.Code.Length > 0)
            .Select(s => new FormOption(s.Code, s.Display.Length > 0 ? s.Display + " (" + s.Code + ")" : s.Code)).ToList();

    public IEnumerable<FormSchema> Schemas()
    {
        foreach (var (kind, title, subject) in new[] { (WorkflowKinds.ContractLifecycle, "Contract workflow", "Contract"), (WorkflowKinds.Variation, "Variation workflow", "Variation") })
            yield return Build(kind, title, subject);
    }

    private static FormSchema Build(string kind, string title, string subject) => new()
    {
        Kind = kind,
        Title = title,
        Intro = subject == "Contract"
            ? "The stages a contract passes through, who may move it from one stage to the next, and the checks each move needs. Nothing is published until the draft is checked, submitted and published by a second person."
            : "The stages a variation passes through and the fields it may change. Nothing is published until the draft is checked, submitted and published by a second person.",
        Sections =
        [
            new FormSection("identity", "About this workflow", "These identify the workflow. They rarely change.",
            [
                Text("workflow", "Workflow code", "A short name for this workflow. Example: contract-lifecycle.", "contract-lifecycle", required: true),
                Select("subject", "What it describes", [subject], "Fixed: this form edits the " + subject.ToLowerInvariant() + " workflow.", required: true, @default: subject),
                LongText("description", "Description", "Free text for your own records.", maxLength: 1000),
            ]),
            new FormSection("states", "Stages", "Each stage is a state a record can be in. Exactly one is the starting stage. A stage that is final has no way out; every other stage needs at least one way out.",
            [
                Rows("states", "Stages", "stage", "display",
                [
                    Text("code", "Stage code", "Letters and digits only. Transitions refer to this code, so do not change it once records use it. Example: Draft.", "Draft", required: true),
                    Text("display", "Name people see", "Shown on screens and in messages. Example: Draft.", "Draft", required: true),
                    Select("role", "What this stage means to the platform", Meaning(WorkflowVocabulary.StateRoles, StateRoleMeaning), "Some steps (legal review, signature, approval, variations, renewals) look for the stage that has this meaning. Leave blank for a plain stage."),
                    Boolean("initial", "Starting stage", "New records start here. Exactly one stage is the starting stage, and it cannot be locked."),
                    Boolean("terminal", "Final stage", "Nothing can happen after this stage."),
                    Boolean("locked", "Locked", "A locked record cannot be edited, except through an authorised variation. A transition can never unlock a record."),
                    Boolean("freezeContent", "Freeze content", "Keep the record's content as it was when it entered this stage."),
                    Select("documentPolicy", "Documents in this stage", ["Open", "LegalOnly", "Closed"], "Open: anyone who may edit can attach documents. LegalOnly: only Legal can. Closed: nobody can.", @default: "Open"),
                    Text("lockReason", "Reason shown when locked", "Up to 200 characters. Example: Awaiting register authorisation.", maxLength: 200),
                ], "The first stage listed is not special; mark the starting stage with the tick box."),
            ]),
            new FormSection("transitions", "Moves between stages", "Each move says which stages it starts from, where it goes, who may do it and what must be true first. Order is for readability only.",
            [
                Rows("transitions", "Moves", "move", "display",
                [
                    Text("code", "Move code", "Letters and digits only. Example: SubmitForReview.", "SubmitForReview", required: true),
                    Text("display", "Name people see", "The button label. Example: Submit for review.", "Submit for review", required: true),
                    ManyDerived("from", "Starts from", States, "Tick every stage this move can be made from."),
                    SelectDerived("to", "Goes to", States, "The stage the record is in afterwards.", required: true),
                    SelectFrom("permission", "Who may do it", "permissions", "The permission a person needs. The permission catalogue lists what each role holds.", required: true),
                    Select("role", "What the move does", Meaning(WorkflowVocabulary.TransitionRoles, RoleMeaning), "An ordinary step has no side effect. The others are carried out by the step that owns them (legal review, signing, approval, variations, renewals) and cannot be done as an ordinary move.", @default: "plain"),
                    Many("guards", "Checks that must pass first", Meaning(WorkflowVocabulary.Guards, GuardMeaning), "All ticked checks must pass before the move is allowed. An approving move must have both the authorised-approver and the submitter-is-not-approver checks."),
                    Boolean("commentRequired", "Ask for a comment", "The person must write a comment to make this move."),
                    Boolean("reasonRequired", "Ask for a reason", "The person must write a reason to make this move."),
                    ManyFrom("requiredFields", "Fields that must be filled in", "requiredFields", "These must be filled in before the move is allowed."),
                    Lines("requiredDocumentTypes", "Document types that must be attached", "One per line. Example: FinalAgreement.", "FinalAgreement"),
                    LongText("description", "What this move is for", "Free text for your own records.", maxLength: 500),
                ]),
            ]),
            new FormSection("settings", "Settings", "How documents, approval, Legal rework, unlock, renewal and variations behave in this workflow.",
            [
                Group("settings", "Settings",
                [
                    Group("documents", "Documents",
                    [
                        Text("executedCopyType", "Signed-copy document type", "The type of the executed copy, attached by the signing step. Example: ExecutedAgreement.", "ExecutedAgreement", required: true),
                        Lines("finalTypes", "Final document types", "Document types that count as the final document. One per line.", "FinalAgreement"),
                        Text("legalMarkupType", "Legal mark-up document type", "The type used for Legal's mark-ups. Example: LegalMarkup.", "LegalMarkup", required: true),
                        Text("approvalEvidenceType", "Approval evidence document type", "The type used for evidence attached to an approval.", "ApprovalEvidence"),
                        Lines("uploadTypes", "Document types people may upload", "One per line. Leave empty to allow every type except the signed copy.", omitWhenEmpty: true),
                    ]),
                    Group("approval", "Approval",
                    [
                        Text("approverGroupKey", "Approver group", "The group whose members give register authorisation when no approval matrix is chosen. Pick one from the approver groups list.", "ProjectDirectors", required: true, suggestions: "groups"),
                        Boolean("requireExecutedCopy", "Require a signed copy", "A signed copy must be attached before approval.", @default: true),
                        SelectFrom("matrix", "Approval matrix", "matrices", "Optional. When chosen, the matrix decides who approves each submission, instead of the single approver group."),
                    ]),
                    Group("legal", "Legal review", [Boolean("reworkReasonRequired", "Ask Legal for a reason when returning for rework", @default: true)]),
                    Group("unlock", "Emergency unlock",
                    [
                        Boolean("enabled", "Allow an authorised unlock", "An unlock is time-limited, needs a reason and a recent sign-in, and is audited.", @default: true),
                        Integer("defaultMinutes", "Default length (minutes)", "At least 5, and no more than the longest length.", "60", @default: "60"),
                        Integer("maxMinutes", "Longest length (minutes)", "Between 5 minutes and 1440 (24 hours).", "240", @default: "240"),
                        Boolean("requireSecondPerson", "Require a second person", "Not supported yet. Leave this off."),
                    ]),
                    Group("renewal", "Renewals",
                    [
                        SelectDerived("successorInitialState", "Successor starts in", States, "The stage a renewal's new record starts in. It cannot be a locked stage."),
                        Many("copyFields", "Fields copied to the successor", WorkflowDefinitionParser.RenewalCopyFields, "Dates, status and history are never copied."),
                        Lines("copyDocumentTypes", "Document types copied to the successor", "One per line."),
                        Select("startDateRule", "Successor start date", ["None", "DayAfterPredecessorEnd"], "None: the person enters it. DayAfterPredecessorEnd: the day after the agreement it renews ends.", @default: "None"),
                    ]),
                    Group("variation", "Variations",
                    [
                        Text("workflow", "Variation workflow code", "The code of the Variation workflow this contract workflow uses. Must differ from this workflow's own code."),
                        Select("legalReview", "Legal review of a variation", ["None", "Optional", "Required"], "None: never. Optional: when the person chooses. Required: always.", @default: "Optional"),
                        Lines("changeableFields", "Fields a variation may change", "One per line. A configured field is written cf. followed by its key.", "estimatedValue"),
                    ]),
                ]),
            ]),
        ],
    };
}
