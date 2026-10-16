using Ecp.Contracts;
using Ecp.Lifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class LifecycleModel(ILifecycleService lifecycle, IVariationService variations, ILegalReviewService legal, IExecutionService execution, IRegisterAuthorisationService approvals,
    ISecuredContractService contracts, IRenewalService renewals, EcpHostOptions host) : StepsPageModel(lifecycle, variations, legal, execution, approvals)
{
    protected override string SubjectKind => Ecp.Lifecycle.Domain.SubjectKinds.Contract;
    public ContractDetails Contract { get; private set; } = null!;
    public LifecycleState State { get; private set; } = null!;
    public IReadOnlyList<HistoryItem> History { get; private set; } = [];
    public IReadOnlyList<VariationListItem> VariationList { get; private set; } = [];
    public IReadOnlyList<RenewalInfo> Renewals { get; private set; } = [];
    [BindProperty] public string? Note { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    protected override async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Contract = await contracts.GetAsync(id, ct);
        State = await Lifecycle.StateAsync(id, ct);
        History = await Lifecycle.HistoryAsync(id, ct);
        VariationList = await Variations.ListForContractAsync(id, ct);
        Renewals = await renewals.ListForContractAsync(id, ct);
        Steps = await BuildStepsAsync(id, State.StatusDisplay, await Lifecycle.AvailableAsync(id, ct), host.Mode != Ecp.SharedKernel.EcpMode.Production, ct);
        ClientRequestId = Guid.NewGuid().ToString("N");
    }

    public Task<IActionResult> OnPostStartRenewalAsync(Guid id, CancellationToken ct) => Step(id, ct, async () => await renewals.StartAsync(id, Note, ct), "The renewal has started. A successor draft was created; this agreement is unchanged.");
    public Task<IActionResult> OnPostCompleteRenewalAsync(Guid id, CancellationToken ct) => Step(id, ct, () => renewals.CompleteAsync(id, Note, ct), "The renewal is complete. This agreement is back to Effective, unchanged. End it separately when the time comes.");
    public Task<IActionResult> OnPostCancelRenewalAsync(Guid id, CancellationToken ct) => Step(id, ct, () => renewals.CancelAsync(id, Note ?? "", ct), "The renewal is cancelled and this agreement is unchanged.");
}
