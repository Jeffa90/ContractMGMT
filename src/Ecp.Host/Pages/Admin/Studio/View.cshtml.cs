using Ecp.Identity;
using System.Globalization;
using Ecp.Contracts.Register;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>Create or change one shared view: columns, filters, sort, page size and formats, with a trial run before it is saved.</summary>
[Authorize(Policy = "perm:config.draft")]
public sealed class ViewModel(IOrganisationStructureService organisations, IAccessService access, IContractViewStudio studio, IContractRegister register) : StudioPage(organisations, access)
{
    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    [BindProperty] public string ViewKey { get; set; } = "";
    [BindProperty] public string ViewName { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public List<string> Columns { get; set; } = [];
    [BindProperty] public string? SortField { get; set; }
    [BindProperty] public bool SortDescending { get; set; }
    [BindProperty] public List<string> Ff { get; set; } = [];
    [BindProperty] public List<string> Fo { get; set; } = [];
    [BindProperty] public List<string> Fv { get; set; } = [];
    [BindProperty] public string PageSize { get; set; } = "25";
    [BindProperty] public bool StatusChips { get; set; } = true;
    [BindProperty] public string DateFormat { get; set; } = "short";
    [BindProperty] public string CurrencyFormat { get; set; } = "plain";
    [BindProperty] public string? GroupBy { get; set; }
    [BindProperty] public string? Text { get; set; }
    public bool IsNew => string.IsNullOrEmpty(Key);
    public ViewStudioState State { get; private set; } = null!;
    public RegisterCatalogue Catalogue { get; private set; } = null!;
    public RegisterPage? Trial { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (!IsNew)
        {
            var v = State.Working.Find(Key!) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
            (ViewKey, ViewName, Description, Columns) = (v.Key, v.Name, v.Description, [.. v.Columns]);
            (SortField, SortDescending) = (v.Sort.FirstOrDefault()?.Field, v.Sort.FirstOrDefault()?.Descending ?? false);
            Ff = v.Filters.Select(f => f.Field).ToList(); Fo = v.Filters.Select(f => f.Op).ToList(); Fv = v.Filters.Select(f => string.Join(",", f.Values)).ToList();
            (PageSize, StatusChips, DateFormat, CurrencyFormat, GroupBy, Text) = (v.PageSize.ToString(CultureInfo.InvariantCulture), v.StatusChips, v.DateFormat, v.CurrencyFormat, v.GroupBy, v.Text);
        }
        return Page();
    });

    private async Task LoadAsync(CancellationToken ct)
    {
        var org = await ResolveOrganisationAsync(ct) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
        State = await studio.LoadAsync(org, false, ct);
        Catalogue = await register.CatalogueAsync(org, ct);
    }

    private ViewDefinition? Build()
    {
        if (!int.TryParse(PageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) { ModelState.AddModelError(nameof(PageSize), "The page size must be a whole number."); return null; }
        var filters = new List<FilterClause>();
        for (var i = 0; i < Ff.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Ff[i])) continue;
            var op = i < Fo.Count ? Fo[i].Trim() : FilterOps.Eq; var raw = i < Fv.Count ? Fv[i] : "";
            var values = op is FilterOps.IsEmpty or FilterOps.IsNotEmpty ? [] : op is FilterOps.In or FilterOps.Between ? raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : (string.IsNullOrWhiteSpace(raw) ? [] : new List<string> { raw.Trim() });
            filters.Add(new FilterClause(Ff[i].Trim(), op, values));
        }
        var sort = string.IsNullOrWhiteSpace(SortField) ? [] : new List<SortClause> { new(SortField.Trim(), SortDescending) };
        return new ViewDefinition(IsNew ? ViewKey.Trim().ToLowerInvariant() : Key!, ViewName.Trim(), (Description ?? "").Trim(), Columns, sort, filters, size, StatusChips, DateFormat, CurrencyFormat,
            string.IsNullOrWhiteSpace(GroupBy) ? null : GroupBy.Trim(), string.IsNullOrWhiteSpace(Text) ? null : Text.Trim());
    }

    public Task<IActionResult> OnPostTryAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (Build() is { } def)
        {
            try { Trial = await studio.PreviewAsync(OrganisationId!.Value, def, ct); }
            catch (RegisterQueryException ex) { foreach (var issue in ex.Issues) ModelState.AddModelError(string.Empty, issue.Message); }
        }
        return Page();
    });

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (Build() is not { } def) return Page();
        if (IsNew && State.Working.Views.Any(v => string.Equals(v.Key, def.Key, StringComparison.OrdinalIgnoreCase))) { ModelState.AddModelError(nameof(ViewKey), "A view with that key already exists in the draft."); return Page(); }
        await studio.SaveViewAsync(OrganisationId!.Value, def, ct);
        Flash("The view is saved in the draft. Validate the draft to check it, then publish.");
        return RedirectToPage("Views", new { organisationId = OrganisationId });
    }, () => LoadAsync(CancellationToken.None));
}
