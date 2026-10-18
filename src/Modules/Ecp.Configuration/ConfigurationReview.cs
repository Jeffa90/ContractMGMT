using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Configuration;

/// <summary>What the signed-in person may do with a draft, and why not when they may not. Used to label buttons honestly; every action still re-checks on the server.</summary>
public sealed record GovernanceInfo(bool SecondPersonRequired, ReviewState State, bool IsSubmittedAsIs, string? SubmittedBy, bool CanSubmit, bool CanPublish, bool CanDecide, bool CanWithdraw, string? Explanation);

/// <summary>The result of pressing Publish: the release when it was published, otherwise the draft now waiting for a reviewer.</summary>
public sealed record PublishOutcome(ReleaseDto? Release, DraftDto Draft, bool Submitted);

public sealed record RollbackOutcome(ReleaseDto? Release, DraftDto? Draft, bool AwaitingReview);

public sealed record ReviewItem(Guid DraftId, Guid OrganisationId, string Kind, int? BasedOnReleaseNumber, string CreatedBy, string LastEditedBy, string? SubmittedBy, DateTime? SubmittedUtc,
    string? SubmissionNote, ReviewState State, string? DecidedBy, DateTime? DecidedUtc, string? DecisionComment, int ErrorCount, int WarningCount, string ContentHash, string? SubmittedHash,
    string PublishPermission, bool CanDecide, bool IsMine, int? RollbackOfNumber, int ChangeCount);

public sealed record ReviewEventDto(string EventType, string Actor, DateTime OccurredUtc, string? Comment, string ContentHash, int? ReleaseNumber);

public sealed record ChangeLine(string Where, string Change, string? Before, string? After);

public sealed record ReviewDetail(DraftDto Draft, ReleaseDto? Current, IReadOnlyList<ChangeLine> Changes, bool ChangesTruncated, IReadOnlyList<ReviewEventDto> History, GovernanceInfo Governance,
    bool BaseIsStale, string PublishPermission, bool CanSeeTechnicalDetail);

