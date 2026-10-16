using System.Security.Claims;
using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Setup;

[Authorize(Policy = "perm:setup.run")]
public sealed class StepModel(ISetupService setup, IAuthorizationService authorisation, IAccessService access, OidcSchemeManager oidc) : EcpPageModel
{
    public StageDescriptor? Stage { get; private set; }
    public StageResult? Result { get; private set; }
    public IReadOnlyList<Ecp.Host.Pages.Setup.TestSignInLink> TestLinks { get; private set; } = [];

    private async Task<bool> RecentAsync() => (await authorisation.AuthorizeAsync(User, Policies.RecentAuthentication)).Succeeded;
    private IActionResult ReauthRedirect() => Redirect("/Reauth?returnUrl=" + Uri.EscapeDataString(Request.Path.Value ?? "/Admin/Setup"));

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (!await RecentAsync()) return ReauthRedirect();
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(string id, CancellationToken ct)
    {
        if (!await RecentAsync()) return ReauthRedirect();
        var values = Request.Form.Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var actor = new SetupActor(User.Identity?.Name ?? "administrator", access.CurrentUserId);
        Result = await setup.ExecuteAsync(id, values, actor, amending: true, ct);
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(string id, CancellationToken ct)
    {
        Stage = await setup.DescribeAsync(id, amending: true, ct);
        if (Stage is null) return false;
        if (Guid.TryParse(User.FindFirstValue(EcpClaims.TenantId), out var tenantId) && id == "identity")
            TestLinks = [.. oidc.ForTenant(tenantId).Select(p => new Ecp.Host.Pages.Setup.TestSignInLink(p.Scheme, p.Settings.Key, p.Settings.DisplayName))];
        return true;
    }
}
