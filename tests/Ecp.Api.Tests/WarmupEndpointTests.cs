using System.Net;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// IIS Application Initialization calls /warmup with no credentials. It must start the application and say nothing: 200, empty body,
/// in every installation state, and it must not open any other path to anonymous callers.
/// </summary>
[Collection("Api")]
[Trait("Category", "Api")]
public sealed class WarmupEndpointTests
{
    [Fact]
    public async Task Warmup_answers_200_with_an_empty_body_and_no_application_information()
    {
        using var app = DemoApp.Start();
        using var client = app.NewClient();
        var response = await client.GetAsync("/warmup");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.False(response.Headers.Contains("Location"));
        Assert.DoesNotContain(response.Headers, h => h.Key.Contains("Version", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/warmup"))).StatusCode);
    }

    [Fact]
    public async Task Warmup_works_before_setup_and_in_Production_like_HTTPS_mode_without_redirecting()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ecp-warm-" + Guid.NewGuid().ToString("N"))).FullName;
        using var app = DemoApp.Start(s =>
        {
            s["Ecp:Mode"] = "Test"; s["Ecp:DemoSeed"] = "false"; s["Ecp:DataDirectory"] = dir; s["Ecp:Network:RequireHttps"] = "true";
            s["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "ecp.db")}";
        });
        using var client = app.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        var response = await client.GetAsync("/warmup");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);                         // not sent to /Setup, not refused for plain HTTP from the local warm-up
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);   // everything else still behaves as before
    }

    [Theory]
    [InlineData("POST", "/warmup")]
    [InlineData("GET", "/warmup/extra")]
    [InlineData("GET", "/warmupx")]
    public async Task Nothing_but_GET_or_HEAD_of_exactly_warmup_is_answered_anonymously(string method, string path)
    {
        using var app = DemoApp.Start();
        using var client = app.NewClient();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void The_IIS_warm_up_script_names_warmup_as_the_only_anonymous_path()
    {
        string Repo(string relative)
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, relative))) return Path.Combine(d.FullName, relative);
            throw new FileNotFoundException(relative);
        }
        var script = File.ReadAllText(Repo("scripts/ci/Set-IisAlwaysOn.ps1"));
        Assert.Contains("$warmupPage = \"/warmup\"", script);
        Assert.Contains("$SiteName/health/live", script);    // the old anonymous path is switched back off
    }
}
