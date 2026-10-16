using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Contracts.Domain;

/// <summary>Default classification levels (final set is open: Q-06). Stored as configuration-backed codes.</summary>
public static class Classifications
{
    public const string Public = "Public";
    public const string Internal = "Internal";
    public const string Confidential = "Confidential";
    public const string Restricted = "Restricted";
    public static readonly IReadOnlyList<string> Default = [Public, Internal, Confidential, Restricted];
}

public enum RegisteredDateProvenance { Platform, Migration }

/// <summary>Authorises a legacy load of a record in a state of Effective or later (decision record, Registered Date exception).</summary>
public sealed record LegacyLoadAuthorisation(Guid MigrationBatchId, string ActingAdministrator, bool AllowRegisteredDateImport);

public sealed class Category : ITenantOwned
{
    private Category() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid? ParentCategoryId { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsActive { get; private set; } = true;
    public string? SourceSystem { get; private set; }
    public string? SourceRecordId { get; private set; }
    public Guid? MigrationBatchId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Category Create(Guid tenantId, Guid organisationId, string code, string name, Guid? parentCategoryId = null) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Code = Guard.NotBlank(code, "Category code", 100).ToUpperInvariant(), Name = Guard.NotBlank(name, "Category name", 200), ParentCategoryId = parentCategoryId
    };

    public void Deactivate() => IsActive = false;
}

public sealed partial class Supplier : ITenantOwned
{
    private Supplier() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string Name { get; private set; } = "";
    public string NormalisedName { get; private set; } = "";
    public string? LegalName { get; private set; }
    public string? Abn { get; private set; }
    public string? VendorNumber { get; private set; }
    public string Status { get; private set; } = "Active";
    public string? SourceSystem { get; private set; }
    public string? SourceRecordId { get; private set; }
    public Guid? MigrationBatchId { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Supplier Create(Guid tenantId, Guid organisationId, string name, string? abn, string? vendorNumber, DateTime nowUtc)
    {
        var cleanName = Guard.NotBlank(name, "Supplier name", 300);
        return new Supplier
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
            Name = cleanName, NormalisedName = NormaliseName(cleanName), Abn = NormaliseAbn(abn),
            VendorNumber = Guard.OptionalBounded(vendorNumber, "Vendor number", 50), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
    }

    /// <summary>Unicode NFKC, trim, collapse whitespace, case-fold. Exact match on this value is the only automatic supplier match.</summary>
    public static string NormaliseName(string name)
    {
        var n = name.Normalize(NormalizationForm.FormKC).Trim();
        n = Whitespace().Replace(n, " ");
        return n.ToUpperInvariant();
    }

    /// <summary>Format check only (eleven digits). Registry validation is an optional adapter and never blocks manual entry.</summary>
    public static string? NormaliseAbn(string? abn)
    {
        if (string.IsNullOrWhiteSpace(abn)) return null;
        var digits = new string(abn.Where(char.IsDigit).ToArray());
        if (digits.Length != 11 || abn.Any(c => !char.IsDigit(c) && c != ' '))
            throw new DomainException("invalid_abn", "ABN must be eleven digits.");
        return digits;
    }

    [GeneratedRegex(@"\s+", RegexOptions.NonBacktracking)]
    private static partial Regex Whitespace();
}

public sealed record NewContract(
    Guid TenantId, Guid OrganisationId, string AgreementReference, string Title, Guid SupplierId, Guid CategoryId,
    string InitialStatusCode, string CreatedBy, DateTime NowUtc, string? Description = null, string Classification = Classifications.Internal,
    string? CurrencyCode = null, decimal? EstimatedValue = null, string? ExtensionType = null, bool LegalReviewRequired = false,
    DateOnly? StartDate = null, DateOnly? EffectiveDate = null, DateOnly? ReviewDate = null, DateOnly? EndDate = null);

public sealed record SourceProvenance(string SourceSystem, string SourceRecordId, string? SourceAgreementReference, string? SourceStatus, DateTime? SourceExtractedUtc);

public sealed class Contract : ITenantOwned
{
    private Contract() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string AgreementReference { get; private set; } = "";
    public string? SourceAgreementReference { get; private set; }
    public string ContractTitle { get; private set; } = "";
    public string? Description { get; private set; }
    public Guid SupplierId { get; private set; }
    public Guid CategoryId { get; private set; }

