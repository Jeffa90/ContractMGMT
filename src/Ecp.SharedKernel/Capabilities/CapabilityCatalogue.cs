namespace Ecp.SharedKernel;

/// <summary>
/// The stable keys of the platform's optional capabilities. A key is a machine identifier: it is never renamed, and it is what pack documents, organisation overrides, gates and tests refer to. Code names a capability only
/// through these constants, so a guard can find every use.
/// </summary>
public static class CapabilityKeys
{
    public const string LegalReview = "legal.review";
    public const string RegisterAuthorisation = "register.authorisation";
    public const string Variations = "variations";
    public const string Renewals = "renewals";
    public const string SignatureRequests = "signature.requests";

    public static readonly IReadOnlyList<string> All = [LegalReview, RegisterAuthorisation, Variations, Renewals, SignatureRequests];
}

/// <summary>
/// One optional capability and what it is bound to in the platform's closed vocabularies. The bindings are how a disabled capability is recognised in a workflow: a definition that uses a bound transition role, state role or
/// guard uses the capability. Labels and descriptions are written with terminology tokens, so each organisation reads them in its own words. Nothing here names a business.
/// </summary>
public sealed record CapabilityDefinition(string Key, string Label, string Description, IReadOnlyList<string> Requires, IReadOnlyList<string> TransitionRoles, IReadOnlyList<string> StateRoles,
    IReadOnlyList<string> Guards, string TurnsOffEffect, IReadOnlyList<string>? Fields = null, IReadOnlyList<string>? Views = null)
{
    /// <summary>Register fields that exist only because of the capability (hidden from columns and filters when it is off).</summary>
    public IReadOnlyList<string> RegisterFields { get; } = Fields ?? [];
    /// <summary>Built-in list views and dashboard cards that exist only because of the capability (same keys for both).</summary>
    public IReadOnlyList<string> ViewKeys { get; } = Views ?? [];
    /// <summary>The platform default: every capability is on, which is how the platform behaved before capabilities were configurable.</summary>
    public bool DefaultEnabled => true;
}

/// <summary>The capability catalogue (docs/CAPABILITIES.md section 2). Adding a capability is a code change; turning one on or off is configuration.</summary>
public static class CapabilityCatalogue
{
    private static readonly CapabilityDefinition[] Definitions =
    [
        new(CapabilityKeys.LegalReview, "[[legal.review|sentence]]",
            "A structured, multi-round [[legal.review|lower]] of a [[contract|lower]] or [[variation|lower]] before it goes ahead, with its own queue, assignment, comments and clearance.",
            [],
            ["submitLegal", "recallLegal", "beginLegal", "returnLegal", "clearLegal", "resubmitLegal", "returnToLegal"],
            ["legalQueue", "legalReview", "legalRework", "legalCleared"],
            ["LegalRequired", "LegalNotRequired", "ControlledFieldChanged"],
            "No new [[legal.review|lower]] can be started, the queue and its pages are hidden, and the review status fields are not shown.",
            ["legalReviewStatus", "legalReviewRequired"], ["awaiting-legal"]),
        new(CapabilityKeys.RegisterAuthorisation, "[[registration.approval|sentence]]",
            "An approver group approves or rejects an executed [[contract|lower]] before it is registered, stamping the [[registered.date|lower]] and locking the record. A [[variation|lower]] is approved the same way.",
            [],
            ["submitRegister", "approve", "reject"],
            ["approval"],
            ["AuthorisedApprover", "SubmitterNotApprover"],
            "Nothing can be submitted for [[registration.approval|lower]], the approvals queue is hidden, and [[variation|plural|lower]] cannot be approved.",
            ["registeredDate"], ["pending-approval"]),
        new(CapabilityKeys.Variations, "[[variation|plural|sentence]]",
            "Controlled changes to an effective [[contract|lower]]: a [[variation|lower]] is drafted, approved and applied, preserving the before and after values.",
            [CapabilityKeys.RegisterAuthorisation],
            ["openVariation", "applyVariation", "rejectVariation", "cancelVariation"],
            ["variation"],
            ["ParentRemainsLocked", "NoOtherOpenVariation", "VariationOutcomeApplied", "VariationOutcomeNotApplied"],
            "No [[variation|lower]] can be raised, and the [[variation|plural|lower]] pages and list views are hidden.",
            null, ["variations-in-progress"]),
        new(CapabilityKeys.Renewals, "[[renewal|plural|sentence]]",
            "A successor [[contract|lower]] that replaces one that is ending, linked both ways, with the predecessor left unchanged until the successor is registered.",
            [],
            ["startRenewal", "completeRenewal", "cancelRenewal"],
            ["renewal"],
            ["NoOpenRenewal", "RenewalSuccessorEffective"],
            "No [[renewal|lower]] can be started, and the [[renewal|plural|lower]] pages and list views are hidden.",
            null, ["renewals-in-progress"]),
        new(CapabilityKeys.SignatureRequests, "Signature requests",
            "Sending the final document for signature through a signature provider (or recording that signing is handled outside the platform) and tracking the request until the executed copy arrives.",
            [],
            ["sendSignature"],
            ["signature"],
            ["SignatureVoidedOrDeclined"],
            "No signature request can be made or refreshed. An executed copy is still attached as an ordinary [[document|lower]]."),
    ];

    public static IReadOnlyList<CapabilityDefinition> All => Definitions;
    public static IReadOnlyDictionary<string, CapabilityDefinition> ByKey { get; } = Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);
    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    /// <summary>The capability a transition role belongs to, or null when the role is generic.</summary>
    public static string? OfTransitionRole(string role) => Definitions.FirstOrDefault(d => d.TransitionRoles.Contains(role))?.Key;
    public static string? OfStateRole(string? role) => role is null ? null : Definitions.FirstOrDefault(d => d.StateRoles.Contains(role))?.Key;
    public static string? OfField(string fieldKey) => Definitions.FirstOrDefault(d => d.RegisterFields.Contains(fieldKey))?.Key;
    public static string? OfView(string viewKey) => Definitions.FirstOrDefault(d => d.ViewKeys.Contains(viewKey))?.Key;
    public static string? OfGuard(string guard) => Definitions.FirstOrDefault(d => d.Guards.Contains(guard))?.Key;
}
