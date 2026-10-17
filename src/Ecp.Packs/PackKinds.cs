namespace Ecp.Packs;

/// <summary>
/// The configuration kinds a pack may carry, by their stable names. This is the platform's vocabulary, not any business's: a kind is installed by the module that owns it, through that module's own validator.
/// The host intersects this list with the kinds it has actually registered. Kinds that describe one organisation's own facts (for example onboarding answers) are deliberately not packable.
/// </summary>
public static class PackKinds
{
    public static readonly IReadOnlyList<string> Packable =
    [
        "WorkflowContractLifecycle", "WorkflowVariation", "NotificationPolicy", "ApprovalMatrix", "ClassificationPolicy", "CustomField", "ContractListView", "Branding", "Terminology", "Capabilities", "RegionalProfile"
    ];

    /// <summary>
    /// What a kind's document is judged against, by kind: an organisation can only publish a document of the key once these have been published. A closed table in the platform's vocabulary (not a rule engine):
    /// it lets a pack assignment tell "this draft waits for the drafts it depends on" from "this draft has a problem".
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Prerequisites = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["ApprovalMatrix"] = ["CustomField"],
        ["ContractListView"] = ["CustomField"],
        ["WorkflowContractLifecycle"] = ["ApprovalMatrix", "CustomField"],
        ["WorkflowVariation"] = ["ApprovalMatrix", "CustomField"],
    };
}
