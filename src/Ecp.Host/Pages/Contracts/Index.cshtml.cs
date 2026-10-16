using Ecp.Contracts;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class IndexModel(ISecuredContractService contracts, ISecuredCategoryService categories) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CategoryId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public PagedResult<ContractListItem> Result { get; private set; } = new([], 0, 1, 25);
    public IReadOnlyList<string> Statuses { get; private set; } = [];
    public IReadOnlyList<CategoryItem> Categories { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Result = await contracts.ListAsync(new ContractFilter(Q, Status, CategoryId, null, Math.Max(1, PageNumber)), ct);
        Statuses = (await contracts.CountByStatusAsync(ct)).Keys.Order().ToList();
        Categories = await categories.ListAsync(ct);
        return Page();
    }

    public string PageLink(int page)
        => "/Contracts?" + string.Join("&", new[] { ("q", Q), ("status", Status), ("categoryId", CategoryId?.ToString()), ("pageNumber", page.ToString()) }
            .Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
}