    /// <summary>Workflow state code. Changed only through <see cref="ApplyWorkflowState"/> by the workflow engine (INV-02).</summary>
    public string Status { get; private set; } = "";
    public bool RecordLocked { get; private set; }
    public DateTime? LockedUtc { get; private set; }
    public string Classification { get; private set; } = Classifications.Internal;
    public string? CurrencyCode { get; private set; }
    public decimal? EstimatedValue { get; private set; }
    public string? ExtensionType { get; private set; }
    public bool LegalReviewRequired { get; private set; }
    public DateOnly? StartDate { get; private set; }
    public DateOnly? EffectiveDate { get; private set; }
    public DateOnly? ReviewDate { get; private set; }
    public DateOnly? EndDate { get; private set; }

    public DateTime? RegisteredDateUtc { get; private set; }
    public string? RegisteredBy { get; private set; }
    public RegisteredDateProvenance? RegisteredDateProvenance { get; private set; }
    public string? SourceRegisteredValue { get; private set; }

    public string? SourceSystem { get; private set; }
    public string? SourceRecordId { get; private set; }
    public string? SourceStatus { get; private set; }
    public DateTime? SourceExtractedUtc { get; private set; }
    public Guid? MigrationBatchId { get; private set; }
    public bool IsLegacyLoad { get; private set; }

    public DateTime CreatedUtc { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTime UpdatedUtc { get; private set; }
    public string UpdatedBy { get; private set; } = "";
    public byte[] RowVersion { get; private set; } = [];

    public static string NormaliseReference(string? reference)
        => Guard.NotBlank(reference, "Agreement reference", 100).ToUpperInvariant();

    public static Contract CreateDraft(NewContract n)
    {
        var c = Build(n);
        c.Status = Guard.NotBlank(n.InitialStatusCode, "Initial status", 100);
        return c;
    }

    /// <summary>
    /// Creates a record in a state of Effective or later from a previous system. The record is locked, flagged as a legacy load and
    /// keeps source provenance. The source Registered Date is imported only if the authorisation allows it (never fabricated).
    /// </summary>
    public static Contract CreateLegacyLoad(NewContract n, string statusCode, SourceProvenance source, LegacyLoadAuthorisation authorisation,
        DateTime? sourceRegisteredUtc, string? sourceRegisteredValue)
    {
        var c = Build(n);
        c.Status = Guard.NotBlank(statusCode, "Status", 100);
        c.IsLegacyLoad = true;
        c.MigrationBatchId = Guard.NotEmpty(authorisation.MigrationBatchId, nameof(authorisation.MigrationBatchId));
        var admin = Guard.NotBlank(authorisation.ActingAdministrator, "Acting migration administrator", 200);
        c.SourceSystem = Guard.NotBlank(source.SourceSystem, "Source system", 100);
        c.SourceRecordId = Guard.NotBlank(source.SourceRecordId, "Source record id", 200);
        c.SourceAgreementReference = Guard.OptionalBounded(source.SourceAgreementReference, "Source agreement reference", 200);
        c.SourceStatus = Guard.OptionalBounded(source.SourceStatus, "Source status", 200);
        c.SourceExtractedUtc = source.SourceExtractedUtc;
        c.RecordLocked = true;
        c.LockedUtc = n.NowUtc;
        if (authorisation.AllowRegisteredDateImport && sourceRegisteredUtc.HasValue)
        {
            c.RegisteredDateUtc = Guard.Utc(sourceRegisteredUtc.Value, "Registered date");
            c.RegisteredBy = admin; // the acting migration administrator; the original approver is not fabricated
            c.RegisteredDateProvenance = Domain.RegisteredDateProvenance.Migration;
            c.SourceRegisteredValue = Guard.OptionalBounded(sourceRegisteredValue, "Source registered value", 200);
        }
        return c;
    }

    private static Contract Build(NewContract n)
    {
        if (n.EstimatedValue is < 0) throw new DomainException("invalid_value", "Estimated value must not be negative.");
        string? currency = null;
        if (!string.IsNullOrWhiteSpace(n.CurrencyCode))
        {
            currency = n.CurrencyCode.Trim().ToUpperInvariant();
            if (!Regex.IsMatch(currency, "^[A-Z]{3}$", RegexOptions.NonBacktracking))
                throw new DomainException("invalid_currency", "Currency must be a three-letter ISO 4217 code.");
        }
        if (n.EstimatedValue.HasValue && currency is null)
            throw new DomainException("currency_required", "A currency is required when an estimated value is given.");
        if (n.StartDate.HasValue && n.EndDate.HasValue && n.EndDate < n.StartDate)
            throw new DomainException("invalid_dates", "End date must not be before the start date.");
        if (!Classifications.Default.Contains(n.Classification))
            throw new DomainException("invalid_classification", $"Classification '{n.Classification}' is not recognised.");

        var actor = Guard.NotBlank(n.CreatedBy, "Created by", 200);
        return new Contract
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(n.TenantId, nameof(n.TenantId)), OrganisationId = Guard.NotEmpty(n.OrganisationId, nameof(n.OrganisationId)),
            AgreementReference = NormaliseReference(n.AgreementReference), ContractTitle = Guard.NotBlank(n.Title, "Contract title", 500),
            Description = Guard.OptionalBounded(n.Description, "Description", 4000), SupplierId = Guard.NotEmpty(n.SupplierId, nameof(n.SupplierId)),
            CategoryId = Guard.NotEmpty(n.CategoryId, nameof(n.CategoryId)), Classification = n.Classification, CurrencyCode = currency,
            EstimatedValue = n.EstimatedValue, ExtensionType = Guard.OptionalBounded(n.ExtensionType, "Extension type", 50),
            LegalReviewRequired = n.LegalReviewRequired, StartDate = n.StartDate, EffectiveDate = n.EffectiveDate, ReviewDate = n.ReviewDate,
            EndDate = n.EndDate, CreatedUtc = Guard.Utc(n.NowUtc, nameof(n.NowUtc)), CreatedBy = actor, UpdatedUtc = n.NowUtc, UpdatedBy = actor
        };
    }

