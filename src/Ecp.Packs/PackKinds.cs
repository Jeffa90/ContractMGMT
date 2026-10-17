namespace Ecp.Packs;

/// <summary>
/// The configuration kinds a pack may carry, by their stable names. This is the platform's vocabulary, not any business's: a kind is installed by the module that owns it, through that module's own validator.
/// The host intersects this list with the kinds it has actually registered. Kinds that describe one organisation's own facts (for example onboarding answers) are deliberately not packable.
/// </summary>
public static class PackKinds
{
    public static readonly IReadOnlyList<string> Packable =
    [
        "WorkflowContractLifecycle", "WorkflowVariation", "NotificationPolicy", "ApprovalMatrix", "ClassificationPolicy", "CustomField", "ContractListView", "Branding", "Terminology"
    ];
}
