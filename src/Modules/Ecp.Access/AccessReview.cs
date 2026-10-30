using Ecp.Identity.Domain;
using Ecp.SharedKernel;

namespace Ecp.Access;

/// <summary>One line of the plain-English review: what a person will or will not be able to do, and where.</summary>
public sealed record ReviewLine(string Text, string? Where);

public sealed record ReviewFact(string Label, string Value);

/// <summary>One real grant, for the Advanced details a specialist can open. The only place a role code appears.</summary>
public sealed record ReviewGrantDetail(string RoleLabel, string RoleCode, string Where, string Ceiling, string Source);

/// <summary>
/// What the administrator reads and confirms before access is given (docs/UX_REDESIGN.md section 8). It is built from the effective-access report of a dry run of the real services, so it cannot say more or
/// less than saving would do. Role and permission codes appear only in <see cref="AdvancedGrants"/>, which is filled only when the administrator asks for the Advanced details.
/// </summary>
public sealed record AccessReview(
    string PersonName, string Organisation, IReadOnlyList<ReviewLine> WillBeAbleTo, IReadOnlyList<ReviewLine> WillNotBeAbleTo, IReadOnlyList<ReviewLine> AlsoIncluded,
    IReadOnlyList<ReviewFact> Facts, IReadOnlyList<string> ApproverGroupKeys, IReadOnlyList<string> WillDo, IReadOnlyList<string> Notes, IReadOnlyList<ReviewGrantDetail> AdvancedGrants)
{
    public bool GivesAnyAccess => WillBeAbleTo.Count > 0;
}

/// <summary>What the review needs to know beyond the dry-run result: the choices in the administrator's own words.</summary>
public sealed record ReviewInput(string PersonName, string OrganisationName, IReadOnlyList<string> ProfileKeys, OwnershipChoice Ownership, string? Ceiling, IReadOnlyList<string>? ApproverGroupKeys = null);

public static class AccessReviewBuilder
{
    /// <summary>Abilities worth a line of their own, in the order a reader expects. Everything else the person can do is under "Also included".</summary>
    private static readonly string[] Headline =
    [
        "create-contracts", "view-contracts", "edit-contracts", "upload-documents", "manage-variations", "manage-renewals", "send-for-review", "submit-for-approval", "legal-review", "approve-contracts"
    ];
    private static readonly string[] AdministrationAbilities = ["manage-people", "manage-organisations", "manage-categories"];

    public static AccessReview Build(ReviewInput input, EffectiveAccessReport after, ClassificationScale scale, IReadOnlyList<PlannedStep> steps, bool includeAdvanced = false)
    {
        AbilityStatus? Find(string key) => after.Abilities.FirstOrDefault(a => a.Key == key);
        var will = new List<ReviewLine>(); var wont = new List<ReviewLine>(); var also = new List<ReviewLine>();

        foreach (var key in Headline)
        {
            var a = Find(key);
            if (a is null) continue;
            if (a.Can) will.Add(new ReviewLine(WillText(a), a.Where.Length > 0 ? a.Where : null));
            else wont.Add(new ReviewLine(WontText(a), null));
        }
        var administers = AdministrationAbilities.Select(Find).Where(a => a is { Can: true }).ToList();
        if (administers.Count > 0)
            will.Add(new ReviewLine("Administer the [[organisation|lower]]: add people and give them access, manage [[category|plural|lower]] and owners, and set up its structure", administers.First()!.Where.Length > 0 ? administers.First()!.Where : null));
        else wont.Add(new ReviewLine("Administer the [[organisation|lower]]", null));

        // Separation of duties applies to everyone, whatever they are given.
        wont.Add(new ReviewLine("Approve or sign off their own submissions (a second person is always needed)", null));
        // The ceiling that matters is the one the chosen access carries (the administrator roles sit at their own, higher level and do not read records).
        var worksOnRecords = input.ProfileKeys.Count > 0;
        var ceiling = !string.IsNullOrWhiteSpace(input.Ceiling) ? scale.Canonical(input.Ceiling) ?? input.Ceiling
            : input.ProfileKeys.Select(AccessProfiles.Find).OfType<AccessProfile>().Select(p => AccessProfiles.DefaultCeiling(p, scale)).OrderByDescending(scale.Rank).FirstOrDefault() ?? after.HighestClassification;
        var above = scale.Levels.Where(l => scale.Rank(l) > scale.Rank(ceiling)).ToList();
        if (above.Count > 0 && worksOnRecords) wont.Add(new ReviewLine($"Open records classified above {ceiling} ({string.Join(", ", above)})", null));

        var shown = new HashSet<string>(Headline.Concat(AdministrationAbilities).Append("run-installation"));
        foreach (var a in after.CanDo.Where(a => !shown.Contains(a.Key))) also.Add(new ReviewLine(WillText(a), a.Where.Length > 0 ? a.Where : null));

        var profiles = input.ProfileKeys.Select(k => AccessProfiles.Find(k)?.Label ?? k).ToList();
        var standing = after.Standing.FirstOrDefault();
        var ownership = input.Ownership switch
        {
            OwnershipChoice.All => $"Every current and future [[category|lower]] in {input.OrganisationName}",
            OwnershipChoice.Selected => after.Owns.Count == 0 ? "The [[category|plural|lower]] chosen" : string.Join(", ", after.Owns.Select(o => o.Name)),
            _ => standing is not null ? $"Every current and future [[category|lower]] in {standing.OrganisationName}" : after.Owns.Count > 0 ? string.Join(", ", after.Owns.Select(o => o.Name)) : "None"
        };
        var facts = new List<ReviewFact>
        {
            new("[[organisation]]", input.OrganisationName),
            new("Access profile", profiles.Count == 0 ? "None chosen" : string.Join(", ", profiles)),
            new("[[contract]] ownership", ownership),
            new("Classification ceiling", worksOnRecords ? $"Up to {ceiling}" : "Not applicable"),
            new("Administration access", administers.Count > 0 ? "Administers the [[organisation|lower]]" : "None"),
        };
        var notes = after.Attention.Select(x => x.Message).ToList();
        var advanced = !includeAdvanced ? [] : after.Grants.Where(g => g.Active || g.Future).Select(g => new ReviewGrantDetail(g.RoleLabel, g.RoleCode, g.Where, g.HighestClassification, g.Source)).ToList();
        var groupKeys = (input.ApproverGroupKeys ?? []).Concat(after.ApproverGroups).Distinct(StringComparer.Ordinal).ToList();
        return new AccessReview(input.PersonName, input.OrganisationName, will, wont, also, facts, groupKeys, steps.Select(s => s.Text).ToList(), notes, advanced);
    }

    private static string WillText(AbilityStatus a) => a.Label;
    private static string WontText(AbilityStatus a) => a.Key switch
    {
        "legal-review" => "Perform formal review ([[legal.review|lower]])",
        "approve-contracts" => "Approve or reject [[contract|plural|lower]] sent to an approver group",
        _ => a.Label
    };
}
