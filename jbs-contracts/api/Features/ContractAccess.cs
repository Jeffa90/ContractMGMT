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

    /// <summary>What the person may do with this contract right now (slice 1: drafts only; later stages add their own actions).</summary>
    public static bool CanEdit(Person me, string stage, bool isLocked) => me.IsContractOwner && !isLocked && stage == "Draft";
}
