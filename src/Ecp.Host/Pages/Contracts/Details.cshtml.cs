using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Notifications;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class DetailsModel(ISecuredContractService contracts, ISecuredContractDocuments documents, IOrganisationStructureService organisations, IAccessService access, IStatusLabels labels,
    IVariationService variations, IRecordUnlockService unlocks, IReminderSettings reminders, INotificationInbox inbox, ILegalReviewService legal, IContractCustomFields customFields, IContractChangeHistory changes) : EcpPageModel
{
    public ContractDetails Contract { get; private set; } = null!;
    public string OrganisationName { get; private set; } = "";
    public string? BusinessUnitName { get; private set; }
    public string? SiteName { get; private set; }
    public string? Value { get; private set; }
    public string? Start { get; private set; }
    public string? End { get; private set; }
    public string? Registered { get; private set; }
    public string Created { get; private set; } = "";
    public IReadOnlyList<ContractDocumentItem> Documents { get; private set; } = [];
    public bool CanSeeMigration { get; private set; }
    public string? SourceExtracted { get; private set; }
    public string StatusDisplay { get; private set; } = "";
    public IReadOnlyList<VariationListItem> VariationList { get; private set; } = [];
    public UnlockInfo? OpenUnlock { get; private set; }
    public bool CanUnlock { get; private set; }
    public ReminderStatusInfo Reminder { get; private set; } = null!;
    public LegalReviewDetail? Review { get; private set; }
    public CustomFieldForm? Fields { get; private set; }
    public IReadOnlyList<ContractChange> Changes { get; private set; } = [];
    public string? LegalEntityName { get; private set; }
    [BindProperty] public int? LeadDays { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        Contract = await contracts.GetAsync(id, ct);
        var fmt = await OrgLocale.ForAsync(organisations, Contract.OrganisationId, ct);
        var structure = await organisations.GetAsync(Contract.OrganisationId, ct);
        OrganisationName = (await organisations.GetOrganisationAsync(Contract.OrganisationId, ct))?.Name ?? "";
        BusinessUnitName = structure.BusinessUnits.FirstOrDefault(b => b.Id == Contract.BusinessUnitId)?.Name;
        SiteName = structure.Sites.FirstOrDefault(s => s.Id == Contract.SiteId)?.Name;
        LegalEntityName = structure.LegalEntities.FirstOrDefault(l => l.Id == Contract.Classifiers?.LegalEntityId)?.Name;
        Value = Contract is { EstimatedValue: { } v } ? fmt.Money(v, Contract.CurrencyCode) : null;
        Start = Contract.StartDate is { } s ? fmt.Date(s) : null;
        End = Contract.EndDate is { } e ? fmt.Date(e) : null;
        Registered = Contract.RegisteredDateUtc is { } r ? fmt.DateTime(r) : null;
        Created = fmt.DateTime(Contract.CreatedUtc);
        Documents = await documents.ListAsync(id, ct);
        SourceExtracted = Contract.Provenance?.SourceExtractedUtc is { } x ? fmt.DateTime(x) : null;
        CanSeeMigration = await access.HoldsAnywhereAsync("migration.stage", ct);
        StatusDisplay = (await labels.AllAsync(ct)).GetValueOrDefault(Contract.Status, Contract.Status);
        VariationList = await variations.ListForContractAsync(id, ct);
        OpenUnlock = Contract.RecordLocked ? await unlocks.CurrentAsync(id, ct) : null;
        CanUnlock = Contract.RecordLocked && await access.HoldsAnywhereAsync("contracts.unlock_emergency", ct);
        Reminder = await reminders.StatusAsync(id, ct);
        LeadDays = Reminder.LeadDays;
        Review = await legal.FindForSubjectAsync(Ecp.Lifecycle.Domain.SubjectKinds.Contract, id, ct);
        Fields = await customFields.ForContractAsync(id, null, null, ct);
        Changes = await changes.ListAsync(id, 25, ct);
        return Page();
    });

    public Task<IActionResult> OnPostLeadAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await reminders.SetLeadDaysAsync(id, LeadDays, ct);
        Flash("The reminder lead time is saved.");
        return RedirectToPage(new { id });
    }, async () => { await OnGetAsync(id, ct); });

    public Task<IActionResult> OnPostAcknowledgeAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await inbox.AcknowledgeContractAsync(id, ct);
        Flash("Thank you. The reminder is acknowledged.");
        return RedirectToPage(new { id });
    }, async () => { await OnGetAsync(id, ct); });
}
