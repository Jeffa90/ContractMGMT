using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Ecp.Persistence;

/// <summary>The model differs slightly by provider (rowversion vs application-stamped token), so the cache key includes it.</summary>
#pragma warning disable EF1001 // ModelCacheKeyFactory is the documented extension point for provider-dependent models.
public sealed class ProviderAwareModelCacheKeyFactory(ModelCacheKeyFactoryDependencies dependencies) : ModelCacheKeyFactory(dependencies)
{
    public override object Create(DbContext context, bool designTime)
        => (base.Create(context, designTime), context.Database.ProviderName);
}
#pragma warning restore EF1001