internal sealed partial class ConfigurationService
{
    private static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal);
    /// <summary>Whoever made the content, put it up for review or last changed it cannot also be the person who decides on it.</summary>
    private static bool Contributed(ConfigurationDraft d, DraftReview? r, string actorLabel) => Same(d.LastEditedBy, actorLabel) || Same(r?.SubmittedBy, actorLabel) || Same(r?.CreatedBy, actorLabel);

    private async Task<bool> HoldsAsync(string permission, ConfigurationDraft d, CancellationToken ct)
        => (await access.CheckSilentAsync(permission, Describe(d.OrganisationId, d.Kind), ct)).Allowed;

    /// <summary>The checks that decide whether the caller may publish this draft right now. A refusal is audited in its own transaction so that it survives.</summary>
    private async Task RequirePublishableByAsync(ConfigurationDraft draft, DraftReview? review, string publisher, CancellationToken ct)
    {
        if (!draft.IsPublishable) throw new DomainException("not_validated", "The draft must be validated, and unchanged since validation, before it is published.");
        if (!options.RequireSecondPersonPublish) return;
        if (review is { State: ReviewState.Submitted } && review.SubmittedHash != draft.ContentHash)
            await RefuseAsync(draft, publisher, "submission_changed", "The content is no longer the content that was submitted for review. Submit it again so that a reviewer sees what will be published.", ct);
        if (review is null || !review.IsSubmittedAsIs(draft.ContentHash))
            await RefuseAsync(draft, publisher, "not_submitted", "In this installation a change is submitted for review and published by a second person. Submit it for review first.", ct);
        if (Contributed(draft, review, publisher))
            await RefuseAsync(draft, publisher, "second_person_required", "A second person must publish this configuration. The person who created, last edited or submitted it cannot publish it.", ct);
    }

    private async Task RefuseAsync(ConfigurationDraft draft, string actorLabel, string code, string message, CancellationToken ct)
    {
        try
        {
            await uow.ExecuteAsync(c =>
            {
                audit.Record(new AuditEntry("config.publish.refused", draft.Kind, draft.Id.ToString(), AuditOutcome.Denied, draft.OrganisationId == Guid.Empty ? null : draft.OrganisationId, Reason: message,
                    NewValuesJson: JsonSerializer.Serialize(new { code, actor = actorLabel, hash = draft.ContentHash })));
                return Task.CompletedTask;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the refusal itself is what the caller sees; a failure to audit it must not mask that */ }
        throw new DomainException(code, message);
    }

    private async Task<ConfigurationDraft> LoadForReviewAsync(Guid draftId, CancellationToken ct)
        => await db.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, ct) ?? throw new ResourceNotFoundException();

    public async Task<DraftDto> SubmitForReviewAsync(Guid draftId, string? comment, CancellationToken ct = default)
    {
        var draft = await LoadForReviewAsync(draftId, ct);
        await access.RequireAsync(DraftPermissionOf(draft.Kind), Describe(draft.OrganisationId, draft.Kind), ct);
        return await SubmitCoreAsync(draft, await EditorLabelAsync(ct), comment, ct);
    }

    private async Task<DraftDto> SubmitCoreAsync(ConfigurationDraft draft, string submitter, string? comment, CancellationToken ct)
    {
        DraftReview review = null!;
        await uow.ExecuteAsync(async c =>
        {
            review = await EnsureReviewAsync(draft, c);
            if (review.IsSubmittedAsIs(draft.ContentHash)) return;                       // already waiting for a reviewer with this exact content
            review.Submit(draft, submitter, comment, clock.UtcNow);
            db.ReviewEvents.Add(ConfigurationReviewEvent.Create(tenant.RequireTenantId(), draft, "Submitted", submitter, comment, clock.UtcNow));
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "Someone else changed this draft at the same time. Reload to see where it stands."); }
            audit.Record(new AuditEntry("config.draft.submitted", draft.Kind, draft.Id.ToString(), OrganisationId: draft.OrganisationId == Guid.Empty ? null : draft.OrganisationId, Reason: comment,
                NewValuesJson: JsonSerializer.Serialize(new { hash = draft.ContentHash, submittedBy = submitter, rollbackOf = review.RollbackOfNumber })));
        }, ct);
        return ToDto(draft, review);
    }

    public async Task<DraftDto> WithdrawSubmissionAsync(Guid draftId, string? reason, CancellationToken ct = default)
    {
        var draft = await LoadForReviewAsync(draftId, ct);
        await access.RequireAsync(DraftPermissionOf(draft.Kind), Describe(draft.OrganisationId, draft.Kind), ct);
        var actorLabel = await EditorLabelAsync(ct);
        var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == draftId, ct);
        if (review is not { State: ReviewState.Submitted }) throw new DomainException("not_submitted", "This draft is not waiting for review.");
        if (!Contributed(draft, review, actorLabel)) throw new DomainException("not_yours", "Only the person who made or submitted this change can withdraw it. A reviewer who does not want it should reject it.");
        await uow.ExecuteAsync(async c =>
        {
            db.ReviewEvents.Add(ConfigurationReviewEvent.Create(tenant.RequireTenantId(), draft, "Withdrawn", actorLabel, reason, clock.UtcNow));
            review.Withdraw(clock.UtcNow);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "Someone else decided on this draft at the same time. Reload to see what happened."); }
            audit.Record(new AuditEntry("config.draft.withdrawn", draft.Kind, draft.Id.ToString(), OrganisationId: draft.OrganisationId == Guid.Empty ? null : draft.OrganisationId, Reason: reason));
        }, ct);
        return ToDto(draft, review);
    }

    public Task<DraftDto> RejectAsync(Guid draftId, string comment, CancellationToken ct = default) => DecideAsync(draftId, ReviewState.Rejected, comment, ct);
    public Task<DraftDto> ReturnForReworkAsync(Guid draftId, string comment, CancellationToken ct = default) => DecideAsync(draftId, ReviewState.ReturnedForRework, comment, ct);

    private async Task<DraftDto> DecideAsync(Guid draftId, ReviewState decision, string comment, CancellationToken ct)
    {
        var draft = await LoadForReviewAsync(draftId, ct);
        await access.RequireAsync(KindOf(draft.Kind).PublishPermission, Describe(draft.OrganisationId, draft.Kind), ct);
        var why = Guard.NotBlank(comment, "Comment", 2000);
        var reviewer = await EditorLabelAsync(ct);
        var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == draftId, ct);
        if (review is null || !review.IsSubmittedAsIs(draft.ContentHash)) throw new DomainException("not_submitted", "This draft is not waiting for review, or its content has changed since it was submitted.");
        if (options.RequireSecondPersonPublish && Contributed(draft, review, reviewer))
            await RefuseAsync(draft, reviewer, "second_person_required", "A second person must review this. The person who created, last edited or submitted it cannot decide on it.", ct);
        var eventType = decision == ReviewState.Rejected ? "Rejected" : "ReturnedForRework";
        await uow.ExecuteAsync(async c =>
        {
            db.ReviewEvents.Add(ConfigurationReviewEvent.Create(tenant.RequireTenantId(), draft, eventType, reviewer, why, clock.UtcNow));
            review.Decide(decision, reviewer, why, clock.UtcNow);                 // the reviewer does not become the editor
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "Someone else decided on this draft at the same time. Reload to see what happened."); }
            audit.Record(new AuditEntry(decision == ReviewState.Rejected ? "config.draft.rejected" : "config.draft.returned", draft.Kind, draft.Id.ToString(),
                OrganisationId: draft.OrganisationId == Guid.Empty ? null : draft.OrganisationId, Reason: why, NewValuesJson: JsonSerializer.Serialize(new { hash = draft.ContentHash, reviewer })));
        }, ct);
        return ToDto(draft, review);
    }

    public async Task<PublishOutcome> SubmitOrPublishAsync(Guid draftId, string note, CancellationToken ct = default)
    {
        var draft = await LoadForReviewAsync(draftId, ct);
        var actorLabel = await EditorLabelAsync(ct);
        if (!options.RequireSecondPersonPublish) return await PublishedOutcomeAsync(draftId, note, ct);
        var review = await ReviewOfAsync(draftId, ct);
        if (review is not null && review.IsSubmittedAsIs(draft.ContentHash) && !Contributed(draft, review, actorLabel) && await HoldsAsync(KindOf(draft.Kind).PublishPermission, draft, ct))
            return await PublishedOutcomeAsync(draftId, note, ct);
        var submitted = await SubmitForReviewAsync(draftId, note, ct);
        return new PublishOutcome(null, submitted, true);
    }

    private async Task<PublishOutcome> PublishedOutcomeAsync(Guid draftId, string note, CancellationToken ct)
    {
        var release = await PublishAsync(draftId, note, ct);
        var d = await db.Drafts.AsNoTracking().FirstAsync(x => x.Id == draftId, ct);
        return new PublishOutcome(release, await DtoAsync(d, ct), false);
    }

    public async Task<GovernanceInfo> GovernanceAsync(Guid draftId, CancellationToken ct = default)
    {
        var d = await LoadForReviewAsync(draftId, ct);
        var kind = KindOf(d.Kind);
        var mayDraft = await HoldsAsync(kind.DraftPermission, d, ct);
        var mayPublish = await HoldsAsync(kind.PublishPermission, d, ct);
        if (!mayDraft && !mayPublish) throw new ResourceNotFoundException();
        var review = await ReviewOfAsync(draftId, ct);
        var me = await EditorLabelAsync(ct);
        var second = options.RequireSecondPersonPublish;
        var asIs = review?.IsSubmittedAsIs(d.ContentHash) ?? false;
        var contributed = Contributed(d, review, me);
        var canSubmit = mayDraft && d.IsPublishable && !asIs;
        var canPublish = mayPublish && d.IsPublishable && (!second || (asIs && !contributed));
        var canDecide = mayPublish && asIs && (!second || !contributed);
        var canWithdraw = mayDraft && review is { State: ReviewState.Submitted } && contributed;
        string? why = null;
        if (!d.IsPublishable) why = "The draft has to be validated, with no errors and no changes since, before it can be submitted or published.";
        else if (second && !asIs) why = "Submit it for review. A second person publishes it.";
        else if (second && contributed) why = "You made or submitted this change, so someone else with permission to publish has to review and publish it.";
        else if (!mayPublish) why = "You do not have permission to publish this kind of configuration.";
        return new GovernanceInfo(second, review?.State ?? ReviewState.None, asIs, review?.SubmittedBy, canSubmit, canPublish, canDecide, canWithdraw, why);
    }

    public async Task<IReadOnlyList<ReviewItem>> ReviewQueueAsync(CancellationToken ct = default)
    {
        var me = await EditorLabelAsync(ct);
        var inReview = await db.Reviews.AsNoTracking().Where(r => r.State != ReviewState.None).ToListAsync(ct);
        var ids = inReview.Select(r => r.Id).ToList();
        var drafts = (await db.Drafts.AsNoTracking().Where(d => ids.Contains(d.Id) && d.Status != DraftStatus.Published && d.Status != DraftStatus.Discarded).ToListAsync(ct)).ToDictionary(d => d.Id);
        var items = new List<ReviewItem>();
        foreach (var r in inReview.OrderBy(r => r.SubmittedUtc ?? r.DecidedUtc))
        {
            if (!drafts.TryGetValue(r.Id, out var d)) continue;
            var kind = kinds.FirstOrDefault(k => k.Kind == d.Kind);
            if (kind is null) continue;
            var mayPublish = await HoldsAsync(kind.PublishPermission, d, ct);
            var mayDraft = mayPublish || await HoldsAsync(kind.DraftPermission, d, ct);
            if (!mayDraft) continue;
            var report = d.ValidationJson is null ? null : JsonSerializer.Deserialize<ValidationReport>(d.ValidationJson, Json);
            var latest = await db.Releases.AsNoTracking().Where(x => x.OrganisationId == d.OrganisationId && x.Kind == d.Kind).OrderByDescending(x => x.Number).Select(x => x.ContentJson).FirstOrDefaultAsync(ct);
            var changes = ConfigurationChangeSummary.Describe(latest, d.ContentJson);
            var contributed = Contributed(d, r, me);
            items.Add(new ReviewItem(d.Id, d.OrganisationId, d.Kind, d.BasedOnReleaseNumber, r.CreatedBy, d.LastEditedBy, r.SubmittedBy, r.SubmittedUtc, r.SubmissionNote, r.State, r.DecidedBy, r.DecidedUtc,
                r.DecisionComment, report?.Messages.Count(m => m.Severity == ValidationSeverity.Error) ?? 0, report?.Messages.Count(m => m.Severity == ValidationSeverity.Warning) ?? 0, d.ContentHash, r.SubmittedHash,
                kind.PublishPermission, mayPublish && r.IsSubmittedAsIs(d.ContentHash) && (!options.RequireSecondPersonPublish || !contributed), contributed, r.RollbackOfNumber, changes.Lines.Count));
        }
        return items;
    }

    public async Task<ReviewDetail> GetReviewAsync(Guid draftId, CancellationToken ct = default)
    {
        var d = await LoadForReviewAsync(draftId, ct);
        var kind = KindOf(d.Kind);
        var governance = await GovernanceAsync(draftId, ct);       // also proves the caller may see this draft at all
        var current = await db.Releases.AsNoTracking().Where(r => r.OrganisationId == d.OrganisationId && r.Kind == d.Kind).OrderByDescending(r => r.Number).FirstOrDefaultAsync(ct);
        var changes = ConfigurationChangeSummary.Describe(current?.ContentJson, d.ContentJson);
        var history = (await db.ReviewEvents.AsNoTracking().Where(e => e.DraftId == draftId).ToListAsync(ct)).OrderBy(e => e.OccurredUtc).ThenBy(e => e.Id).ToList();   // ids are time-ordered, so events recorded in the same instant keep their order
        var technical = (await access.CheckSilentAsync(kind.HistoryPermission, Describe(d.OrganisationId, d.Kind), ct)).Allowed;
        return new ReviewDetail(await DtoAsync(d, ct), current is null ? null : ToDto(current), changes.Lines, changes.Truncated,
            history.Select(e => new ReviewEventDto(e.EventType, e.Actor, e.OccurredUtc, e.Comment, e.ContentHash, e.ReleaseNumber)).ToList(), governance,
            d.BasedOnReleaseNumber != current?.Number, kind.PublishPermission, technical);
    }

    public async Task<RollbackOutcome> RequestRollbackAsync(string kind, Guid organisationId, int toReleaseNumber, string reason, CancellationToken ct = default)
    {
        if (maintenanceGate is not null) await maintenanceGate.EnsureWritableAsync("Rolling back configuration", ct);
        await access.RequireAsync(KindOf(kind).RollbackPermission, Describe(organisationId, kind), ct);
        if (options.RequireRecentSignInForRollback && currentUser.UserId is not null) reauth.Require(clock.UtcNow, options.RecentSignInWindow);
        var why = Guard.NotBlank(reason, "Reason", 1000);
        var target = await db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.OrganisationId == organisationId && r.Kind == kind && r.Number == toReleaseNumber, ct)
            ?? throw new DomainException("release_not_found", "That release was not found.");
        var editor = await EditorLabelAsync(ct);
        var created = await CreateDraftCoreAsync(kind, organisationId, target.ContentJson, editor, ct);
        var draft = await db.Drafts.FirstAsync(d => d.Id == created.Id, ct);
        await uow.ExecuteAsync(async c =>
        {
            var review = await EnsureReviewAsync(draft, c);
            review.MarkRollback(toReleaseNumber, why, clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("config.rollback.requested", kind, draft.Id.ToString(), OrganisationId: organisationId == Guid.Empty ? null : organisationId, Reason: why,
                NewValuesJson: JsonSerializer.Serialize(new { sourceVersion = toReleaseNumber, requestedBy = editor, secondPerson = options.RequireSecondPersonPublish })));
        }, ct);
        var checkedDraft = await ValidateCoreAsync(draft, ct);   // the earlier content is re-validated against today's rules before it takes effect again
        if (!checkedDraft.IsPublishable)
        {
            await uow.ExecuteAsync(async c => { draft.Discard(clock.UtcNow); await db.SaveChangesAsync(c); }, ct);
            throw new DomainException("rollback_invalid", "That earlier release no longer passes today's checks, so it cannot be put back: "
                + string.Join("; ", checkedDraft.Validation!.Messages.Where(m => m.Severity == ValidationSeverity.Error).Select(m => m.Message)));
        }
        if (options.RequireSecondPersonPublish)
            return new RollbackOutcome(null, await SubmitCoreAsync(draft, editor, $"Rollback to release {toReleaseNumber}: {why}", ct), true);
        var review2 = await db.Reviews.FirstAsync(r => r.Id == draft.Id, ct);
        return new RollbackOutcome(await PublishCoreAsync(draft, review2, toReleaseNumber, NoteFor(review2, ""), editor, ct, "SinglePerson"), null, false);
    }
}

