using Ecp.DataMigration;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

/// <summary>Masked-data declarations: what an organisation says about data it has masked itself before loading it into a Test environment. The platform records and enforces; it does not mask, inspect or vouch for the data.</summary>
public sealed class MaskedDataModel(IMaskedDataDeclarations declarations, IOrganisationStructureService organisations, IMigrationProjects projects, Microsoft.Extensions.Options.IOptions<MigrationOptions> options) : MigrationPageModel
{
    [BindProperty(SupportsGet = true)] public Guid OrganisationId { get; set; }
    public IReadOnlyList<MaskedDeclarationDto> Items { get; private set; } = [];
    public IReadOnlyList<OrganisationOption> Organisations { get; private set; } = [];
    public bool Available => options.Value.Mode != Ecp.SharedKernel.EcpMode.Production;

    [BindProperty] public string DatasetOwner { get; set; } = "";
    [BindProperty] public string SourceSystem { get; set; } = "";
    [BindProperty] public string MaskingMethod { get; set; } = "";
    [BindProperty] public string FieldsMasked { get; set; } = "";
    [BindProperty] public bool AttachmentsIncluded { get; set; }
    [BindProperty] public bool PersonalDataIncluded { get; set; }
    [BindProperty] public bool ConfidentialDataIncluded { get; set; }
    [BindProperty] public string ApprovalReference { get; set; } = "";
    [BindProperty] public string ApprovedBy { get; set; } = "";
    [BindProperty] public DateOnly ApprovedOn { get; set; }
    [BindProperty] public DateOnly ReviewBy { get; set; }
    [BindProperty] public string? PermittedUsers { get; set; }
    [BindProperty] public Guid DeclarationId { get; set; }
    [BindProperty] public string? Reason { get; set; }

    private async Task LoadAsync(CancellationToken ct)
    {
        Organisations = await projects.ListOrganisationsAsync(ct);
        if (OrganisationId == Guid.Empty || Organisations.All(o => o.Id != OrganisationId)) OrganisationId = Organisations.FirstOrDefault()?.Id ?? Guid.Empty;   // only an organisation the person may run migrations for
        if (OrganisationId != Guid.Empty && Available) { await LoadFormatterAsync(organisations, OrganisationId, ct); Items = await declarations.ListAsync(OrganisationId, ct); }
        if (ApprovedOn == default) ApprovedOn = DateOnly.FromDateTime(DateTime.UtcNow);
        if (ReviewBy == default) ReviewBy = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
    }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostDeclareAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var approved = DateTime.SpecifyKind(ApprovedOn.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var review = DateTime.SpecifyKind(ReviewBy.ToDateTime(new TimeOnly(23, 59)), DateTimeKind.Utc);
        await declarations.DeclareAsync(new MaskedDeclarationInput(OrganisationId, DatasetOwner, SourceSystem, MaskingMethod, FieldsMasked, AttachmentsIncluded, PersonalDataIncluded, ConfidentialDataIncluded,
            ApprovalReference, ApprovedBy, approved, review, PermittedUsers), ct);
        Flash("The declaration was recorded. It does not mean the data is safe: that is your organisation's responsibility. Link it to a batch to load the data.");
        return RedirectToPage(new { OrganisationId });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostRevokeAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await declarations.RevokeAsync(DeclarationId, Reason ?? "", ct);
        Flash("The declaration was revoked. Batches that depend on it can no longer be committed.");
        return RedirectToPage(new { OrganisationId });
    }, () => LoadAsync(ct));
}
