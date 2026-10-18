using System.Text.Json;
using System.Text.Json.Nodes;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Configuration.Forms;

/// <summary>What a form page needs to show one kind of configuration: its form, the document behind it, the draft and release it comes from and what the last check found.</summary>
public sealed record FormEditState(
    FormSchema Schema, JsonObject Document, DraftDto? Draft, ReleaseDto? Latest, GovernanceInfo? Governance, IReadOnlyList<FormProblem> Problems, IReadOnlyList<ValidationMessage> Messages,
    IReadOnlyDictionary<string, IReadOnlyList<FormOption>> Lookups)
{
    /// <summary>True when there is no draft yet: the form shows what is in force and cannot be changed until a draft is started.</summary>
    public bool ReadOnly => Draft is null;
}

/// <summary>
/// Editing the documents that drive the platform without seeing them. Every operation here goes through the configuration lifecycle (<see cref="IConfigurationService"/>): the same permissions, the same validation, the
/// same draft, submission, second-person publication, release history and rollback as any other configuration. The form only changes how the document is written.
/// </summary>
public interface IConfigurationFormEditor
{
    IReadOnlyList<FormSchema> Schemas();
    FormSchema SchemaOf(string kind);
    Task<FormEditState> OpenAsync(string kind, Guid organisationId, CancellationToken ct = default);
    Task<FormEditState> StartAsync(string kind, Guid organisationId, CancellationToken ct = default);
    /// <summary>Saves the document as the draft's content and returns the draft; nothing is published.</summary>
    Task<DraftDto> SaveAsync(string kind, Guid organisationId, JsonObject document, CancellationToken ct = default);
    /// <summary>Saves, then validates with the same rules that gate publication.</summary>
    Task<DraftDto> ValidateAsync(string kind, Guid organisationId, JsonObject document, CancellationToken ct = default);
    /// <summary>Validation messages placed on the fields they are about; messages about the whole document keep an empty path.</summary>
    IReadOnlyList<FormProblem> PlaceProblems(FormSchema schema, JsonObject document, IEnumerable<ValidationMessage> messages);
    string Serialise(JsonObject document);
    /// <summary>The preview and impact analysis of the document as it stands in the form, or null when the kind offers none.</summary>
    Task<FormInsight?> InsightAsync(string kind, Guid organisationId, JsonObject document, CancellationToken ct = default);
}

internal sealed class ConfigurationFormEditor(IConfigurationService configuration, IEnumerable<IFormSchemaSource> sources, IEnumerable<IFormInsight> insights, IServiceProvider services) : IConfigurationFormEditor
{
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private IReadOnlyList<FormSchema> All => sources.SelectMany(s => s.Schemas()).ToList();

    public IReadOnlyList<FormSchema> Schemas() => All;

    public FormSchema SchemaOf(string kind) => All.FirstOrDefault(s => s.Kind == kind) ?? throw new DomainException("no_form", "That kind of configuration has no form editor.");

    public async Task<FormInsight?> InsightAsync(string kind, Guid organisationId, JsonObject document, CancellationToken ct = default)
    {
        SchemaOf(kind);
        var insight = insights.FirstOrDefault(i => i.Kind == kind);
        if (insight is null) return null;
        var latest = await configuration.LatestReleaseAsync(kind, organisationId, ct);
        return await insight.AnalyseAsync(organisationId, document, latest is null ? null : Parse(latest.ContentJson), ct);
    }

    public string Serialise(JsonObject document) => document.ToJsonString(Write);

    private static JsonObject Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public async Task<FormEditState> OpenAsync(string kind, Guid organisationId, CancellationToken ct = default)
    {
        var schema = SchemaOf(kind);
        var draft = await configuration.FindOpenDraftAsync(kind, organisationId, ct);
        var latest = await configuration.LatestReleaseAsync(kind, organisationId, ct);
        var document = Parse(draft?.ContentJson ?? latest?.ContentJson ?? "{}");
        GovernanceInfo? governance = null;
        if (draft is not null)
        {
            try { governance = await configuration.GovernanceAsync(draft.Id, ct); }
            catch (ResourceNotFoundException) { }
            catch (ForbiddenException) { }
        }
        var messages = draft?.Validation?.Messages ?? [];
        var lookups = services.GetService<IFormLookupProvider>() is { } provider ? await provider.LookupsAsync(organisationId, ct) : new Dictionary<string, IReadOnlyList<FormOption>>();
        return new FormEditState(schema, document, draft, latest, governance, PlaceProblems(schema, document, messages), messages, lookups);
    }

    public async Task<FormEditState> StartAsync(string kind, Guid organisationId, CancellationToken ct = default)
    {
        SchemaOf(kind);
        var latest = await configuration.LatestReleaseAsync(kind, organisationId, ct);
        await configuration.GetOrCreateDraftAsync(kind, organisationId, latest?.ContentJson, ct);
        return await OpenAsync(kind, organisationId, ct);
    }

    private async Task<DraftDto> DraftAsync(string kind, Guid organisationId, CancellationToken ct)
    {
        var existing = await configuration.FindOpenDraftAsync(kind, organisationId, ct);
        if (existing is not null) return existing;
        var latest = await configuration.LatestReleaseAsync(kind, organisationId, ct);
        return await configuration.GetOrCreateDraftAsync(kind, organisationId, latest?.ContentJson, ct);
    }

    public async Task<DraftDto> SaveAsync(string kind, Guid organisationId, JsonObject document, CancellationToken ct = default)
    {
        SchemaOf(kind);
        var draft = await DraftAsync(kind, organisationId, ct);
        return await configuration.SaveDraftAsync(draft.Id, Serialise(document), ct);
    }

    public async Task<DraftDto> ValidateAsync(string kind, Guid organisationId, JsonObject document, CancellationToken ct = default)
    {
        var saved = await SaveAsync(kind, organisationId, document, ct);
        return await configuration.ValidateAsync(saved.Id, ct);
    }

    public IReadOnlyList<FormProblem> PlaceProblems(FormSchema schema, JsonObject document, IEnumerable<ValidationMessage> messages)
        => messages.Select(m => new FormProblem(FormBinder.FormPathOf(schema, document, m.Path) ?? "", m.Message, m.Severity == ValidationSeverity.Error)).ToList();
}
