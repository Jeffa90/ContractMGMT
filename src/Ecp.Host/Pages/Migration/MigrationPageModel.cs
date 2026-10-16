using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

/// <summary>Shared behaviour of the migration pages: plain-language names for the choices, display formatting in the organisation's culture and time zone, and file downloads.</summary>
[Authorize(Policy = "perm:migration.stage")]
public abstract class MigrationPageModel : EcpPageModel
{
    protected LocaleFormatter Fmt { get; private set; } = new("en-AU", "Australia/Brisbane", "AUD");

    protected async Task LoadFormatterAsync(IOrganisationStructureService organisations, Guid organisationId, CancellationToken ct)
        => Fmt = await OrgLocale.ForAsync(organisations, organisationId, ct);

    public string When(DateTime? utc) => utc is { } u ? Fmt.DateTime(u) : "-";

    public static readonly IReadOnlyList<(DuplicateStrategy Value, string Label, string Hint)> Strategies =
    [
        (DuplicateStrategy.SkipExisting, "Skip existing", "A source record that was loaded before is left as it is. Re-running the same file changes nothing."),
        (DuplicateStrategy.UpdateMapped, "Update mapped fields", "A source record loaded before has only its mapped fields updated; the earlier values are kept so the change can be undone."),
        (DuplicateStrategy.Reject, "Reject", "A source record that was loaded before, or whose agreement reference is in use, is rejected and reported."),
        (DuplicateStrategy.CreateNewReference, "Create with a new reference", "A source reference that is already in use gets a new reference from the numbering rule; the source reference is kept separately."),
        (DuplicateStrategy.ManualReview, "Manual review", "Each duplicate waits for a person to decide before the batch can be committed.")
    ];

    public static readonly IReadOnlyList<(DocumentDuplicateOption Value, string Label)> DocumentOptions =
    [
        (DocumentDuplicateOption.AddVersion, "Add a new version"), (DocumentDuplicateOption.LinkExisting, "Link the existing content"),
        (DocumentDuplicateOption.RejectDuplicateHash, "Reject a duplicate"), (DocumentDuplicateOption.KeepSeparateWithWarning, "Keep it separate, with a warning"),
        (DocumentDuplicateOption.ManualReview, "Hold for a person to decide")
    ];

    public static string Label(DuplicateStrategy s) => Strategies.First(x => x.Value == s).Label;
    public static string Label(DocumentDuplicateOption o) => DocumentOptions.First(x => x.Value == o).Label;
    public static string Label(BatchKind k) => k switch { BatchKind.DryRun => "Dry run (validate only)", BatchKind.Mock => "Mock load (rehearsal)", _ => "Final load" };
    public static string Label(BatchStatus s) => s switch { BatchStatus.Created => "Created", BatchStatus.Profiled => "Sources loaded", BatchStatus.Validated => "Validated", BatchStatus.Committing => "Committing",
        BatchStatus.Committed => "Committed", BatchStatus.Reconciled => "Reconciled", BatchStatus.Accepted => "Accepted", BatchStatus.RolledBack => "Rolled back", BatchStatus.Compensated => "Compensated", _ => "Failed (can be resumed)" };
    public static string BadgeFor(BatchStatus s) => s switch { BatchStatus.Accepted => "ok", BatchStatus.Reconciled or BatchStatus.Committed => "ok", BatchStatus.Failed => "bad", BatchStatus.RolledBack or BatchStatus.Compensated => "warn", _ => "" };

    protected FileContentResult Download(ExportFile f) => File(f.Content, f.ContentType, f.FileName);
}
