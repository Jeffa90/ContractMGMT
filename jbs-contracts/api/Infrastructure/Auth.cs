using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.Options;

namespace JbsContracts.Api.Infrastructure;

public static class Auth
{
    public const string HeaderScheme = "Header";
    public const string HeaderName = "X-Signed-In-As";

    /// <summary>
    /// On the servers, IIS does Windows sign-in (Anonymous is off for the site) and hands the account to the app.
    /// "Header" mode, for local development and tests only, takes the account from a request header; AppSettings refuses it anywhere else.
    /// </summary>
    public static IServiceCollection AddAppAuthentication(this IServiceCollection services, AppSettings settings)
    {
        if (string.Equals(settings.AuthMode, HeaderScheme, StringComparison.OrdinalIgnoreCase))
            services.AddAuthentication(HeaderScheme).AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderScheme, _ => { });
        else
            services.AddAuthentication(IISDefaults.AuthenticationScheme);
        services.AddAuthorization();
        return services;
    }

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var account = Request.Headers[HeaderName].ToString();
            if (string.IsNullOrWhiteSpace(account)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, account)], HeaderScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), HeaderScheme)));
        }
    }
}
