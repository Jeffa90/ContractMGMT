using System.Text;

namespace Ecp.Packs;

/// <summary>A sanitised, human-readable summary of a package: nothing from the host, no key material, no signature bytes.</summary>
public static class PackInspector
{
    public static string Summarise(OpenedPackage package, TrustResult? trust = null, string? culture = "en")
    {
        var sb = new StringBuilder();
        if (package.Manifest is not { } m)
        {
            sb.AppendLine("The package could not be read.");
            foreach (var i in package.Issues.Where(i => i.IsError)) sb.AppendLine($"  [{i.Code}] {i.Message}");
            return sb.ToString();
        }
        sb.AppendLine($"Pack:        {m.DisplayName.Resolve(culture)} ({m.PackKey})");
        sb.AppendLine($"Version:     {m.Version}");
        sb.AppendLine($"Package id:  {m.PackageId}");
        sb.AppendLine($"Publisher:   {m.Publisher.Name} ({m.Publisher.Id})");
        sb.AppendLine($"Created:     {m.CreatedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"Platform:    {m.Compatibility.MinPlatformVersion} to {m.Compatibility.MaxPlatformVersion ?? "any later"}");
        sb.AppendLine($"Synthetic:   {(m.Synthetic ? "yes (example data only)" : "no")}");
        sb.AppendLine($"Signature:   {DescribeSignature(package, trust)}");
        sb.AppendLine($"SHA-256:     {package.Sha256}");
        sb.AppendLine($"Content digest: {package.ContentDigest}");
        if (m.Capabilities.Count > 0) sb.AppendLine($"Capabilities: {string.Join(", ", m.Capabilities)}");
        if (m.Dependencies.Count > 0) sb.AppendLine($"Dependencies: {string.Join(", ", m.Dependencies.Select(d => d.PackKey + (d.MinVersion is null ? "" : " >= " + d.MinVersion)))}");
        sb.AppendLine("Content:");
        foreach (var c in m.Content) sb.AppendLine($"  {c.Path}  kind={c.Kind} scope={c.Scope} schema={c.SchemaVersion} {(c.Required ? "required" : "optional")}  {c.Sha256[..12]}...");
        var warnings = package.Issues.Where(i => i.Severity != PackSeverity.Info).ToList();
        if (warnings.Count > 0)
        {
            sb.AppendLine("Findings:");
            foreach (var i in warnings) sb.AppendLine($"  [{i.Code}] {i.Severity}: {i.Message}");
        }
        return sb.ToString();
    }

    public static string DescribeSignature(OpenedPackage package, TrustResult? trust)
    {
        if (trust is null) return package.SignatureFile is null ? "unsigned" : "signed (not checked)";
        return trust.Status switch
        {
            TrustStatus.Trusted => $"valid, key {trust.KeyId} ({trust.Algorithm}), {trust.KeySource}",
            TrustStatus.UnsignedSyntheticAllowed => "UNSIGNED (accepted only as a synthetic package in a non-Production environment)",
            _ => $"not accepted: {trust.Status}" + (trust.KeyId is null ? "" : $" (key {trust.KeyId})")
        };
    }
}
