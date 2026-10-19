using Ecp.Operations;
using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

/// <summary>The installation's email provider: settings (secrets only as references), a real connection and synthetic test message, and the readiness that follows from them (G-12).</summary>
[Authorize(Policy = "perm:operations.read")]
public sealed class EmailModel(IEnablementAdministration enablement) : EcpPageModel
{
    public EmailAdminView View { get; private set; } = null!;
    [BindProperty] public string Provider { get; set; } = "";
    [BindProperty] public string? From { get; set; }
    [BindProperty] public string? Host { get; set; }
    [BindProperty] public int Port { get; set; } = 587;
    [BindProperty] public bool UseTls { get; set; } = true;
    [BindProperty] public string? UserRef { get; set; }
    [BindProperty] public string? PasswordRef { get; set; }
    [BindProperty] public string? TestRecipient { get; set; }

    /// <summary>A value that is not a secret reference might be a password typed into the wrong box, so it is never echoed back into the page.</summary>
    private static string? OnlyReferences(string? value) => Ecp.SharedKernel.Secrets.SecretReference.TryParse(value, out _) ? value : null;

    private async Task LoadAsync(CancellationToken ct, bool fillForm = true)
    {
        View = await enablement.EmailAsync(ct);
        if (!fillForm) { UserRef = OnlyReferences(UserRef); PasswordRef = OnlyReferences(PasswordRef); ModelState.Remove(nameof(UserRef)); ModelState.Remove(nameof(PasswordRef)); return; }
        var c = View.Config;
        Provider = c.ProviderKey; From = c.FromAddress; Host = c.Host; Port = c.Port; UseTls = c.UseTls; UserRef = c.UserRef; PasswordRef = c.PasswordRef;
    }

    public IReadOnlyList<(string Key, string Label)> Providers() =>
    [
        (EmailProviderKeys.Smtp, "SMTP server"),
        (EmailProviderKeys.Graph, "Microsoft Graph (not connected in this release; stays Not Verified)"),
        .. View.Mode == Ecp.SharedKernel.EcpMode.Production ? Array.Empty<(string, string)>() : [(EmailProviderKeys.DevCapture, "Development capture mailbox (simulated)"), (EmailProviderKeys.DemoLog, "Demonstration log (simulated)")],
        (EmailProviderKeys.Disabled, "Disabled (in-app inbox only)"),
    ];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await enablement.SaveEmailAsync(Provider, new EmailSettings(EmailProviderKind.Smtp, From, Host, Port, UseTls, UserRef, PasswordRef), ct);
        Flash("The email settings were saved. They are not verified until a test message has been sent with them.");
        return RedirectToPage();
    }, () => LoadAsync(ct, fillForm: false));

    public Task<IActionResult> OnPostTestAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var result = await enablement.TestEmailAsync(TestRecipient ?? "", ct);
        Flash(result.Succeeded ? (result.Simulated ? "The simulated provider accepted the test message. Nothing was delivered to a real mailbox." : "The mail server was reached and accepted the test message.")
            : "The test did not pass: " + (result.ConnectivityOutcome == "Failed" ? result.ConnectivityDetail : result.DeliveryDetail) + $" (reference {result.CorrelationId})");
        return RedirectToPage();
    }, () => LoadAsync(ct, fillForm: false));
}
