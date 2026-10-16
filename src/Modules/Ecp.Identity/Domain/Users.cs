using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Identity.Domain;

/// <summary>A person within one tenant. A person in two tenants has two independent users. Accounts link to identity providers by issuer and subject, never by email alone.</summary>
public sealed class UserProfile : ITenantOwned
{
    private UserProfile() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string DisplayName { get; private set; } = "";
    public string Email { get; private set; } = "";
    public string EmailNormalised { get; private set; } = "";
    public UserStatus Status { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public DateTime? ActivatedUtc { get; private set; }
    public DateTime? LastSignInUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static string NormaliseEmail(string? email)
    {
        var e = Guard.NotBlank(email, "Email", 254).ToLowerInvariant();
        if (!EmailPattern.IsMatch(e)) throw new DomainException("invalid_email", "Email must look like name@example.com.");
        return e;
    }

    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    public static UserProfile Invite(Guid tenantId, string displayName, string email, DateTime nowUtc)
    {
        var normalised = NormaliseEmail(email);
        return new UserProfile
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), DisplayName = Guard.NotBlank(displayName, "Display name", 200),
            Email = normalised, EmailNormalised = normalised, Status = UserStatus.Invited, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
    }

    /// <summary>First successful sign-in of an invited user.</summary>
    public void Activate(DateTime nowUtc)
    {
        if (Status == UserStatus.Disabled) throw new DomainException("user_disabled", "This account is disabled.");
        if (Status == UserStatus.Invited) { Status = UserStatus.Active; ActivatedUtc = nowUtc; }
    }

    public void RecordSignIn(DateTime nowUtc) => LastSignInUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    public void Disable() => Status = UserStatus.Disabled;
    public void Reactivate() => Status = ActivatedUtc.HasValue ? UserStatus.Active : UserStatus.Invited;
    public void Rename(string displayName) => DisplayName = Guard.NotBlank(displayName, "Display name", 200);
}

/// <summary>Links a user to an (issuer, subject) pair asserted by an identity provider.</summary>
public sealed class ExternalIdentity : ITenantOwned
{
    private ExternalIdentity() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string ProviderKey { get; private set; } = "";
    public string Issuer { get; private set; } = "";
    public string Subject { get; private set; } = "";
    public DateTime LinkedUtc { get; private set; }
    public DateTime LastSeenUtc { get; private set; }

    public static ExternalIdentity Link(Guid tenantId, Guid userId, string providerKey, string issuer, string subject, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), UserId = Guard.NotEmpty(userId, nameof(userId)),
        ProviderKey = Guard.NotBlank(providerKey, "Provider", 100), Issuer = Guard.NotBlank(issuer, "Issuer", 500),
        Subject = Guard.NotBlank(subject, "Subject", 500), LinkedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), LastSeenUtc = nowUtc
    };

    public void Seen(DateTime nowUtc) => LastSeenUtc = Guard.Utc(nowUtc, nameof(nowUtc));
}