    /// <summary>Edits descriptive fields. Refused on a locked record for every caller (INV-03).</summary>
    public void UpdateDetails(string title, string? description, string updatedBy, DateTime nowUtc)
    {
        EnsureEditable();
        ContractTitle = Guard.NotBlank(title, "Contract title", 500);
        Description = Guard.OptionalBounded(description, "Description", 4000);
        Touch(updatedBy, nowUtc);
    }

    /// <summary>Called only by the workflow engine. Sets the state code and, when the target state locks, locks the record.</summary>
    public void ApplyWorkflowState(string statusCode, bool locks, string actor, DateTime nowUtc)
    {
        Status = Guard.NotBlank(statusCode, "Status", 100);
        if (locks && !RecordLocked) { RecordLocked = true; LockedUtc = nowUtc; }
        Touch(actor, nowUtc);
    }

    /// <summary>Stamps Registered Date once, on successful register authorisation (INV-05).</summary>
    public void StampRegistration(string registeredBy, DateTime nowUtc)
    {
        if (RegisteredDateUtc.HasValue)
            throw new DomainException("already_registered", "Registered Date has already been stamped and cannot be changed.");
        RegisteredDateUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        RegisteredBy = Guard.NotBlank(registeredBy, "Registered by", 200);
        RegisteredDateProvenance = Domain.RegisteredDateProvenance.Platform;
        Touch(registeredBy, nowUtc);
    }

    private void EnsureEditable()
    {
        if (RecordLocked)
            throw new DomainException("record_locked", "This record is locked. Changes to an Effective record require an authorised Variation or an audited emergency unlock.");
    }

    private void Touch(string by, DateTime nowUtc) { UpdatedBy = Guard.NotBlank(by, "Updated by", 200); UpdatedUtc = nowUtc; }
}
