using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Identity.Domain;
using Ecp.Identity.Persistence;
using Ecp.Identity.Providers;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Identity.Windows;

/// <summary>
/// A Windows identity the web server has already authenticated. It carries who the person is and nothing else: no role, scope, group or permission is read
/// from it. <see cref="Account"/> is DOMAIN\name; <see cref="Sid"/> is the stable security identifier (it survives a rename; a re-created account has a new one).
/// </summary>
public sealed record WindowsLogin(string Account, string Sid);

public sealed record WindowsAccountState(string? InvitedAccount, bool Linked, DateTime? LinkedUtc);

public static partial class WindowsAccountName
{
    public const string Issuer = "windows";
    public const string AccountPrefix = "account:";
    public const string SidPrefix = "sid:";

    /// <summary>DOMAIN\name in upper case. Returns null when it is not a Windows account name (a UPN or a bare name is not accepted: the web server supplies DOMAIN\name).</summary>
    public static string? Normalise(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        var value = account.Trim();
        return AccountPattern().IsMatch(value) ? value.ToUpperInvariant() : null;
    }

    public static string DomainOf(string normalised) => normalised[..normalised.IndexOf('\\')];

    public static bool IsSid(string? sid) => !string.IsNullOrWhiteSpace(sid) && SidPattern().IsMatch(sid);

    /// <summary>The anonymous identity and any account's built-in Guest (RID 501) are never accepted.</summary>
    public static bool IsAnonymousOrGuest(string sid) => sid == "S-1-5-7" || sid == "S-1-1-0" || sid.EndsWith("-501", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}\\[A-Za-z0-9][A-Za-z0-9._$ -]{0,62}$", RegexOptions.NonBacktracking)]
    private static partial Regex AccountPattern();

    [GeneratedRegex(@"^S-1-\d{1,3}(-\d{1,10}){1,15}$", RegexOptions.NonBacktracking)]
    private static partial Regex SidPattern();
}

public interface IWindowsSignIn
{
    /// <summary>The key of the tenant's enabled Windows provider, or null when Windows sign-in is not enabled for the tenant.</summary>
    Task<string?> ProviderKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Decides whether an authenticated Windows identity may sign in: the person must have been invited (by account name, then linked by SID), be active, and hold
    /// effective access. A refusal is audited (event auth.signin_refused). Never creates an account and never grants a role.
    /// </summary>
    Task<LoginResolution> ResolveAsync(WindowsLogin login, bool auditRefusal = true, CancellationToken ct = default);

    /// <summary>Step-up: true when the Windows identity is the one this user is linked to.</summary>
    Task<bool> IsLinkedAsync(Guid userId, WindowsLogin login, CancellationToken ct = default);
}

public interface IWindowsAccounts
{
    Task<WindowsAccountState?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Records the Windows account a person was invited as. The caller has already been authorised (users.manage, or setup). The link to the stable SID is made at the first sign-in.</summary>
    Task SetInvitedAccountAsync(Guid userId, string account, string source, CancellationToken ct = default);
}

internal sealed class WindowsSignIn(IdentityDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, ActorContext actor, IClock clock) : IWindowsSignIn, IWindowsAccounts
{
    public async Task<string?> ProviderKeyAsync(CancellationToken ct = default)
        => (await db.Providers.AsNoTracking().Where(p => p.Kind == IdentityProviderKind.Windows && p.IsEnabled).OrderBy(p => p.Key).FirstOrDefaultAsync(ct))?.Key;

