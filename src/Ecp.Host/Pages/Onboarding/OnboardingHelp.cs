using Ecp.Setup.Onboarding;

namespace Ecp.Host.Pages.Onboarding;

public sealed record StepHelp(string Id, string Title, string Purpose, string[] Guidance, string Format, string Example);

/// <summary>Contextual help for each step. Plain text only; the example is what the import accepts.</summary>
public static class OnboardingHelp
{
    public static readonly IReadOnlyList<StepHelp> All =
    [
        new("pack", "Solution pack and classification", "A solution pack is a starting set of categories and contract types. It is data only: you can change everything it brings.",
            ["Choose Blank if you want to build the taxonomy yourself.", "Loading a pack's defaults adds its categories and contract types to this draft; nothing you already entered is removed.",
             "The classification levels (for example Public to Restricted) are set during installation and can be changed in Installation setup by an installation administrator. Every viewer's ceiling must be one of them."],
            "", ""),
        new("structure", "Organisation structure", "The legal entities, divisions, business units and sites that scope people's access and appear on contracts.",
            ["One line per item: the type, a short key you will use in other steps, and a name.", "A business unit may name its division in parentKey. A site may name an IANA time zone such as Australia/Brisbane.",
             "Keys are letters, digits, '.', '_' and '-', up to 40 characters. They are not case sensitive.", "Items are never deleted. Removing a line in a later release ends only what onboarding itself created."],
            OnboardingCsv.StructureHeader, "LegalEntity,JBS-AUS,Example Holdings Pty Ltd,,,\nDivision,OPS,Operations,,,\nBusinessUnit,PKG,Packaging,OPS,,\nSite,BNE,Brisbane,,Australia/Brisbane,"),
        new("categories", "Categories (taxonomy)", "The category tree contracts are filed under. A parent covers its sub-categories for access.",
            ["Import a whole tree at once: paste or upload as text, one category per line.", "A category's parent must be on another line (or already exist). Nesting is limited to six levels."],
            OnboardingCsv.CategoryHeader, "PACKAGING,Packaging,\nPACKAGING-FLEX,Flexible packaging,PACKAGING\nENERGY,Energy,"),
        new("types", "Contract types", "The kinds of agreement the organisation signs. Recorded with the release; contract creation will use them in a later milestone.",
            ["One line per type: a code and a name."], OnboardingCsv.TypeHeader, "MSA,Master services agreement\nNDA,Non-disclosure agreement"),
        new("owners", "Category owners", "People who own a category. An owner can read every contract in the organisation (within their classification ceiling) but edit only contracts in the categories they own.",
            ["Each person must already exist in this tenant: invite them in user administration first.", "Ownership is a relationship, not a role: the person also needs the Category Owner role, given by a group mapping or in user administration."],
            OnboardingCsv.OwnerHeader, "PACKAGING,sam.owner@example.test,yes"),
        new("viewers", "Viewer scopes", "A Viewer is never a global reader. Each viewer scope limits one person to a category, business unit, site, or the intersection of several, up to a classification ceiling.",
            ["Kinds: AllContracts (the whole organisation), Category, BusinessUnit, Site, Restricted (the intersection of the dimensions you fill in).", "Use ';' between several values in a cell.",
             "The ceiling is the highest classification the person may see. Records above it stay hidden even inside the person's scope."],
            OnboardingCsv.ViewerHeader, "vera.viewer@example.test,Category,PACKAGING,,,Internal\nsite.viewer@example.test,Site,,,BNE,Confidential"),
        new("mappings", "Identity group mappings", "Give people roles automatically from the groups your identity provider says they belong to, each time they sign in.",
            ["The group is the value the provider sends in its groups claim (a name or an object id).", "Onboarding can map Viewer, Category Owner, Legal, Auditor, Procurement Administrator and Division/Site Administrator. Administrator and approver roles are assigned in user administration.",
             "A mapping that grants a privileged role applies to everyone in the group: confirm the group's membership is controlled."],
            OnboardingCsv.MappingHeader, "corp-sso,Procurement-Viewers,Viewer,BusinessUnit,,PKG,,Internal\ncorp-sso,Legal-Team,Legal,,,,,Confidential"),
        new("review", "Review, simulate and publish", "Check the draft, see what each person could open, and publish. Publishing is versioned, audited and can be rolled back.",
            ["Validate first: errors must be fixed; warnings are advice.", "The preview asks the real access evaluator about synthetic records; nothing real is read.", "Rolling back publishes the earlier release again as a new release and ends only what a later release introduced."],
            "", ""),
    ];

    public static StepHelp? Find(string id) => All.FirstOrDefault(h => h.Id == id);
}
