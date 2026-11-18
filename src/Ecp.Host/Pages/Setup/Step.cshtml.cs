using Ecp.Host.Security;
using Ecp.Host.Setup;
using Ecp.Setup;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Setup;

/// <summary>A way to test sign-in. For an OpenID Connect provider <see cref="Scheme"/> names the scheme; for Windows sign-in it is <see cref="WindowsScheme"/> and the web server does the authenticating.</summary>
public sealed record TestSignInLink(string Scheme, string Key, string Name)
{
    public const string WindowsScheme = "windows";
    public string Href(string returnUrl) => Scheme == WindowsScheme
        ? $"/Account/WindowsSignIn?testProvider={Uri.EscapeDataString(Key)}&returnUrl={Uri.EscapeDataString(returnUrl)}"
        : $"/Account/Challenge?scheme={Uri.EscapeDataString(Scheme)}&testProvider={Uri.EscapeDataString(Key)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
}

[AllowAnonymous]
public sealed class StepModel(ISetupService setup, OidcSchemeManager oidc, Ecp.Identity.Windows.IWindowsSignIn windows, Ecp.SharedKernel.TenantContext tenantContext) : SetupPageModel(setup)
{
    public StageDescriptor? Stage { get; private set; }
    public StageResult? Result { get; private set; }
    public IReadOnlyList<TestSignInLink> TestLinks { get; private set; } = [];
    public IReadOnlyList<ReadinessSnapshot> Checks { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (!await HasSessionAsync(ct)) return RedirectToPage("Index");
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(string id, CancellationToken ct)
    {
        if (!await HasSessionAsync(ct)) return RedirectToPage("Index");
        Result = await Setup.ExecuteAsync(id, FormValues(), SessionActor, amending: false, ct);
        if (Result.Succeeded && id == "complete")
        {
            await Setup.EndSessionAsync(ct);
            Response.Cookies.Delete(SetupGateMiddleware.SessionCookie, new CookieOptions { Path = "/Setup" });
            TempData["Flash"] = "Setup is complete. Sign in with the administrator you created.";
            return Redirect("/SignIn");
        }
        if (Result.Succeeded && (await Setup.SnapshotAsync(false, ct)) is { } snap && snap.NextStageId is { } next && id != "readiness")
        {
            Flash(Result.Message);
            return Redirect("/Setup/Step/" + next);
        }
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostSkipAsync(string id, CancellationToken ct)
    {
        if (!await HasSessionAsync(ct)) return RedirectToPage("Index");
        var r = await Setup.SkipAsync(id, SessionActor, ct);
        Flash(r.Message);
        return RedirectToPage("Index");
    }

    private async Task<bool> LoadAsync(string id, CancellationToken ct)
    {
        Stage = await Setup.DescribeAsync(id, false, ct);
        if (Stage is null) return false;
        var snapshot = await Setup.SnapshotAsync(false, ct);
        if (snapshot.TenantId is { } tenantId && id is "admin" or "readiness" or "identity")
        {
            var links = oidc.ForTenant(tenantId).Select(p => new TestSignInLink(p.Scheme, p.Settings.Key, p.Settings.DisplayName)).ToList();
            using (tenantContext.Use(tenantId))
                if (await windows.ProviderKeyAsync(ct) is { } windowsKey) links.Add(new TestSignInLink(TestSignInLink.WindowsScheme, windowsKey, "Windows"));
            TestLinks = links;
        }
        if (id == "readiness" || id == "complete") Checks = await Setup.RunChecksAsync(ct);
        return true;
    }
}
