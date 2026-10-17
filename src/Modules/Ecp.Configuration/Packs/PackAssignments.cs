using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Packs;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Configuration;

public sealed record PackKindOutcome(string Kind, string Scope, string Outcome, bool NeedsAttention, string? Detail);
public sealed record PackAssignmentResult(Guid OrganisationId, string PackKey, string Version, IReadOnlyList<PackKindOutcome> Kinds, IReadOnlyList<string> NextSteps);
public sealed record PackAssignmentPreviewItem(string Kind, string Scope, bool HasPublishedRelease, bool HasOpenDraft, string Effect);
public sealed record PackAssignmentPreview(Guid OrganisationId, string OrganisationName, string? CurrentPackKey, PackCatalogueEntry Entry, IReadOnlyList<PackAssignmentPreviewItem> Items, IReadOnlyList<string> Warnings,
    IReadOnlyList<TerminologyPreviewRow>? Terminology = null)
{
    /// <summary>For each capability, whether it is on now and whether it would be on once the pack is assigned (the organisation's own published choice still wins).</summary>
    public IReadOnlyList<(string Key, bool Now, bool After)> Capabilities { get; init; } = [];
    /// <summary>Problems the assignment would create: a capability turned off while work is in flight or the organisation's published workflows still use it. Errors stop the assignment.</summary>
    public IReadOnlyList<CapabilityFinding> CapabilityFindings { get; init; } = [];
}
public sealed record OrganisationPackStatus(Guid OrganisationId, string Name, string? PackKey, string? Version, string? Source, string? Origin, DateTime? AssignedUtc, bool PackUsable);

/// <summary>
/// Assigning a pack to an organisation records which pack it works from and creates <b>drafts</b> from the pack's documents. It never publishes, never overwrites a draft that is already open, and never touches a
/// published release or a contract pinned to one: an organisation's existing configuration keeps running until an administrator publishes something new through the ordinary lifecycle.
/// </summary>
public interface IPackAssignments
{
    Task<PackAssignmentPreview> PreviewAsync(Guid organisationId, string packKey, string version, CancellationToken ct = default);
    Task<PackAssignmentResult> AssignAsync(Guid organisationId, string packKey, string version, CancellationToken ct = default);
    Task<IReadOnlyList<OrganisationPackStatus>> OrganisationsAsync(CancellationToken ct = default);
}

