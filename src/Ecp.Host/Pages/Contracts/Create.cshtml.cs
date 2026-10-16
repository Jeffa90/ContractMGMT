using System.Globalization;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.create")]
public sealed class CreateModel(ISecuredContractService contracts, ISecuredSupplierService suppliers, ISecuredCategoryService categories,
    IOrganisationStructureService organisations, IAccessService access, IContractCustomFields customFields, ICoreFieldLists coreLists) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string Title { get; set; } = "";
    [BindProperty] public Guid? SupplierId { get; set; }
    [BindProperty] public Guid? CategoryId { get; set; }
    [BindProperty] public string Classification { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public ContractFieldsInput Input { get; set; } = new();

    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public string OrganisationName { get; private set; } = "";
    public string NextReference { get; private set; } = "";
    public IReadOnlyList<SupplierListItem> Suppliers { get; private set; } = [];
    public IReadOnlyList<CategoryItem> Categories { get; private set; } = [];
    public IReadOnlyList<string> Levels { get; private set; } = [];
    public ContractFieldOptions Options { get; private set; } = ContractFieldOptions.Empty;
    public CustomFieldForm? Fields { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(null, null, ct); return Page(); });

    /// <summary>Shows the configured fields that apply to the chosen contract type without creating anything. What was typed is kept.</summary>
    public Task<IActionResult> OnPostRefreshAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(CustomFieldFormReader.Read(Request.Form), null, ct);
        ModelState.Clear();
        return Page();
    });

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var submitted = CustomFieldFormReader.Read(Request.Form);
        await LoadAsync(submitted, null, ct);
        if (OrganisationId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(Title)) ModelState.AddModelError(nameof(Title), "Enter a title.");
        if (SupplierId is null) ModelState.AddModelError(nameof(SupplierId), "Choose a supplier.");
        if (CategoryId is null) ModelState.AddModelError(nameof(CategoryId), "Choose a category.");
        decimal? value = null;
        if (!string.IsNullOrWhiteSpace(Input.EstimatedValue))
        {
            if (decimal.TryParse(Input.EstimatedValue.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)) value = parsed;
            else ModelState.AddModelError("Input.EstimatedValue", "Enter the estimated value as digits with an optional decimal point.");
        }
        DateOnly? Date(string? raw, string field, string label)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            ModelState.AddModelError("Input." + field, $"Enter a valid {label}.");
            return null;
        }
        var start = Date(Input.StartDate, nameof(Input.StartDate), "start date");
        var effective = Date(Input.EffectiveDate, nameof(Input.EffectiveDate), "effective date");
        var review = Date(Input.ReviewDate, nameof(Input.ReviewDate), "review date");
        var end = Date(Input.EndDate, nameof(Input.EndDate), "end date");
        int? notice = null;
        if (!string.IsNullOrWhiteSpace(Input.NoticePeriodValue))
        {
            if (int.TryParse(Input.NoticePeriodValue.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) notice = n;
            else ModelState.AddModelError("Input.NoticePeriodValue", "Enter the notice period as a whole number.");
        }
        if (!ModelState.IsValid) return Page();

        Guid id;
        try
        {
            string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            id = await contracts.CreateDraftAsync(new CreateContractRequest(OrganisationId.Value, "", Title, SupplierId!.Value, CategoryId!.Value, Description,
                string.IsNullOrEmpty(Classification) ? Levels[0] : Classification, Blank(Input.CurrencyCode), value, Blank(Input.ExtensionType), Input.LegalReviewRequired, start, effective, review, end,
                Input.BusinessUnitId, Input.SiteId, Input.LegalEntityId, Blank(Input.ContractType), Blank(Input.AgreementType), Blank(Input.RiskLevel), Blank(Input.FunctionCode),
                Blank(Input.SapOutlineAgreementNumber), notice, Blank(Input.NoticePeriodUnit), Input.CriticalNotice, CustomInput: submitted), ct);
        }
        catch (ForbiddenException)
        {
            ModelState.AddModelError(nameof(CategoryId), "You can create contracts only in categories you currently own, unless your role allows organisation-wide creation.");
            return Page();
        }
        catch (CustomFieldValidationException ex)
        {
            await LoadAsync(submitted, ex.Errors, ct);
            foreach (var (key, message) in ex.Errors) ModelState.AddModelError("cf_" + key, message);
            return Page();
        }
        Flash("Draft contract created.");
        return RedirectToPage("Details", new { id });
    });

    private async Task LoadAsync(CustomFieldInput? submitted, IReadOnlyDictionary<string, string>? errors, CancellationToken ct)
    {
        // Organisations where the caller holds a create grant (an organisation-scoped grant names them; a tenant-wide grant covers all).
        var grants = await access.GrantsAsync("contracts.create", ct);
        var all = await organisations.ListOrganisationsAsync(ct);
        Organisations = all.Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (OrganisationId is null && Organisations.Count == 1) OrganisationId = Organisations[0].Id;
        if (OrganisationId is not { } org) return;
        var current = Organisations.FirstOrDefault(o => o.Id == org);
        if (current is null) throw new ResourceNotFoundException();
        OrganisationName = current.Name;
        Input.CurrencyCode ??= current.DefaultCurrencyCode;
        NextReference = await contracts.PreviewNextReferenceAsync(org, ct);
        Suppliers = (await suppliers.ListAsync(null, ct)).Where(s => s.OrganisationId == org).ToList();
        Categories = (await categories.ListAsync(ct)).Where(c => c.OrganisationId == org && c.IsActive).ToList();
        Options = await ContractFieldOptionsLoader.LoadAsync(org, organisations, coreLists, ct);
        var scale = await access.ClassificationScaleAsync(ct);
        Levels = scale.Levels;
        if (string.IsNullOrEmpty(Classification)) Classification = scale.DefaultLevel;
        Fields = await customFields.ForNewAsync(org, string.IsNullOrWhiteSpace(Input.ContractType) ? null : Input.ContractType.Trim(), submitted, errors, ct);
    }
}
