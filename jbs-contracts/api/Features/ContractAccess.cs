using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

/// <summary>
/// Who can see a contract (product-spec section 3, decision D-05). Everyone added sees every contract except Confidential ones,
/// which only their owner and backup, Legal, approvers, admins and auditors see. Enforced in every query, never only in the screens.
/// </summary>
public static class ContractAccess
{
    /// <summary>A WHERE condition for a contracts table alias; needs the parameters from <see cref="Params"/>.</summary>
    public static string VisibleSql(string alias) =>
        $"({alias}.IsConfidential = 0 OR @seesAllConfidential = 1 OR {alias}.OwnerId = @meId OR {alias}.BackupOwnerId = @meId)";

    public static Dapper.DynamicParameters Params(Person me, object? more = null)
    {
        var p = new Dapper.DynamicParameters(more);
        p.Add("meId", me.Id);
        p.Add("seesAllConfidential", me.SeesAllConfidential);
        return p;
    }

    public static bool CanSee(Person me, bool isConfidential, int ownerId, int? backupOwnerId) =>
        !isConfidential || me.SeesAllConfidential || ownerId == me.Id || backupOwnerId == me.Id;

    /// <summary>
    /// Whether the person may change the contract's details now: any contract owner, on a draft or on the owner's turn in a Legal review (D-55).
    /// On Legal's turn, and from Signing on, details are frozen.
    /// </summary>
    public static bool CanEdit(Person me, ContractRow c) =>
        me.IsContractOwner && !c.IsLocked && (c.Stage == "Draft" || c is { Stage: "LegalReview", WhoseTurn: "Owner" });

    /// <summary>Contract owners can withdraw a contract that isn't Effective yet: from Draft, Legal review or Signing, with a reason.</summary>
    public static bool CanWithdraw(Person me, ContractRow c) => me.IsContractOwner && !c.IsLocked && c.Stage is "Draft" or "LegalReview" or "Signing";
}
