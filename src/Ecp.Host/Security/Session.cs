using System.Security.Claims;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Ecp.Host.Security;

public static class EcpClaims
{
    public const string UserId = "ecp:uid";
    public const string TenantId = "ecp:tid";
    public const string Provider = "ecp:provider";
    public const string AuthTime = "ecp:auth_time";
    public const string ReauthUtc = "ecp:reauth_utc";
    /// <summary>The support or emergency elevation this session is working under, and the tenant it targets. Re-validated against the database on every request.</summary>
    public const string ElevationId = "ecp:elev";
    public const string ElevationTenantId = "ecp:elev_tid";
}

public static class EcpSchemes
{
    public const string Session = "ecp.session";
    public const string OidcPrefix = "oidc-";
}

internal static class SessionPrincipalFactory
{
    public static ClaimsPrincipal Create(Guid tenantId, Guid userId, string displayName, string providerKey, DateTime nowUtc)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, displayName),
            new(EcpClaims.UserId, userId.ToString()),
            new(EcpClaims.TenantId, tenantId.ToString()),
            new(EcpClaims.Provider, providerKey),
            new(EcpClaims.AuthTime, new DateTimeOffset(nowUtc).ToUnixTimeSeconds().ToString()),
            new(EcpClaims.ReauthUtc, nowUtc.ToString("O")),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, EcpSchemes.Session, ClaimTypes.Name, ClaimTypes.Role));
    }
}

/// <summary>Issues and refreshes the application session. The tenant and user come from the verified identity, never from request input (TENANCY section 3).</summary>
public sealed class SessionIssuer(IClock clock)
{
    public async Task SignInAsync(HttpContext context, Guid tenantId, Guid userId, string displayName, string providerKey)
    {
        var principal = SessionPrincipalFactory.Create(tenantId, userId, displayName, providerKey, clock.UtcNow);
        await context.SignInAsync(EcpSchemes.Session, principal, new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
    }

    /// <summary>Marks a fresh authentication (step-up) on the existing session.</summary>
    public async Task RefreshReauthenticationAsync(HttpContext context)
    {
        var principal = context.User;
        var identity = new ClaimsIdentity(principal.Claims.Where(c => c.Type != EcpClaims.ReauthUtc), EcpSchemes.Session, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(EcpClaims.ReauthUtc, clock.UtcNow.ToString("O")));
        await context.SignInAsync(EcpSchemes.Session, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
    }

    /// <summary>Points the session at an elevation (the system administrator keeps their own identity; only the working tenant changes, and only while the elevation is active).</summary>
    public async Task BeginElevationAsync(HttpContext context, Guid tenantId, Guid elevationId)
    {
        var identity = new ClaimsIdentity(context.User.Claims.Where(c => c.Type is not (EcpClaims.ElevationId or EcpClaims.ElevationTenantId)), EcpSchemes.Session, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(EcpClaims.ElevationId, elevationId.ToString()));
        identity.AddClaim(new Claim(EcpClaims.ElevationTenantId, tenantId.ToString()));
        await context.SignInAsync(EcpSchemes.Session, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
    }

    public async Task EndElevationAsync(HttpContext context)
    {
        var identity = new ClaimsIdentity(context.User.Claims.Where(c => c.Type is not (EcpClaims.ElevationId or EcpClaims.ElevationTenantId)), EcpSchemes.Session, ClaimTypes.Name, ClaimTypes.Role);
        await context.SignInAsync(EcpSchemes.Session, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
    }

    public static (Guid ElevationId, Guid TenantId)? IdentifyElevation(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(EcpClaims.ElevationId), out var e) && Guid.TryParse(user.FindFirstValue(EcpClaims.ElevationTenantId), out var t) ? (e, t) : null;

    public static (Guid UserId, Guid TenantId)? Identify(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(EcpClaims.UserId), out var u) && Guid.TryParse(user.FindFirstValue(EcpClaims.TenantId), out var t) ? (u, t) : null;

    public static bool RecentlyReauthenticated(ClaimsPrincipal user, DateTime nowUtc, TimeSpan window)
        => DateTime.TryParse(user.FindFirstValue(EcpClaims.ReauthUtc), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && nowUtc - at <= window && at <= nowUtc.AddMinutes(1);
}

/// <summary>
/// Establishes the tenant and the current user for the request from the signed session, then rejects the session if the account has been
/// disabled or the tenant suspended since sign-in. Replaces the anonymous demonstration tenant resolution for signed-in requests.
/// </summary>
public sealed class SessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, CurrentUser current, ActorContext actor, IUserService users, ITenantDirectory directory,
        ReauthenticationContext reauth, ElevationContext elevationContext, IElevationService elevations, SessionIssuer sessions)
    {
        if (context.User.Identity?.IsAuthenticated == true && SessionIssuer.Identify(context.User) is { } who)
        {
            // When the person last authenticated comes from the signed session; services that guard sensitive actions read it from here, never from request input.
            if (DateTime.TryParse(context.User.FindFirstValue(EcpClaims.ReauthUtc), null, System.Globalization.DateTimeStyles.RoundtripKind, out var reauthAt)) reauth.Set(reauthAt);
            var info = who.TenantId == SystemTenant.Id ? null : await directory.FindAsync(who.TenantId, context.RequestAborted);
            var tenantOk = who.TenantId == SystemTenant.Id || info is { Status: Ecp.Tenancy.Domain.TenantStatus.Active };
            if (tenantOk)
            {
                using (tenant.Use(who.TenantId))
                {
                    var user = await users.GetAsync(who.UserId, context.RequestAborted);
                    if (user is { Status: Ecp.Identity.Domain.UserStatus.Active })
                    {
                        // Support or emergency access: only an installation administrator's session can carry one, and it is valid only while the elevation is active, for this person, in the tenant it names.
                        if (who.TenantId == SystemTenant.Id && SessionIssuer.IdentifyElevation(context.User) is { } named)
                        {
                            var grant = await elevations.ResolveActiveAsync(named.TenantId, named.ElevationId, who.UserId, context.RequestAborted);
                            if (grant is not null)
                            {
                                using (tenant.Use(grant.TenantId))
                                {
                                    elevationContext.Activate(grant);
                                    current.Set(who.UserId);
                                    actor.Type = ActorType.Support; actor.ActorId = who.UserId.ToString(); actor.ElevationId = grant.ElevationId;
                                    await next(context);
                                    return;
                                }
                            }
                            await sessions.EndElevationAsync(context); // expired, ended or revoked: the session returns to the person's own tenant
                        }
                        current.Set(who.UserId);
                        actor.Type = ActorType.User; actor.ActorId = who.UserId.ToString();
                        await next(context);
                        return;
                    }
                }
            }
            await context.SignOutAsync(EcpSchemes.Session);
            if (!context.Request.Path.StartsWithSegments("/api")) { context.Response.Redirect("/SignIn?reason=inactive"); return; }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(context);
    }
}
