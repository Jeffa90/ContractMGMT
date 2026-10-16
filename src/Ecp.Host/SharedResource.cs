namespace Ecp.Host;

/// <summary>
/// Marker type for the shared UI resources (<c>Resources/SharedResource*.resx</c>). The neutral resource holds the Australian English
/// text (the reference locale); a culture file overrides only the keys that differ. Adding a locale is adding a resx file and
/// listing the culture in <c>Ecp:Localisation:SupportedCultures</c>.
/// </summary>
public sealed class SharedResource;
