using Ecp.Identity.Providers;
using Ecp.SharedKernel;

namespace Ecp.Identity.Domain;

/// <summary>Stored identity-provider settings for a tenant. Holds a secret <em>reference</em> only, never a secret value.</summary>
public sealed class IdentityProviderRegistration : ITenantOwned
{
    private IdentityProviderRegistration() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Key { get; private set; } = "";
    public IdentityProviderKind Kind { get; private set; }
    public string DisplayName { get; private set; } = "";
    public string? Authority { get; private set; }
    public string? ClientId { get; private set; }
    public string? ClientSecretRef { get; private set; }
    public string? EntraTenantId { get; private set; }
    public string Scopes { get; private set; } = "openid profile email";
    public string GroupsClaim { get; private set; } = "groups";
    public bool IsEnabled { get; private set; }
    public DateTime? TestSignInUtc { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IdentityProviderSettings ToSettings() => new(Key, Kind, DisplayName, Authority, ClientId, ClientSecretRef, EntraTenantId, Scopes, GroupsClaim, IsEnabled);

    public static IdentityProviderRegistration Create(Guid tenantId, IdentityProviderSettings s, DateTime nowUtc)
    {
        var r = new IdentityProviderRegistration { Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)) };
        r.Apply(s, nowUtc);
        return r;
    }

    public void Apply(IdentityProviderSettings s, DateTime nowUtc)
    {
        Key = Guard.NotBlank(s.Key, "Provider key", 40);
        Kind = s.Kind;
        DisplayName = Guard.NotBlank(s.DisplayName, "Display name", 200);
        Authority = Guard.OptionalBounded(s.Authority, "Authority", 500);
        ClientId = Guard.OptionalBounded(s.ClientId, "Client id", 200);
        ClientSecretRef = Guard.OptionalBounded(s.ClientSecretRef, "Client secret reference", 500);
        EntraTenantId = Guard.OptionalBounded(s.EntraTenantId, "Directory id", 100);
        Scopes = Guard.NotBlank(s.Scopes, "Scopes", 500);
        GroupsClaim = Guard.NotBlank(s.GroupsClaim, "Groups claim", 100);
        IsEnabled = s.Enabled;
        UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        TestSignInUtc = null; // changed settings must be tested again
    }

    public void RecordTestSignIn(DateTime nowUtc) => TestSignInUtc = Guard.Utc(nowUtc, nameof(nowUtc));
}
