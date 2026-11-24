using Ecp.Configuration;
using Ecp.Packs;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// ADR-0047, through the real pack loader and setup catalogue in Production mode: the unsigned packs built from templates/packs, placed in a folder inside the
/// application folder, are trusted as "shipped with the application" and usable; the same package treated as an upload is still refused; and the setting can
/// switch the rule off.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShippedPackTrustScenarios
{
    /// <summary>A copy of the unsigned shipped packs inside the application (test) folder, as a deployment places them.</summary>
    private static string ShippedInsideApplication()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "shipped-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(TestShippedPacks.UnsignedDirectory, "*.ecpkg")) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        return dir;
    }

    private static Task<TestApp> ProductionAsync(string shippedDirectory, bool trustShipped)
        => TestApp.CreateSqliteAsync(EcpMode.Production, services => services.PostConfigure<PackOptions>(o =>
        {
            o.ShippedDirectory = shippedDirectory;
            o.TrustedKeys.Clear();                       // no signing key anywhere: the shipped packs are trusted only by ADR-0047
            o.TrustShippedPacks = trustShipped;
        }));

    [Fact]
    public async Task Unsigned_packs_shipped_inside_the_application_are_trusted_in_production_and_an_upload_of_the_same_package_is_not()
    {
        var dir = ShippedInsideApplication();
        try
        {
            await using var app = await ProductionAsync(dir, trustShipped: true);
            await using var scope = app.Services.CreateAsyncScope();

            // The setup wizard's catalogue (runs before any tenant exists) offers them.
            var catalogue = scope.ServiceProvider.GetRequiredService<ISolutionPackCatalogue>().ListEntries();
            Assert.NotEmpty(catalogue);
            Assert.All(catalogue, e => { Assert.Equal(TrustStatus.ShippedWithApplication, e.Trust.Status); Assert.True(e.IsUsable); Assert.True(e.ProductionEligible); });

            // The pack loader the running application uses agrees.
            var shipped = (await scope.ServiceProvider.GetRequiredService<IPackLoader>().ListAsync()).Where(e => e.IsShipped).ToList();
            Assert.NotEmpty(shipped);
            Assert.All(shipped, e => Assert.Equal(TrustStatus.ShippedWithApplication, e.Trust.Status));

            // The same bytes treated as an upload (anything not read from the shipped folder) still need a trusted signature.
            var opened = PackReader.Open(File.ReadAllBytes(Directory.EnumerateFiles(dir, "*.ecpkg").First()));
            var asUpload = await scope.ServiceProvider.GetRequiredService<PackTrustEvaluator>().EvaluateAsync(opened, default);
            Assert.Equal(TrustStatus.Missing, asUpload.Status);
            Assert.False(asUpload.IsAcceptable);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task With_the_setting_off_production_refuses_unsigned_shipped_packs_as_before()
    {
        var dir = ShippedInsideApplication();
        try
        {
            await using var app = await ProductionAsync(dir, trustShipped: false);
            await using var scope = app.Services.CreateAsyncScope();
            Assert.DoesNotContain(scope.ServiceProvider.GetRequiredService<ISolutionPackCatalogue>().ListEntries(), e => e.IsUsable);
            Assert.All((await scope.ServiceProvider.GetRequiredService<IPackLoader>().ListAsync()).Where(e => e.IsShipped), e => Assert.Equal(TrustStatus.Missing, e.Trust.Status));
        }
        finally { Directory.Delete(dir, true); }
    }
}
