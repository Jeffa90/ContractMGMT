using Ecp.Configuration;
using Ecp.SharedKernel.Localisation;

namespace Ecp.Host.Pages;

/// <summary>Builds the display formatter for an organisation from its regional settings (UTC is stored; the organisation's zone and locale are displayed). With no settings the invariant formatter is returned.</summary>
public static class OrgLocale
{
    public static Task<LocaleFormatter> ForAsync(IRegionalSettingsResolver regional, Guid organisationId, CancellationToken ct) => regional.FormatterAsync(organisationId, ct);
}
