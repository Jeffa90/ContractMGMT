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
    /// <summary>Optional owning division, used to scope delegated (Division/Site) administration of the taxonomy.</summary>
    public Guid? DivisionId { get; private set; }
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
    public void Activate() => IsActive = true;
    public void Rename(string name) => Name = Guard.NotBlank(name, "Category name", 200);
    public void SetDivision(Guid? divisionId) => DivisionId = divisionId;

    /// <summary>Records that a migration batch created this category (INV-41).</summary>
    public void MarkMigrated(string sourceSystem, string sourceRecordId, Guid batchId)
    {
        SourceSystem = Guard.NotBlank(sourceSystem, "Source system", 100);
        SourceRecordId = Guard.NotBlank(sourceRecordId, "Source record id", 200);
        MigrationBatchId = Guard.NotEmpty(batchId, nameof(batchId));
    }
}

/// <summary>
/// Effective-dated ownership of a category by a user (IDN-04). Edit rights follow the assignment that is active at the moment of the action;
/// ending an assignment removes them immediately and history is retained. Ownership of a category covers its subcategories.
/// </summary>
public sealed class CategoryAssignment : ITenantOwned
{
    private CategoryAssignment() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid CategoryId { get; private set; }
    public Guid UserId { get; private set; }
    public bool IsPrimary { get; private set; }
    public DateTime EffectiveFromUtc { get; private set; }
    public DateTime? EffectiveToUtc { get; private set; }
    public string AssignedByLabel { get; private set; } = "";
    public string? Reason { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CategoryAssignment Create(Guid tenantId, Guid organisationId, Guid categoryId, Guid userId, bool isPrimary, DateTime effectiveFromUtc,
        DateTime? effectiveToUtc, string assignedBy, string? reason, DateTime nowUtc)
    {
        if (effectiveToUtc.HasValue && Guard.Utc(effectiveToUtc.Value, nameof(effectiveToUtc)) <= Guard.Utc(effectiveFromUtc, nameof(effectiveFromUtc)))
            throw new DomainException("invalid_dates", "The assignment must end after it starts.");
        return new CategoryAssignment
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
            CategoryId = Guard.NotEmpty(categoryId, nameof(categoryId)), UserId = Guard.NotEmpty(userId, nameof(userId)), IsPrimary = isPrimary,
            EffectiveFromUtc = effectiveFromUtc, EffectiveToUtc = effectiveToUtc, AssignedByLabel = Guard.NotBlank(assignedBy, "Assigned by", 200),
            Reason = Guard.OptionalBounded(reason, "Reason", 1000), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
    }

    public bool IsActiveAt(DateTime utc) => EffectiveFromUtc <= utc && (EffectiveToUtc is null || utc < EffectiveToUtc);

    public void End(DateTime nowUtc)
    {
        Guard.Utc(nowUtc, nameof(nowUtc));
        if (EffectiveToUtc is null || EffectiveToUtc > nowUtc) EffectiveToUtc = nowUtc < EffectiveFromUtc ? EffectiveFromUtc : nowUtc;
    }
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

    /// <summary>Records that a migration batch created this supplier (INV-41). The source record id is the vendor number, or the normalised name where there is none.</summary>
    public void MarkMigrated(string sourceSystem, string sourceRecordId, Guid batchId)
    {
        SourceSystem = Guard.NotBlank(sourceSystem, "Source system", 100);
        SourceRecordId = Guard.NotBlank(sourceRecordId, "Source record id", 200);
        MigrationBatchId = Guard.NotEmpty(batchId, nameof(batchId));
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

/// <summary>Last Agreement Reference number issued for an organisation. Numbers are allocated transactionally and never reused (D3).</summary>
public sealed class ReferenceCounter : ITenantOwned
{
    private ReferenceCounter() { }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public long LastValue { get; private set; }

    public static ReferenceCounter Start(Guid tenantId, Guid organisationId) => new() { TenantId = tenantId, OrganisationId = organisationId, LastValue = 0 };
}

public sealed record NewContract(
    Guid TenantId, Guid OrganisationId, string AgreementReference, string Title, Guid SupplierId, Guid CategoryId,
    string InitialStatusCode, string CreatedBy, DateTime NowUtc, string? Description = null, string Classification = Classifications.Internal,
    string? CurrencyCode = null, decimal? EstimatedValue = null, string? ExtensionType = null, bool LegalReviewRequired = false,
    DateOnly? StartDate = null, DateOnly? EffectiveDate = null, DateOnly? ReviewDate = null, DateOnly? EndDate = null,
    Guid? BusinessUnitId = null, Guid? SiteId = null);

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
    public Guid? BusinessUnitId { get; private set; }
    public Guid? SiteId { get; private set; }

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

    /// <summary>Compensating archival (never a delete): an archived record is hidden from lists and counts but stays readable for provenance and audit.</summary>
    public bool IsArchived { get; private set; }
    public DateTime? ArchivedUtc { get; private set; }
    public string? ArchiveReason { get; private set; }
    public Guid? ArchivedByBatchId { get; private set; }

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

    /// <summary>
    /// Creates an unlocked record in the initial workflow state from a previous system. It keeps source provenance and the batch id but is not a
    /// legacy load: it is an ordinary editable record that has simply arrived by migration.
    /// </summary>
    public static Contract CreateMigrated(NewContract n, SourceProvenance source, Guid migrationBatchId)
    {
        var c = Build(n);
        c.Status = Guard.NotBlank(n.InitialStatusCode, "Initial status", 100);
        c.MigrationBatchId = Guard.NotEmpty(migrationBatchId, nameof(migrationBatchId));
        c.SourceSystem = Guard.NotBlank(source.SourceSystem, "Source system", 100);
        c.SourceRecordId = Guard.NotBlank(source.SourceRecordId, "Source record id", 200);
        c.SourceAgreementReference = Guard.OptionalBounded(source.SourceAgreementReference, "Source agreement reference", 200);
        c.SourceStatus = Guard.OptionalBounded(source.SourceStatus, "Source status", 200);
        c.SourceExtractedUtc = source.SourceExtractedUtc;
        return c;
    }

    /// <summary>The core fields a migration may set on an existing record, by their stable field names.</summary>
    public static readonly IReadOnlyList<string> MigratableFields =
    [
        "contractTitle", "description", "status", "supplierId", "categoryId", "currencyCode", "estimatedValue", "extensionType",
        "legalReviewRequired", "startDate", "effectiveDate", "reviewDate", "endDate"
    ];

    /// <summary>Current values of the named fields as invariant text. This is the before-image stored in the provenance record.</summary>
    public IReadOnlyDictionary<string, string?> CaptureFields(IEnumerable<string> names)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in names)
            result[name] = name switch
            {
                "contractTitle" => ContractTitle,
                "description" => Description,
                "status" => Status,
                "supplierId" => SupplierId.ToString("D"),
                "categoryId" => CategoryId.ToString("D"),
                "currencyCode" => CurrencyCode,
                "estimatedValue" => EstimatedValue?.ToString(CultureInfo.InvariantCulture),
                "extensionType" => ExtensionType,
                "legalReviewRequired" => LegalReviewRequired ? "true" : "false",
                "startDate" => StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "effectiveDate" => EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "reviewDate" => ReviewDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "endDate" => EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => throw new DomainException("unknown_field", $"'{name}' is not a field a migration can set.")
            };
        return result;
    }

