using System.Net;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Identity.Providers;
using Ecp.SharedKernel;
using Ecp.Tenancy;

namespace Ecp.Host.Security;

public sealed class DemoIdentityOptions
{
    /// <summary>Accept demo sign-in from non-loopback addresses (for example a published container port). Off by default.</summary>
    public bool AllowRemote { get; set; }
}

public sealed record DemoUser(Guid TenantId, string TenantName, Guid UserId, string DisplayName, string Email, string Roles);

/// <summary>
/// Demonstration identity provider: pick a seeded synthetic user, no credentials. Exists only in Demo and Test modes; Production start-up is
/// refused while it is enabled (R-ID-01) and the service itself refuses to sign anyone in outside those modes.
/// </summary>
public sealed class DemoIdentityService(EcpHostOptions host, DemoIdentityOptions options, IServiceScopeFactory scopes, SessionIssuer sessions)
{
    public const string ProviderKey = "demo";
    public const string Issuer = "https://demo.ecp.invalid";

    public bool IsEnabled => host.Mode != EcpMode.Production && host.DemoTenantResolution;

    public bool IsRequestAllowed(HttpContext context)
        => IsEnabled && (options.AllowRemote || context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress));

    public async Task<IReadOnlyList<DemoUser>> ListAsync(CancellationToken ct)
    {
        if (!IsEnabled) return [];
        await using var scope = scopes.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
        var result = new List<DemoUser>();
        var tenants = (await directory.ListAsync(ct)).Select(t => (t.Id, t.Name)).Append((SystemTenant.Id, "Installation")).ToList();
        foreach (var (id, name) in tenants)
        {
            using var _ = tenantContext.Use(id);
            var users = await scope.ServiceProvider.GetRequiredService<IUserService>().ListAsync(ct);
            var assignments = await scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>().ListAsync(activeOnly: true, ct);
            foreach (var u in users.Where(u => u.Status == UserStatus.Active))
                result.Add(new DemoUser(id, name, u.Id, u.DisplayName, u.Email,
                    string.Join(", ", assignments.Where(a => a.UserId == u.Id).Select(a => a.RoleCode + (a.ViewerKind is { } k ? $" ({k})" : "")).Distinct())));
        }
        return result;
    }

    /// <summary>Signs in the chosen demo user through the same account-resolution path as any provider (so sign-in is audited and mappings apply).</summary>
    public async Task<bool> SignInAsync(HttpContext context, Guid tenantId, Guid userId)
    {
        if (!IsRequestAllowed(context)) return false;
        await using var scope = scopes.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        using var _ = tenantContext.Use(tenantId);
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var user = await users.GetAsync(userId, context.RequestAborted);
        if (user is not { Status: UserStatus.Active }) return false;
        var resolution = await users.ResolveLoginAsync(new ExternalLogin(ProviderKey, Issuer, user.Email, user.Email, true, user.DisplayName, []), context.RequestAborted);
        if (!resolution.Succeeded) return false;
        await sessions.SignInAsync(context, tenantId, userId, user.DisplayName, ProviderKey);
        return true;
    }
}
