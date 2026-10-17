using Ecp.Contracts;
using Ecp.Lifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Variations;

[Authorize(Policy = "perm:contracts.read")]
public sealed class DetailsModel(ILifecycleService lifecycle, IVariationService variations, ILegalReviewService legal, ISignatureRequestService execution, IRegisterAuthorisationService approvals, EcpHostOptions host)
    : StepsPageModel(lifecycle, variations, legal, execution, approvals)
{
    protected override string SubjectKind => Ecp.Lifecycle.Domain.SubjectKinds.Variation;
    public VariationDetail Variation { get; private set; } = null!;
    public bool Editing { get; private set; }
    [BindProperty] public VariationForm Form { get; set; } = new();
    [BindProperty] public string? DocumentRole { get; set; }
    [BindProperty] public string? DocumentTitle { get; set; }
    public IReadOnlyDictionary<string, string?> Current { get; private set; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string?> Proposed { get; private set; } = new Dictionary<string, string?>();

    public Task<IActionResult> OnGetAsync(Guid id, bool edit, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); Editing = edit && Variation.CanEdit; if (Editing) FillForm(); return Page(); });

    protected override async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Variation = await Variations.GetAsync(id, ct);
        Steps = await BuildStepsAsync(id, Variation.Summary.StatusDisplay, Variation.Steps, host.Mode != Ecp.SharedKernel.EcpMode.Production, ct);
        ClientRequestId = Guid.NewGuid().ToString("N");
        Current = Variation.Comparison.ToDictionary(c => c.Field, c => c.Current);
        Proposed = Variation.Comparison.ToDictionary(c => c.Field, c => c.After);
    }

    private void FillForm()
    {
        var s = Variation.Summary;
        Form = new VariationForm { Title = s.Title, Description = Variation.Description, Reason = Variation.Reason, ProposedDate = s.ProposedDate, LegalReviewRequired = Variation.LegalReviewRequired,
            FinancialImpactAmount = s.ImpactAmount, FinancialImpactCurrency = s.ImpactCurrency, FinancialImpactNote = Variation.ImpactNote };
    }

    public Task<IActionResult> OnPostSaveAsync(Guid id, CancellationToken ct) => Step(id, ct, () => Variations.UpdateAsync(id, Form.ToInput(Request.Form), ct), "The Variation is saved.");

    public Task<IActionResult> OnPostDocumentAsync(Guid id, IFormFile? file, CancellationToken ct) => Step(id, ct, async () =>
    {
        if (file is null || file.Length == 0) throw new Ecp.SharedKernel.DomainException("file_required", "Choose a file to add.");
        await using var stream = file.OpenReadStream();
        await Variations.AddDocumentAsync(new AddVariationDocumentRequest(id, DocumentRole ?? "Supporting", string.IsNullOrWhiteSpace(DocumentTitle) ? Path.GetFileNameWithoutExtension(file.FileName) : DocumentTitle!,
            Path.GetFileName(file.FileName), file.ContentType, stream), ct);
    }, "The document is added to the Variation.");
}
