using Ecp.Tenancy;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;

namespace Ecp.Notifications.Policy;

public static class NotificationPolicyKinds
{
    public const string Policy = "NotificationPolicy";
}

/// <summary>Configuration kind for the notification policy: draft, validate, publish, version history, rollback and audit like any other configuration.</summary>
internal sealed class NotificationPolicyKind(IClassificationScaleProvider scales) : IConfigurationKind
{
    public string Kind => NotificationPolicyKinds.Policy;

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        var (policy, messages) = NotificationPolicyParser.Parse(contentJson);
        if (policy is not null) messages.AddRange(NotificationPolicyParser.Validate(policy, await scales.GetAsync(ct)));
        return new ValidationReport(messages);
    }

    // The scanner and dispatcher read the latest release when they run, so a published policy takes effect for the next message; messages already written are not rewritten.
    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct) => Task.CompletedTask;
}

public interface INotificationPolicyStore
{
    /// <summary>The policy in force for the organisation. The pack's reference policy is installed first if the organisation has published none.</summary>
    Task<NotificationPolicy> CurrentAsync(Guid organisationId, CancellationToken ct = default);
}

internal sealed class NotificationPolicyStore(IConfigurationReleases releases, IConfigurationBootstrap bootstrap, Ecp.Tenancy.IOrganisationStructureService organisations, IOrganisationPackContent packContent, IOptions<NotificationsOptions> options) : INotificationPolicyStore
{
    private static readonly ConcurrentDictionary<Guid, NotificationPolicy> Parsed = new();

    public async Task<NotificationPolicy> CurrentAsync(Guid organisationId, CancellationToken ct = default)
    {
        var latest = await releases.LatestAsync(NotificationPolicyKinds.Policy, organisationId, ct);
        if (latest is null)
        {
            var packId = await organisations.ResolveAsync(organisationId, options.Value.DefaultPackId, ct);
            // The starting policy comes from the organisation's pack through the unified pack loader; the release records which pack version and package it came from.
            var document = await packContent.GetStartingDocumentAsync(organisationId, packId, NotificationPolicyKinds.Policy, ct);
            try
            {
                latest = await bootstrap.DraftValidatePublishAsync(NotificationPolicyKinds.Policy, organisationId, document.Json, "system",
                    $"Installed the {document.PackKey} {document.Version} reference notification policy (package {document.PackageSha256[..12]})", ct);
                await packContent.RecordStartAsync(organisationId, document, ct);
            }
            catch (PreviewReadOnlyException)
            {
                latest = UnsavedRelease.Of(NotificationPolicyKinds.Policy, organisationId, document.Json, "Reference policy (preview: not installed)");   // a read-only preview uses it without installing it
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                latest = await releases.LatestAsync(NotificationPolicyKinds.Policy, organisationId, ct);
                if (latest is null) ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }
        return Parsed.GetOrAdd(latest!.Id, _ =>
        {
            var (policy, messages) = NotificationPolicyParser.Parse(latest.ContentJson);
            return policy ?? throw new DomainException("policy_unreadable", "The published [[notification|lower]] policy could not be read: " + string.Join("; ", messages.Select(x => x.Message)));
        });
    }
}
