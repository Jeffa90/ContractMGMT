using System.Text.Json;
using Ecp.Configuration;
using Ecp.Contracts.Register;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>Versions of the field definitions or the shared views: what each release changed, who published it and why, and restoring an earlier one as a new release.</summary>
[Authorize(Policy = "perm:config.history.read")]
public sealed class HistoryModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio fields, IContractViewStudio views) : StudioPage(organisations, access)
{
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "fields";
    [BindProperty] public int ToRelease { get; set; }
    [BindProperty] public string? Reason { get; set; }
    public IReadOnlyList<(ReleaseDto Release, string Summary)> Releases { get; private set; } = [];
    public string? Denied { get; private set; }
    public int? Current { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        var org = await ResolveOrganisationAsync(ct) ?? throw new ResourceNotFoundException();
        IReadOnlyList<ReleaseDto>? releases; int? current;
        if (Kind == "views") { var s = await views.LoadAsync(org, true, ct); (releases, Denied, current) = (s.Releases, s.ReleasesDenied, s.Release); }
        else { var s = await fields.LoadAsync(org, true, ct); (releases, Denied, current) = (s.Releases, s.ReleasesDenied, s.Release?.Number); }
        Current = current;
        var ordered = (releases ?? []).OrderBy(r => r.Number).ToList();
        Releases = ordered.Select((r, i) => (r, Describe(i == 0 ? null : ordered[i - 1].ContentJson, r.ContentJson))).OrderByDescending(x => x.r.Number).ToList();
    }

    private string Describe(string? before, string after)
    {
        if (Kind == "views")
        {
            var b = before is null ? null : ViewParser.Parse(before).Set; var a = ViewParser.Parse(after).Set;
            var added = (a?.Views ?? []).Count(v => b?.Find(v.Key) is null); var removed = (b?.Views ?? []).Count(v => a?.Find(v.Key) is null);
            var changed = (a?.Views ?? []).Count(v => b?.Find(v.Key) is { } old && ViewParser.ToJson(new ViewSet(1, [old])) != ViewParser.ToJson(new ViewSet(1, [v])));
            return $"{added} view(s) added, {changed} changed, {removed} removed";
        }
        var pb = before is null ? null : FieldParser.Parse(before).Set; var pa = FieldParser.Parse(after).Set;
        var add = (pa?.Fields ?? []).Count(f => pb?.Find(f.Key) is null);
        var chg = (pa?.Fields ?? []).Count(f => pb?.Find(f.Key) is { } old && JsonSerializer.Serialize(old) != JsonSerializer.Serialize(f));
        var ret = (pa?.Fields ?? []).Count(f => f.IsRetired && pb?.Find(f.Key) is { IsRetired: false });
        return $"{add} field(s) added, {chg} changed ({ret} retired)";
    }

    public Task<IActionResult> OnPostRollbackAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct) ?? throw new ResourceNotFoundException();
        if (string.IsNullOrWhiteSpace(Reason)) throw new DomainException("reason_required", "Give a reason for restoring the earlier version.");
        if (Kind == "views") await views.RollbackAsync(org, ToRelease, Reason!, ct); else await fields.RollbackAsync(org, ToRelease, Reason!, ct);
        Flash("The earlier version is published again as a new release. History is kept.");
        return RedirectToPage(new { organisationId = org, kind = Kind });
    }, () => LoadAsync(CancellationToken.None));
}