    public async Task<LoginResolution> ResolveAsync(WindowsLogin login, bool auditRefusal = true, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var provider = await db.Providers.AsNoTracking().Where(p => p.Kind == IdentityProviderKind.Windows && p.IsEnabled).OrderBy(p => p.Key).FirstOrDefaultAsync(ct);
        var account = WindowsAccountName.Normalise(login.Account);
        async Task<LoginResolution> RefuseAsync(string code, string message, Guid? userId = null)
        {
            if (auditRefusal) await RecordRefusalAsync(provider?.Key ?? "windows", code, account ?? "(unreadable)", login.Sid, userId, ct);
            return LoginResolution.Denied(code, message);
        }

        if (provider is null) return LoginResolution.Denied("windows_not_enabled", "Windows sign-in is not enabled.");   // not audited: nothing was attempted against an account
        if (account is null || !WindowsAccountName.IsSid(login.Sid) || WindowsAccountName.IsAnonymousOrGuest(login.Sid))
            return await RefuseAsync("account_unreadable", "The web server did not supply a usable Windows account.");
        var domains = provider.ToSettings().WindowsDomains;
        if (domains.Count > 0 && !domains.Contains(WindowsAccountName.DomainOf(account), StringComparer.Ordinal))
            return await RefuseAsync("domain_not_allowed", "Accounts from this Windows domain are not accepted here.");

        var sidSubject = WindowsAccountName.SidPrefix + login.Sid;
        var link = await db.ExternalIdentities.FirstOrDefaultAsync(x => x.Issuer == WindowsAccountName.Issuer && x.Subject == sidSubject, ct);
        var firstSignIn = false;
        if (link is null)
        {
            // Not linked yet: only an invitation that names this Windows account can be linked. Email is never used, and nothing is created.
            var invitation = await db.ExternalIdentities.FirstOrDefaultAsync(x => x.Issuer == WindowsAccountName.Issuer && x.Subject == WindowsAccountName.AccountPrefix + account, ct);
            if (invitation is null) return await RefuseAsync("not_invited", "No account has been invited for this Windows account. Ask an administrator to invite you.");
            if (await db.ExternalIdentities.AnyAsync(x => x.UserId == invitation.UserId && x.Issuer == WindowsAccountName.Issuer && x.Subject.StartsWith(WindowsAccountName.SidPrefix), ct))
                return await RefuseAsync("identity_mismatch", "This account name is already linked to a different Windows identity. Ask an administrator to confirm who you are.", invitation.UserId);
            link = invitation; firstSignIn = true;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == link.UserId, ct);
        if (user is null) return await RefuseAsync("not_invited", "No account has been invited for this Windows account. Ask an administrator to invite you.");
        if (user.Status == UserStatus.Disabled) return await RefuseAsync("user_disabled", "This account is disabled.", user.Id);

        // Authentication is not authorisation: without at least one active role the person cannot do anything, so they are not signed in. Nothing is changed for them.
        var now = clock.UtcNow;
        var hasAccess = (await db.RoleAssignments.AsNoTracking().Where(a => a.UserId == user.Id).ToListAsync(ct)).Any(a => a.IsActiveAt(now));
        if (!hasAccess) return await RefuseAsync("no_access", "Your account exists but has no access to the application yet. Ask an administrator to give you access.", user.Id);

        if (firstSignIn) link.RebindSubject(sidSubject, now);
        actor.Type = ActorType.User; actor.ActorId = user.Id.ToString(); actor.Source = "WindowsSignIn";
        var wasInvited = user.Status == UserStatus.Invited;
        user.Activate(now);
        user.RecordSignIn(now);
        link.Seen(now);
        await uow.ExecuteAsync(async c =>
        {
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry(wasInvited ? "user.activated" : "user.signed_in", nameof(UserProfile), user.Id.ToString(),
                NewValuesJson: IdentityJson.Of(new { provider = provider.Key, method = "windows", account, linked = firstSignIn })));
        }, ct);
        return LoginResolution.Success(user.Id);
    }

    public async Task<bool> IsLinkedAsync(Guid userId, WindowsLogin login, CancellationToken ct = default)
        => WindowsAccountName.IsSid(login.Sid)
           && await db.ExternalIdentities.AsNoTracking().AnyAsync(x => x.UserId == userId && x.Issuer == WindowsAccountName.Issuer && x.Subject == WindowsAccountName.SidPrefix + login.Sid, ct);

    private async Task RecordRefusalAsync(string providerKey, string code, string account, string sid, Guid? userId, CancellationToken ct)
    {
        actor.Type = ActorType.System; actor.ActorId = null; actor.Source = "WindowsSignIn";
        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry("auth.signin_refused", "WindowsAccount", account, AuditOutcome.Denied, Reason: code,
                NewValuesJson: IdentityJson.Of(new { provider = providerKey, method = "windows", code, account, sid, userId })));
            return Task.CompletedTask;
        }, ct);
    }

    public async Task<WindowsAccountState?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.ExternalIdentities.AsNoTracking().Where(x => x.UserId == userId && x.Issuer == WindowsAccountName.Issuer).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var linked = rows.FirstOrDefault(r => r.Subject.StartsWith(WindowsAccountName.SidPrefix, StringComparison.Ordinal));
        var invited = rows.FirstOrDefault(r => r.Subject.StartsWith(WindowsAccountName.AccountPrefix, StringComparison.Ordinal));
        return new WindowsAccountState(invited?.Subject[WindowsAccountName.AccountPrefix.Length..], linked is not null, linked?.LinkedUtc);
    }

    public async Task SetInvitedAccountAsync(Guid userId, string account, string source, CancellationToken ct = default)
    {
        var normalised = WindowsAccountName.Normalise(account)
            ?? throw new DomainException("windows_account", "Enter the Windows account as DOMAIN\\username, for example CONTOSO\\jsmith.");
        var tenantId = tenant.RequireTenantId();
        var provider = (await ProviderKeyAsync(ct)) ?? "windows";
        await uow.ExecuteAsync(async c =>
        {
            _ = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, c) ?? throw new DomainException("user_not_found", "The user was not found.");
            var subject = WindowsAccountName.AccountPrefix + normalised;
            var clash = await db.ExternalIdentities.AnyAsync(x => x.Issuer == WindowsAccountName.Issuer && x.Subject == subject && x.UserId != userId, c);
            if (clash) throw new DomainException("windows_account_taken", "That Windows account is already invited as another person.");
            if (await db.ExternalIdentities.AnyAsync(x => x.UserId == userId && x.Issuer == WindowsAccountName.Issuer && x.Subject.StartsWith(WindowsAccountName.SidPrefix), c))
                throw new DomainException("windows_already_linked", "This person has already signed in with a Windows account. The link cannot be changed here; disable the account and invite a new one.");
            var existing = await db.ExternalIdentities.Where(x => x.UserId == userId && x.Issuer == WindowsAccountName.Issuer && x.Subject.StartsWith(WindowsAccountName.AccountPrefix)).ToListAsync(c);
            if (existing.Count == 1 && existing[0].Subject == subject) return;   // already invited as this account: nothing to change
            db.ExternalIdentities.RemoveRange(existing);
            db.ExternalIdentities.Add(ExternalIdentity.Link(tenantId, userId, provider, WindowsAccountName.Issuer, subject, clock.UtcNow));
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("windows_account_taken", "That Windows account is already invited as another person."); }
            audit.Record(new AuditEntry("user.windows_account_invited", nameof(UserProfile), userId.ToString(), NewValuesJson: IdentityJson.Of(new { account = normalised, source })));
        }, ct);
    }
}
