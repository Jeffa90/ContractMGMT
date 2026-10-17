using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.Configuration.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Configuration;

public sealed record PackCategory(string Code, string Name, string? ParentCode = null);
public sealed record PackContractType(string Code, string Name);
public sealed record PackClassification(IReadOnlyList<string> Levels, string DefaultLevel, IReadOnlyList<string>? PrivilegedLevels = null);

public sealed record PackDefaults(string? Culture, string? TimeZoneId, string? CurrencyCode, string? ReferencePrefix, PackClassification Classification,
    IReadOnlyList<PackContractType> ContractTypes, IReadOnlyList<PackCategory> Categories);

public sealed record SolutionPackInfo(string PackId, string Name, string Version, string Description, bool Synthetic, string Publisher,
    IReadOnlyList<string> Features, string OwnershipModel, string ApprovalModel, PackDefaults Defaults);

/// <summary>
/// The packs a first-run setup can offer: the shipped, read-only packages that are usable in this environment (compatible, and trusted or explicitly allowed as unsigned synthetic packs in Demo and Test).
/// It is synchronous because setup runs before any tenant has installed anything. Anything tenant-aware goes through <see cref="IPackLoader"/>.
/// </summary>
public interface ISolutionPackCatalogue
{
    IReadOnlyList<SolutionPackInfo> List();
    SolutionPackInfo? Find(string packId);
    /// <summary>The same packs with their version, publisher, capabilities, signature and trust status, for display.</summary>
    IReadOnlyList<PackCatalogueEntry> ListEntries();
}

/// <summary>Tenant-level record of the applied solution pack (version-controlled like any configuration). The pack must exist and be usable here.</summary>
internal sealed class SolutionPackKind(IPackLoader packs) : IConfigurationKind
{
    public const string KindName = "SolutionPack";
    public string Kind => KindName;

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        var messages = new List<ValidationMessage>();
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var id = doc.RootElement.TryGetProperty("packId", out var p) ? p.GetString() : null;
            if (id is null || await packs.FindAsync(id, null, usableOnly: true, ct) is null)
                messages.Add(new(ValidationSeverity.Error, "$.packId", "unknown_pack", $"Solution pack '{id}' is not available. It may not be installed, may not be trusted in this environment, or may be retired."));
        }
        catch (JsonException) { messages.Add(new(ValidationSeverity.Error, "$", "invalid_json", "Not valid JSON.")); }
        return new ValidationReport(messages);
    }

    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct) => Task.CompletedTask;
}

public sealed record Branding(string ProductName, string AccentColour, string FooterText, IReadOnlyDictionary<string, string> Terminology)
{
    public static readonly Branding Default = new("Enterprise Contract Platform", "#1f3a8a", "", new Dictionary<string, string>());

    private static readonly Regex Colour = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex TermKey = new("^[a-z][a-zA-Z0-9]{1,30}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    public static Branding Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Str(string name, string fallback) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
            var terms = new Dictionary<string, string>();
            if (r.TryGetProperty("terminology", out var t) && t.ValueKind == JsonValueKind.Object)
                foreach (var kv in t.EnumerateObject()) terms[kv.Name] = kv.Value.GetString() ?? "";
            return new Branding(Str("productName", Default.ProductName), Str("accentColour", Default.AccentColour), Str("footerText", ""), terms);
        }
        catch (JsonException) { throw new DomainException("invalid_json", "The branding document is not valid JSON."); }
    }

    public IReadOnlyList<ValidationMessage> Check()
    {
        var m = new List<ValidationMessage>();
        if (string.IsNullOrWhiteSpace(ProductName) || ProductName.Length > 60) m.Add(new(ValidationSeverity.Error, "$.productName", "product_name", "The product name must be 1 to 60 characters."));
        if (!Colour.IsMatch(AccentColour)) m.Add(new(ValidationSeverity.Error, "$.accentColour", "accent_colour", "The accent colour must be a six-digit hex colour such as #1f3a8a."));
        else if (Contrast(AccentColour, "#ffffff") < 4.5) m.Add(new(ValidationSeverity.Error, "$.accentColour", "accent_contrast", "The accent colour needs a contrast ratio of at least 4.5:1 against white text (WCAG 2.2 AA)."));
        if (FooterText.Length > 200) m.Add(new(ValidationSeverity.Error, "$.footerText", "footer_text", "The footer text must be at most 200 characters."));
        foreach (var (key, value) in Terminology)
        {
            if (!TermKey.IsMatch(key)) m.Add(new(ValidationSeverity.Error, $"$.terminology.{key}", "term_key", "Terminology keys are camel-case words."));
            if (string.IsNullOrWhiteSpace(value) || value.Length > 60) m.Add(new(ValidationSeverity.Error, $"$.terminology.{key}", "term_value", "A term must be 1 to 60 characters."));
        }
        return m;
    }

    public string ToJson() => JsonSerializer.Serialize(new { productName = ProductName, accentColour = AccentColour, footerText = FooterText, terminology = Terminology });

    /// <summary>WCAG relative-luminance contrast ratio between two hex colours.</summary>
    public static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            double Channel(int v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            var n = Convert.ToInt32(hex.TrimStart('#'), 16);
            return 0.2126 * Channel((n >> 16) & 255) + 0.7152 * Channel((n >> 8) & 255) + 0.0722 * Channel(n & 255);
        }
        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}

/// <summary>Tenant-level branding and terminology: closed vocabulary, validated, versioned like any configuration.</summary>
internal sealed class BrandingKind : IConfigurationKind
{
    public const string KindName = "Branding";
    public string Kind => KindName;

    public Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        try { return Task.FromResult(new ValidationReport(Branding.Parse(contentJson).Check().ToList())); }
        catch (DomainException ex) { return Task.FromResult(new ValidationReport([new ValidationMessage(ValidationSeverity.Error, "$", ex.Code, ex.Message)])); }
    }

    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct) => Task.CompletedTask;
}

public interface IBrandingProvider
{
    Task<Branding> GetAsync(CancellationToken ct = default);
}

internal sealed class ConfiguredBrandingProvider(ConfigurationDbContext db, ITenantContext tenant) : IBrandingProvider
{
    private Branding? _cached; private Guid? _forTenant;

    public async Task<Branding> GetAsync(CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } t) return Branding.Default;
        if (_cached is not null && _forTenant == t) return _cached;
        var json = await db.Releases.AsNoTracking().Where(r => r.OrganisationId == Guid.Empty && r.Kind == BrandingKind.KindName).OrderByDescending(r => r.Number)
            .Select(r => r.ContentJson).FirstOrDefaultAsync(ct);
        _forTenant = t;
        return _cached = json is null ? Branding.Default : Branding.Parse(json);
    }
}
