using Ecp.Documents;
using Ecp.Identity;
using Ecp.Operations;
using Ecp.SharedKernel;

namespace Ecp.Recovery;

public sealed record ScannerAdminView(ScannerReadinessInfo Readiness, ScannerProviderOptions Settings, EcpMode Mode, MockScannerAcceptanceInfo? Acceptance, IReadOnlyList<AcceptedExceptionDto> AcceptanceHistory, bool AllowSkippedScan);

public sealed record EmailAdminView(EmailConfigView Config, EmailReadinessInfo Readiness, IReadOnlyList<EmailTestView> RecentTests, EcpMode Mode, TimeSpan MaxAge);

/// <summary>
/// Company enablement actions that belong to the installation rather than to one organisation: the email provider (settings, test, readiness) and the written acceptance of a warning. Each checks the permission here, and
/// every change needs a recent sign-in. Reading needs <c>operations.read</c>; changing needs <c>operations.maintenance</c>.
/// </summary>
public interface IEnablementAdministration
{
    Task<EmailAdminView> EmailAsync(CancellationToken ct = default);
    Task<EmailConfigView> SaveEmailAsync(string providerKey, EmailSettings settings, CancellationToken ct = default);
    Task<EmailTestView> TestEmailAsync(string recipient, CancellationToken ct = default);
    Task<ScannerAdminView> ScannerAsync(CancellationToken ct = default);
    Task<ScannerTestResult> TestScannerAsync(CancellationToken ct = default);
    Task<AcceptedExceptionDto> AcceptMockScannerAsync(string scope, string reason, int validDays, CancellationToken ct = default);
    Task<IReadOnlyList<AcceptedExceptionDto>> AcceptedExceptionsAsync(bool activeOnly, CancellationToken ct = default);
    Task<AcceptedExceptionDto> AcceptWarningAsync(string kind, string code, string scope, string reason, int validDays, CancellationToken ct = default);
    Task RevokeAcceptanceAsync(Guid id, CancellationToken ct = default);
}

internal sealed class EnablementAdministration(IAccessService access, CurrentUser currentUser, ReauthenticationContext reauth, IClock clock, ITenantContext tenant, ActorContext actor, IEmailConfigurationService email,
    IAcceptedExceptions exceptions, EmailReadinessOptions readiness, Microsoft.Extensions.Options.IOptions<RecoveryOptions> recovery,
    IScannerReadiness scanner, ScannerProviderOptions scannerOptions, IMockScannerAcceptance mockAcceptance, Microsoft.Extensions.Options.IOptions<DocumentsOptions> documents, HostModeAccessor? hostMode = null) : IEnablementAdministration
{
    private HostModeAccessor mode => hostMode ?? new HostModeAccessor(EcpMode.Production);

    private async Task RequireAsync(string permission, bool recentSignIn = false)
    {
        if (tenant.TenantId != SystemTenant.Id) throw new ForbiddenException("You do not have permission to do this.");
        if (!await access.HoldsAnywhereAsync(permission)) throw new ForbiddenException("You do not have permission to do this.");
        if (recentSignIn) reauth.Require(clock.UtcNow, TimeSpan.FromMinutes(Math.Max(1, recovery.Value.ReauthenticationMinutes)));
    }

    private OperatorIdentity Operator()
    {
        var id = currentUser.UserId?.ToString("N") ?? actor.ActorId ?? "unknown";
        return new OperatorIdentity(id, id);
    }

    public async Task<EmailAdminView> EmailAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        return new EmailAdminView(await email.EffectiveAsync(ct), await email.ReadinessAsync(ct), await email.RecentTestsAsync(10, ct), mode.Mode, readiness.MaxAge);
    }

    public async Task<EmailConfigView> SaveEmailAsync(string providerKey, EmailSettings settings, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        return await email.SaveAsync(providerKey, settings, Operator(), ct);
    }

    public async Task<EmailTestView> TestEmailAsync(string recipient, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        return await email.TestAsync(recipient, Operator(), ct);
    }

    public async Task<ScannerAdminView> ScannerAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        var history = (await exceptions.ListAsync(false, ct)).Where(e => e.Kind == AcceptedExceptionKinds.MockScanner).Take(10).ToList();
        // The endpoint is a service address and the secret is a reference; neither is a secret value. They are shown as configured so an administrator can see what the adapter will use.
        return new ScannerAdminView(await scanner.EvaluateAsync(ct), scannerOptions, mode.Mode, await mockAcceptance.CurrentAsync(ct), history, documents.Value.AllowSkippedScan);
    }

    public async Task<ScannerTestResult> TestScannerAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        return await scanner.RunSelfTestAsync(Operator().Id, ct);
    }

    public async Task<AcceptedExceptionDto> AcceptMockScannerAsync(string scope, string reason, int validDays, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        if (mode.Mode != EcpMode.Test) throw new DomainException("scanner_acceptance_mode", mode.Mode == EcpMode.Production
            ? "The demonstration scanner is refused in Production. Connect a real scanner."
            : "Demo needs no acceptance: it carries synthetic data only. An acceptance is for a masked-data rehearsal in Test.");
        if (scannerOptions.EffectiveProvider != "mock") throw new DomainException("scanner_acceptance_provider", "The demonstration scanner is not the configured scanner, so there is nothing to accept.");
        return await exceptions.AcceptAsync(AcceptedExceptionKinds.MockScanner, "MOCK-SCANNER", string.IsNullOrWhiteSpace(scope) ? "masked-data rehearsal" : scope, reason, Operator(), TimeSpan.FromDays(Math.Clamp(validDays <= 0 ? 14 : validDays, 1, 90)), ct);
    }

    public async Task<IReadOnlyList<AcceptedExceptionDto>> AcceptedExceptionsAsync(bool activeOnly, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        return await exceptions.ListAsync(activeOnly, ct);
    }

    public async Task<AcceptedExceptionDto> AcceptWarningAsync(string kind, string code, string scope, string reason, int validDays, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        if (kind is not (AcceptedExceptionKinds.PreflightWarning or AcceptedExceptionKinds.ReadinessWarning or AcceptedExceptionKinds.MockScanner)) throw new DomainException("kind", "That kind of acceptance is not recognised.");
        return await exceptions.AcceptAsync(kind, code, scope, reason, Operator(), TimeSpan.FromDays(Math.Clamp(validDays <= 0 ? 30 : validDays, 1, 90)), ct);
    }

    public async Task RevokeAcceptanceAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        await exceptions.RevokeAsync(id, Operator(), ct);
    }
}
