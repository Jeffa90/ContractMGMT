using System.Reflection;

namespace Ecp.Access;

/// <summary>A thing a person can do, in the words of someone who is not a developer. Several permissions of the engine make up one ability.</summary>
public sealed record Ability(string Key, string Group, string Label, int Order);

/// <summary>
/// Maps every permission of the platform to the plain-English ability it belongs to, so a screen can say "Create contracts" instead of listing permission codes. The mapping is data
/// (<c>abilities.csv</c>); a test fails the build if a permission has none, so a new permission cannot be added without being explained.
/// </summary>
public static class AbilityCatalogue
{
    public const string Work = "Work on [[contract|plural|lower]]";
    public const string Review = "Review and approve";
    public const string People = "People and set-up";
    public const string Rules = "Business rules";
    public const string Records = "Records and audit";
    public const string Installation = "Installation";

    public static readonly IReadOnlyList<Ability> All =
    [
        new("view-contracts", Work, "View [[contract|plural|lower]]", 10),
        new("download-documents", Work, "Download documents", 11),
        new("view-counterparties", Work, "View [[supplier|plural|lower]]", 12),
        new("manage-counterparties", Work, "Create and edit [[supplier|plural|lower]]", 13),
        new("create-contracts", Work, "Create [[contract|plural|lower]]", 14),
        new("edit-contracts", Work, "Edit [[contract|plural|lower]]", 15),
        new("upload-documents", Work, "Upload documents", 16),
        new("send-for-review", Work, "Send for review and reply to reviewers", 17),
        new("submit-for-approval", Work, "Submit executed [[contract|plural|lower]] for approval", 18),
        new("manage-variations", Work, "Create [[variation|plural|lower]]", 19),
        new("manage-renewals", Work, "Start [[renewal|plural|lower]]", 20),
        new("close-contracts", Work, "Close, suspend or dispose of [[contract|plural|lower]]", 21),
        new("set-reminders", Work, "Set reminders", 22),
        new("unlock-records", Work, "Unlock a locked record in an emergency", 23),
        new("use-ai", Work, "Use AI assistance", 24),
        new("legal-review", Review, "Perform [[legal.review|lower]]", 30),
        new("approve-contracts", Review, "Approve or reject [[contract|plural|lower]]", 31),
        new("delegate-approval", Review, "Hand their approval authority to a colleague", 32),
        new("view-approval-rules", Review, "See approval rules", 33),
        new("manage-retention", Review, "Manage retention, holds and disposal", 34),
        new("manage-people", People, "Add people and give them access", 40),
        new("manage-identity-mappings", People, "Manage identity group mappings", 41),
        new("manage-organisations", People, "Set up [[organisation|plural|lower]] and their structure", 42),
        new("manage-categories", People, "Manage [[category|plural|lower]] and owners", 43),
        new("migrate-data", People, "Load data from an old system", 44),
        new("configure-rules", Rules, "Configure business rules", 50),
        new("configure-terminology", Rules, "Change the wording", 51),
        new("configure-capabilities", Rules, "Switch optional steps on or off", 52),
        new("manage-packs", Rules, "Manage solution packs", 53),
        new("manage-notifications", Rules, "See and retry notification failures", 54),
        new("export-records", Records, "Export records and evidence", 60),
        new("lower-classification", Records, "Lower a record's classification", 61),
        new("read-privileged", Records, "Read records at the highest privileged level", 62),
        new("read-audit", Records, "Read the audit trail", 63),
        new("use-external-reporting", Records, "Use external reporting views", 64),
        new("run-installation", Installation, "Run the installation (technical)", 70),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Map = new(Load);

    /// <summary>Permission code to ability key, for every permission of the platform.</summary>
    public static IReadOnlyDictionary<string, string> AbilityOfPermission => Map.Value;

    public static Ability? Find(string key) => All.FirstOrDefault(a => a.Key == key);

    public static IEnumerable<string> PermissionsOf(string abilityKey) => AbilityOfPermission.Where(kv => kv.Value == abilityKey).Select(kv => kv.Key);

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(AbilityCatalogue).Assembly.GetManifestResourceStream("Ecp.Access.abilities.csv") ?? throw new InvalidOperationException("The ability catalogue is missing from the build.");
        using var reader = new StreamReader(stream);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        _ = reader.ReadLine();   // header
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            result[parts[0].Trim()] = parts[1].Trim();
        }
        return result;
    }
}
