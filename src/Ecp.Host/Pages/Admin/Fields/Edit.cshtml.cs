using System.Globalization;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>
/// Add or change one field, guided: its name, its type (and only the settings that type has), its choices, where it goes, its rules, how it will look, and what publishing it will do in plain English. Nothing
/// here asks for a field key, a stored code, JSON or a release number; the machine names are generated and kept out of sight. Saving puts the field in the organisation's draft (opened for the person if needed);
/// the panel under the preview publishes or submits it.
/// </summary>
[Authorize(Policy = "perm:config.draft")]
public sealed class EditModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio, IConfigurationService configuration, IEnumerable<IWorkflowStateSource> stateSources)
    : FieldsPage(organisations, access, studio, configuration)
{
    public const string NewSection = "__new";

    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    [BindProperty] public FieldForm Form { get; set; } = new();
    [BindProperty] public string? PreviousType { get; set; }
    [BindProperty] public string? OptionAction { get; set; }
    /// <summary>The type button the person pressed, if they changed the type.</summary>
    [BindProperty] public string? NewType { get; set; }

    public FieldPreviewModel? Preview { get; private set; }
    public IReadOnlyList<string> Levels { get; private set; } = [];
    public IReadOnlySet<string>? KnownStates { get; private set; }
    public IReadOnlyList<string> Roles => RoleCodes.All;
    public IReadOnlyList<OperandChoice> Operands { get; private set; } = [];
    public IReadOnlyList<FieldChoice> ContractTypeChoices { get; private set; } = [];
    public bool IsNew => string.IsNullOrEmpty(Key);
    public bool Published { get; private set; }
    public string DefaultClassification { get; private set; } = "";
    public bool Saved { get; private set; }
    public FieldDefinition? Existing { get; private set; }

    public Task<IActionResult> OnGetAsync(bool saved, string? type, CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Saved = saved;
        if (!IsNew)
        {
            Existing = State.Working.Find(Key!) ?? throw new ResourceNotFoundException();
            Form = FieldForm.From(Existing, State.Working);
            PreviousType = Form.Type;
            BuildPreview(quiet: true);
        }
        else
        {
            Form = FieldForm.New(FieldTypes.IsKnown(type) ? type! : FieldTypes.Text, DefaultClassification);
            if (FieldGuide.HasOptions(Form.Type)) Form.Options = [new OptionRow(), new OptionRow()];
            PreviousType = Form.Type;
        }
        return Page();
    });

    /// <summary>Shows the settings that belong to the chosen type. Nothing is saved; what was typed is kept.</summary>
    public Task<IActionResult> OnPostRefreshAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Existing = IsNew ? null : State.Working.Find(Key!);
        ApplyTypeChange();
        ModelState.Clear();
        BuildPreview(quiet: true);
        return Page();
    });

    public Task<IActionResult> OnPostPreviewAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Existing = IsNew ? null : State.Working.Find(Key!);
        ApplyTypeChange();
        ModelState.Clear();
        BuildPreview(quiet: false);
        return Page();
    });

    /// <summary>Add, move, switch off or remove one option of a choice field, then show the editor again.</summary>
    public Task<IActionResult> OnPostOptionAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Existing = IsNew ? null : State.Working.Find(Key!);
        ApplyTypeChange();
        ModelState.Clear();
        var rows = Form.Options;
        var (action, at) = SplitAction(OptionAction);
        switch (action)
        {
            case "add": rows.Add(new OptionRow()); break;
            case "up" when at > 0 && at < rows.Count: (rows[at - 1], rows[at]) = (rows[at], rows[at - 1]); break;
            case "down" when at >= 0 && at < rows.Count - 1: (rows[at + 1], rows[at]) = (rows[at], rows[at + 1]); break;
            case "remove" when at >= 0 && at < rows.Count && !IsPublishedOption(rows[at]): rows.RemoveAt(at); break;
        }
        BuildPreview(quiet: true);
        return Page();
    });

    private static (string Action, int At) SplitAction(string? raw)
    {
        var parts = (raw ?? "").Split(':', 2);
        return (parts[0], parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1);
    }

    public bool IsPublishedOption(OptionRow row)
        => !string.IsNullOrWhiteSpace(row.Code) && Key is not null && State.Published.Find(Key)?.Choices.Any(c => string.Equals(c.Code, row.Code, StringComparison.OrdinalIgnoreCase)) == true;

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await LoadAsync(ct);
        Existing = IsNew ? null : State.Working.Find(Key!) ?? throw new ResourceNotFoundException();
        ApplyTypeChange();
        var context = new FormContext(State.Working, Operands, DefaultClassification);
        var (sectionKey, sectionLabel, newSection) = ResolveSection();
        var key = IsNew ? ChooseKey() : Key!;
        var def = Form.Build(ModelState, context, key, sectionKey, Existing);
        if (def is null || !ModelState.IsValid) { BuildPreview(quiet: true); return Page(); }

        await EnsureDraftAsync(org, ct);
        if (newSection is not null) await Studio.SaveSectionAsync(org, newSection, ct);
        await PlaceAndSaveAsync(org, def, sectionKey, ct);
        Flash("Saved as a draft. Check the review below, then publish.");
        return RedirectToPage(null, null, new { organisationId = org, key, saved = true }, "review-h");
    }, () => LoadAsync(CancellationToken.None));

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------------------

    private async Task<Guid> LoadAsync(CancellationToken ct)
    {
        var org = await LoadStateAsync(ct);
        Published = !IsNew && State.Published.Find(Key!) is not null;
        Levels = (await Access.ClassificationScaleAsync(ct)).Levels;
        DefaultClassification = (await Access.ClassificationScaleAsync(ct)).DefaultLevel;
        foreach (var source in stateSources) if (await source.StatesAsync(org, ct) is { } known) { KnownStates = known; break; }
        var plain = State.Working.Fields.Where(f => f.Type != FieldTypes.Calculated && !f.IsRetired && !string.Equals(f.Key, Key, StringComparison.OrdinalIgnoreCase))
            .Select(f => new OperandChoice(f.Key, f.Label, f.Type));
        var core = CoreFields.All.Where(c => c.Type is FieldTypes.Integer or FieldTypes.Decimal or FieldTypes.Currency or FieldTypes.Percentage or FieldTypes.Date or FieldTypes.DateTime or FieldTypes.Text)
            .Select(c => new OperandChoice(c.Key, Terms.Expand(c.Label), c.Type));
        Operands = plain.Concat(core).ToList();
        ContractTypeChoices = State.Working.FindList("contractType")?.Members.Where(m => m.Active).ToList() ?? [];
        return org;
    }

    /// <summary>When the type was changed on the form, the rules that belong to the new type start from its sensible defaults and options that no longer apply are dropped.</summary>
    private void ApplyTypeChange()
    {
        if (FieldTypes.IsKnown(NewType)) Form.Type = NewType!;
        if (Published && Existing is not null) Form.Type = Existing.Type;      // the type of a published field never changes; the engine refuses it too
        if (!FieldTypes.IsKnown(Form.Type)) Form.Type = FieldTypes.Text;
        if (!string.IsNullOrEmpty(PreviousType) && PreviousType != Form.Type)
        {
            var (s, f, o, _) = FieldGuide.Defaults(Form.Type);
            (Form.Searchable, Form.Filterable, Form.Sortable) = (s, f, o);
            if (!FieldGuide.HasOptions(Form.Type)) Form.Options = [];
            Form.DefaultValue = null;
            PreviousType = Form.Type;
        }
        if (FieldGuide.HasOptions(Form.Type) && Form.Options.Count == 0) Form.Options = [new OptionRow(), new OptionRow()];
        if (string.IsNullOrWhiteSpace(Form.Classification)) Form.Classification = DefaultClassification;
        if (Form.Position.StartsWith("after:", StringComparison.Ordinal)) (Form.Position, Form.AfterKey) = ("after", Form.Position["after:".Length..]);
        Form.Status = Existing?.Status ?? FieldStatuses.Active;
        Form.Order = Existing?.Order ?? 0;
    }

    private string ChooseKey()
    {
        var taken = State.Working.Fields.Select(f => f.Key);
        if (!string.IsNullOrWhiteSpace(Form.CustomKey))
        {
            var custom = Form.CustomKey.Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(custom, "^[a-z][a-z0-9_]{1,62}$", System.Text.RegularExpressions.RegexOptions.NonBacktracking))
                ModelState.AddModelError("Form.CustomKey", "Use lower-case letters, digits and underscores, starting with a letter (at least two characters).");
            else if (CoreFields.IsCore(custom) || taken.Contains(custom, StringComparer.OrdinalIgnoreCase))
                ModelState.AddModelError("Form.CustomKey", "That key is already used by another field. Leave it blank and one is made for you.");
            return custom;
        }
        return FieldGuide.KeyFromName(Form.Name ?? "", taken);
    }

    private (string Key, string Label, FieldSection? New) ResolveSection()
    {
        if (Form.Section == NewSection)
        {
            var name = (Form.NewSectionName ?? "").Trim();
            if (name.Length == 0) { ModelState.AddModelError("Form.NewSectionName", "Give the new section a name, or choose an existing one."); return ("general", "Additional details", null); }
            if (name.Length > 100) { ModelState.AddModelError("Form.NewSectionName", "The section name is too long. Use 100 characters or fewer."); return ("general", "Additional details", null); }
            var existing = State.Working.Sections.FirstOrDefault(s => string.Equals(s.Label, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return (existing.Key, existing.Label, null);
            var key = FieldGuide.KeyFromName(name, State.Working.Sections.Select(s => s.Key).Append("general"));
            return (key, name, new FieldSection(key, name, (State.Working.Sections.Select(s => s.Order).DefaultIfEmpty(0).Max()) + 10));
        }
        var chosen = State.Working.Sections.FirstOrDefault(s => s.Key == Form.Section);
        return chosen is not null ? (chosen.Key, chosen.Label, null) : ("general", SectionLabel(State.Working, "general"), null);
    }

    /// <summary>Saves the field and, when the person asked for a particular place, renumbers its section neighbours so the order is exactly what they chose.</summary>
    private async Task PlaceAndSaveAsync(Guid org, FieldDefinition def, string sectionKey, CancellationToken ct)
    {
        var neighbours = State.Working.Fields.Where(f => f.Section == sectionKey && !string.Equals(f.Key, def.Key, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Order).ThenBy(f => f.Label, StringComparer.Ordinal).ToList();
        var sameSection = Existing is not null && Existing.Section == sectionKey;
        if (sameSection && Form.Position is "keep") { await Studio.SaveFieldAsync(org, def with { Order = Existing!.Order }, ct); return; }
        var at = Form.Position switch
        {
            "start" => 0,
            "after" => neighbours.FindIndex(f => string.Equals(f.Key, Form.AfterKey, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i + 1 : neighbours.Count,
            _ => neighbours.Count
        };
        if (at == neighbours.Count) { await Studio.SaveFieldAsync(org, def with { Order = (neighbours.Select(f => f.Order).DefaultIfEmpty(0).Max()) + 10 }, ct); return; }
        var ordered = neighbours.ToList();
        ordered.Insert(at, def);
        for (var n = 0; n < ordered.Count; n++)
        {
            var wanted = (n + 1) * 10;
            if (ordered[n].Order != wanted || ReferenceEquals(ordered[n], def)) await Studio.SaveFieldAsync(org, ordered[n] with { Order = wanted }, ct);
        }
    }

    /// <summary>The preview and the plain-English review of what is on the form. When the form has mistakes, the preview is skipped (quietly) or the mistakes are listed (when the person asked for a preview).</summary>
    private void BuildPreview(bool quiet)
    {
        var (sectionKey, sectionLabel, _) = ResolveSection();
        var scratch = new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();
        var context = new FormContext(State.Working, Operands, DefaultClassification);
        var key = IsNew ? FieldGuide.KeyFromName(Form.Name ?? "", State.Working.Fields.Select(f => f.Key)) : Key!;
        var def = Form.Build(scratch, context, key, sectionKey, Existing);
        if (def is null)
        {
            if (!quiet) foreach (var (k, entry) in scratch) foreach (var err in entry.Errors) ModelState.AddModelError(k, err.ErrorMessage);
            return;
        }
        Preview = PreviewOf(def, sectionKey, sectionLabel, Published);
    }
}
