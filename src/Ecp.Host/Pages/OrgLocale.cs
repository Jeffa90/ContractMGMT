using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;

namespace Ecp.Host.Pages;

/// <summary>Builds the display formatter for an organisation: its culture, time zone and default currency (UTC is stored; the organisation's zone is displayed).</summary>
public static class OrgLocale
{
    public static async Task<LocaleFormatter> ForAsync(IOrganisationStructureService organisations, Guid organisationId, CancellationToken ct)
    {
        var org = await organisations.GetOrganisationAsync(organisationId, ct);
        return org is null ? new LocaleFormatter("en-AU", "Australia/Brisbane", "AUD") : new LocaleFormatter(org.CultureCode, org.TimeZoneId, org.DefaultCurrencyCode);
    }
}
