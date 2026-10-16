using Ecp.Lifecycle;
using Ecp.Lifecycle.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Legal;

[Authorize(Policy = "perm:contracts.read")]
public sealed class ReviewModel(ILegalReviewService legal) : EcpPageModel
{
    public LegalReviewDetail Review { get; private set; } = null!;
    public IReadOnlyList<LegalReviewer> Reviewers { get; private set; } = [];

    [BindProperty] public string? Text { get; set; }
    [BindProperty] public Guid? ParentId { get; set; }
    [BindProperty] public Guid? ReviewerId { get; set; }
    [BindProperty] public string? Classification { get; set; }
    [BindProperty] public string? Title { get; set; }
    [BindProperty] public LegalAttachmentKind Kind { get; set; }
    [BindProperty] public Guid? ExistingDocumentId { get; set; }

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Review = await legal.GetAsync(id, ct);
        Reviewers = Review.Actions.CanAssign ? await legal.ReviewersAsync(Review.OrganisationId, ct) : [];
    }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    private Task<IActionResult> Act(Guid id, CancellationToken ct, Func<Task> action, string flash) => RunAsync(async () => { await action(); Flash(flash); return RedirectToPage(new { id }); }, () => LoadAsync(id, ct));

    public Task<IActionResult> OnPostBeginAsync(Guid id, CancellationToken ct) => Act(id, ct, () => legal.BeginAsync(id, ct), "The review has begun.");
    public Task<IActionResult> OnPostAssignAsync(Guid id, CancellationToken ct) => Act(id, ct, () => legal.AssignAsync(id, ReviewerId ?? Guid.Empty, ct), "The review is assigned.");
    public Task<IActionResult> OnPostCommentAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => await legal.CommentAsync(new AddLegalCommentRequest(id, Text ?? "", ParentId), ct), "Your comment is recorded.");
    public Task<IActionResult> OnPostReturnAsync(Guid id, CancellationToken ct) => Act(id, ct, () => legal.ReturnForReworkAsync(id, Text ?? "", ct), "Returned for rework. The round is closed and kept; the owner will respond and resubmit.");
    public Task<IActionResult> OnPostClearAsync(Guid id, CancellationToken ct) => Act(id, ct, () => legal.ClearAsync(id, Text, ct), "Cleared by Legal. This is not register authorisation.");

    public Task<IActionResult> OnPostResubmitAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        var detail = await legal.GetAsync(id, ct);
        await legal.SubmitAsync(new SubmitLegalRequest(detail.SubjectKind, detail.SubjectId, Text), ct);
    }, "Resubmitted to Legal for the next round.");

    public Task<IActionResult> OnPostRecallAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        var detail = await legal.GetAsync(id, ct);
        await legal.RecallAsync(detail.SubjectKind, detail.SubjectId, Text ?? "", ct);
    }, "Recalled from Legal. The round is withdrawn and kept.");

    public Task<IActionResult> OnPostAttachAsync(Guid id, IFormFile? file, CancellationToken ct) => Act(id, ct, async () =>
    {
        if (file is null || file.Length == 0) throw new Ecp.SharedKernel.DomainException("file_required", "Choose a file to attach.");
        await using var stream = file.OpenReadStream();
        await legal.AttachAsync(new AddLegalAttachmentRequest(id, Kind, Path.GetFileName(file.FileName), file.ContentType, stream, string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(file.FileName) : Title!,
            ExistingDocumentId, null, string.IsNullOrWhiteSpace(Classification) ? null : Classification), ct);
    }, "The document is attached to this round.");
}
