using Ecp.Configuration;
using Ecp.Persistence;
using Ecp.Setup.Onboarding;
using Ecp.Setup.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Setup;

public static class SetupModule
{
    public const string Name = "Setup";
    public const string Schema = "setup";
    public const int Tier = 9;

    public static IServiceCollection AddSetupModule(this IServiceCollection services, SetupEnvironment environment)
    {
        services.AddSingleton(environment);
        // The email and scanner services judge a setting by the installation's mode. The installation's mode is the one setup runs in.
        services.AddSingleton(new Ecp.Operations.HostModeAccessor(environment.HostMode));
        services.AddModuleDbContext<SetupDbContext>(Name, Tier, Schema);
        services.AddSingleton<ISetupStore, SetupStore>();
        services.AddSingleton<ISetupService, SetupService>();
        services.AddScoped<OnboardingFactsLoader>();
        services.AddScoped<OnboardingApplier>();
        services.AddScoped<IConfigurationKind, OrganisationOnboardingKind>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        services.AddScoped<IOnboardingReadiness, OnboardingReadiness>();
        services.AddScoped<Organisations.IOrganisationCreation, Organisations.OrganisationCreation>();
        foreach (var stage in Stages.SetupStages.All()) services.AddSingleton(typeof(ISetupStage), stage);
        foreach (var check in Stages.SetupChecks.All()) services.AddSingleton(typeof(ISetupCheck), check);
        return services;
    }
}
