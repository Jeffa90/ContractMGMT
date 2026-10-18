using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class ValidationModel(IOperationsAdministration operations) : EcpPageModel
{
    public ValidationReport? Report { get; private set; }
    public bool CanSeeDetail { get; private set; }

    private async Task LoadAsync(CancellationToken ct) => Report ??= await operations.LastValidationAsync(ct);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); CanSeeDetail = Report?.Checks.Any(c => c.TechnicalDetail.Length > 0) == true; return Page(); });

    public Task<IActionResult> OnPostRunAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Report = await operations.ValidateInstallationAsync(ct);
        CanSeeDetail = true;
        return Page();
    }, () => LoadAsync(ct));
}
