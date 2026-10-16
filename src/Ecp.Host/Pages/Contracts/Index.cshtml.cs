using Ecp.Contracts;
using Ecp.Lifecycle;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class IndexModel(ISecuredContractService contracts, ISecuredCategoryService categories, IStatusLabels labels) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? CategoryId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public bool Owned { get; set; }
    public IReadOnlyDictionary<string, string> StatusLabels { get; private set; } = new Dictionary<string, string>();
    /// <summary>Renewal links the reader may follow: id to agreement reference, only for agreements they can open.</summary>
    public IReadOnlyDictionary<Guid, string> Linked { get; private set; } = new Dictionary<Guid, string>();

    public PagedResult<ContractListItem> Result { get; private set; } = new([], 0, 1, 25);
    public IReadOnlyList<string> Statuses { get; private set; } = [];
    public IReadOnlyList<CategoryItem> Categories { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Result = await contracts.ListAsync(new ContractFilter(Q, Status, CategoryId, null, Math.Max(1, PageNumber), OwnedByMe: Owned), ct);
        Statuses = (await contracts.CountByStatusAsync(ct)).Keys.Order().ToList();
        Categories = await categories.ListAsync(ct);
        StatusLabels = await labels.AllAsync(ct);
        var linkIds = Result.Items.SelectMany(i => new[] { i.PredecessorContractId, i.SuccessorContractId }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        Linked = linkIds.Length == 0 ? new Dictionary<Guid, string>() : (await contracts.ListAsync(new ContractFilter(Ids: linkIds, PageSize: 200), ct)).Items.ToDictionary(i => i.Id, i => i.AgreementReference);
        return Page();
    }

    public string PageLink(int page)
        => "/Contracts?" + string.Join("&", new[] { ("q", Q), ("status", Status), ("categoryId", CategoryId?.ToString()), ("owned", Owned ? "true" : null), ("pageNumber", page.ToString()) }
            .Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
}
