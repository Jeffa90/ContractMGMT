using System.Text.RegularExpressions;

namespace JbsContracts.Api.Infrastructure;

/// <summary>Windows logons in DOMAIN\user form, e.g. AMH\jsmith. Matching is case-insensitive.</summary>
public static partial class WindowsAccount
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,14}$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^[^\\/\[\]:;|=,+*?<>""@\s]{1,64}$")]
    private static partial Regex UserPattern();

    /// <summary>
    /// Accepts "AMH\jsmith", "amh\jsmith" or just "jsmith" (the default domain is put in front).
    /// Returns the logon with the domain in capitals and the user name as typed.
    /// </summary>
    public static bool TryNormalise(string? input, string? defaultDomain, out string account)
    {
        account = "";
        var text = (input ?? "").Trim();
        if (text.Length == 0) return false;
        string domain, user;
        var slash = text.IndexOf('\\');
        if (slash >= 0)
        {
            domain = text[..slash];
            user = text[(slash + 1)..];
        }
        else
        {
            if (string.IsNullOrWhiteSpace(defaultDomain)) return false;
            domain = defaultDomain;
            user = text;
        }
        if (!DomainPattern().IsMatch(domain) || !UserPattern().IsMatch(user)) return false;
        account = $"{domain.ToUpperInvariant()}\\{user}";
        return true;
    }

    public static string Key(string account) => account.Trim().ToUpperInvariant();
}
