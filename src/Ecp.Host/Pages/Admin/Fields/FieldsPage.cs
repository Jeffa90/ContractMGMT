using Ecp.Configuration;
using Ecp.Host.Pages.Admin.Studio;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>One line of the Fields page: a field as the administrator thinks of it, with what is published and what the open draft says about it.</summary>
public sealed record FieldRow(FieldDefinition Field, string SectionLabel, string TypeLabel, IReadOnlyList<string> UsedOn, string StatusKey, string StatusText, int RecordsWithValues);

/// <summary>
/// Shared behaviour of the guided Fields pages. They are a presentation of the existing custom-field engine: the draft, validation, publication, history, rollback and audit are those of every configuration
/// document, reached through <see cref="ICustomFieldStudio"/>. A draft is opened for the person when they first change something, so nobody has to know that one exists.
/// </summary>
public abstract class FieldsPage(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio, IConfigurationService configuration) : StudioPage(organisations, access)
{
    protected ICustomFieldStudio Studio { get; } = studio;
    public StudioState State { get; protected set; } = null!;
    public bool CanEdit { get; private set; }
    public bool MayPublish { get; private set; }
    public GovernanceInfo? Governance { get; private set; }

    /// <summary>Loads what is published and the open draft (if any) for the organisation being worked on.</summary>
    protected async Task<Guid> LoadStateAsync(CancellationToken ct)
    {
        var org = await ResolveOrganisationAsync(ct) ?? throw new ResourceNotFoundException();
        State = await Studio.LoadAsync(org, false, ct);
        CanEdit = (await Access.GrantsAsync("config.draft", ct)).Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(org));
        MayPublish = (await Access.GrantsAsync("config.publish", ct)).Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(org));
        Governance = null;
        if (State.Draft is { } draft)
            try { Governance = await configuration.GovernanceAsync(draft.Id, ct); } catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException) { }
        return org;
    }

    /// <summary>Opens the organisation's draft if there is none, so a first change needs no separate step.</summary>
    protected async Task EnsureDraftAsync(Guid org, CancellationToken ct)
    {
        if (!State.HasDraft) { await Studio.StartDraftAsync(org, ct); State = await Studio.LoadAsync(org, false, ct); }
    }

    public string SectionLabel(CustomFieldSet set, string key)
        => set.Sections.FirstOrDefault(s => s.Key == key)?.Label ?? (key == "general" ? "Additional details" : key);

    /// <summary>The fields whose draft differs from what is published.</summary>
    public IReadOnlyList<FieldDefinition> PendingFields => State.Working.Fields.Where(f => FieldGuide.Differs(State.Published.Find(f.Key), f)).ToList();

    public IReadOnlyList<FieldRow> Rows(string? query = null)
    {
        var set = State.Working;
        var rows = new List<FieldRow>();
        foreach (var f in set.Fields.OrderBy(f => set.Sections.FirstOrDefault(s => s.Key == f.Section)?.Order ?? int.MinValue).ThenBy(f => f.Order).ThenBy(f => f.Label, StringComparer.Ordinal))
        {
            var (key, text) = FieldGuide.StatusOf(f, State.Published.Find(f.Key));
            rows.Add(new FieldRow(f, SectionLabel(set, f.Section), FieldGuide.Label(f.Type), FieldGuide.UsedOn(f), key, text, State.Usage.Of(f.Key)));
        }
        if (string.IsNullOrWhiteSpace(query)) return rows;
        var q = query.Trim();
        return rows.Where(r => r.Field.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || r.TypeLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.SectionLabel.Contains(q, StringComparison.OrdinalIgnoreCase) || r.StatusText.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The checks on the open draft as sentences a person can act on, each tied to the field it is about.</summary>
    public IReadOnlyList<(bool IsError, string Text)> Checks()
    {
        var fields = State.Working.Fields;
        var lines = new List<(bool, string)>();
        foreach (var m in State.DraftMessages)
        {
            var index = System.Text.RegularExpressions.Regex.Match(m.Path, @"\$\.fields\[(\d+)\]", System.Text.RegularExpressions.RegexOptions.NonBacktracking);
            var about = index.Success && int.TryParse(index.Groups[1].Value, out var i) && i < fields.Count ? fields[i].Label + ": " : "";
            lines.Add((m.Severity == ValidationSeverity.Error, about + m.Message));
        }
        return lines;
    }

    /// <summary>The words for a role, as the organisation uses them.</summary>
    public string RoleName(string code) => Terms.Role(code);

    /// <summary>The preview and plain-English review of a field, drawn with the organisation's own formats and words.</summary>
    protected FieldPreviewModel PreviewOf(FieldDefinition def, string sectionKey, string sectionLabel, bool published)
        => new(def, State.Working, sectionKey, sectionLabel, FieldGuide.Review(def, sectionLabel, State.Working, RoleName, Format.Date, published), Example(def, State.Working));

    protected string Example(FieldDefinition d, CustomFieldSet set) => d.Type switch
    {
        FieldTypes.Date => Format.Date(new DateOnly(2027, 6, 30)),
        FieldTypes.DateTime => Format.DateTime(new DateTime(2027, 6, 30, 9, 30, 0, DateTimeKind.Utc)),
        FieldTypes.Boolean => "Yes",
        FieldTypes.SingleChoice or FieldTypes.MultiChoice or FieldTypes.Lookup => set.ChoicesOf(d).FirstOrDefault(c => c.Active)?.Label ?? "-",
        FieldTypes.Integer => "30",
        FieldTypes.Decimal => "2.75",
        FieldTypes.Currency => "1,250.00",
        FieldTypes.Percentage => "15%",
        FieldTypes.Email => "name@example.org",
        FieldTypes.Url => "https://example.org",
        FieldTypes.Person => "A colleague",
        FieldTypes.FileReference => "A document",
        FieldTypes.Calculated => "Worked out when the page is shown",
        _ => "Example text"
    };
}
