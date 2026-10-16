using System.Text;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Mapping;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration.Profiles;

public sealed class DetailsModel(IMappingProfiles profiles) : MigrationPageModel
{
    public ProfileDetail Profile { get; private set; } = null!;
    public ProfileVersionDetail? Version { get; private set; }
    public IReadOnlyList<string> Notes { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public Guid? VersionId { get; set; }
    [BindProperty] public bool CreateMissingCategories { get; set; }
    [BindProperty] public string? NamingRule { get; set; }
    [BindProperty] public string? OpenEndedTypes { get; set; }
    [BindProperty] public string? PublishNote { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    public Task<IActionResult> OnPostOpenDraftAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var draft = await profiles.OpenDraftAsync(id, ct);
        return RedirectToPage(new { id, versionId = draft });
    }, () => LoadAsync(id, ct));

    public Task<IActionResult> OnPostFieldsAsync(Guid id, IFormFile? file, CancellationToken ct) => Upload(id, file, ct, async s => { Notes = await profiles.ImportFieldMappingsAsync(VersionId!.Value, s, ct); Flash("The field mappings were loaded into the draft."); });

    public Task<IActionResult> OnPostLookupsAsync(Guid id, IFormFile? file, CancellationToken ct) => Upload(id, file, ct, async s => { Notes = await profiles.ImportLookupsAsync(VersionId!.Value, s, ct); Flash("The lookups were loaded into the draft."); });

    public Task<IActionResult> OnPostOptionsAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var open = (OpenEndedTypes ?? "").Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        await profiles.SetOptionsAsync(VersionId!.Value, new ProfileOptions(CreateMissingCategories, string.IsNullOrWhiteSpace(NamingRule) ? null : NamingRule.Trim(), open), ct);
        Flash("The profile options were saved.");
        return RedirectToPage(new { id, versionId = VersionId });
    }, () => LoadAsync(id, ct));

    public Task<IActionResult> OnPostPublishAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await profiles.PublishAsync(VersionId!.Value, PublishNote, ct);
        Flash("The version was published. It can no longer be changed; a batch can now use it.");
        return RedirectToPage(new { id, versionId = VersionId });
    }, () => LoadAsync(id, ct));

    public async Task<IActionResult> OnGetExportFieldsAsync(Guid id, CancellationToken ct) => await RunAsync(async () =>
        File(await profiles.ExportFieldMappingsAsync(VersionId!.Value, ct), "text/csv; charset=utf-8", "field-mappings.csv").AsActionResult());

    public async Task<IActionResult> OnGetExportLookupsAsync(Guid id, CancellationToken ct) => await RunAsync(async () =>
        File(await profiles.ExportLookupsAsync(VersionId!.Value, ct), "text/csv; charset=utf-8", "lookups.csv").AsActionResult());

    private Task<IActionResult> Upload(Guid id, IFormFile? file, CancellationToken ct, Func<Stream, Task> import) => RunAsync(async () =>
    {
        if (file is null || file.Length == 0) throw new Ecp.SharedKernel.DomainException("file_required", "Choose a CSV file to load.");
        if (file.Length > 5 * 1024 * 1024) throw new Ecp.SharedKernel.DomainException("file_too_large", "A mapping file larger than 5 MB is not expected. Check that it is the right file.");
        await using var stream = file.OpenReadStream();
        await import(stream);
        if (Notes.Count > 0) TempData["Flash"] = TempData["Flash"] + " " + string.Join(" ", Notes.Take(5));
        return RedirectToPage(new { id, versionId = VersionId });
    }, () => LoadAsync(id, ct));

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Profile = await profiles.GetAsync(id, ct);
        VersionId ??= Profile.Versions.FirstOrDefault(v => v.Status == ProfileVersionStatus.Draft)?.Id ?? Profile.Versions.FirstOrDefault()?.Id;
        if (VersionId is { } vid && Profile.Versions.Any(v => v.Id == vid))
        {
            Version = await profiles.GetVersionAsync(vid, ct);
            CreateMissingCategories = Version.Definition.Options.CreateMissingCategories;
            NamingRule ??= Version.Definition.Options.NamingRule;
            OpenEndedTypes ??= string.Join(", ", Version.Definition.Options.OpenEndedExtensionTypes ?? []);
        }
    }
}

internal static class FileResultExtensions
{
    public static IActionResult AsActionResult(this FileContentResult r) => r;
}