internal sealed class PackAssignments(IPackLoader loader, IConfigurationService configuration, ConfigurationDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IAccessService access,
    CurrentUser currentUser, ActorContext actor, IClock clock, IOrganisationStructureService organisations, PackKindRegistry kinds, ITerminologyResolver terminology, ICapabilityResolver capabilities, IServiceProvider services) : IPackAssignments
{
    private string Who() => currentUser.UserId?.ToString() ?? actor.ActorId ?? "system";
    private static ResourceDescriptor OrgResource(Guid tenantId, Guid organisationId) => new("SolutionPack", tenantId, organisationId, Dimensions: ScopeDimensions.OrganisationOnly);

    private async Task<(OrganisationSummary Org, PackCatalogueEntry Entry, OpenedPackage Package)> ResolveAsync(Guid organisationId, string packKey, string version, string permission, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync(permission, OrgResource(tenantId, organisationId), ct);
        var org = await organisations.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();
        var opened = await loader.OpenAsync(packKey, version, ct) ?? throw new DomainException("pack_not_available", "That pack version is not available.");
        if (!opened.Entry.IsUsable) throw new DomainException("pack_not_usable", opened.Entry.Retired ? "That pack version has been retired and cannot be assigned." : "That pack version cannot be used here: it is not trusted or not compatible in this environment.");
        foreach (var d in opened.Package.Manifest!.Dependencies)
            if (await loader.FindAsync(d.PackKey, null, usableOnly: true, ct) is null) throw new DomainException("pack_dependency_missing", $"This pack needs the pack '{d.PackKey}', which is not installed.");
        return (org, opened.Entry, opened.Package);
    }

    private IEnumerable<PackContentEntry> InstallableContent(OpenedPackage package)
        => package.Manifest!.Content.Where(c => c.Kind != PackReader.SolutionPackKind && kinds.Known.Contains(c.Kind) && c.Scope != PackScopes.Catalogue);

    public async Task<PackAssignmentPreview> PreviewAsync(Guid organisationId, string packKey, string version, CancellationToken ct = default)
    {
        var (org, entry, package) = await ResolveAsync(organisationId, packKey, version, "packs.assign", ct);
        var items = new List<PackAssignmentPreviewItem>();
        foreach (var c in InstallableContent(package))
        {
            var scopeOrg = c.Scope == PackScopes.Tenant ? Guid.Empty : organisationId;
            var hasRelease = await db.Releases.AsNoTracking().AnyAsync(r => r.OrganisationId == scopeOrg && r.Kind == c.Kind, ct);
            var hasDraft = await db.Drafts.AsNoTracking().AnyAsync(d => d.OrganisationId == scopeOrg && d.Kind == c.Kind && (d.Status == DraftStatus.Draft || d.Status == DraftStatus.Validated), ct);
            var effect = hasDraft ? "An open draft already exists and is kept. Nothing is created for this kind."
                : hasRelease ? "A draft is created from the pack. The published release stays in force until you publish the draft." : "A draft is created from the pack. Nothing is in force for this kind yet.";
            if (c.Kind == TerminologyKind.KindName)
            {
                var count = package.Document(c.Kind) is { } doc ? TerminologyDocument.Parse(doc).Terms.Count : 0;
                effect = hasRelease
                    ? $"The organisation already publishes its own terminology, which stays in force. The pack's {count} terms apply only to keys it does not set. No draft is created."
                    : hasDraft ? $"A terminology draft is already open and is kept. The pack's {count} terms apply as defaults beneath it once the pack is assigned. Nothing is published."
                    : $"The pack's {count} terms apply to this organisation as defaults as soon as the pack is assigned, and a terminology draft is prepared from them so you can review and change labels before publishing your own. Nothing is published, and no permission or workflow changes.";
            }
            if (c.Kind == CapabilityKinds.Name)
                effect = hasRelease ? "The organisation already publishes its own capability choices, which stay in force. The pack's defaults apply only to capabilities it has not decided. No draft is created."
                    : "The pack's capability defaults apply to this organisation as soon as the pack is assigned (see the table below). No draft is created; an organisation can publish its own choice later.";
            items.Add(new PackAssignmentPreviewItem(c.Kind, c.Scope, hasRelease, hasDraft, effect));
        }
        var warnings = new List<string>();
        if (org.SolutionPackKey is not null && org.SolutionPackKey != packKey) warnings.Add($"The organisation currently works from the pack '{org.SolutionPackKey}'. Its published configuration is not changed, but its starting pack changes to '{packKey}'.");
        if (!entry.Trust.IsTrusted) warnings.Add("This pack is unsigned. It is accepted here only because this is a Demo or Test environment that allows unsigned synthetic packs.");
        IReadOnlyList<TerminologyPreviewRow> terms = [];
        if (package.Document(TerminologyKind.KindName) is { } termsJson)
        {
            var now = await terminology.GetAsync(organisationId, ct: ct);
            terms = TerminologyPreview.Rows(TerminologyDocument.Parse(termsJson), now);
        }
        var (capabilityRows, findings) = await CapabilityEffectAsync(organisationId, packKey, version, ct);
        if (findings.Any(f => f.Severity == ValidationSeverity.Error)) warnings.Add("The capability defaults of this pack do not fit this organisation as it is now; the assignment would be refused (see the capability problems below).");
        return new PackAssignmentPreview(organisationId, org.Name, org.SolutionPackKey, entry, items, warnings, terms) { Capabilities = capabilityRows, CapabilityFindings = findings };
    }

    /// <summary>What assigning the pack would do to the organisation's capabilities, and whether the result is coherent with its workflows and the work in flight.</summary>
    private async Task<(IReadOnlyList<(string Key, bool Now, bool After)> Rows, IReadOnlyList<CapabilityFinding> Findings)> CapabilityEffectAsync(Guid organisationId, string packKey, string version, CancellationToken ct)
    {
        var now = await capabilities.GetAsync(organisationId, ct);
        var after = await capabilities.AfterPackAsync(organisationId, packKey, version, ct);
        var findings = new List<CapabilityFinding>();
        foreach (var u in after.Unmet()) findings.Add(new(ValidationSeverity.Error, u.Capability, "capability_requires", $"'{u.Capability}' needs '{u.Requires}', which is off."));
        foreach (var check in services.GetServices<ICapabilityCompatibility>()) findings.AddRange(await check.CheckAsync(organisationId, after, now, ct));
        return (CapabilityKeys.All.Select(k => (k, now.IsEnabled(k), after.IsEnabled(k))).ToList(), findings);
    }

    public async Task<PackAssignmentResult> AssignAsync(Guid organisationId, string packKey, string version, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var (org, entry, package) = await ResolveAsync(organisationId, packKey, version, "packs.assign", ct);
        // A pack's capability defaults are an operating-model change: never apply them where they would switch a capability off from under work in flight or leave the organisation's workflows using it.
        var (_, capabilityFindings) = await CapabilityEffectAsync(organisationId, packKey, version, ct);
        if (capabilityFindings.Where(f => f.Severity == ValidationSeverity.Error).ToList() is { Count: > 0 } blockers)
            throw new DomainException("capability_conflict", "This pack's capability defaults do not fit this organisation as it is now, so it was not assigned. " + string.Join(" ", blockers.Select(b => b.Message)));
        var outcomes = new List<PackKindOutcome>();
        foreach (var c in InstallableContent(package))
        {
            var scopeOrg = c.Scope == PackScopes.Tenant ? Guid.Empty : organisationId;
            try
            {
                var open = await configuration.FindOpenDraftAsync(c.Kind, scopeOrg, ct);
                if (open is not null) { outcomes.Add(new PackKindOutcome(c.Kind, c.Scope, "An open draft already exists and was kept", true, "Review that draft; the pack's content was not added to it.")); continue; }
                var json = package.Document(c.Kind)!;
                if (c.Kind == CapabilityKinds.Name)
                {
                    // Capability defaults are layered beneath the organisation's own choice and apply from the assignment; nothing is drafted or published.
                    outcomes.Add(new PackKindOutcome(c.Kind, c.Scope, "Applied as defaults", false, "The pack's capability defaults apply to this organisation now; its own published choice, if any, still wins."));
                    continue;
                }
                if (c.Kind == TerminologyKind.KindName)
                {
                    // Terminology is layered: the pack's wording already applies beneath the organisation's own. An organisation that publishes its own terminology keeps it untouched; otherwise a draft is prepared from the pack.
                    if (await configuration.LatestReleaseAsync(c.Kind, scopeOrg, ct) is not null)
                    { outcomes.Add(new PackKindOutcome(c.Kind, c.Scope, "Existing terminology kept", false, "This organisation publishes its own terminology; the pack's terms apply only where it has none.")); continue; }
                    json = TerminologyDocument.Parse(json).WithSource(packKey, version, TerminologyDocument.Overrides).ToJson();
                }
                var draft = await configuration.GetOrCreateDraftAsync(c.Kind, scopeOrg, json, ct);
                var validated = await configuration.ValidateAsync(draft.Id, ct);
                var errors = validated.Validation?.Messages.Count(m => m.Severity == ValidationSeverity.Error) ?? 0;
                outcomes.Add(new PackKindOutcome(c.Kind, c.Scope, errors == 0 ? "Draft created and validated" : "Draft created; validation found problems", errors > 0,
                    errors == 0 ? "It can be published after review." : $"{errors} problem(s) must be fixed before it can be published."));
            }
            catch (ForbiddenException)
            {
                outcomes.Add(new PackKindOutcome(c.Kind, c.Scope, "Not created: you do not have permission to draft this kind", true, c.Scope == PackScopes.Tenant ? "This is tenant-wide configuration; a tenant administrator can create it." : "Ask an administrator who can draft configuration."));
            }
        }

        await organisations.SetSolutionPackAsync(organisationId, packKey, ct);
        var outcomeJson = JsonSerializer.Serialize(outcomes.ToDictionary(o => o.Kind, o => o.Outcome));
        await uow.ExecuteAsync(async c =>
        {
            foreach (var previous in await db.PackAssignments.Where(a => a.OrganisationId == organisationId && a.Status == PackAssignment.Active).ToListAsync(c)) previous.Supersede();
            db.PackAssignments.Add(PackAssignment.Create(tenantId, organisationId, packKey, version, entry.PackageSha256, entry.Source, PackAssignment.OriginPackManager, Who(), clock.UtcNow, outcomeJson, actor.CorrelationId));
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("pack.assigned", "SolutionPack", $"{packKey}@{version}", OrganisationId: organisationId, NewValuesJson: JsonSerializer.Serialize(new { packKey, version, sha256 = entry.PackageSha256, kinds = outcomes })));
        }, ct);
        return new PackAssignmentResult(organisationId, packKey, version, outcomes,
        [
            "Nothing has been published. Existing configuration and contracts are unchanged.",
            "Open Configuration, review each draft, validate it, and publish it when you are satisfied. Second-person publication applies where it is required."
        ]);
    }

    public async Task<IReadOnlyList<OrganisationPackStatus>> OrganisationsAsync(CancellationToken ct = default)
    {
        await access.RequireAsync("packs.read", PackManager.TenantResource(tenant.RequireTenantId()), ct);
        var orgs = await organisations.ListOrganisationsAsync(ct);
        var assignments = await db.PackAssignments.AsNoTracking().Where(a => a.Status == PackAssignment.Active).ToListAsync(ct);
        var list = new List<OrganisationPackStatus>();
        foreach (var o in orgs)
        {
            var a = assignments.FirstOrDefault(x => x.OrganisationId == o.Id);
            var usable = o.SolutionPackKey is not null && await loader.FindAsync(o.SolutionPackKey, a?.Version, usableOnly: false, ct) is { IsUsable: true };
            list.Add(new OrganisationPackStatus(o.Id, o.Name, o.SolutionPackKey, a?.Version, a?.SourceType, a?.Origin, a?.AssignedUtc, usable));
        }
        return list;
    }
}

