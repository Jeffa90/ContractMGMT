using Ecp.Access.Persistence;
using Ecp.Contracts;
using Ecp.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Access;

/// <summary>
/// The guided-access layer (docs/UX_REDESIGN.md): access profiles, the effective-access report, and the services that turn plain-English choices into the platform's ordinary role grants and ownerships.
/// It adds no authorisation rule of its own; every decision stays with the access evaluator and the delegation policy.
/// </summary>
public static class AccessModule
{
    public const string Name = "Access";
    public const string Schema = "access";
    public const int Tier = 8;

    public static IServiceCollection AddAccessModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<AccessDbContext>(Name, Tier, Schema);
        services.AddScoped<EffectiveAccessService>();
        services.AddScoped<IEffectiveAccess>(sp => sp.GetRequiredService<EffectiveAccessService>());
        services.AddScoped<ITrustedEffectiveAccess>(sp => sp.GetRequiredService<EffectiveAccessService>());
        services.AddScoped<IAccessProfileService, AccessProfileService>();
        services.AddScoped<IPersonAccessService, PersonAccessService>();
        services.AddScoped<IPreviewService, PreviewService>();
        services.AddScoped<IAdministratorBootstrap, AdministratorBootstrap>();
        services.AddScoped<ICategoryOwnership, CategoryOwnershipService>();
        services.AddScoped<IGroupMappingAdministration, GroupMappingAdministration>();
        services.AddScoped<IApprovalAuthorities, ApprovalAuthorityService>();
        services.AddScoped<CategoryOwnershipPolicyService>();
        services.AddScoped<ICategoryOwnershipPolicy>(sp => sp.GetRequiredService<CategoryOwnershipPolicyService>());
        services.AddScoped<IOwnershipPolicyEngine>(sp => sp.GetRequiredService<CategoryOwnershipPolicyService>());
        services.AddScoped<ICategoryLifecycleObserver>(sp => sp.GetRequiredService<CategoryOwnershipPolicyService>());
        return services;
    }
}
