using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Integration.Tests;

/// <summary>
/// Runs a shared scenario on a fresh application on each provider. The lightweight (SQLite) run checks provider-independent behaviour only and is labelled so;
/// the SQL Server run is the evidence for constraints, concurrency and Row-Level Security, and is reported NOT VERIFIED (skipped) when no SQL Server is available.
/// </summary>
internal static class ProviderRunner
{
    public static async Task SqliteAsync(Func<TestApp, Task> scenario, Action<IServiceCollection>? configure = null)
    {
        await using var app = await TestApp.CreateSqliteAsync(configure: configure);
        await scenario(app);
    }

    public static async Task SqlServerAsync(Func<TestApp, Task> scenario, Action<IServiceCollection>? configure = null)
    {
        await using var app = await TestApp.CreateSqlServerAsync(configure: configure);
        await scenario(app);
    }
}
