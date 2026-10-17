using System.Globalization;
using System.Text.Json;
using Ecp.Host;
using Ecp.SharedKernel.Localisation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Ecp.Api.Tests;

[Collection("Api")]
[Trait("Category", "Api")]
public sealed class LocalisationApiTests
{
    private static (EcpFactory Factory, Microsoft.Data.Sqlite.SqliteConnection Keeper) Start()
    {
        var settings = EcpFactory.DemoSqlite();
        var keeper = new Microsoft.Data.Sqlite.SqliteConnection(settings["Database:ConnectionString"]!);
        keeper.Open();
        return (new EcpFactory(settings), keeper);
    }

    [Fact]
    public async Task The_reference_locale_is_en_AU_with_Brisbane_and_AUD_and_the_host_has_real_culture_data()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<LocalisationOptions>();
        Assert.Equal("en-AU", options.DefaultCulture);
        Assert.Equal("Australia/Brisbane", options.DefaultTimeZoneId);
        Assert.Equal("AUD", options.DefaultCurrencyCode);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/health/ready"));
        var check = doc.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "R-OPS-02");
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Resources_default_to_Australian_English_and_another_locale_overrides_only_what_differs()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-AU");
            Assert.Equal("Organisation", localizer["Org.Label"].Value);
            Assert.Equal("Enterprise Contract Platform", localizer["App.Name"].Value);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Organization", localizer["Org.Label"].Value);
            Assert.Equal("Enterprise Contract Platform", localizer["App.Name"].Value); // falls back to the neutral text

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR"); // no resource file: falls back, never throws
            Assert.Equal("Organisation", localizer["Org.Label"].Value);
            Assert.True(localizer["No.Such.Key"].ResourceNotFound);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task The_culture_cookie_selects_a_supported_culture_and_browser_language_does_not()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        var response = await client.SendAsync(request);
        Assert.Contains("en-AU", string.Join(",", response.Content.Headers.ContentLanguage), StringComparison.OrdinalIgnoreCase);

        var cookie = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        cookie.Headers.Add("Cookie", ".AspNetCore.Culture=c%3Den-US%7Cuic%3Den-US");
        var chosen = await client.SendAsync(cookie);
        Assert.Contains("en-US", string.Join(",", chosen.Content.Headers.ContentLanguage), StringComparison.OrdinalIgnoreCase);

        var unsupported = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        unsupported.Headers.Add("Cookie", ".AspNetCore.Culture=c%3Dde-DE%7Cuic%3Dde-DE");
        Assert.Contains("en-AU", string.Join(",", (await client.SendAsync(unsupported)).Content.Headers.ContentLanguage), StringComparison.OrdinalIgnoreCase);
    }
}
