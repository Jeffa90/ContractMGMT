using Ecp.SharedKernel;

namespace Ecp.Tenancy;

/// <summary>
/// Which solution pack an organisation works from. The organisation's own pack (chosen at setup or onboarding) decides; an organisation that has none uses the installation default, which is host
/// configuration (<c>Ecp:DefaultPackId</c>) and is not set unless the installer sets it. If neither names a pack the answer is a refusal, never a silent choice of some business's pack: a pack carries a
/// business's lifecycle, approvers and notification rules, so choosing one for an organisation that did not ask for it would apply another business's rules (PRT-001, PRT-002).
/// </summary>
public static class PackSelection
{
    public static async Task<string> ResolveAsync(this IOrganisationStructureService organisations, Guid organisationId, string? installationDefault, CancellationToken ct = default)
    {
        var organisation = await organisations.GetOrganisationAsync(organisationId, ct) ?? throw new DomainException("organisation_not_found", "The [[organisation|lower]] was not found.");
        if (!string.IsNullOrWhiteSpace(organisation.SolutionPackKey)) return organisation.SolutionPackKey;
        if (!string.IsNullOrWhiteSpace(installationDefault)) return installationDefault.Trim();
        throw new DomainException("no_solution_pack",
            $"The [[organisation|lower]] '{organisation.Name}' has no [[solution.pack|lower]] and no installation default pack is configured (Ecp:DefaultPackId). Choose [[solution.pack|lower|a]] for the [[organisation|lower]].");
    }
}
