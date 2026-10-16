using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages;

/// <summary>Everything the shared steps panel shows for a contract or a Variation.</summary>
public sealed record StepsView(string SubjectKind, Guid SubjectId, string StatusDisplay, IReadOnlyList<AvailableTransition> Steps, ExecutionInfo? Execution, IReadOnlyList<ProviderInfo> Providers,
    ApprovalDetail? Approval, LegalReviewDetail? Legal, bool IsMockEnvironment);

/// <summary>
/// The steps a contract and a Variation share: take a plain step, submit to Legal, send for signature, attach the executed copy, submit for register authorisation. Each handler passes the request to the
/// service that owns the step; the page decides nothing, so a crafted post gets the same answer as a button would.
/// </summary>
public abstract class StepsPageModel(ILifecycleService lifecycle, IVariationService variations, ILegalReviewService legal, IExecutionService execution, IRegisterAuthorisationService approvals) : EcpPageModel
{
    protected ILifecycleService Lifecycle { get; } = lifecycle;
    protected IVariationService Variations { get; } = variations;
    protected ILegalReviewService Legal { get; } = legal;
    protected IExecutionService Execution { get; } = execution;
    protected IRegisterAuthorisationService Approvals { get; } = approvals;

    protected abstract string SubjectKind { get; }
    public StepsView Steps { get; protected set; } = null!;
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public string? ClientRequestId { get; set; }

    protected abstract Task LoadAsync(Guid id, CancellationToken ct);

    protected Task<IActionResult> Step(Guid id, CancellationToken ct, Func<Task> action, string flash) => RunAsync(async () => { await action(); Flash(flash); return RedirectToPage(new { id }); }, () => LoadAsync(id, ct));

    protected async Task<StepsView> BuildStepsAsync(Guid id, string statusDisplay, IReadOnlyList<AvailableTransition> steps, bool mock, CancellationToken ct)
    {
        var exec = await Execution.CurrentAsync(SubjectKind, id, ct);
        var approval = await Approvals.CurrentForSubjectAsync(SubjectKind, id, ct);
        var review = await Legal.FindForSubjectAsync(SubjectKind, id, ct);
        return new StepsView(SubjectKind, id, statusDisplay, steps, exec, Execution.Providers(), approval, review, mock);
    }

    public Task<IActionResult> OnPostStepAsync(Guid id, string code, CancellationToken ct) => Step(id, ct, async () =>
    {
        if (SubjectKind == SubjectKinds.Variation) await Variations.TransitionAsync(id, code, Comment, ct);
        else await Lifecycle.TransitionAsync(new TransitionRequest(id, code, Comment, Comment, null, ClientRequestId), ct);
    }, "Done.");

    public Task<IActionResult> OnPostLegalSubmitAsync(Guid id, CancellationToken ct) => Step(id, ct, async () => await Legal.SubmitAsync(new SubmitLegalRequest(SubjectKind, id, Comment, ClientRequestId), ct), "Submitted to Legal.");

    public Task<IActionResult> OnPostSendSignatureAsync(Guid id, string provider, CancellationToken ct) => Step(id, ct, async () => await Execution.SendForSignatureAsync(SubjectKind, id, provider, ClientRequestId, ct),
        "Sent for signature. The agreement is now out for signature; Legal clearance and signing are separate from register authorisation.");

    public Task<IActionResult> OnPostExecutedCopyAsync(Guid id, IFormFile? file, CancellationToken ct) => Step(id, ct, async () =>
    {
        if (file is null || file.Length == 0) throw new DomainException("file_required", "Choose the executed copy to upload.");
        await using var stream = file.OpenReadStream();
        await Execution.RecordExecutedCopyAsync(new RecordExecutedCopyRequest(SubjectKind, id, Path.GetFileName(file.FileName), file.ContentType, stream), ct);
    }, "The executed copy is attached. It is scanned like any upload and counts only once it has passed.");

    public Task<IActionResult> OnPostSimulateAsync(Guid id, ExecutionStatus status, CancellationToken ct) => Step(id, ct, async () => await Execution.SimulateEventAsync(SubjectKind, id, status, ct), "The mock signature service reported: " + status + ".");

    public Task<IActionResult> OnPostSubmitApprovalAsync(Guid id, CancellationToken ct) => Step(id, ct, async () => await Approvals.SubmitAsync(SubjectKind, id, Comment, ClientRequestId, ct), "Submitted for register authorisation.");
}
