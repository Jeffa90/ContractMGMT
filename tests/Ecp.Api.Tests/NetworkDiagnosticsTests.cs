using System.Net;
using Ecp.Host.Network;
using Ecp.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// G-33: HTTPS and reverse-proxy behaviour. A valid direct HTTPS request and a valid request through a TLS-terminating proxy both work and keep Secure cookies; a proxy that does not forward the scheme, forwarded headers from a
/// sender the host does not trust, and plain HTTP each get a safe page with a stable code, a plain explanation and the correlation reference, never a bare error and never an internal header, address or stack trace.
/// </summary>
[Collection("Api")]
[Trait("Category", "Network")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class NetworkDiagnosticsTests
{
    private static DemoApp Start(bool forwarded, string? knownProxies = "127.0.0.1")
        => DemoApp.Start(s =>
        {
            s["Ecp:Network:RequireHttps"] = "true";
            if (forwarded) { s["Ecp:Network:ForwardedHeaders:Enabled"] = "true"; if (knownProxies is not null) s["Ecp:Network:ForwardedHeaders:KnownProxies"] = knownProxies; }
        }, services => services.AddSingleton<IStartupFilter, TestRemoteAddressFilter>());

    private static HttpClient PlainHttpClient(DemoApp app)
        => app.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("http://localhost") });

    private static bool HasSecure(HttpResponseMessage r, string cookie)
        => r.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(v => v.StartsWith(cookie + "=", StringComparison.Ordinal) && v.Contains("secure", StringComparison.OrdinalIgnoreCase));

    private static async Task AssertSafeAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, body);
        Assert.Contains("What to do", body);
        var reference = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        Assert.Contains(reference, body);                                             // the person can quote the same reference support will search for
        Assert.DoesNotContain("X-Forwarded", body.Replace("X-Forwarded-Proto and X-Forwarded-For", ""));   // only the operator's instruction names them; no header from the request is echoed
        Assert.DoesNotContain("10.9.9.9", body);
        Assert.DoesNotContain("   at ", body);
        Assert.DoesNotContain("Exception", body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Direct_https_works_and_the_cookies_stay_secure()
    {
        using var app = Start(forwarded: false);
        using var client = app.NewClient();                                           // https://localhost
        var response = await client.GetAsync("/SignIn");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(HasSecure(response, "ecp.csrf"), "The antiforgery cookie must be Secure.");
        using var signedIn = await app.SignInAsync("sysadmin@installation.example.test");   // sign-in with antiforgery works end to end
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/Admin/Operations")).StatusCode);
    }

    [Fact]
    public async Task A_tls_terminating_proxy_that_forwards_the_scheme_works_and_the_cookies_stay_secure()
    {
        using var app = Start(forwarded: true);
        using var client = PlainHttpClient(app);                                      // the proxy talks plain HTTP to the application...
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");               // ...and says the visitor used HTTPS
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "contracts.example.test");   // HSTS is never sent for localhost, so the visitor's host name is forwarded too
        client.DefaultRequestHeaders.Add("X-Test-Remote", "127.0.0.1");               // from the proxy the host was told to trust
        var response = await client.GetAsync("/SignIn");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(HasSecure(response, "ecp.csrf"), "The antiforgery cookie must be Secure behind the proxy as well.");
        Assert.Contains("max-age", string.Join(' ', response.Headers.TryGetValues("Strict-Transport-Security", out var h) ? h : []));
    }

    [Fact]
    public async Task A_proxy_that_does_not_forward_the_scheme_gets_a_safe_explanation_not_an_error()
    {
        using var app = Start(forwarded: true);
        using var client = PlainHttpClient(app);
        client.DefaultRequestHeaders.Add("X-Test-Remote", "127.0.0.1");
        var response = await client.GetAsync("/SignIn");
        await AssertSafeAsync(response, "ECP-NET-001");
        Assert.Contains("X-Forwarded-Proto", await response.Content.ReadAsStringAsync());   // the operator is told which setting the proxy needs
        // a form post is refused the same way, before any antiforgery or cookie work could fail
        var post = await client.PostAsync("/SignIn", new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "y" }));
        await AssertSafeAsync(post, "ECP-NET-001");
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_sender_are_ignored_and_explained()
    {
        using var app = Start(forwarded: true);
        using var client = PlainHttpClient(app);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Test-Remote", "10.9.9.9");                // not a proxy the host was told to trust
        var response = await client.GetAsync("/SignIn");
        await AssertSafeAsync(response, "ECP-NET-002");
        Assert.Contains("KnownProxies", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Forwarded_headers_when_handling_is_not_enabled_say_how_to_enable_it()
    {
        using var app = Start(forwarded: false);
        using var client = PlainHttpClient(app);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        var response = await client.GetAsync("/SignIn");
        await AssertSafeAsync(response, "ECP-NET-002");
        Assert.Contains("Ecp:Network:ForwardedHeaders:Enabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Plain_http_is_refused_for_pages_and_the_api_but_health_probes_stay_reachable()
    {
        using var app = Start(forwarded: false);
        using var client = PlainHttpClient(app);
        await AssertSafeAsync(await client.GetAsync("/"), "ECP-NET-001");
        var api = await client.GetAsync("/api/v1/contracts");
        Assert.Equal(HttpStatusCode.BadRequest, api.StatusCode);
        Assert.Equal("application/problem+json", api.Content.Headers.ContentType?.MediaType);
        Assert.Contains("ECP-NET-001", await api.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Readiness_reports_the_misconfiguration_while_it_is_happening()
    {
        using var app = Start(forwarded: false);
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var before = (await scope.ServiceProvider.GetRequiredService<ReadinessService>().RunAsync()).Results.Single(r => r.Id == "R-NET-01");
            Assert.Equal(ReadinessStatus.Healthy, before.Status);
            Assert.Contains("must be reached by HTTPS directly", before.Message);
        }
        using var client = PlainHttpClient(app);
        await client.GetAsync("/SignIn");
        await using var after = app.Factory.Services.CreateAsyncScope();
        var result = (await after.ServiceProvider.GetRequiredService<ReadinessService>().RunAsync()).Results.Single(r => r.Id == "R-NET-01");
        Assert.Equal(ReadinessStatus.Degraded, result.Status);
        Assert.Contains("ECP-NET-001", result.Message);
    }

    [Fact]
    public void Production_always_requires_https_and_the_setting_cannot_switch_it_off()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ecp:Network:RequireHttps"] = "false" }).Build();
        Assert.True(NetworkOptions.From(config, production: true).RequireHttps);
        Assert.False(NetworkOptions.From(config, production: false).RequireHttps);
    }

    [Theory]
    [InlineData(true, "https", null, NetworkFinding.Ok)]
    [InlineData(true, "http", null, NetworkFinding.PlainHttp)]
    [InlineData(true, "http", "http", NetworkFinding.PlainHttp)]
    [InlineData(true, "http", "https", NetworkFinding.ForwardedHeadersUntrusted)]
    [InlineData(true, "http", "https, http", NetworkFinding.ForwardedHeadersUntrusted)]
    [InlineData(false, "http", null, NetworkFinding.Ok)]
    public void The_diagnosis_follows_the_scheme_the_host_sees_and_the_header_it_was_not_able_to_use(bool require, string scheme, string? forwarded, NetworkFinding expected)
        => Assert.Equal(expected, NetworkDiagnoser.Evaluate(new NetworkOptions { RequireHttps = require }, scheme, forwarded).Finding);
}
