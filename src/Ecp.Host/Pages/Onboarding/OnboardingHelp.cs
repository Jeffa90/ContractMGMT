using Ecp.Setup.Onboarding;

namespace Ecp.Host.Pages.Onboarding;

public sealed record StepHelp(string Id, string Title, string Purpose, string[] Guidance, string Format, string Example);

/// <summary>Contextual help for each step. Plain text only; the example is what the import accepts.</summary>
public static class OnboardingHelp
{
    public static readonly IReadOnlyList<StepHelp> All =
    [
        new("pack", "[[solution.pack|sentence]] and classification", "[[solution.pack|lower|a|cap]] is a starting set of [[category|plural|lower]] and [[contract.type|plural|lower]]. It is data only: you can change everything it brings.",
            ["Choose Blank if you want to build the taxonomy yourself.", "Loading a pack's defaults adds its [[category|plural|lower]] and [[contract.type|plural|lower]] to this draft; nothing you already entered is removed.",
             "The classification levels (for example Public to Restricted) are set during installation and can be changed in Installation setup by an installation administrator. Every viewer's ceiling must be one of them."],
            "", ""),
        new("structure", "[[organisation]] structure", "The [[legal.entity|plural|lower]], [[division|plural|lower]], [[business.unit|plural|lower]] and [[site|plural|lower]] that scope people's access and appear on [[contract|plural|lower]].",
            ["One line per item: the type, a short key you will use in other steps, and a name.", "[[business.unit|lower|a|cap]] may name its [[division|lower]] in parentKey. [[site|lower|a|cap]] may name an IANA time zone such as Europe/London.",
             "Keys are letters, digits, '.', '_' and '-', up to 40 characters. They are not case sensitive.", "Items are never deleted. Removing a line in a later release ends only what onboarding itself created."],
            OnboardingCsv.StructureHeader, "LegalEntity,LE-1,Example Holdings Ltd,,,\nDivision,OPS,Operations,,,\nBusinessUnit,FIN,Finance,OPS,,\nSite,HQ,Head office,,Europe/London,"),
        new("categories", "[[category|plural]] (taxonomy)", "The [[category|lower]] tree [[contract|plural|lower]] are filed under. A parent covers its sub-categories for access.",
            ["Import a whole tree at once: paste or upload as text, one [[category|lower]] per line.", "[[category|lower|a|cap]]'s parent must be on another line (or already exist). Nesting is limited to six levels."],
            OnboardingCsv.CategoryHeader, "GENERAL,General,\nGENERAL-IT,Information technology,GENERAL\nFACILITIES,Facilities,"),
        new("types", "[[contract.type|plural|sentence]]", "The kinds of [[agreement|lower]] the [[organisation|lower]] signs. Recorded with the release; [[contract|lower]] creation will use them in a later milestone.",
            ["One line per type: a code and a name."], OnboardingCsv.TypeHeader, "MSA,Master services [[agreement|lower]]\nNDA,Non-disclosure [[agreement|lower]]"),
        new("owners", "[[contract.owner|plural|sentence]]", "People who own [[category|lower|a]]. An owner can read every [[contract|lower]] in the [[organisation|lower]] (within their classification ceiling) but edit only [[contract|plural|lower]] in the [[category|plural|lower]] they own.",
            ["Each person must already exist in this tenant: invite them in user administration first.", "Ownership is a relationship, not a role: the person also needs the [[contract.owner]] role, given by a group mapping or in user administration."],
            OnboardingCsv.OwnerHeader, "GENERAL,sam.owner@example.test,yes"),
        new("viewers", "Viewer scopes", "A Viewer is never a global reader. Each viewer scope limits one person to [[category|lower|a]], [[business.unit|lower]], [[site|lower]], or the intersection of several, up to a classification ceiling.",
            ["Kinds: AllContracts (the whole [[organisation|lower]]), [[category]], BusinessUnit, [[site]], Restricted (the intersection of the dimensions you fill in).", "Use ';' between several values in a cell.",
             "The ceiling is the highest classification the person may see. Records above it stay hidden even inside the person's scope."],
            OnboardingCsv.ViewerHeader, "vera.viewer@example.test,Category,GENERAL,,,Internal\nsite.viewer@example.test,Site,,,HQ,Confidential"),
        new("mappings", "Identity group mappings", "Give people roles automatically from the groups your identity provider says they belong to, each time they sign in.",
            ["The group is the value the provider sends in its groups claim (a name or an object id).", "Onboarding can map Viewer, [[contract.owner]], [[reviewer]], Auditor, [[contract.administrator]] and [[division]]/Site Administrator. Administrator and approver roles are assigned in user administration.",
             "A mapping that grants a privileged role applies to everyone in the group: confirm the group's membership is controlled."],
            OnboardingCsv.MappingHeader, "corp-sso,Finance-Viewers,Viewer,BusinessUnit,,FIN,,Internal\ncorp-sso,Review-Team,Legal,,,,,Confidential"),
        new("review", "Review, simulate and publish", "Check the draft, see what each person could open, and publish. Publishing is versioned, audited and can be rolled back.",
            ["Validate first: errors must be fixed; warnings are advice.", "The preview asks the real access evaluator about synthetic records; nothing real is read.", "Rolling back publishes the earlier release again as a new release and ends only what a later release introduced."],
            "", ""),
    ];

    public static StepHelp? Find(string id) => All.FirstOrDefault(h => h.Id == id);
}