/// <summary>A pack document chosen as an organisation's starting point when it has published nothing yet, with provenance recorded. Used by the stores that install an organisation's first definitions.</summary>
public interface IOrganisationPackContent
{
    /// <summary>
    /// The document of this kind from the pack the organisation works from: the version it was assigned, or the latest usable version. Refused with a clear reason if the pack is missing or not usable, or if an
    /// administrator already has a draft waiting to be published (the draft, not the pack, is then the starting point).
    /// </summary>
    Task<PackDocument> GetStartingDocumentAsync(Guid organisationId, string packKey, string kind, CancellationToken ct = default);
    /// <summary>Records the provenance of the starting configuration (once per organisation and pack): which pack version it came from.</summary>
    Task RecordStartAsync(Guid organisationId, PackDocument document, CancellationToken ct = default);
}

internal sealed class OrganisationPackContent(IPackLoader loader, ConfigurationDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, ActorContext actor, IClock clock) : IOrganisationPackContent
{
    public async Task<PackDocument> GetStartingDocumentAsync(Guid organisationId, string packKey, string kind, CancellationToken ct = default)
    {
        var assigned = await db.PackAssignments.AsNoTracking().Where(a => a.OrganisationId == organisationId && a.PackKey == packKey && a.Status == PackAssignment.Active).Select(a => a.Version).FirstOrDefaultAsync(ct);
        var entry = assigned is not null ? await loader.FindAsync(packKey, assigned, usableOnly: false, ct) : await loader.FindAsync(packKey, null, usableOnly: true, ct);
        if (entry is null || entry.Unavailable || !entry.IsCompatible || !entry.Trust.IsAcceptable)
            throw new DomainException("pack_not_available", $"The [[solution.pack|lower]] '{packKey}' is not available here. It may not be installed, or it may not be trusted in this environment. Ask an administrator to check Administration, [[solution.pack|plural|sentence]].");
        if (await db.Drafts.AsNoTracking().AnyAsync(d => d.OrganisationId == organisationId && d.Kind == kind && (d.Status == DraftStatus.Draft || d.Status == DraftStatus.Validated), ct))
            throw new DomainException("configuration_pending_publication", "This [[organisation|lower]]'s configuration from its [[solution.pack|lower]] is waiting to be reviewed and published by an administrator.");
        var json = await loader.GetDocumentAsync(packKey, entry.Version, kind, ct)
            ?? throw new DomainException("pack_content_missing", $"The [[solution.pack|lower]] '{packKey}' (version {entry.Version}) does not carry this kind of configuration ({kind}).");
        return new PackDocument(json, packKey, entry.Version, entry.PackageSha256, entry.Source);
    }