    /// <summary>
    /// Applies only the named fields. Used by an Update-mapped-fields migration and by its rollback (restoring a before-image). A locked record is changed
    /// only when the caller states that the migration policy authorises it; nothing else about the record changes.
    /// </summary>
    public void ApplyMigratedFields(IReadOnlyDictionary<string, string?> fields, bool lockedOverwriteAuthorised, string actor, DateTime nowUtc)
    {
        if (RecordLocked && !lockedOverwriteAuthorised)
            throw new DomainException("record_locked", "This record is locked. A migration may overwrite it only under an explicit legacy-load policy.");
        var title = ContractTitle; var description = Description; var status = Status; var supplier = SupplierId; var category = CategoryId;
        var currency = CurrencyCode; var value = EstimatedValue; var extension = ExtensionType; var legal = LegalReviewRequired;
        var start = StartDate; var effective = EffectiveDate; var review = ReviewDate; var end = EndDate;
        foreach (var (name, raw) in fields)
        {
            switch (name)
            {
                case "contractTitle": title = Guard.NotBlank(raw, "Contract title", 500); break;
                case "description": description = Guard.OptionalBounded(raw, "Description", 4000); break;
                case "status": status = Guard.NotBlank(raw, "Status", 100); break;
                case "supplierId": supplier = Guard.NotEmpty(Guid.Parse(raw ?? ""), "Supplier"); break;
                case "categoryId": category = Guard.NotEmpty(Guid.Parse(raw ?? ""), "Category"); break;
                case "currencyCode": currency = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToUpperInvariant(); break;
                case "estimatedValue": value = string.IsNullOrWhiteSpace(raw) ? null : decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture); break;
                case "extensionType": extension = Guard.OptionalBounded(raw, "Extension type", 50); break;
                case "legalReviewRequired": legal = string.Equals(raw, "true", StringComparison.Ordinal); break;
                case "startDate": start = ParseDate(raw); break;
                case "effectiveDate": effective = ParseDate(raw); break;
                case "reviewDate": review = ParseDate(raw); break;
                case "endDate": end = ParseDate(raw); break;
                default: throw new DomainException("unknown_field", $"'{name}' is not a field a migration can set.");
            }
        }
        if (value is < 0) throw new DomainException("invalid_value", "Estimated value must not be negative.");
        if (currency is not null && !Regex.IsMatch(currency, "^[A-Z]{3}$", RegexOptions.NonBacktracking)) throw new DomainException("invalid_currency", "Currency must be a three-letter ISO 4217 code.");
        if (value.HasValue && currency is null) throw new DomainException("currency_required", "A currency is required when an estimated value is given.");
        if (start.HasValue && end.HasValue && end < start) throw new DomainException("invalid_dates", "End date must not be before the start date.");
        (ContractTitle, Description, Status, SupplierId, CategoryId, CurrencyCode, EstimatedValue, ExtensionType, LegalReviewRequired, StartDate, EffectiveDate, ReviewDate, EndDate) =
            (title, description, status, supplier, category, currency, value, extension, legal, start, effective, review, end);
        Touch(actor, nowUtc);
    }

    private static DateOnly? ParseDate(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : DateOnly.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Compensating archival of a migrated record. The record stays readable; it leaves lists and counts.</summary>
    public void Archive(string reason, Guid batchId, string actor, DateTime nowUtc)
    {
        if (IsArchived) return;
        IsArchived = true; ArchivedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); ArchiveReason = Guard.NotBlank(reason, "Archive reason", 1000); ArchivedByBatchId = batchId;
        Touch(actor, nowUtc);
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
            EndDate = n.EndDate, BusinessUnitId = n.BusinessUnitId, SiteId = n.SiteId, CreatedUtc = Guard.Utc(n.NowUtc, nameof(n.NowUtc)), CreatedBy = actor, UpdatedUtc = n.NowUtc, UpdatedBy = actor
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
