using System.Reflection;
using Ecp.Operations;
using Ecp.SharedKernel.Localisation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

[Authorize(Policy = "perm:ops.manage")]
public sealed class HealthModel(ReadinessService readiness, EcpHostOptions host, LocalisationOptions localisation) : EcpPageModel
{
    public ReadinessReport Report { get; private set; } = new(ReadinessStatus.Healthy, []);
    public string Mode => host.Mode.ToString();
    public string Culture => localisation.DefaultCulture;
    public string TimeZone => localisation.DefaultTimeZoneId;
    public string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) { Report = await readiness.RunAsync(ct); return Page(); }
}
