using Ecp.Persistence;
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
        services.AddModuleDbContext<SetupDbContext>(Name, Tier, Schema);
        services.AddSingleton<ISetupStore, SetupStore>();
        services.AddSingleton<ISetupService, SetupService>();
        foreach (var stage in Stages.SetupStages.All()) services.AddSingleton(typeof(ISetupStage), stage);
        foreach (var check in Stages.SetupChecks.All()) services.AddSingleton(typeof(ISetupCheck), check);
        return services;
    }
}
