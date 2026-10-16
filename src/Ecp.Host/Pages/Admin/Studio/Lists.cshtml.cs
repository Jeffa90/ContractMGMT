using System.Globalization;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>The sections that group fields on forms, and the lookup lists that fields (and the contract type, risk level and similar core fields) choose from.</summary>
[Authorize(Policy = "perm:config.draft")]
public sealed class ListsModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio) : StudioPage(organisations, access)
{
    [BindProperty(SupportsGet = true)] public string? List { get; set; }
    [BindProperty] public string SectionKey { get; set; } = "";
    [BindProperty] public string SectionLabel { get; set; } = "";
    [BindProperty] public string? SectionOrder { get; set; }
    [BindProperty] public string ListName { get; set; } = "";
    [BindProperty] public string ListLabel { get; set; } = "";
    [BindProperty] public string? Members { get; set; }
    public StudioState State { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (List is not null && State.Working.FindList(List) is { } l)
            (ListName, ListLabel, Members) = (l.Name, l.Label, string.Join("\n", l.Members.Select(m => m.Active ? $"{m.Code} | {m.Label}" : $"{m.Code} | {m.Label} | inactive")));
        return Page();
    });

    private async Task LoadAsync(CancellationToken ct)
    {
        var org = await ResolveOrganisationAsync(ct) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
        State = await studio.LoadAsync(org, false, ct);
    }

    public Task<IActionResult> OnPostSectionAsync(CancellationToken ct) => Act(async org =>
    {
        var order = int.TryParse(SectionOrder, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0;
        await studio.SaveSectionAsync(org, new FieldSection(SectionKey.Trim().ToLowerInvariant(), SectionLabel.Trim(), order), ct);
    }, "The section is saved in the draft.", () => LoadAsync(CancellationToken.None));

    public Task<IActionResult> OnPostListAsync(CancellationToken ct) => Act(async org =>
    {
        var members = new List<FieldChoice>();
        foreach (var line in (Members ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var parts = t.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) throw new Ecp.SharedKernel.DomainException("list_member", $"Write each member as CODE | Label (or CODE | Label | inactive). Check: {t}");
            members.Add(new FieldChoice(parts[0], parts[1], !(parts.Length == 3 && parts[2].Equals("inactive", StringComparison.OrdinalIgnoreCase))));
        }
        await studio.SaveListAsync(org, new LookupList(ListName.Trim(), ListLabel.Trim(), members), ct);
    }, "The list is saved in the draft.", () => LoadAsync(CancellationToken.None));
}
