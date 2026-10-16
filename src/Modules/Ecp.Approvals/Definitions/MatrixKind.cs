using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Ecp.Approvals.Resolution;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Approvals.Definitions;

/// <summary>
/// Configuration kind for approval matrices: draft, validate, publish, version history, rollback and audit like any other configuration. Publication is refused when a matrix that a workflow still refers to
/// would disappear, and each publication writes an audit event naming what changed row by row.
/// </summary>
internal sealed class ApprovalMatrixKind(
    IClassificationScaleProvider scales, IAccessDirectory directory, IClock clock, IAuditWriter audit, IApprovalReferenceData? reference = null, IApprovalMatrixReferences? references = null, IServiceProvider? services = null) : IConfigurationKind
{
    public string Kind => ApprovalsModule.MatrixKind;

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        var (set, messages) = MatrixParser.Parse(contentJson);
        if (set is null) return new ValidationReport(messages);
        var data = reference is null ? MatrixReferenceData.Empty : await reference.LoadAsync(organisationId, ct);
        messages.AddRange(MatrixParser.Validate(set, await scales.GetAsync(ct), data));

        // A group that nobody holds today is a warning, not an error: people join and leave, and a future-dated rule may name a group whose approvers start later.
        foreach (var group in set.Matrices.SelectMany(m => m.Rows).SelectMany(r => r.Steps).Select(s => s.ApproverGroup).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.Ordinal).Take(100))
            if ((await directory.ApproversAsync(group, clock.UtcNow, ct)).Count == 0)
                messages.Add(new ValidationMessage(ValidationSeverity.Warning, "$.matrices", "group_empty", $"Nobody currently holds the approver group '{group}'. A submission that reaches a step for it is blocked until someone does."));

        // Only conditions the platform can really judge are allowed: a row on a fact that nothing supplies would never match.
        // Resolved on use: the fact source reaches the configuration service, which is building this very list of kinds.
        if (services?.GetService(typeof(IApprovalFactSource)) is IApprovalFactSource facts)
        {
            var live = await facts.CatalogueAsync(organisationId, ct);
            foreach (var m in set.Matrices)
                foreach (var row in m.Rows)
                {
                    var w = row.When; var path = $"$.matrices[{m.Code}].rows[{row.Code}].when";
                    void Need(bool used, string dimension, string label)
                    {
                        if (used && !live.Dimensions.Contains(dimension)) messages.Add(new ValidationMessage(ValidationSeverity.Error, path, "fact_unavailable", $"This platform does not yet supply {label} when an approval is submitted, so a rule on it could never match. Remove the condition."));
                    }
                    Need(w.HasValueBounds, ApprovalFactCatalogue.Value, "the value");
                    Need(w.Categories.Count > 0, ApprovalFactCatalogue.Categories, "the category");
                    Need(w.ContractTypes.Count > 0, ApprovalFactCatalogue.ContractTypes, "the contract type");
                    Need(w.LegalEntities.Count > 0, ApprovalFactCatalogue.LegalEntities, "the legal entity");
                    Need(w.BusinessUnits.Count > 0, ApprovalFactCatalogue.BusinessUnits, "the business unit");
                    Need(w.Sites.Count > 0, ApprovalFactCatalogue.Sites, "the site");
                    Need(w.Classifications.Count > 0, ApprovalFactCatalogue.Classifications, "the classification");
                    Need(w.RiskLevels.Count > 0, ApprovalFactCatalogue.RiskLevels, "the risk level");
                    Need(w.SupplierTypes.Count > 0, ApprovalFactCatalogue.SupplierTypes, "the supplier type");
                    foreach (var c in w.Custom)
                        if (!live.CustomKeys.Contains(c.Field)) messages.Add(new ValidationMessage(ValidationSeverity.Error, path, "fact_unavailable", $"The custom field '{c.Field}' is not available to approvals. Publish it as a field that is marked usable in approvals, or remove the condition."));
                }
        }

        if (references is not null)
            foreach (var code in await references.ReferencedMatrixCodesAsync(organisationId, ct))
                if (set.Find(code) is null)
                    messages.Add(new ValidationMessage(ValidationSeverity.Error, "$.matrices", "matrix_in_use", $"A workflow still refers to the matrix '{code}'. Change the workflow first, or keep this matrix."));
        return new ValidationReport(messages);
    }

    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct)
    {
        var (set, _) = MatrixParser.Parse(contentJson);
        var before = previousContentJson is null ? null : MatrixParser.Parse(previousContentJson).Set;
        var changes = set is null ? null : MatrixDiff.Compare(before, set);
        // Submissions resolve against the latest release when they are made, so a published matrix takes effect for the next submission; plans already stored keep the release they were made under.
        audit.Record(new AuditEntry("approval.matrix.published", "ApprovalMatrix", releaseId.ToString(), OrganisationId: organisationId == Guid.Empty ? null : organisationId, ConfigReleaseId: releaseId,
            Reason: changes?.Summary,
            NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                release = releaseNumber, hash = ApprovalMatrixStore.Hash(contentJson),
                matrices = set?.Matrices.Select(m => new { m.Code, rows = m.Rows.Count }),
                added = changes?.RowsAdded, removed = changes?.RowsRemoved, changed = changes?.RowsChanged, matricesAdded = changes?.MatricesAdded, matricesRemoved = changes?.MatricesRemoved
            })));
        return Task.CompletedTask;
    }
}