    public async Task RecordStartAsync(Guid organisationId, PackDocument document, CancellationToken ct = default)
    {
        if (await db.PackAssignments.AsNoTracking().AnyAsync(a => a.OrganisationId == organisationId && a.PackKey == document.PackKey && a.Version == document.Version, ct)) return;
        var tenantId = tenant.RequireTenantId();
        try
        {
            await uow.ExecuteAsync(async c =>
            {
                foreach (var previous in await db.PackAssignments.Where(a => a.OrganisationId == organisationId && a.Status == PackAssignment.Active && a.PackKey != document.PackKey).ToListAsync(c)) previous.Supersede();
                db.PackAssignments.Add(PackAssignment.Create(tenantId, organisationId, document.PackKey, document.Version, document.PackageSha256, document.Source, PackAssignment.OriginBootstrap, actor.ActorId ?? "system", clock.UtcNow,
                    "{\"startingDefinitions\":\"installed when first needed\"}", actor.CorrelationId));
                await db.SaveChangesAsync(c);
                audit.Record(new AuditEntry("pack.start.recorded", "SolutionPack", $"{document.PackKey}@{document.Version}", OrganisationId: organisationId,
                    NewValuesJson: JsonSerializer.Serialize(new { document.PackKey, document.Version, sha256 = document.PackageSha256 })));
            }, ct);
        }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); /* provenance is best effort and idempotent; a concurrent writer recorded it */ }
    }
}
