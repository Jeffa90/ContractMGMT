namespace Ecp.Tenancy;

/// <summary>One thing an organisation already holds, counted so that "reset setup" is refused once it would affect real work.</summary>
public sealed record OrganisationUsageLine(string What, int Count);

/// <summary>
/// Implemented by each module that owns organisation data (contracts, migrations ...). Counts only; it reads no content and changes nothing. A module that holds nothing for the organisation returns no lines.
/// </summary>
public interface IOrganisationUsageSource
{
    Task<IReadOnlyList<OrganisationUsageLine>> UsageAsync(Guid organisationId, CancellationToken ct = default);
}