internal sealed class ApprovalMatrixStore(IServiceProvider services) : IApprovalMatrixStore
{
    // Resolved on use, not on construction: the configuration service builds its list of kinds, and a kind may need this store, so taking the service here would be a cycle.
    private IConfigurationReleases releases => services.GetRequiredService<IConfigurationReleases>();
    private IConfigurationService configuration => services.GetRequiredService<IConfigurationService>();

    private static readonly ConcurrentDictionary<string, MatrixSet> Parsed = new();

    internal static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    private static MatrixSet Read(string json, string hash)
        => Parsed.GetOrAdd(hash, _ =>
        {
            var (set, messages) = MatrixParser.Parse(json);
            return set ?? throw new DomainException("matrix_unreadable", "The approval matrix could not be read: " + string.Join("; ", messages.Select(x => x.Message)));
        });

    public async Task<LoadedMatrices?> TryLoadPublishedAsync(Guid organisationId, CancellationToken ct = default)
    {
        var latest = await releases.LatestAsync(ApprovalsModule.MatrixKind, organisationId, ct);
        if (latest is null) return null;
        return new LoadedMatrices(Read(latest.ContentJson, latest.ContentHash), latest.Number, latest.Id, latest.ContentHash, $"release {latest.Number} (in force)", latest.ContentJson);
    }

    public async Task<LoadedMatrices> LoadAsync(Guid organisationId, MatrixSource source, CancellationToken ct = default)
    {
        switch (source.Kind)
        {
            case MatrixSourceKind.Published:
                return await TryLoadPublishedAsync(organisationId, ct)
                    ?? throw new DomainException("matrix_not_published", "No approval matrix has been published for this organisation. An administrator must publish one before it can be used.");
            case MatrixSourceKind.Release:
            {
                var n = source.ReleaseNumber ?? throw new DomainException("release_required", "Give the release number to use.");
                var r = (await configuration.ReleasesAsync(ApprovalsModule.MatrixKind, organisationId, ct)).FirstOrDefault(x => x.Number == n)
                    ?? throw new DomainException("release_not_found", $"There is no release {n} of the approval matrix.");
                return new LoadedMatrices(Read(r.ContentJson, r.ContentHash), r.Number, r.Id, r.ContentHash, $"release {r.Number} (history)", r.ContentJson);
            }
            default:
            {
                var d = await configuration.FindOpenDraftAsync(ApprovalsModule.MatrixKind, organisationId, ct)
                    ?? throw new DomainException("no_draft", "There is no open draft of the approval matrix.");
                var (set, messages) = MatrixParser.Parse(d.ContentJson);
                if (set is null) throw new DomainException("draft_unreadable", "The draft cannot be read: " + string.Join("; ", messages.Select(x => x.Message)));
                return new LoadedMatrices(set, null, null, Hash(d.ContentJson), "your open draft (not published)", d.ContentJson);
            }
        }
    }
}
