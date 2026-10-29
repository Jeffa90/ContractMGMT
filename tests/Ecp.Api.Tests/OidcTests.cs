using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Host;
using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Identity.Providers;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// A mock identity provider reachable only through the host's back-channel: discovery, signing keys and a token endpoint that returns a real
/// RS256-signed ID token. It lets the actual OpenID Connect handler (state, nonce, PKCE, signature, issuer, audience, lifetime) run end to end.
/// It is a mock: it proves our integration, not any real provider.
/// </summary>
internal sealed class MockIdp : HttpMessageHandler, IOidcOptionsCustomizer
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _otherRsa = RSA.Create(2048);
    private readonly string _kid = "mock-key-1";
    public string Issuer { get; }
    public string ClientId { get; }
    public string? PendingNonce { get; set; }
    public string? PendingCodeChallenge { get; set; }
    public Dictionary<string, object> Claims { get; } = [];
    public bool SignWithWrongKey { get; set; }
    public string? AudienceOverride { get; set; }
    public string? ReceivedClientSecret { get; private set; }
    public string? ReceivedVerifier { get; private set; }
    public int TokenRequests { get; private set; }

    public MockIdp(string issuer, string clientId) { Issuer = issuer.TrimEnd('/'); ClientId = clientId; }

    public void Customize(string scheme, OpenIdConnectOptions options)
    {
        options.BackchannelHttpHandler = this;
        options.RequireHttpsMetadata = false;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            return Json(new
            {
                issuer = Issuer, authorization_endpoint = Issuer + "/authorize", token_endpoint = Issuer + "/token", jwks_uri = Issuer + "/keys",
                response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" },
                code_challenge_methods_supported = new[] { "S256" }
            });
        if (path.EndsWith("/keys", StringComparison.Ordinal))
        {
            var p = _rsa.ExportParameters(false);
            return Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = _kid, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) } } });
        }
        if (path.EndsWith("/token", StringComparison.Ordinal))
        {
            TokenRequests++;
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x.Length > 1 ? x[1].Replace('+', ' ') : ""));
            ReceivedClientSecret = form.GetValueOrDefault("client_secret");
            ReceivedVerifier = form.GetValueOrDefault("code_verifier");
            // PKCE: the verifier must hash to the challenge sent on the authorisation request.
            var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(ReceivedVerifier ?? "")));
            if (PendingCodeChallenge is not null && challenge != PendingCodeChallenge) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
            var now = DateTime.UtcNow;
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = Issuer, Audience = AudienceOverride ?? ClientId, IssuedAt = now, NotBefore = now.AddMinutes(-1), Expires = now.AddMinutes(10),
                Claims = new Dictionary<string, object>(Claims) { ["nonce"] = PendingNonce ?? "" },
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(SignWithWrongKey ? _otherRsa : _rsa) { KeyId = _kid }, SecurityAlgorithms.RsaSha256)
            };
            var idToken = new JsonWebTokenHandler().CreateToken(descriptor);
            return Json(new { id_token = idToken, access_token = "mock-access-token", token_type = "Bearer", expires_in = 600 });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class OidcSignInTests
{
    private const string Authority = "https://idp.mock.test/realm";

    private sealed record Setup(DemoApp App, MockIdp Idp, string Scheme, Guid TenantId) : IDisposable
    {
        public void Dispose() => App.Dispose();
    }

    private static async Task<Setup> StartAsync(IdentityProviderKind kind = IdentityProviderKind.Oidc, string? entraTenant = null)
    {
        Environment.SetEnvironmentVariable("ECP_MOCK_IDP_SECRET", "mock-client-secret-value");
        var issuer = kind == IdentityProviderKind.Entra ? $"https://login.microsoftonline.com/{entraTenant}/v2.0" : Authority;
        var idp = new MockIdp(issuer, "ecp-client");
        var app = DemoApp.Start(services: s => s.AddSingleton<IOidcOptionsCustomizer>(idp));
        using var warm = app.NewClient(); // start the host
        var (tenantId, _) = await app.FindUserAsync("tenantadmin@a.example.test");
        var key = kind == IdentityProviderKind.Entra ? "entra" : "mock";
        await app.WithTenantAsync(tenantId, async sp =>
        {
            var issues = await sp.GetRequiredService<IProviderRegistry>().SaveAsync(new IdentityProviderSettings(key, kind, "Mock sign-in",
                kind == IdentityProviderKind.Entra ? null : Authority, "ecp-client", "env:ECP_MOCK_IDP_SECRET", entraTenant));
            Assert.DoesNotContain(issues, i => i.Blocking);
            // pre-invite the person and map an identity group to a role
            var users = sp.GetRequiredService<IUserService>();
            await users.InviteAsync("Olive Oidc", "olive.oidc@a.example.test");
            return true;
        });
        return new Setup(app, idp, OidcSchemeManager.SchemeName(tenantId, key), tenantId);
    }

    private static (string State, string Nonce, string Challenge, string RedirectUri, Uri Location) ParseAuthorize(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        var q = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);   // PKCE is always used
        Assert.Equal("ecp-client", q["client_id"]);
        Assert.Null(q["client_secret"]);                      // the secret never travels through the browser
        return (q["state"]!, q["nonce"]!, q["code_challenge"]!, q["redirect_uri"]!, location);
    }

    private static async Task<HttpResponseMessage> CallbackAsync(HttpClient client, Setup s, string state, string nonce, string challenge, Action<MockIdp>? tweak = null)
    {
        s.Idp.PendingNonce = nonce; s.Idp.PendingCodeChallenge = challenge;
        tweak?.Invoke(s.Idp);
        return await client.GetAsync($"/auth/callback/{s.Scheme}?code=mock-code&state={Uri.EscapeDataString(state)}");
    }

    [Fact]
    public async Task A_full_authorisation_code_flow_with_pkce_signs_in_an_invited_user_and_applies_group_mappings()
    {
        using var s = await StartAsync();
        var (tenantId, _) = await s.App.FindUserAsync("tenantadmin@a.example.test");
        await s.App.WithTenantAsync(tenantId, async sp =>
        {
            var org = (await sp.GetRequiredService<Ecp.Tenancy.IOrganisationStructureService>().ListOrganisationsAsync()).First(o => o.Name == "Demo Procurement Group").Id;
            await sp.GetRequiredService<IGroupMappingService>().AddAsync("mock", "grp-legal", RoleCodes.Legal, null, ScopeSet.ForOrganisations([org], "Restricted"), null);
            return true;
        });
        s.Idp.Claims["sub"] = "olive-subject-1"; s.Idp.Claims["email"] = "olive.oidc@a.example.test"; s.Idp.Claims["email_verified"] = true;
        s.Idp.Claims["name"] = "Olive Oidc"; s.Idp.Claims["groups"] = new[] { "grp-legal", "grp-other" };

        using var browser = s.App.NewClient();
        var start = await browser.GetAsync($"/Account/Challenge?scheme={Uri.EscapeDataString(s.Scheme)}&returnUrl=%2FContracts");
        var (state, nonce, challenge, redirectUri, _) = ParseAuthorize(start);
        Assert.EndsWith($"/auth/callback/{s.Scheme}", redirectUri);

        var callback = await CallbackAsync(browser, s, state, nonce, challenge);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/Contracts", callback.Headers.Location!.OriginalString);
        Assert.Equal("mock-client-secret-value", s.Idp.ReceivedClientSecret);  // the confidential client authenticated at the token endpoint
        Assert.False(string.IsNullOrEmpty(s.Idp.ReceivedVerifier));

        var register = await browser.GetAsync("/Contracts");
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);                  // Legal (from the group mapping) reads contracts
        var home = await browser.GetStringAsync("/Access");
        Assert.Contains("Olive Oidc", home);
        Assert.Contains("Legal", home);
    }

    [Fact]
    public async Task A_person_who_was_not_invited_is_refused_even_with_a_valid_token()
    {
        using var s = await StartAsync();
        s.Idp.Claims["sub"] = "stranger-1"; s.Idp.Claims["email"] = "stranger@a.example.test"; s.Idp.Claims["email_verified"] = true;
        using var browser = s.App.NewClient();
        var (state, nonce, challenge, _, _) = ParseAuthorize(await browser.GetAsync($"/Account/Challenge?scheme={Uri.EscapeDataString(s.Scheme)}"));
        var callback = await CallbackAsync(browser, s, state, nonce, challenge);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Contains("/SignIn?error=not_invited", callback.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Contracts")).StatusCode); // no session
    }

    [Fact]
    public async Task An_unverified_email_does_not_link_an_invited_account()
    {
        using var s = await StartAsync();
        s.Idp.Claims["sub"] = "olive-subject-2"; s.Idp.Claims["email"] = "olive.oidc@a.example.test"; s.Idp.Claims["email_verified"] = false;
        using var browser = s.App.NewClient();
        var (state, nonce, challenge, _, _) = ParseAuthorize(await browser.GetAsync($"/Account/Challenge?scheme={Uri.EscapeDataString(s.Scheme)}"));
        var callback = await CallbackAsync(browser, s, state, nonce, challenge);
        Assert.Contains("not_invited", callback.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData("wrong-key")]
    [InlineData("wrong-audience")]
    [InlineData("tampered-state")]
    [InlineData("pkce-mismatch")]
    public async Task Invalid_tokens_and_tampered_flows_never_create_a_session(string attack)
    {
        using var s = await StartAsync();
        s.Idp.Claims["sub"] = "olive-subject-3"; s.Idp.Claims["email"] = "olive.oidc@a.example.test"; s.Idp.Claims["email_verified"] = true;
        using var browser = s.App.NewClient();
        var (state, nonce, challenge, _, _) = ParseAuthorize(await browser.GetAsync($"/Account/Challenge?scheme={Uri.EscapeDataString(s.Scheme)}"));
        var response = attack switch
        {
            "wrong-nonce" => await CallbackAsync(browser, s, state, "not-the-nonce-we-sent", challenge),
            "wrong-key" => await CallbackAsync(browser, s, state, nonce, challenge, i => i.SignWithWrongKey = true),
            "wrong-audience" => await CallbackAsync(browser, s, state, nonce, challenge, i => i.AudienceOverride = "another-application"),
            "tampered-state" => await CallbackAsync(browser, s, state + "x", nonce, challenge),
            _ => await CallbackAsync(browser, s, state, nonce, "a-different-challenge-value-entirely")
        };
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/SignIn?error=", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Contracts")).StatusCode);
        Assert.Contains("/SignIn", (await browser.GetAsync("/Contracts")).Headers.Location!.ToString());
    }

    [Fact]
    public async Task Only_registered_schemes_can_be_challenged()
    {
        using var s = await StartAsync();
        using var browser = s.App.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/Account/Challenge?scheme=ecp.session")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/Account/Challenge?scheme=oidc-unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/Account/Challenge")).StatusCode);
    }

    [Fact]
    public async Task Microsoft_entra_requires_the_configured_directory_and_signs_in_by_object_id()
    {
        const string directory = "11111111-2222-3333-4444-555555555555";
        using var s = await StartAsync(IdentityProviderKind.Entra, directory);
        s.Idp.Claims["oid"] = "entra-object-1"; s.Idp.Claims["sub"] = "pairwise-1"; s.Idp.Claims["tid"] = directory;
        s.Idp.Claims["preferred_username"] = "olive.oidc@a.example.test"; s.Idp.Claims["name"] = "Olive Oidc";
        using var browser = s.App.NewClient();
        var (state, nonce, challenge, _, location) = ParseAuthorize(await browser.GetAsync($"/Account/Challenge?scheme={Uri.EscapeDataString(s.Scheme)}"));
        Assert.StartsWith($"https://login.microsoftonline.com/{directory}/v2.0/", location.ToString());
        var ok = await CallbackAsync(browser, s, state, nonce, challenge);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.DoesNotContain("error", ok.Headers.Location!.OriginalString);
        Assert.Contains("Olive Oidc", await browser.GetStringAsync("/"));

        // A token from another directory is refused even though it is validly signed.
        s.Idp.Claims["tid"] = "99999999-2222-3333-4444-555555555555";
        using var second = s.App.NewClient();
        var (st2, no2, ch2, _, _) = ParseAuthorize(await second.GetAsync($"/Account/Challenge?scheme={Uri.EscapeDataString(s.Scheme)}"));
        var denied = await CallbackAsync(second, s, st2, no2, ch2);
        Assert.StartsWith("/SignIn?error=", denied.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Production_refuses_the_demonstration_identity_and_demo_seed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ecp-prod-" + Guid.NewGuid().ToString("N"));
        var settings = EcpFactory.DemoSqlite(dir);
        settings["Ecp:Mode"] = "Production";
        var ex = Assert.ThrowsAny<Exception>(() => { using var f = new EcpFactory(settings); using var c = f.CreateClient(); });
        var inner = ex is StartupRefusedException r ? r : ex.InnerException as StartupRefusedException ?? (ex.InnerException?.InnerException as StartupRefusedException);
        Assert.NotNull(inner);
        Assert.Contains(inner!.Violations, v => v.CheckId == "R-ID-01");
        Assert.Contains(inner.Violations, v => v.CheckId == "R-DEMO-01");
    }
}
