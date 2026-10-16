using System.Security.Claims;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;

namespace Ecp.Identity.Providers;

public enum IdentityProviderKind { Oidc, Entra, Demo }

/// <summary>
/// Non-secret settings of one identity provider plus a secret <em>reference</em> for the client secret (never the value).
/// Generic OpenID Connect and Microsoft Entra ID share this shape; Entra adds a directory (tenant) id and derives its authority.
/// </summary>
public sealed record IdentityProviderSettings(
    string Key, IdentityProviderKind Kind, string DisplayName, string? Authority, string? ClientId, string? ClientSecretRef,
    string? EntraTenantId, string Scopes = "openid profile email", string GroupsClaim = "groups", bool Enabled = true)
{
    /// <summary>The OpenID Connect authority. Entra's is derived from the directory id so a typo cannot point at another directory.</summary>
    public string? EffectiveAuthority => Kind == IdentityProviderKind.Entra && !string.IsNullOrWhiteSpace(EntraTenantId)
        ? $"https://login.microsoftonline.com/{EntraTenantId!.Trim()}/v2.0"
        : Authority?.Trim();
}

public sealed record ConfigurationIssue(string Code, string Message, bool Blocking = true);

/// <summary>Validates identity-provider configuration without contacting the provider (discovery is checked separately by readiness R-ID-02).</summary>
public static partial class IdentityProviderValidator
{
    public static IReadOnlyList<ConfigurationIssue> Validate(IdentityProviderSettings s, EcpMode mode, ISecretProvider? secrets = null)
    {
        var issues = new List<ConfigurationIssue>();
        void Add(string code, string message, bool blocking = true) => issues.Add(new ConfigurationIssue(code, message, blocking));

        if (string.IsNullOrWhiteSpace(s.Key) || !KeyPattern().IsMatch(s.Key)) Add("key", "The provider key must be 2 to 40 lower-case letters, digits or hyphens.");
        if (string.IsNullOrWhiteSpace(s.DisplayName)) Add("display_name", "A display name is required.");

        if (s.Kind == IdentityProviderKind.Demo)
        {
            if (mode == EcpMode.Production) Add("demo_in_production", "Demonstration identity is refused in Production.");
            return issues;
        }

        if (s.Kind == IdentityProviderKind.Entra)
        {
            if (string.IsNullOrWhiteSpace(s.EntraTenantId) || !Guid.TryParse(s.EntraTenantId, out var dir) || dir == Guid.Empty)
                Add("entra_tenant", "The Microsoft Entra directory (tenant) id must be a GUID. 'common', 'organizations' and 'consumers' are not accepted because they allow sign-in from other directories.");
            if (!string.IsNullOrWhiteSpace(s.Authority) && !string.Equals(s.Authority.TrimEnd('/'), s.EffectiveAuthority?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                Add("entra_authority", "For Microsoft Entra ID the authority is derived from the directory id; remove the custom authority.");
        }
        else
        {
            var authority = s.Authority?.Trim();
            if (string.IsNullOrWhiteSpace(authority)) Add("authority", "The authority (issuer URL) is required.");
            else if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)) Add("authority", "The authority must be an absolute URL.");
            else
            {
                var loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && mode != EcpMode.Production;
                if (uri.Scheme != Uri.UriSchemeHttps && !loopbackHttp) Add("authority_https", "The authority must use HTTPS (plain HTTP is accepted only for loopback outside Production).");
                if (!string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) Add("authority_format", "The authority must not contain a fragment or credentials.");
            }
        }

        if (string.IsNullOrWhiteSpace(s.ClientId)) Add("client_id", "The client id is required.");

        // The client secret is a reference. A literal secret in configuration is refused (SEC-030).
        if (!string.IsNullOrWhiteSpace(s.ClientSecretRef))
        {
            if (!SecretReference.TryParse(s.ClientSecretRef, out var reference))
                Add("client_secret_ref", "The client secret must be a secret reference (env:NAME, file:/path or keyvault:name), never the secret itself.");
            else if (secrets is not null && secrets.Resolve(reference) is null)
                Add("client_secret_unresolved", $"The secret reference {reference.Scheme}:… does not resolve in this environment.", blocking: mode == EcpMode.Production);
        }
        else Add("client_secret_missing", "No client secret reference is set; only a public client using PKCE without a secret will work.", blocking: false);

        var scopes = (s.Scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("openid", StringComparer.Ordinal)) Add("scope_openid", "The scopes must include 'openid'.");
        if (string.IsNullOrWhiteSpace(s.GroupsClaim)) Add("groups_claim", "A groups claim name is required for identity-group mapping (use 'groups').");
        return issues;
    }

    public static bool IsValid(IReadOnlyList<ConfigurationIssue> issues) => issues.All(i => !i.Blocking);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{1,39}$", RegexOptions.NonBacktracking)]
    private static partial Regex KeyPattern();
}

/// <summary>What the platform learns from a verified sign-in, independent of the provider type.</summary>
public sealed record ExternalLogin(
    string ProviderKey, string Issuer, string Subject, string? Email, bool EmailTrusted, string? DisplayName, IReadOnlyList<string> Groups,
    bool GroupsOverage = false);

/// <summary>Maps validated ID-token claims to an <see cref="ExternalLogin"/>. Pure function: tested with synthetic claim sets.</summary>
public static class ClaimsMapper
{
    public static ExternalLogin? Map(IdentityProviderSettings settings, ClaimsPrincipal principal)
    {
        string? Find(params string[] types) => types.Select(t => principal.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // The OpenID Connect handler has already validated signature, issuer, audience, nonce and lifetime against the provider's
        // discovery metadata; the issuer recorded here is the exact `iss` value, which together with the subject identifies the account.
        var issuer = Find("iss");
        if (string.IsNullOrWhiteSpace(issuer)) return null;

        string? subject;
        bool emailTrusted;
        if (settings.Kind == IdentityProviderKind.Entra)
        {
            // Entra: the directory in the token must be the configured one, and the object id is the stable account key.
            var tid = Find("tid", "http://schemas.microsoft.com/identity/claims/tenantid");
            if (!string.Equals(tid, settings.EntraTenantId?.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            subject = Find("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier") ?? Find("sub");
            emailTrusted = true; // the address is administered by the configured directory
        }
        else
        {
            subject = Find("sub", ClaimTypes.NameIdentifier);
            var verified = Find("email_verified");
            emailTrusted = string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase);
        }
        if (string.IsNullOrWhiteSpace(subject)) return null;

        var email = Find("email", ClaimTypes.Email, "preferred_username", "upn");
        var name = Find("name", ClaimTypes.Name) ?? email;
        var groups = principal.FindAll(settings.GroupsClaim).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList();
        // Entra signals that the user is in too many groups for the token with _claim_names; the groups are then not resolved here.
        var overage = settings.Kind == IdentityProviderKind.Entra && principal.HasClaim(c => c.Type == "_claim_names" && c.Value.Contains("groups", StringComparison.Ordinal));
        return new ExternalLogin(settings.Key, issuer, subject, email, emailTrusted && email is not null, name, groups, overage);
    }
}
