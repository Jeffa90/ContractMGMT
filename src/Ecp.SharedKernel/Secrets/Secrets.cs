namespace Ecp.SharedKernel.Secrets;

/// <summary>
/// A reference to a secret (never the value). Schemes: <c>env:NAME</c>, <c>file:/path</c> (Docker/Kubernetes secrets),
/// <c>keyvault:name</c> and <c>dpapi:path</c> (resolved by host-specific providers, not implemented in M1).
/// Configuration stores references only; ordinary configuration values never hold secrets (decision A9).
/// </summary>
public readonly record struct SecretReference(string Scheme, string Target)
{
    public static bool TryParse(string? text, out SecretReference reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var idx = text.IndexOf(':');
        if (idx <= 0 || idx == text.Length - 1) return false;
        var scheme = text[..idx].Trim().ToLowerInvariant();
        if (scheme is not ("env" or "file" or "keyvault" or "dpapi")) return false;
        reference = new SecretReference(scheme, text[(idx + 1)..].Trim());
        return reference.Target.Length > 0;
    }

    public override string ToString() => $"{Scheme}:{Target}";
}

public interface ISecretProvider
{
    /// <summary>Resolves a reference. Returns null when the secret is not available. Never logs the value.</summary>
    string? Resolve(SecretReference reference);
}

public sealed class EnvironmentAndFileSecretProvider : ISecretProvider
{
    public string? Resolve(SecretReference reference) => reference.Scheme switch
    {
        "env" => Environment.GetEnvironmentVariable(reference.Target),
        "file" => File.Exists(reference.Target) ? File.ReadAllText(reference.Target).Trim() : null,
        _ => null // keyvault and dpapi providers are host-specific and arrive with the deployment hardening milestone
    };
}

public static class Redactor
{
    private static readonly string[] SensitiveKeys = ["password", "pwd", "secret", "token", "apikey", "api_key", "accountkey", "sharedaccesssignature"];

    /// <summary>Masks the values of sensitive keys in a connection string or key=value text.</summary>
    public static string MaskConnectionString(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var parts = text.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            var key = parts[i][..eq].Trim().ToLowerInvariant().Replace(" ", "");
            if (SensitiveKeys.Any(key.Contains)) parts[i] = parts[i][..(eq + 1)] + "***";
        }
        return string.Join(';', parts);
    }
}
