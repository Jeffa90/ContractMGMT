using Ecp.Contracts;
using Ecp.Lifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Variations;

[Authorize(Policy = "perm:contracts.read")]
public sealed class CreateModel(IVariationService variations, ISecuredContractService contracts, IContractCustomFields customFields) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid ContractId { get; set; }
    [BindProperty] public VariationForm Form { get; set; } = new();
    public ContractDetails Contract { get; private set; } = null!;
    public IReadOnlyList<string> Fields { get; private set; } = [];
    public IReadOnlyDictionary<string, string?> Current { get; private set; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string> Labels { get; private set; } = new Dictionary<string, string>();

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Contract = await contracts.GetAsync(ContractId, ct);
        // The workflow names the core fields a Variation may change; the configured fields it may change are added, limited to those this person can see.
        var custom = await variations.ChangeableCustomFieldsAsync(ContractId, ct);
        Fields = (await variations.ChangeableFieldsAsync(ContractId, ct)).Where(f => !f.StartsWith("cf.", StringComparison.Ordinal)).Concat(custom.Select(d => "cf." + d.Key)).ToList();
        Labels = custom.ToDictionary(d => "cf." + d.Key, d => d.Label);
        var shown = (await customFields.ForContractAsync(ContractId, ct: ct)).Fields.ToDictionary(e => "cf." + e.Definition.Key, e => (string?)e.Raw.FirstOrDefault());
        Current = new Dictionary<string, string?>(shown)
        {
            ["currencyCode"] = Contract.CurrencyCode, ["estimatedValue"] = Contract.EstimatedValue?.ToString(System.Globalization.CultureInfo.InvariantCulture), ["extensionType"] = Contract.ExtensionType,
            ["noticePeriodDays"] = Contract.Reminders?.NoticePeriodDays?.ToString(), ["criticalNotice"] = (Contract.Reminders?.CriticalNotice ?? false) ? "true" : "false",
            ["reviewDate"] = Contract.ReviewDate?.ToString("yyyy-MM-dd"), ["endDate"] = Contract.EndDate?.ToString("yyyy-MM-dd")
        };
    }

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var id = await variations.CreateAsync(ContractId, Form.ToInput(Request.Form), ct);
        Flash("The Variation is drafted. The agreement stays locked until it is applied, rejected or cancelled.");
        return Redirect("/Variations/Details/" + id);
    }, () => LoadAsync(ct));
}

public sealed class VariationForm
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Reason { get; set; } = "";
    public DateOnly? ProposedDate { get; set; }
    public bool LegalReviewRequired { get; set; }
    public decimal? FinancialImpactAmount { get; set; }
    public string? FinancialImpactCurrency { get; set; }
    public string? FinancialImpactNote { get; set; }

    /// <summary>The proposed changes: each field the person ticked, with the value they gave (blank clears it).</summary>
    public VariationInput ToInput(IFormCollection form)
    {
        var changes = new Dictionary<string, string?>();
        foreach (var key in form.Keys.Where(k => k.StartsWith("Change_", StringComparison.Ordinal)))
        {
            var field = key["Change_".Length..];
            var value = form["Value_" + field].ToString();
            changes[field] = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        return new VariationInput(Title, Description, Reason, ProposedDate, LegalReviewRequired, FinancialImpactAmount, FinancialImpactCurrency, FinancialImpactNote, changes);
    }
}
