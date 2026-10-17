using System.Collections;
using System.Globalization;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.Extensions.Localization;

namespace Ecp.Host.Terminology;

/// <summary>
/// The terminology in force for the current request. It is loaded once, before the page runs, from the one resolver (organisation, then solution pack, then neutral wording); pages never read pack files and
/// never query for a label. A page that is about one organisation's record (a contract, a supplier) calls <see cref="UseOrganisationAsync"/> so that record is described in its own organisation's words.
/// Terminology is presentation only: nothing here decides what a person may see or do.
/// </summary>
public sealed class TermContext(ITerminologyResolver resolver, ITenantContext tenant, ILogger<TermContext> logger)
{
    private bool _loaded;
    public TermSet Current { get; private set; } = TermSet.Neutral();
    public Guid? OrganisationId { get; private set; }

    /// <summary>Loads the terminology for the signed-in person's home organisation: the organisation of their first active organisation-level assignment, otherwise the tenant's first organisation (see <c>docs/TERMINOLOGY.md</c>).</summary>
    public async Task InitialiseAsync(bool signedIn, CancellationToken ct)
    {
        if (_loaded) return;
        _loaded = true;
        if (tenant.TenantId is null) { Current = TermSet.Neutral(CultureInfo.CurrentUICulture.Name); return; }
        Guid? home = null;
        try { if (signedIn) home = await resolver.HomeOrganisationAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "The home organisation for terminology could not be determined; the installation wording is used."); }
        await LoadAsync(home, ct);
    }

    /// <summary>Describes the rest of the request in the words of one organisation. Safe to call more than once.</summary>
    public async Task UseOrganisationAsync(Guid organisationId, CancellationToken ct = default)
    {
        _loaded = true;
        if (OrganisationId == organisationId) return;
        await LoadAsync(organisationId, ct);
    }

    private async Task LoadAsync(Guid? organisationId, CancellationToken ct)
    {
        OrganisationId = organisationId;
        Current = await resolver.GetAsync(organisationId, CultureInfo.CurrentUICulture.Name, ct);   // never throws: falls back to the neutral wording
    }

    /// <summary>The display name of an approver group for ordinary pages: the name the organisation or its pack gave it, otherwise the key.</summary>
    public string Group(string key) => Current.Group(key);

    /// <summary>For administrator pages: the name with the stable key beside it, so nobody has to guess what the key is. An unnamed group is just its key.</summary>
    public string GroupLabel(string key) => Current.HasGroupName(key) ? $"{Current.Group(key)} ({key})" : key;

    /// <summary>Whether a role is offered in assignment pickers (presentation only; see docs/TERMINOLOGY.md).</summary>
    public bool IsRoleOffered(string roleCode) => Current.IsRoleOffered(roleCode);

    public string Singular(string key) => Current.Singular(key);
    public string Plural(string key) => Current.Plural(key);
    public string Expand(string? text) => text is null ? "" : Current.Expand(text);

    /// <summary>
    /// The label for a role code (the code itself is a stable identifier and is shown separately where an administrator needs it). A value such as <c>Viewer (Site)</c>, a code followed by a qualifier, keeps its qualifier.
    /// An unknown code is returned as written, as a diagnostic.
    /// </summary>
    public string Role(string roleCode)
    {
        var cut = roleCode.IndexOf(" (", StringComparison.Ordinal);
        var code = cut > 0 ? roleCode[..cut] : roleCode;
        var key = TermCatalogue.KeyForRole(code);
        return key.Length == 0 ? roleCode : Current.Singular(key) + (cut > 0 ? roleCode[cut..] : "");
    }

    /// <summary>The machine identifier inside a role value (drops a qualifier such as ' (Site)').</summary>
    public static string RoleCode(string roleValue) => roleValue.IndexOf(" (", StringComparison.Ordinal) is var cut and > 0 ? roleValue[..cut] : roleValue;
}

/// <summary>
/// The page text localiser. Page strings are English keys that may carry <c>[[term]]</c> tokens; the resource file (if it has the key) is applied first, then the tokens are replaced with the organisation's
/// words, and only then are any {0} arguments formatted, so a value that came from a person can never be read as a token.
/// </summary>
public sealed class TermLocalizer(IStringLocalizerFactory factory, TermContext terms) : IStringLocalizer<SharedResource>
{
    private readonly IStringLocalizer _inner = factory.Create(typeof(SharedResource));

    public LocalizedString this[string name]
    {
        get
        {
            var found = _inner[name];
            return new LocalizedString(name, terms.Current.Expand(found.Value), found.ResourceNotFound, found.SearchedLocation);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var found = _inner[name];
            var template = terms.Current.Expand(found.Value);
            var text = arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, arguments);
            return new LocalizedString(name, text, found.ResourceNotFound, found.SearchedLocation);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => _inner.GetAllStrings(includeParentCultures);
}

/// <summary>Loads the request's terminology before any page runs. Static files, health and API calls do not need it.</summary>
public sealed class TermMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TermContext terms)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/css"))
            await terms.InitialiseAsync(context.User.Identity?.IsAuthenticated == true, context.RequestAborted);
        await next(context);
    }
}
