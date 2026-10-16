using Ecp.Configuration;
using Ecp.SharedKernel;

namespace Ecp.Contracts.Register;

public sealed record ViewStudioState(Guid OrganisationId, ViewSet Published, int? Release, DraftDto? Draft, ViewSet? DraftSet, IReadOnlyList<ValidationMessage> Messages, IReadOnlyList<ReleaseDto>? Releases, string? ReleasesDenied)
{
    public ViewSet Working => DraftSet ?? Published;
}

/// <summary>
/// The Studio's editing of the views an organisation shares. Edits change the open draft of the <c>ContractListView</c> configuration, which is validated, published, versioned, rolled back and audited by the
/// configuration service like every other configuration, so a shared view can never name more than the person who publishes it may see.
/// </summary>
public interface IContractViewStudio
{
    Task<ViewStudioState> LoadAsync(Guid organisationId, bool includeHistory = false, CancellationToken ct = default);
    Task StartDraftAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Adds the view, or replaces the draft's view with the same key.</summary>
    Task SaveViewAsync(Guid organisationId, ViewDefinition view, CancellationToken ct = default);
    Task RemoveViewAsync(Guid organisationId, string key, CancellationToken ct = default);
    Task<ValidationReport> ValidateAsync(Guid organisationId, CancellationToken ct = default);
    Task<ReleaseDto> PublishAsync(Guid organisationId, string note, CancellationToken ct = default);
    Task RollbackAsync(Guid organisationId, int toRelease, string reason, CancellationToken ct = default);
    Task DiscardDraftAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Runs a definition as the signed-in administrator would see it (their own access), so a view can be tried before it is published.</summary>
    Task<RegisterPage> PreviewAsync(Guid organisationId, ViewDefinition view, CancellationToken ct = default);
    /// <summary>The fields a shared view can use in this organisation (all of them, as definitions).</summary>
    Task<RegisterCatalogue> CatalogueAsync(Guid organisationId, CancellationToken ct = default);
}

internal sealed class ContractViewStudio(IConfigurationService configuration, IContractViews views, IContractRegister register) : IContractViewStudio
{
    private const string Kind = ContractListViewKind.KindName;

    private static ViewSet ParseOrEmpty(string? json) => json is null ? new ViewSet(1, []) : ViewParser.Parse(json).Set ?? new ViewSet(1, []);

    public async Task<ViewStudioState> LoadAsync(Guid organisationId, bool includeHistory = false, CancellationToken ct = default)
    {
        var release = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        DraftDto? draft = null;
        try { draft = await configuration.FindOpenDraftAsync(Kind, organisationId, ct); } catch (ForbiddenException) { }   // a history-only reader has no draft to see
        ViewSet? draftSet = null; var messages = new List<ValidationMessage>();
        if (draft is not null)
        {
            var (set, parse) = ViewParser.Parse(draft.ContentJson);
            draftSet = set; messages.AddRange(draft.Validation?.Messages ?? parse);
        }
        IReadOnlyList<ReleaseDto>? releases = null; string? denied = null;
        if (includeHistory)
        {
            try { releases = await configuration.ReleasesAsync(Kind, organisationId, ct); }
            catch (ForbiddenException) { denied = "You cannot read the version history."; }
        }
        return new ViewStudioState(organisationId, ParseOrEmpty(release?.ContentJson), release?.Number, draft, draftSet, messages, releases, denied);
    }

    private async Task<(DraftDto Draft, ViewSet Set)> OpenAsync(Guid organisationId, CancellationToken ct)
    {
        var draft = await configuration.FindOpenDraftAsync(Kind, organisationId, ct) ?? throw new DomainException("no_draft", "Start a draft before making changes.");
        var (set, messages) = ViewParser.Parse(draft.ContentJson);
        return (draft, set ?? throw new DomainException("draft_unreadable", "The draft could not be read: " + messages.FirstOrDefault()?.Message));
    }

    public async Task StartDraftAsync(Guid organisationId, CancellationToken ct = default)
    {
        var latest = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        await configuration.GetOrCreateDraftAsync(Kind, organisationId, latest?.ContentJson ?? ViewParser.ToJson(new ViewSet(1, [])), ct);
    }

    public async Task SaveViewAsync(Guid organisationId, ViewDefinition view, CancellationToken ct = default)
    {
        var (draft, set) = await OpenAsync(organisationId, ct);
        var list = set.Views.ToList();
        var i = list.FindIndex(v => string.Equals(v.Key, view.Key, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) list[i] = view; else list.Add(view);
        await configuration.SaveDraftAsync(draft.Id, ViewParser.ToJson(new ViewSet(1, list)), ct);
    }

    public async Task RemoveViewAsync(Guid organisationId, string key, CancellationToken ct = default)
    {
        var (draft, set) = await OpenAsync(organisationId, ct);
        await configuration.SaveDraftAsync(draft.Id, ViewParser.ToJson(new ViewSet(1, set.Views.Where(v => !string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)).ToList())), ct);
    }

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, CancellationToken ct = default)
    {
        var (draft, _) = await OpenAsync(organisationId, ct);
        return (await configuration.ValidateAsync(draft.Id, ct)).Validation ?? ValidationReport.Valid();
    }

    public async Task<ReleaseDto> PublishAsync(Guid organisationId, string note, CancellationToken ct = default)
    {
        var (draft, _) = await OpenAsync(organisationId, ct);
        await configuration.ValidateAsync(draft.Id, ct);
        return await configuration.PublishAsync(draft.Id, note, ct);
    }

    public Task RollbackAsync(Guid organisationId, int toRelease, string reason, CancellationToken ct = default) => configuration.RollbackAsync(Kind, organisationId, toRelease, reason, ct);

    public async Task DiscardDraftAsync(Guid organisationId, CancellationToken ct = default)
    {
        var (draft, _) = await OpenAsync(organisationId, ct);
        await configuration.DiscardDraftAsync(draft.Id, ct);
    }

    public Task<RegisterCatalogue> CatalogueAsync(Guid organisationId, CancellationToken ct = default) => register.DefinitionCatalogueAsync(organisationId, ct);

    public async Task<RegisterPage> PreviewAsync(Guid organisationId, ViewDefinition view, CancellationToken ct = default)
    {
        var issues = await views.ValidateAsync(view, organisationId, ct);
        if (issues.Count > 0) throw new RegisterQueryException(issues);
        return await register.SearchAsync(views.ToQuery(view, organisationId, 1), ct);
    }
}