/// <summary>
/// Describes, in words a business person can read, what a draft would change compared with what is in force. It compares the two documents by name and by identity (a state, a row, a step), never by position
/// when the entries carry a code, so moving an entry does not read as deleting and re-adding it.
/// </summary>
public static class ConfigurationChangeSummary
{
    public const int MaxLines = 300;
    private static readonly string[] IdentityProperties = ["code", "key", "event", "field", "name", "id", "currency", "approverGroup", "locale", "type"];

    public sealed record Summary(IReadOnlyList<ChangeLine> Lines, bool Truncated);

    public static Summary Describe(string? beforeJson, string afterJson)
    {
        var lines = new List<ChangeLine>();
        try
        {
            using var after = JsonDocument.Parse(afterJson);
            using var before = JsonDocument.Parse(string.IsNullOrWhiteSpace(beforeJson) ? "{}" : beforeJson);
            Compare(before.RootElement, after.RootElement, [], lines);
        }
        catch (JsonException) { lines.Add(new ChangeLine("Document", "Changed", null, "(the document could not be read)")); }
        var truncated = lines.Count > MaxLines;
        return new Summary(truncated ? lines.Take(MaxLines).ToList() : lines, truncated);
    }

    private static void Compare(JsonElement a, JsonElement b, List<string> path, List<ChangeLine> lines)
    {
        if (lines.Count > MaxLines) return;
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            var names = a.EnumerateObject().Select(p => p.Name).Concat(b.EnumerateObject().Select(p => p.Name)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var n in names)
            {
                var inA = a.TryGetProperty(n, out var va); var inB = b.TryGetProperty(n, out var vb);
                var next = new List<string>(path) { Humanise(n) };
                if (inA && inB) Compare(va, vb, next, lines);
                else if (inB) lines.Add(new ChangeLine(Where(next), "Added", null, Show(vb)));
                else lines.Add(new ChangeLine(Where(next), "Removed", Show(va), null));
            }
            return;
        }
        if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            if (b.EnumerateArray().Concat(a.EnumerateArray()).All(e => e.ValueKind == JsonValueKind.Object) && (a.GetArrayLength() + b.GetArrayLength()) > 0)
            {
                var prop = IdentityProperties.FirstOrDefault(p => a.EnumerateArray().Concat(b.EnumerateArray()).All(e => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String));
                if (prop is not null)
                {
                    var ma = a.EnumerateArray().ToDictionary(e => e.GetProperty(prop).GetString()!, StringComparer.OrdinalIgnoreCase);
                    var mb = b.EnumerateArray().GroupBy(e => e.GetProperty(prop).GetString()!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    var last = path.Count == 0 ? "Items" : path[^1];
                    foreach (var (id, eb) in mb)
                    {
                        var label = new List<string>(path.Take(path.Count - 1)) { $"{last} “{id}”" };
                        if (ma.TryGetValue(id, out var ea)) Compare(ea, eb, label, lines);
                        else lines.Add(new ChangeLine(Where(label), "Added", null, Show(eb)));
                    }
                    foreach (var (id, ea) in ma.Where(k => !mb.ContainsKey(k.Key)))
                        lines.Add(new ChangeLine(Where(new List<string>(path.Take(path.Count - 1)) { $"{last} “{id}”" }), "Removed", Show(ea), null));
                    return;
                }
            }
            if (a.EnumerateArray().Concat(b.EnumerateArray()).All(e => e.ValueKind != JsonValueKind.Object && e.ValueKind != JsonValueKind.Array))
            {
                var sa = a.EnumerateArray().Select(Show).ToList(); var sb = b.EnumerateArray().Select(Show).ToList();
                var added = sb.Except(sa).ToList(); var removed = sa.Except(sb).ToList();
                if (added.Count > 0 || removed.Count > 0) lines.Add(new ChangeLine(Where(path), "Changed", string.Join(", ", sa), string.Join(", ", sb)));
                else if (!sa.SequenceEqual(sb)) lines.Add(new ChangeLine(Where(path), "Reordered", string.Join(", ", sa), string.Join(", ", sb)));
                return;
            }
            var n = Math.Max(a.GetArrayLength(), b.GetArrayLength());
            var ea2 = a.EnumerateArray().ToList(); var eb2 = b.EnumerateArray().ToList();
            for (var i = 0; i < n; i++)
            {
                var next = new List<string>(path.Take(Math.Max(0, path.Count - 1))) { $"{(path.Count == 0 ? "Item" : path[^1])} {i + 1}" };
                if (i < ea2.Count && i < eb2.Count) Compare(ea2[i], eb2[i], next, lines);
                else if (i < eb2.Count) lines.Add(new ChangeLine(Where(next), "Added", null, Show(eb2[i])));
                else lines.Add(new ChangeLine(Where(next), "Removed", Show(ea2[i]), null));
            }
            return;
        }
        if (a.ValueKind == b.ValueKind && a.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) && a.GetRawText() == b.GetRawText()) return;
        lines.Add(new ChangeLine(Where(path), "Changed", Show(a), Show(b)));
    }

    private static string Where(List<string> path) => path.Count == 0 ? "Document" : string.Join(" › ", path);

    private static string Humanise(string camel)
    {
        if (string.IsNullOrEmpty(camel)) return camel;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < camel.Length; i++)
        {
            var ch = camel[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(camel[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string Show(JsonElement e)
    {
        var text = e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.Null => "(none)",
            JsonValueKind.Object => $"({e.EnumerateObject().Count()} settings)",
            JsonValueKind.Array => $"({e.GetArrayLength()} entries)",
            _ => e.GetRawText()
        };
        return text.Length > 160 ? text[..157] + "…" : text;
    }
}
