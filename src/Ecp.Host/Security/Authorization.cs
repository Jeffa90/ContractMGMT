using Ecp.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Ecp.Host.Security;

/// <summary>Page-level gate: the caller must hold the permission through at least one active assignment. Record-level checks still run in the services.</summary>
public sealed record PermissionRequirement(IReadOnlyList<string> Permissions) : IAuthorizationRequirement;

/// <summary>The caller authenticated recently (step-up) before a sensitive action such as reopening setup.</summary>
public sealed record RecentAuthenticationRequirement(TimeSpan Window) : IAuthorizationRequirement;

public static class Policies
{
    public const string PermissionPrefix = "perm:";
    public const string RecentAuthentication = "recent-authentication";
    public static string For(params string[] anyOf) => PermissionPrefix + string.Join('|', anyOf);
}

/// <summary>Creates a policy per permission code on demand: <c>[Authorize(Policy = "perm:contracts.read")]</c>. Unknown codes are refused.</summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Policies.PermissionPrefix, StringComparison.Ordinal))
        {
            // "perm:a|b" means any one of the listed permissions.
            var permissions = policyName[Policies.PermissionPrefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries);
            foreach (var permission in permissions)
                if (!Ecp.Identity.Access.PermissionMatrix.Instance.IsKnown(permission))
                    throw new InvalidOperationException($"Unknown permission '{permission}' in an authorisation policy.");
            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permissions)).Build();
        }
        return await base.GetPolicyAsync(policyName);
    }
}

internal sealed class PermissionHandler(IAccessService access) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        foreach (var permission in requirement.Permissions)
            if (await access.HoldsAnywhereAsync(permission)) { context.Succeed(requirement); return; }
    }
}

internal sealed class RecentAuthenticationHandler(Ecp.SharedKernel.IClock clock) : AuthorizationHandler<RecentAuthenticationRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RecentAuthenticationRequirement requirement)
    {
        if (SessionIssuer.RecentlyReauthenticated(context.User, clock.UtcNow, requirement.Window)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
