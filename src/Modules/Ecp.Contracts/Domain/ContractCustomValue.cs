using Ecp.SharedKernel;

namespace Ecp.Contracts.Domain;

/// <summary>
/// One stored value of a custom field on a contract, in the typed column its field type uses. A multiple-choice field has one row per chosen item (ordinal 0, 1, ...). The field definition is
/// configuration, so adding a field never changes this table; the value remembers the definition release it was written under. Migrated values keep the batch and the verbatim source text.
/// </summary>
public sealed class ContractCustomValue : ITenantOwned
{
    private ContractCustomValue() { }

    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid ContractId { get; private set; }
    public string FieldKey { get; private set; } = "";
    public int Ordinal { get; private set; }

    public string? TextValue { get; private set; }
    public string? LongTextValue { get; private set; }
    public decimal? NumberValue { get; private set; }
    public DateOnly? DateValue { get; private set; }
    public DateTime? DateTimeValue { get; private set; }
    public bool? BoolValue { get; private set; }
    public Guid? RefValue { get; private set; }

    /// <summary>The release number of the field definitions in force when this value was written.</summary>
    public int DefinitionRelease { get; private set; }
    public DateTime UpdatedUtc { get; private set; }
    public string UpdatedBy { get; private set; } = "";
    public Guid? MigrationBatchId { get; private set; }
    public string? SourceSystem { get; private set; }
    /// <summary>What the source file said, verbatim, when the value was migrated (a choice or lookup mapping can change it on the way in).</summary>
    public string? SourceValue { get; private set; }

    public static ContractCustomValue Create(Guid tenantId, Guid organisationId, Guid contractId, string fieldKey, int ordinal, int definitionRelease, string actor, DateTime nowUtc,
        string? text = null, string? longText = null, decimal? number = null, DateOnly? date = null, DateTime? dateTimeUtc = null, bool? boolean = null, Guid? reference = null,
        Guid? migrationBatchId = null, string? sourceSystem = null, string? sourceValue = null)
        => new()
        {
            TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)), ContractId = Guard.NotEmpty(contractId, nameof(contractId)),
            FieldKey = Guard.NotBlank(fieldKey, "Field key", 64), Ordinal = ordinal, DefinitionRelease = definitionRelease, UpdatedBy = Guard.NotBlank(actor, "Updated by", 200), UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)),
            TextValue = text, LongTextValue = longText, NumberValue = number, DateValue = date, DateTimeValue = dateTimeUtc is null ? null : Guard.Utc(dateTimeUtc.Value, nameof(dateTimeUtc)), BoolValue = boolean, RefValue = reference,
            MigrationBatchId = migrationBatchId, SourceSystem = Guard.OptionalBounded(sourceSystem, "Source system", 100), SourceValue = Guard.OptionalBounded(sourceValue, "Source value", 450)
        };
}
