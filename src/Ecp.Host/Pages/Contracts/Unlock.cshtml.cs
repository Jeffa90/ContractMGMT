using Ecp.Contracts;
using Ecp.Lifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class UnlockModel(IRecordUnlockService unlocks, ISecuredContractService contracts) : EcpPageModel
{
    public ContractDetails Contract { get; private set; } = null!;
    public UnlockPolicyInfo Policy { get; private set; } = null!;
    public UnlockInfo? Current { get; private set; }
    public IReadOnlyList<UnlockInfo> History { get; private set; } = [];
    public IReadOnlyDictionary<string, string?> Values { get; private set; } = new Dictionary<string, string?>();
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public int Minutes { get; set; } = 60;

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Contract = await contracts.GetAsync(id, ct);
        Policy = await unlocks.PolicyAsync(id, ct);
        Current = await unlocks.CurrentAsync(id, ct);
        History = await unlocks.HistoryAsync(id, ct);
        Minutes = Math.Min(Policy.DefaultMinutes, Policy.MaxMinutes);
        Values = new Dictionary<string, string?>
        {
            ["contractTitle"] = Contract.Title, ["description"] = Contract.Description, ["currencyCode"] = Contract.CurrencyCode, ["estimatedValue"] = Contract.EstimatedValue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["extensionType"] = Contract.ExtensionType, ["noticePeriodDays"] = Contract.Reminders?.NoticePeriodDays?.ToString(), ["criticalNotice"] = (Contract.Reminders?.CriticalNotice ?? false) ? "true" : "false",
            ["reviewDate"] = Contract.ReviewDate?.ToString("yyyy-MM-dd"), ["endDate"] = Contract.EndDate?.ToString("yyyy-MM-dd")
        };
    }

    public Task<IActionResult> OnPostOpenAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var fields = Request.Form.Keys.Where(k => k.StartsWith("Open_", StringComparison.Ordinal)).Select(k => k["Open_".Length..]).ToList();
        await unlocks.OpenAsync(new UnlockRequest(id, Reason, Minutes, fields), ct);
        Flash("The record is unlocked for the fields you named, until the time limit. Everything you change is audited with the unlock.");
        return RedirectToPage(new { id });
    }, () => LoadAsync(id, ct));

    public Task<IActionResult> OnPostChangeAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var changes = Request.Form.Keys.Where(k => k.StartsWith("Value_", StringComparison.Ordinal))
            .Where(k => Request.Form.ContainsKey("Apply_" + k["Value_".Length..]))
            .ToDictionary(k => k["Value_".Length..], k => (string?)(string.IsNullOrWhiteSpace(Request.Form[k]) ? null : Request.Form[k].ToString().Trim()));
        var applied = await unlocks.ApplyChangesAsync(id, changes, ct);
        Flash(applied.Count == 0 ? "Nothing changed." : "Changed: " + string.Join(", ", applied.Select(a => a.Field)) + ".");
        return RedirectToPage(new { id });
    }, () => LoadAsync(id, ct));

    public Task<IActionResult> OnPostCloseAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await unlocks.CloseAsync(id, ct);
        Flash("The unlock is closed and the record is locked again.");
        return RedirectToPage(new { id });
    }, () => LoadAsync(id, ct));
}
