using System.Text.Json;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>One published version of the fields: when, by whom, and what it did in words, field by field.</summary>
public sealed record FieldVersion(ReleaseDto Release, IReadOnlyList<string> Changes, bool InForce);

/// <summary>The version history of the fields (or of one field), with a plain description of what each version changed, and the way back: an earlier version is published again as a new version, so nothing is ever erased.</summary>
[Authorize(Policy = "perm:config.history.read")]
public sealed class HistoryModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio, IConfigurationService configuration)
    : FieldsPage(organisations, access, studio, configuration)
{
    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    [BindProperty] public int ToRelease { get; set; }
    [BindProperty] public string? Reason { get; set; }
    public IReadOnlyList<FieldVersion> Versions { get; private set; } = [];
    public string? Denied { get; private set; }
    public string? FieldLabel { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        var org = await LoadStateAsync(ct);
        State = await Studio.LoadAsync(org, true, ct);
        Denied = State.ReleasesDenied;
        FieldLabel = Key is null ? null : State.Working.Find(Key)?.Label ?? Key;
        var ordered = (State.Releases ?? []).OrderBy(r => r.Number).ToList();
        var versions = new List<FieldVersion>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var before = i == 0 ? null : FieldParser.Parse(ordered[i - 1].ContentJson).Set;
            var after = FieldParser.Parse(ordered[i].ContentJson).Set ?? CustomFieldSet.Empty;
            var changes = Describe(before, after).Where(c => Key is null || string.Equals(c.Key, Key, StringComparison.OrdinalIgnoreCase)).Select(c => c.Text).ToList();
            if (Key is not null && changes.Count == 0) continue;
            versions.Add(new FieldVersion(ordered[i], changes, ordered[i].Number == State.Release?.Number));
        }
        Versions = versions.OrderByDescending(v => v.Release.Number).ToList();
    }

    private static IEnumerable<(string Key, string Text)> Describe(CustomFieldSet? before, CustomFieldSet after)
    {
        foreach (var f in after.Fields)
        {
            var old = before?.Find(f.Key);
            if (old is null) yield return (f.Key, $"Added {f.Label}");
            else if (!old.IsRetired && f.IsRetired) yield return (f.Key, $"Retired {f.Label}");
            else if (old.IsRetired && !f.IsRetired) yield return (f.Key, $"Reinstated {f.Label}");
            else if (JsonSerializer.Serialize(old) != JsonSerializer.Serialize(f)) yield return (f.Key, $"Changed {f.Label}");
        }
        foreach (var gone in (before?.Fields ?? []).Where(o => after.Find(o.Key) is null)) yield return (gone.Key, $"Removed {gone.Label}");
    }

    public Task<IActionResult> OnPostRollbackAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct) ?? throw new ResourceNotFoundException();
        if (string.IsNullOrWhiteSpace(Reason)) throw new DomainException("reason_required", "Say why you are restoring the earlier version.");
        var outcome = await Studio.RequestRollbackAsync(org, ToRelease, Reason!, ct);
        Flash(outcome.Release is not null
            ? "The earlier version is published again as a new version. Nothing was erased."
            : "Submitted for publication. A second person has to review and publish the restore; nothing changes until they do.");
        return RedirectToPage(new { organisationId = org, key = Key });
    }, () => LoadAsync(CancellationToken.None));
}
