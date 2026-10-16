using Ecp.Contracts;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Legal;

[Authorize(Policy = "perm:contracts.read")]
public sealed class IndexModel(ILegalReviewService legal, ISecuredCategoryService categories, ISecuredSupplierService suppliers) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public LegalReviewStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Owner { get; set; }      // me | unassigned | (blank)
    [BindProperty(SupportsGet = true)] public Guid? CategoryId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? SupplierId { get; set; }
    [BindProperty(SupportsGet = true)] public int? MinAgeDays { get; set; }
    [BindProperty(SupportsGet = true)] public bool Mine { get; set; }
    [BindProperty(SupportsGet = true)] public bool All { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public PagedResult<LegalQueueItem> Result { get; private set; } = new([], 0, 1, 25);
    public IReadOnlyList<CategoryItem> Categories { get; private set; } = [];
    public IReadOnlyList<SupplierListItem> Suppliers { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Result = await legal.QueueAsync(new LegalQueueFilter(Status, null, Owner == "me", Owner == "unassigned", CategoryId, SupplierId, MinAgeDays, Mine, !All, Math.Max(1, PageNumber)), ct);
        Categories = await categories.ListAsync(ct);
        Suppliers = await suppliers.ListAsync(null, ct);
        return Page();
    });

    public string PageLink(int page) => "/Legal?" + string.Join("&", new[] { ("status", Status?.ToString()), ("owner", Owner), ("categoryId", CategoryId?.ToString()), ("supplierId", SupplierId?.ToString()),
        ("minAgeDays", MinAgeDays?.ToString()), ("mine", Mine ? "true" : null), ("all", All ? "true" : null), ("pageNumber", page.ToString()) }.Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
}
