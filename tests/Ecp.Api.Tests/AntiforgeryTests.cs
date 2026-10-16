using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class AntiforgeryTests
{
    private static readonly string[] StateChangingMethods = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>Every page that has a state-changing handler is covered by antiforgery validation, and none opts out except the error page (which only re-renders).</summary>
    [Fact]
    public async Task Every_state_changing_page_handler_is_protected_by_antiforgery_validation()
    {
        using var app = DemoApp.Start();
        using var client = app.NewClient(); // start the host
        var provider = app.Factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>();
        var loader = app.Factory.Services.GetRequiredService<PageLoader>();
        var checkedPages = 0; var checkedHandlers = 0;
        foreach (var descriptor in provider.ActionDescriptors.Items.OfType<PageActionDescriptor>())
        {
            var compiled = await loader.LoadAsync(descriptor, EndpointMetadataCollection.Empty);
            var handlers = compiled.HandlerMethods.Where(h => h.HttpMethod is not null && StateChangingMethods.Contains(h.HttpMethod.ToUpperInvariant())).ToList();
            if (handlers.Count == 0) continue;
            checkedPages++; checkedHandlers += handlers.Count;
            var page = compiled.ViewEnginePath;
            var policies = compiled.FilterDescriptors.Select(f => f.Filter).Where(f => f is AutoValidateAntiforgeryTokenAttribute or ValidateAntiForgeryTokenAttribute).ToList();
            var ignored = compiled.FilterDescriptors.Select(f => f.Filter).Any(f => f is IgnoreAntiforgeryTokenAttribute);
            if (page == "/Error") { Assert.True(ignored, "The error page only re-renders and opts out explicitly."); continue; }
            Assert.False(ignored, $"{page} opts out of antiforgery validation");
            Assert.True(policies.Count > 0, $"{page} has state-changing handlers but no antiforgery policy filter. Filters: " + string.Join(", ", compiled.FilterDescriptors.Select(f => f.Filter.GetType().Name)));
        }
        Assert.True(checkedPages >= 8, $"expected the state-changing pages to be discovered (found {checkedPages})");
        Assert.True(checkedHandlers >= 15, $"expected the state-changing handlers to be discovered (found {checkedHandlers})");
    }

    [Theory]
    [InlineData("/SignOut")]
    [InlineData("/SignIn")]
    public async Task Anonymous_state_changing_endpoints_reject_requests_without_a_token(string url)
    {
        using var app = DemoApp.Start();
        using var client = app.NewClient();
        var response = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["pick"] = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_user_cannot_act_without_the_token_or_with_a_token_from_another_session()
    {
        using var app = DemoApp.Start();
        using var owner = await app.SignInAsync("catowner.packaging@a.example.test");
        // No token: refused before any handler runs.
        var noToken = await owner.PostAsync("/Contracts/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["Title"] = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        var signOutNoToken = await owner.PostAsync("/SignOut", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, signOutNoToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/Contracts")).StatusCode); // still signed in: nothing happened

        // A token minted for one browser session is useless in another.
        using var attacker = app.NewClient();
        var foreignToken = await DemoApp.TokenAsync(attacker, "/SignIn");
        var forged = await owner.PostAsync("/SignOut", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = foreignToken }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/Contracts")).StatusCode);

        // With its own token the same request works.
        var ok = await DemoApp.PostFormAsync(owner, "/SignOut", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await owner.GetAsync("/Contracts")).StatusCode); // signed out
    }

    [Fact]
    public async Task The_antiforgery_and_session_cookies_are_http_only_and_same_site()
    {
        using var app = DemoApp.Start();
        using var client = app.NewClient();
        var response = await client.GetAsync("/SignIn");
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ecp.csrf=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        using var signedIn = await app.SignInAsync("viewer.all@a.example.test");
        var home = await signedIn.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        // The session cookie is issued at sign-in; check it on a fresh sign-in response.
        using var raw = app.NewClient();
        var (tenant, user) = await app.FindUserAsync("viewer.all@a.example.test");
        var token = await DemoApp.TokenAsync(raw, "/SignIn");
        var post = await raw.PostAsync("/SignIn", new FormUrlEncodedContent(new Dictionary<string, string> { ["pick"] = $"{tenant}:{user}", ["__RequestVerificationToken"] = token }));
        var session = post.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ecp.session=", StringComparison.Ordinal));
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", session, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", session, StringComparison.OrdinalIgnoreCase); // a session cookie, not persistent
    }
}
