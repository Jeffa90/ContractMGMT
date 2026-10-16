using System.Security.Cryptography;
using System.Text;
using Ecp.SharedKernel;

namespace Ecp.Configuration.Domain;

public enum DraftStatus { Draft, Validated, Published, Discarded }

public static class ConfigHash
{
    public static string Of(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}

/// <summary>
/// An editable, unpublished configuration document. Editing resets validation; only a validated draft whose content has not changed since
/// validation can be published. Configuration is data validated against closed vocabularies: it never carries code, SQL or scripts.
/// </summary>
public sealed class ConfigurationDraft : ITenantOwned
{
    private ConfigurationDraft() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    /// <summary>Organisation the configuration applies to; <see cref="Guid.Empty"/> for tenant-level configuration.</summary>
    public Guid OrganisationId { get; private set; }
    public string Kind { get; private set; } = "";
    public string ContentJson { get; private set; } = "{}";
    public string ContentHash { get; private set; } = "";
    public DraftStatus Status { get; private set; }
    public string? ValidatedHash { get; private set; }
    public string? ValidationJson { get; private set; }
    public int? BasedOnReleaseNumber { get; private set; }
    public string LastEditedBy { get; private set; } = "";
    public DateTime CreatedUtc { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static ConfigurationDraft Create(Guid tenantId, Guid organisationId, string kind, string contentJson, int? basedOn, string editedBy, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = organisationId, Kind = Guard.NotBlank(kind, "Kind", 100),
        ContentJson = contentJson, ContentHash = ConfigHash.Of(contentJson), Status = DraftStatus.Draft, BasedOnReleaseNumber = basedOn,
        LastEditedBy = Guard.NotBlank(editedBy, "Edited by", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), UpdatedUtc = nowUtc
    };

    public void Edit(string contentJson, string editedBy, DateTime nowUtc)
    {
        if (Status is DraftStatus.Published or DraftStatus.Discarded) throw new DomainException("draft_closed", "This draft has been published or discarded; start a new draft.");
        ContentJson = contentJson; ContentHash = ConfigHash.Of(contentJson);
        Status = DraftStatus.Draft; ValidatedHash = null; ValidationJson = null;
        LastEditedBy = Guard.NotBlank(editedBy, "Edited by", 200); UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    public void RecordValidation(bool valid, string validationJson, DateTime nowUtc)
    {
        if (Status is DraftStatus.Published or DraftStatus.Discarded) throw new DomainException("draft_closed", "This draft has been published or discarded.");
        ValidationJson = validationJson; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        if (valid) { Status = DraftStatus.Validated; ValidatedHash = ContentHash; } else { Status = DraftStatus.Draft; ValidatedHash = null; }
    }

    public bool IsPublishable => Status == DraftStatus.Validated && ValidatedHash == ContentHash;

    public void MarkPublished(DateTime nowUtc) { Status = DraftStatus.Published; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); }
    public void Discard(DateTime nowUtc) { if (Status != DraftStatus.Published) { Status = DraftStatus.Discarded; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); } }
}

/// <summary>An immutable published version of a configuration document. Rolling back publishes an earlier version's content as a new release.</summary>
public sealed class ConfigurationRelease : ITenantOwned
{
    private ConfigurationRelease() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string Kind { get; private set; } = "";
    public int Number { get; private set; }
    public string ContentJson { get; private set; } = "{}";
    public string ContentHash { get; private set; } = "";
    public Guid? DraftId { get; private set; }
    public int? RollbackOfNumber { get; private set; }
    public string Note { get; private set; } = "";
    public string PublishedBy { get; private set; } = "";
    public DateTime PublishedUtc { get; private set; }

    public static ConfigurationRelease Create(Guid tenantId, Guid organisationId, string kind, int number, string contentJson, Guid? draftId, int? rollbackOf,
        string note, string publishedBy, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = organisationId, Kind = Guard.NotBlank(kind, "Kind", 100), Number = number,
        ContentJson = contentJson, ContentHash = ConfigHash.Of(contentJson), DraftId = draftId, RollbackOfNumber = rollbackOf,
        Note = Guard.OptionalBounded(note, "Note", 1000) ?? "", PublishedBy = Guard.NotBlank(publishedBy, "Published by", 200), PublishedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}
