using System.Collections.Concurrent;
using Ecp.Identity;
using Ecp.Identity.Providers;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Ecp.Host.Security;

/// <summary>Test seam: lets a test supply a mock identity provider's back-channel. Not registered in the product.</summary>
public interface IOidcOptionsCustomizer
{
    void Customize(string scheme, OpenIdConnectOptions options);
}

public sealed record RegisteredOidcProvider(string Scheme, Guid TenantId, IdentityProviderSettings Settings);

/// <summary>
/// Registers one OpenID Connect scheme per enabled identity provider of each tenant, from the stored (non-secret) settings. Schemes are
/// rebuilt when settings change, so enabling a provider needs no restart and the host never reads provider secrets from configuration files.
/// </summary>
public sealed class OidcSchemeManager(IAuthenticationSchemeProvider schemes, IOptionsMonitorCache<OpenIdConnectOptions> optionsCache, ILogger<OidcSchemeManager> logger)
{
    private readonly ConcurrentDictionary<string, RegisteredOidcProvider> _providers = new(StringComparer.Ordinal);

    public static string SchemeName(Guid tenantId, string key) => $"{EcpSchemes.OidcPrefix}{tenantId:N}-{key}";
    public static string CallbackPath(string scheme) => $"/auth/callback/{scheme}";

    public RegisteredOidcProvider? Get(string scheme) => _providers.TryGetValue(scheme, out var p) ? p : null;
    public IReadOnlyList<RegisteredOidcProvider> ForTenant(Guid tenantId) => _providers.Values.Where(p => p.TenantId == tenantId).OrderBy(p => p.Settings.DisplayName).ToList();

    public void Register(Guid tenantId, IdentityProviderSettings settings)
    {
        var scheme = SchemeName(tenantId, settings.Key);
        Remove(tenantId, settings.Key);
        if (!settings.Enabled || settings.Kind == IdentityProviderKind.Demo) return;
        _providers[scheme] = new RegisteredOidcProvider(scheme, tenantId, settings);
        schemes.AddScheme(new AuthenticationScheme(scheme, settings.DisplayName, typeof(OpenIdConnectHandler)));
        logger.LogInformation("Registered identity provider {Provider} for tenant {Tenant}", settings.Key, tenantId);
    }

    public void Remove(Guid tenantId, string key)
    {
        var scheme = SchemeName(tenantId, key);
        if (_providers.TryRemove(scheme, out _))
        {
            schemes.RemoveScheme(scheme);
            optionsCache.TryRemove(scheme);
        }
    }

    /// <summary>Loads every tenant's providers (start-up and after a settings change).</summary>
    public async Task ReloadAsync(IServiceProvider root, CancellationToken ct = default)
    {
        await using var scope = root.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        foreach (var tenant in await directory.ListAsync(ct))
        {
            using var _ = tenantContext.Use(tenant.Id);
            var registry = scope.ServiceProvider.GetRequiredService<IProviderRegistry>();
            foreach (var settings in await registry.ListAsync(ct)) Register(tenant.Id, settings);
        }
    }
}

/// <summary>Builds the OpenID Connect options for a registered scheme: authorization-code flow with PKCE, secrets resolved from references.</summary>
internal sealed class OidcOptionsConfigurator(OidcSchemeManager manager, ISecretProvider secrets, IEnumerable<IOidcOptionsCustomizer> customizers,
    OidcEvents events, EcpHostOptions host) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name is null || manager.Get(name) is not { } provider) return;
        var s = provider.Settings;
        options.Authority = s.EffectiveAuthority;
        options.ClientId = s.ClientId;
        if (SecretReference.TryParse(s.ClientSecretRef, out var reference)) options.ClientSecret = secrets.Resolve(reference); // never logged
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;                 // keep raw claim names (sub, iss, oid, groups)
        options.CallbackPath = OidcSchemeManager.CallbackPath(name);
        options.SignedOutCallbackPath = OidcSchemeManager.CallbackPath(name) + "-signout";
        options.SignInScheme = EcpSchemes.Session;
        options.RequireHttpsMetadata = host.Mode == EcpMode.Production || (s.EffectiveAuthority?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ?? true);
        options.Scope.Clear();
        foreach (var scope in s.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) options.Scope.Add(scope);
        options.Events = events.Create(name);
        foreach (var c in customizers) c.Customize(name, options);
    }

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);
}

/// <summary>Sign-in events: validate the provider's token (done by the handler), map claims, resolve the account (invite-only) and issue the application session.</summary>
public sealed class OidcEvents(OidcSchemeManager manager, IServiceScopeFactory scopes, IClock clock)
{
    public OpenIdConnectEvents Create(string scheme) => new()
    {
        OnRedirectToIdentityProvider = context =>
        {
            if (context.Properties.Items.TryGetValue("ecp:reauth", out var v) && v == "1")
            {
                context.ProtocolMessage.Prompt = "login";
                context.ProtocolMessage.MaxAge = "0";
            }
            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            var provider = manager.Get(scheme);
            var login = provider is null || context.Principal is null ? null : ClaimsMapper.Map(provider.Settings, context.Principal);
            if (provider is null || login is null) { context.Fail("The identity provider's token could not be mapped to an account."); return; }

            await using var scope = scopes.CreateAsyncScope();
            var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
            using var _ = tenant.Use(provider.TenantId);
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var resolution = await users.ResolveLoginAsync(login, context.HttpContext.RequestAborted);
            if (!resolution.Succeeded) { context.Fail(resolution.DenialCode ?? "denied"); return; }

            var user = (await users.GetAsync(resolution.UserId!.Value, context.HttpContext.RequestAborted))!;
            var principal = SessionPrincipalFactory.Create(provider.TenantId, user.Id, user.DisplayName, provider.Settings.Key, clock.UtcNow);
            context.Principal = principal;
            context.Properties!.IsPersistent = false;

            if (context.Properties.Items.TryGetValue("ecp:test-provider", out var testKey) && testKey == provider.Settings.Key)
                await scope.ServiceProvider.GetRequiredService<IProviderRegistry>().RecordTestSignInAsync(testKey, context.HttpContext.RequestAborted);
        },
        OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Ecp.SignIn")
                .LogWarning("Sign-in through {Scheme} failed: {Failure}", scheme, context.Failure?.GetType().Name + ": " + context.Failure?.Message);
            var code = context.Failure?.Message is { Length: > 0 and < 40 } m && m.All(c => char.IsLetterOrDigit(c) || c == '_') ? m : "sign_in_failed";
            context.Response.Redirect("/SignIn?error=" + Uri.EscapeDataString(code));
            return Task.CompletedTask;
        },
    };
}

/// <summary>Rebuilds a tenant's sign-in scheme when its identity-provider settings change.</summary>
internal sealed class OidcSchemeListener(OidcSchemeManager manager) : IIdentityProviderListener
{
    public Task ChangedAsync(Guid tenantId, IdentityProviderSettings settings, CancellationToken ct)
    {
        manager.Register(tenantId, settings);
        return Task.CompletedTask;
    }
}
