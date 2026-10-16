using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecp.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers shared persistence infrastructure. Call once from the composition root.</summary>
    public static IServiceCollection AddEcpPersistence(this IServiceCollection services, DatabaseOptions options)
    {
        services.TryAddSingleton(options);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.TryAddScoped<ActorContext>();
        services.TryAddScoped<IActorContext>(sp => sp.GetRequiredService<ActorContext>());
        services.TryAddScoped<IScopedConnection, ScopedConnection>();
        services.TryAddScoped<DbContextServices>();
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddSingleton<KeepScopedConnectionOpenInterceptor>();
        services.TryAddSingleton<TenantSessionContextInterceptor>();
        services.TryAddSingleton<MigrationRunner>();
        return services;
    }

    /// <summary>Registers a module's DbContext on the scope's shared connection and records its descriptor.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string name, int tier, string schema)
        where TContext : TenantDbContext
    {
        services.AddSingleton(new ModuleDescriptor(name, tier, typeof(TContext), schema));
        services.AddDbContext<TContext>((sp, builder) =>
        {
            var connection = sp.GetRequiredService<IScopedConnection>();
            if (connection.Provider == DatabaseProvider.SqlServer)
                builder.UseSqlServer(connection.Connection, o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema));
            else
                builder.UseSqlite(connection.Connection);
            builder.ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>();
            builder.AddInterceptors(
                sp.GetRequiredService<KeepScopedConnectionOpenInterceptor>(),
                sp.GetRequiredService<TenantSessionContextInterceptor>());
        });
        services.AddScoped<IRollbackHook>(sp => new ChangeTrackerReset(() => sp.GetRequiredService<TContext>()));
        return services;
    }
}

/// <summary>Clears a module context's tracked entities after a rollback, so changes from the failed transaction cannot be saved by the next one.</summary>
internal sealed class ChangeTrackerReset(Func<DbContext> context) : IRollbackHook
{
    public void OnRollback() => context().ChangeTracker.Clear();
}
