using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Recovery;

/// <summary>One operational fact summed (or time-combined) over every tenant.</summary>
internal sealed record FactTotal(string Area, string Key, long Value, DateTime? Utc);

/// <summary>
/// Gathers the modules' operational facts (<see cref="IOperationalProbe"/>) one tenant at a time, each inside that tenant's own scope so row-level security applies, and combines them. It checks no permission: callers
/// (the dashboard, which does; the support bundle, which is reached from an authorised page or from the server's command line) decide who may see the result. It never reads record content, because the probes cannot return any.
/// </summary>
internal sealed class OperationalFactCollector(IServiceProvider root)
{
    public async Task<IReadOnlyList<FactTotal>?> CollectAsync(DateTime now, CancellationToken ct)
    {
        try
        {
            var combined = new Dictionary<(string, string), (long Value, DateTime? Utc, FactCombine Rule)>();
            foreach (var tenantId in await UpgradeScopes.TenantsAsync(root, ct))
            {
                await using var scope = root.CreateAsyncScope();
                using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
                foreach (var probe in scope.ServiceProvider.GetServices<IOperationalProbe>())
                    foreach (var f in await probe.ProbeAsync(now, ct))
                    {
                        var key = (f.Area, f.Key);
                        if (!combined.TryGetValue(key, out var have)) { combined[key] = (f.Value, f.Utc, f.Combine); continue; }
                        DateTime? utc = f.Utc is null ? have.Utc : have.Utc is null ? f.Utc : f.Combine == FactCombine.Earliest ? (f.Utc < have.Utc ? f.Utc : have.Utc) : f.Combine == FactCombine.Latest ? (f.Utc > have.Utc ? f.Utc : have.Utc) : have.Utc;
                        combined[key] = (have.Value + f.Value, utc, f.Combine);
                    }
            }
            return combined.OrderBy(k => k.Key.Item1, StringComparer.Ordinal).ThenBy(k => k.Key.Item2, StringComparer.Ordinal).Select(k => new FactTotal(k.Key.Item1, k.Key.Item2, k.Value.Value, k.Value.Utc)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
