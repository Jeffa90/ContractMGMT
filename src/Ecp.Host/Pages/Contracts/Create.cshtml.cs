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
    IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string Title { get; set; } = "";
    [BindProperty] public Guid? SupplierId { get; set; }
    [BindProperty] public Guid? CategoryId { get; set; }
    [BindProperty] public Guid? BusinessUnitId { get; set; }
    [BindProperty] public Guid? SiteId { get; set; }
    [BindProperty] public string Classification { get; set; } = "";
    [BindProperty] public string? CurrencyCode { get; set; }
    [BindProperty] public string? EstimatedValue { get; set; }
    [BindProperty] public string? StartDate { get; set; }
    [BindProperty] public string? EndDate { get; set; }
    [BindProperty] public string? Description { get; set; }

    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public string OrganisationName { get; private set; } = "";
    public string NextReference { get; private set; } = "";
    public IReadOnlyList<SupplierListItem> Suppliers { get; private set; } = [];
    public IReadOnlyList<CategoryItem> Categories { get; private set; } = [];
    public IReadOnlyList<BusinessUnitDto> BusinessUnits { get; private set; } = [];
    public IReadOnlyList<SiteDto> Sites { get; private set; } = [];
    public IReadOnlyList<string> Levels { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (OrganisationId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(Title)) ModelState.AddModelError(nameof(Title), "Enter a title.");
        if (SupplierId is null) ModelState.AddModelError(nameof(SupplierId), "Choose a supplier.");
        if (CategoryId is null) ModelState.AddModelError(nameof(CategoryId), "Choose a category.");
        decimal? value = null;
        if (!string.IsNullOrWhiteSpace(EstimatedValue))
        {
            if (decimal.TryParse(EstimatedValue.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)) value = parsed;
            else ModelState.AddModelError(nameof(EstimatedValue), "Enter the estimated value as digits with an optional decimal point.");
        }
        DateOnly? start = null, end = null;
        if (!string.IsNullOrWhiteSpace(StartDate) && !DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)) ModelState.AddModelError(nameof(StartDate), "Enter a valid start date.");
        else if (!string.IsNullOrWhiteSpace(StartDate)) start = DateOnly.ParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(EndDate) && !DateOnly.TryParseExact(EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)) ModelState.AddModelError(nameof(EndDate), "Enter a valid end date.");
        else if (!string.IsNullOrWhiteSpace(EndDate)) end = DateOnly.ParseExact(EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!ModelState.IsValid) return Page();

        Guid id;
        try
        {
            id = await contracts.CreateDraftAsync(new CreateContractRequest(OrganisationId.Value, "", Title, SupplierId!.Value, CategoryId!.Value, Description,
            string.IsNullOrEmpty(Classification) ? Levels[0] : Classification, string.IsNullOrWhiteSpace(CurrencyCode) ? null : CurrencyCode, value, StartDate: start, EndDate: end,
            BusinessUnitId: BusinessUnitId, SiteId: SiteId), ct);
        }
        catch (ForbiddenException)
        {
            ModelState.AddModelError(nameof(CategoryId), "You can create contracts only in categories you currently own, unless your role allows organisation-wide creation.");
            return Page();
        }
        Flash("Draft contract created.");
        return RedirectToPage("Details", new { id });
    });

    private async Task LoadAsync(CancellationToken ct)
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
        CurrencyCode ??= current.DefaultCurrencyCode;
        NextReference = await contracts.PreviewNextReferenceAsync(org, ct);
        Suppliers = (await suppliers.ListAsync(null, ct)).Where(s => s.OrganisationId == org).ToList();
        Categories = (await categories.ListAsync(ct)).Where(c => c.OrganisationId == org && c.IsActive).ToList();
        var structure = await organisations.GetAsync(org, ct);
        BusinessUnits = structure.BusinessUnits.Where(b => b.IsActive).ToList();
        Sites = structure.Sites.Where(s => s.IsActive).ToList();
        var scale = await access.ClassificationScaleAsync(ct);
        Levels = scale.Levels;
        if (string.IsNullOrEmpty(Classification)) Classification = scale.DefaultLevel;
    }
}
