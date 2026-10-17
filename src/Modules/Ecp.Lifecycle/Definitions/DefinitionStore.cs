using Ecp.Tenancy;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Reflection;
using Ecp.Configuration;
using Ecp.Identity.Access;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;

namespace Ecp.Lifecycle.Definitions;

public sealed class LifecycleOptions
{
    /// <summary>
    /// The installation default solution pack, used only for an organisation that has none of its own (<c>Ecp:DefaultPackId</c>). Not set unless the installer sets it. An organisation that has published no
    /// workflow yet gets the reference definitions of <b>its own</b> pack, as a starting point edited like any configuration.
    /// </summary>
    public string? DefaultPackId { get; set; }
    /// <summary>Installation mode. Mock providers and simulated signing events are refused in Production.</summary>
    public EcpMode Mode { get; set; } = EcpMode.Production;
    /// <summary>How recently the person must have signed in to force-unlock a record.</summary>
    public TimeSpan ReauthenticationWindow { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>A workflow definition as it was published, with the reference a contract is pinned to.</summary>
public sealed record ResolvedDefinition(WorkflowDefinition Definition, string WorkflowRef, Guid ReleaseId, int ReleaseNumber, string Kind);

public static class WorkflowKinds
{
    public const string ContractLifecycle = "WorkflowContractLifecycle";
    public const string Variation = "WorkflowVariation";

    public static string ExpectedSubject(string kind) => kind == Variation ? "Variation" : "Contract";
}

/// <summary>Configuration kind for a workflow definition: gives it draft, validate, preview, publish, version history, rollback and audit like any other configuration.</summary>
internal sealed class WorkflowDefinitionKind(string kind, Ecp.Approvals.IApprovalMatrixStore? matrices = null) : IConfigurationKind
{
    public string Kind => kind;

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        var (definition, messages) = WorkflowDefinitionParser.Parse(contentJson);
        if (definition is not null)
        {
            messages.AddRange(WorkflowDefinitionParser.Validate(definition, WorkflowKinds.ExpectedSubject(kind), PermissionMatrix.Instance));
            // A workflow that names an approval matrix is only safe to publish when that matrix is already published and in force: otherwise every submission would be blocked.
            if (definition.Settings.Approval.Matrix is { } code && matrices is not null)
            {
                var published = await matrices.TryLoadPublishedAsync(organisationId, ct);
                if (published is null) messages.Add(new ValidationMessage(ValidationSeverity.Error, "$.settings.approval.matrix", "matrix_missing", $"The workflow names the approval matrix '{code}', but no approval matrix has been published for this organisation. Publish the matrix first."));
                else if (published.Set.Find(code) is null) messages.Add(new ValidationMessage(ValidationSeverity.Error, "$.settings.approval.matrix", "matrix_missing", $"The workflow names the approval matrix '{code}', which is not in the published approval matrix document. Publish it first or correct the code."));
            }
        }
        return new ValidationReport(messages);
    }

    // A published definition takes effect for contracts created from now on and for contracts not yet pinned; a contract already pinned keeps the release it started under.
    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct) => Task.CompletedTask;
}

public interface IWorkflowDefinitions
{
    /// <summary>The definition a contract (or Variation) is pinned to. When it has none yet, the latest published release is used.</summary>
    Task<ResolvedDefinition> ResolveAsync(string kind, Guid organisationId, Guid? pinnedReleaseId, CancellationToken ct = default);
    /// <summary>The latest published release; the pack's reference definition is installed first if the organisation has published none.</summary>
    Task<ResolvedDefinition> CurrentAsync(string kind, Guid organisationId, CancellationToken ct = default);
}

internal sealed class WorkflowDefinitionStore(IConfigurationReleases releases, IConfigurationBootstrap bootstrap, Ecp.Tenancy.IOrganisationStructureService organisations, IOrganisationPackContent packContent, IOptions<LifecycleOptions> options) : IWorkflowDefinitions
{
    // A published release is immutable, so parsing it once per release id is safe for the life of the process.
    private static readonly ConcurrentDictionary<Guid, WorkflowDefinition> Parsed = new();

    public async Task<ResolvedDefinition> ResolveAsync(string kind, Guid organisationId, Guid? pinnedReleaseId, CancellationToken ct = default)
    {
        if (pinnedReleaseId is { } id)
        {
            var pinned = await releases.FindAsync(id, ct);
            if (pinned is not null && pinned.Kind == kind) return ToResolved(pinned);
        }
        return await CurrentAsync(kind, organisationId, ct);
    }

    public async Task<ResolvedDefinition> CurrentAsync(string kind, Guid organisationId, CancellationToken ct = default)
    {
        var latest = await releases.LatestAsync(kind, organisationId, ct);
        if (latest is null)
        {
            var packId = await organisations.ResolveAsync(organisationId, options.Value.DefaultPackId, ct);
            // The starting definition comes from the organisation's pack through the unified pack loader; the release records which pack version and package it came from.
            var document = await packContent.GetStartingDocumentAsync(organisationId, packId, kind, ct);
            try
            {
                latest = await bootstrap.DraftValidatePublishAsync(kind, organisationId, document.Json, "system",
                    $"Installed the {document.PackKey} {document.Version} reference definition (package {document.PackageSha256[..12]})", ct);
                await packContent.RecordStartAsync(organisationId, document, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Another request may have installed it first (the release number is unique); use theirs. Anything else is a real failure.
                latest = await releases.LatestAsync(kind, organisationId, ct);
                if (latest is null) ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }
        return ToResolved(latest!);
    }

    private static ResolvedDefinition ToResolved(ReleaseDto release)
    {
        var definition = Parsed.GetOrAdd(release.Id, _ =>
        {
            var (parsed, messages) = WorkflowDefinitionParser.Parse(release.ContentJson);
            return parsed ?? throw new DomainException("workflow_unreadable", "The published workflow definition could not be read: " + string.Join("; ", messages.Select(m => m.Message)));
        });
        return new ResolvedDefinition(definition, $"{definition.Workflow}@{release.Number}", release.Id, release.Number, release.Kind);
    }
}
