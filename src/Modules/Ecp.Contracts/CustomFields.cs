using System.Globalization;
using Ecp.Audit;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Metadata;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

/// <summary>One custom field as a person sees it on a contract: the definition, what is stored (or what they typed), and whether they may see it and change it.</summary>
public sealed record CustomFieldEntry(FieldDefinition Definition, IReadOnlyList<string> Raw, string Display, bool Editable, string? Error = null)
{
    public bool HasValue => Raw.Count > 0;
}

public sealed record CustomFieldForm(int Release, CustomFieldSet Set, IReadOnlyList<CustomFieldEntry> Fields, IReadOnlyList<PersonName> People)
{
    /// <summary>The visible fields grouped by section in the configured order. A section with nothing to show is left out.</summary>
    public IEnumerable<(FieldSection Section, IReadOnlyList<CustomFieldEntry> Fields)> Sections()
    {
        var sections = Set.OrderedSections.ToList();
        if (sections.All(s => s.Key != "general")) sections.Insert(0, new FieldSection("general", "Additional details", int.MinValue));
        foreach (var s in sections)
        {
            var inSection = Fields.Where(f => string.Equals(f.Definition.Section, s.Key, StringComparison.Ordinal)).OrderBy(f => f.Definition.Order).ThenBy(f => f.Definition.Label, StringComparer.Ordinal).ToList();
            if (inSection.Count > 0) yield return (s, inSection);
        }
    }
    public bool AnyEditable => Fields.Any(f => f.Editable);
}

/// <summary>Raised when entered custom field values are not acceptable. The messages are by field key so a form can show each beside its field.</summary>
public sealed class CustomFieldValidationException(IReadOnlyDictionary<string, string> errors)
    : DomainException("custom_field_invalid", "Some of the additional details need attention: " + string.Join(" ", errors.Values.Take(3)))
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>Form input for custom fields: the field key to the raw text(s) entered (a multiple-choice field posts several).</summary>
public sealed class CustomFieldInput : Dictionary<string, IReadOnlyList<string?>>
{
    public CustomFieldInput() : base(StringComparer.OrdinalIgnoreCase) { }
    public CustomFieldInput(IEnumerable<KeyValuePair<string, IReadOnlyList<string?>>> items) : base(items, StringComparer.OrdinalIgnoreCase) { }
    public static CustomFieldInput From(IReadOnlyDictionary<string, string?> single)
        => new(single.Select(kv => new KeyValuePair<string, IReadOnlyList<string?>>(kv.Key, kv.Value is null ? [] : [kv.Value])));
}

/// <summary>
/// Reading and writing the configured fields of a contract for the signed-in person. Every call applies the contract's own access rules first, then each field's rules: role, lifecycle state,
/// contract type and classification. A field the person may not see is not returned at all (not blanked), so nothing about it reaches a page, an export or a log.
/// </summary>
public interface IContractCustomFields
{
    /// <summary>The fields for a contract that does not exist yet, with defaults filled in (or the values just submitted, with their errors, when a form is shown again).</summary>
    Task<CustomFieldForm> ForNewAsync(Guid organisationId, string? contractType, CustomFieldInput? submitted = null, IReadOnlyDictionary<string, string>? errors = null, CancellationToken ct = default);
    /// <summary>The fields of an existing contract the signed-in person may see, with their values.</summary>
    Task<CustomFieldForm> ForContractAsync(Guid contractId, CustomFieldInput? submitted = null, IReadOnlyDictionary<string, string>? errors = null, CancellationToken ct = default);
    /// <summary>Saves the submitted values (a field not present is left alone; a blank value clears it). Needs the right to edit the contract, which must not be locked or frozen. Audited with before and after.</summary>
    Task<IReadOnlyList<FieldChange>> SaveAsync(Guid contractId, CustomFieldInput input, CancellationToken ct = default);
    /// <summary>Checks values for a contract about to be created, as the signed-in person, before anything is written. Throws <see cref="CustomFieldValidationException"/>.</summary>
    Task ValidateForNewAsync(Guid organisationId, string? contractType, string initialStatus, CustomFieldInput input, CancellationToken ct = default);
    /// <summary>The fields the signed-in person may propose to change by Variation (current, applicable, role-editable; the workflow state does not matter because a Variation exists for locked records).</summary>
    Task<IReadOnlyList<FieldDefinition>> ProposableAsync(Guid contractId, CancellationToken ct = default);
}

/// <summary>Trusted reads of stored values for other modules that judge a contract (the approval matrix). Only fields an administrator has marked usable in approvals are returned.</summary>
public interface IContractCustomFieldReader
{
    /// <summary>Key to the stored value (text, in invariant form) of each current, applicable field that may feed an approval matrix. A field with no value is absent.</summary>
    Task<IReadOnlyDictionary<string, string?>> ApprovalFactsAsync(Guid contractId, CancellationToken ct = default);
    /// <summary>The keys of the fields that may feed an approval matrix for the organisation.</summary>
    Task<IReadOnlyList<string>> ApprovalFactKeysAsync(Guid organisationId, CancellationToken ct = default);
}

/// <summary>Trusted writes for the lifecycle (applying a Variation) and for migration. The caller has already authorised the actor; types, choices and lengths are still enforced.</summary>
public interface IContractCustomFieldWriter
{
    /// <summary>Validates proposed changes (key to raw text) and returns what would change. Nothing is saved.</summary>
    Task<IReadOnlyList<FieldChange>> PreviewAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default);
    /// <summary>Applies them inside the caller's unit of work; the audit event carries the authority (for example the Variation).</summary>
    Task<IReadOnlyList<FieldChange>> ApplyAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, string actor, string authorityKind, Guid authorityId, string? reason, CancellationToken ct = default);
    /// <summary>Writes values for a contract being created or loaded (no role, state or lock rules; required is not enforced).</summary>
    Task WriteNewAsync(Guid contractId, CustomFieldInput input, string actor, CancellationToken ct = default);
}

internal sealed class ContractCustomFieldService(
    ContractsDbContext db, ICustomFieldCatalogue catalogue, IAccessService access, ContractAccessContext context, IUnitOfWork uow, IAuditWriter audit, IClock clock,
    CurrentUser currentUser, IPeopleDirectory people, Ecp.Documents.IDocumentQuery documents, IOrganisationStructureService structure) : IContractCustomFields, IContractCustomFieldWriter, IContractCustomFieldReader
{
    private string Actor => currentUser.UserId?.ToString() ?? "system";

    private async Task<(CultureInfo Culture, TimeZoneInfo Zone, DateOnly Today)> LocaleAsync(Guid organisationId, CancellationToken ct)
    {
        var org = await structure.GetOrganisationAsync(organisationId, ct);
        var zone = org is null ? TimeZoneInfo.Utc : TimeZones.Find(org.TimeZoneId);
        var culture = org is null ? CultureInfo.GetCultureInfo("en-AU") : CultureInfo.GetCultureInfo(org.CultureCode);
        return (culture, zone, TimeZones.LocalDate(clock.UtcNow, zone));
    }

    private async Task<IReadOnlySet<string>> RolesAsync(Guid organisationId, CancellationToken ct)
    {
        var subject = await access.GetSubjectAsync(ct);
        var now = clock.UtcNow;
        return subject is null ? new HashSet<string>() : subject.Assignments.Where(a => a.IsActiveAt(now) && (a.Scope.Level is ScopeLevel.Tenant or ScopeLevel.Installation || a.Scope.OrganisationIds.Contains(organisationId))).Select(a => a.RoleCode).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, List<TypedValue>>> StoredAsync(Guid contractId, CustomFieldSet set, CancellationToken ct)
    {
        var rows = await db.CustomValues.AsNoTracking().Where(v => v.ContractId == contractId).OrderBy(v => v.FieldKey).ThenBy(v => v.Ordinal).ToListAsync(ct);
        var result = new Dictionary<string, List<TypedValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var def = set.Find(r.FieldKey);
            var value = def is null ? new TypedValue(Text: r.TextValue ?? r.LongTextValue) : ToTyped(r, FieldTypes.StorageOf(def.Type));
            (result.TryGetValue(r.FieldKey, out var list) ? list : result[r.FieldKey] = []).Add(value);
        }
        return result;
    }

    internal static TypedValue ToTyped(ContractCustomValue r, StorageKind kind) => kind switch
    {
        StorageKind.Text => new TypedValue(Text: r.TextValue),
        StorageKind.LongText => new TypedValue(Text: r.LongTextValue),
        StorageKind.Number => new TypedValue(Number: r.NumberValue),
        StorageKind.Date => new TypedValue(Date: r.DateValue),
        StorageKind.DateTime => new TypedValue(DateTimeUtc: r.DateTimeValue),
        StorageKind.Bool => new TypedValue(Bool: r.BoolValue),
        StorageKind.Ref => new TypedValue(Ref: r.RefValue),
        _ => new TypedValue()
    };

    private ContractCustomValue ToRow(Guid tenantId, Guid organisationId, Guid contractId, FieldDefinition d, TypedValue v, int ordinal, int release, string actor, DateTime now,
        Guid? batch = null, string? sourceSystem = null, string? sourceValue = null)
    {
        var kind = FieldTypes.StorageOf(d.Type);
        return ContractCustomValue.Create(tenantId, organisationId, contractId, d.Key, ordinal, release, actor, now,
            text: kind == StorageKind.Text ? v.Text : null, longText: kind == StorageKind.LongText ? v.Text : null, number: kind == StorageKind.Number ? v.Number : null, date: kind == StorageKind.Date ? v.Date : null,
            dateTimeUtc: kind == StorageKind.DateTime ? v.DateTimeUtc : null, boolean: kind == StorageKind.Bool ? v.Bool : null, reference: kind == StorageKind.Ref ? v.Ref : null,
            migrationBatchId: batch, sourceSystem: sourceSystem, sourceValue: sourceValue);
    }

    /// <summary>The level a field's value is held at: the more sensitive of the field's own level and the contract's. The reader needs the right to read the contract at that level.</summary>
    private static string LevelOf(FieldDefinition d, string contractClassification, ClassificationScale scale)
        => scale.Rank(d.Classification) > scale.Rank(contractClassification) ? d.Classification : contractClassification;

    private async Task<Func<FieldDefinition, bool>> ClassificationGateAsync(ResourceDescriptor contract, string contractClassification, CancellationToken ct)
    {
        var scale = await access.ClassificationScaleAsync(ct);
        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var level in scale.Levels) allowed[level] = (await access.CheckSilentAsync("contracts.read", contract with { Classification = level }, ct)).Allowed;
        return d => allowed.TryGetValue(LevelOf(d, contractClassification, scale), out var ok) && ok;
    }

    // ---- reading ---------------------------------------------------------------------------------------------------------------------------------------

    public async Task<CustomFieldForm> ForContractAsync(Guid contractId, CustomFieldInput? submitted = null, IReadOnlyDictionary<string, string>? errors = null, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(c, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        var set = await catalogue.PublishedAsync(c.OrganisationId, ct);
        var stored = await StoredAsync(contractId, set, ct);
        var roles = await RolesAsync(c.OrganisationId, ct);
        var (culture, zone, today) = await LocaleAsync(c.OrganisationId, ct);
        var gate = await ClassificationGateAsync(descriptor, c.Classification, ct);
        var canEdit = !c.IsProtectedAt(clock.UtcNow) && (await access.CheckSilentAsync("contracts.edit_owned", descriptor, ct)).Allowed;
        var visibleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<CustomFieldEntry>();
        foreach (var d in set.Fields)
        {
            var has = stored.ContainsKey(d.Key);
            var fc = new FieldContext(roles, c.Status, c.ContractType, today, has);
            if (!FieldRules.Visible(d, fc) || !gate(d)) continue;
            visibleKeys.Add(d.Key);
            var editable = canEdit && FieldRules.Editable(d, fc);
            var values = stored.GetValueOrDefault(d.Key) ?? [];
            var raw = (IReadOnlyList<string>)ValueCodec.ToRaw(d, values);
            if (editable && submitted is not null && submitted.TryGetValue(d.Key, out var typed)) raw = typed.Select(x => x ?? "").ToList();
            var display = d.IsCalculated ? "" : ValueCodec.Format(d, set, values, culture, zone, c.CurrencyCode);
            entries.Add(new CustomFieldEntry(d, raw, display, editable, errors?.GetValueOrDefault(d.Key)));
        }
        // A calculated value is shown only when everything it is built from is visible to this person, so it cannot be used to read a hidden value.
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.Definition.IsCalculated) continue;
            var operands = e.Definition.Calculated!.Operands.Concat(PlaceholdersOf(e.Definition.Calculated.Template)).ToList();
            var shown = operands.All(o => CoreFields.IsCore(o) ? (CoreAllowed(o, stored, c) is not false) : visibleKeys.Contains(o));
            var text = shown ? Calculate(e.Definition, set, stored, c, culture, zone) : null;
            entries[i] = e with { Display = text ?? "" };
        }
        var people_ = entries.Any(e => e.Editable && e.Definition.Type == FieldTypes.Person) ? await people.ColleaguesAsync(c.OrganisationId, ct) : [];
        return new CustomFieldForm(set.ReleaseNumber, set, entries, people_);
    }

    private static IEnumerable<string> PlaceholdersOf(string? template)
        => template is null ? [] : System.Text.RegularExpressions.Regex.Matches(template, @"\{([^{}]*)\}", System.Text.RegularExpressions.RegexOptions.NonBacktracking).Select(m => m.Groups[1].Value);

    private static bool? CoreAllowed(string key, Dictionary<string, List<TypedValue>> stored, Contract c) => true;

    private string? Calculate(FieldDefinition d, CustomFieldSet set, Dictionary<string, List<TypedValue>> stored, Contract c, CultureInfo culture, TimeZoneInfo zone)
    {
        decimal? Number(string k) => CoreFields.IsCore(k) ? k switch
        {
            "estimatedValue" => c.EstimatedValue, "noticePeriodValue" => c.NoticePeriodValue, _ => null
        } : stored.GetValueOrDefault(k)?.FirstOrDefault()?.Number;
        DateOnly? Date(string k) => CoreFields.IsCore(k) ? k switch
        {
            "startDate" => c.StartDate, "effectiveDate" => c.EffectiveDate, "reviewDate" => c.ReviewDate, "endDate" => c.EndDate,
            "registeredDate" => c.RegisteredDateUtc is { } r ? DateOnly.FromDateTime(r) : null, _ => null
        } : stored.GetValueOrDefault(k) is { Count: > 0 } v ? v[0].Date ?? (v[0].DateTimeUtc is { } dt ? DateOnly.FromDateTime(dt) : null) : null;
        string? Text(string k)
        {
            if (CoreFields.IsCore(k)) return k switch
            {
                "agreementReference" => c.AgreementReference, "contractTitle" => c.ContractTitle, "currencyCode" => c.CurrencyCode, "contractType" => c.ContractType, "riskLevel" => c.RiskLevel,
                "estimatedValue" => c.EstimatedValue?.ToString("#,0.00", culture), "endDate" => c.EndDate?.ToString("d", culture), "startDate" => c.StartDate?.ToString("d", culture), _ => null
            };
            return set.Find(k) is { } f && stored.TryGetValue(k, out var vals) ? ValueCodec.Format(f, set, vals, culture, zone, c.CurrencyCode) : null;
        }
        return Calculator.Evaluate(d, Number, Date, Text, culture);
    }

    public async Task<CustomFieldForm> ForNewAsync(Guid organisationId, string? contractType, CustomFieldInput? submitted = null, IReadOnlyDictionary<string, string>? errors = null, CancellationToken ct = default)
    {
        var grants = await access.GrantsAsync("contracts.create", ct);
        if (!grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(organisationId))) throw new ForbiddenException("You cannot create contracts in this organisation.");
        var set = await catalogue.PublishedAsync(organisationId, ct);
        var roles = await RolesAsync(organisationId, ct);
        var (culture, zone, today) = await LocaleAsync(organisationId, ct);
        var entries = new List<CustomFieldEntry>();
        foreach (var d in set.Fields)
        {
            var fc = new FieldContext(roles, "", contractType, today, false);
            if (!FieldRules.IsCurrent(d, today) || !FieldRules.AppliesToType(d, contractType) || d.IsCalculated) continue;
            if (d.VisibleToRoles.Count > 0 && !d.VisibleToRoles.Any(roles.Contains)) continue;
            var editable = FieldRules.Editable(d with { VisibleInStates = [], EditableInStates = [] }, fc);
            if (!editable) continue;      // a field the creator cannot fill in has no place on the Create page; it can be filled in later by whoever may
            var raw = submitted is not null && submitted.TryGetValue(d.Key, out var typed) ? typed.Select(x => x ?? "").ToList() : (IReadOnlyList<string>)d.Default;
            entries.Add(new CustomFieldEntry(d, raw, "", true, errors?.GetValueOrDefault(d.Key)));
        }
        var people_ = entries.Any(e => e.Definition.Type == FieldTypes.Person) ? await people.ColleaguesAsync(organisationId, ct) : [];
        return new CustomFieldForm(set.ReleaseNumber, set, entries, people_);
    }

    public async Task<IReadOnlyList<FieldDefinition>> ProposableAsync(Guid contractId, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(c, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        var set = await catalogue.PublishedAsync(c.OrganisationId, ct);
        var stored = await StoredAsync(contractId, set, ct);
        var roles = await RolesAsync(c.OrganisationId, ct);
        var (_, _, today) = await LocaleAsync(c.OrganisationId, ct);
        var gate = await ClassificationGateAsync(descriptor, c.Classification, ct);
        return set.Fields.Where(d => !d.IsCalculated && FieldRules.IsCurrent(d, today) && FieldRules.AppliesToType(d, c.ContractType) && gate(d)
            && (d.VisibleToRoles.Count == 0 || d.VisibleToRoles.Any(roles.Contains)) && (d.EditableByRoles.Count == 0 || d.EditableByRoles.Any(roles.Contains))).ToList();
    }

    // ---- validating and writing -----------------------------------------------------------------------------------------------------------------------------

    private sealed record Plan(Dictionary<string, List<TypedValue>> After, List<FieldChange> Changes, Dictionary<string, string> Errors);

    /// <summary>The shared rule engine. <paramref name="canEdit"/> says which fields the writer may change (null means all: a trusted write).</summary>
    private async Task<Plan> PlanAsync(CustomFieldSet set, Dictionary<string, List<TypedValue>> stored, CustomFieldInput input, Func<FieldDefinition, bool>? canEdit, bool enforceRequired, bool applyDefaults, CancellationToken ct, Guid? contractId = null)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var after = stored.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        var changes = new List<FieldChange>();
        foreach (var (key, raw) in input)
        {
            var d = set.Find(key);
            if (d is null) { errors[key] = $"'{key}' is not a field of this organisation."; continue; }
            var keep = stored.TryGetValue(d.Key, out var old) ? ValueCodec.ToRaw(d, old).ToHashSet(StringComparer.Ordinal) : null;
            if (canEdit is not null && !canEdit(d))
            {
                // A form that posts back a read-only field with the value it already holds changes nothing; any other value is refused.
                var echoed = ValueCodec.Parse(d, set, raw.Count == 1 && FieldTypes.IsMulti(d.Type) && raw[0] is { } j && j.Contains(';') ? j.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : raw, keep);
                var current = old is null ? [] : ValueCodec.ToRaw(d, old);
                if (echoed.IsValid && echoed.Values.Count == current.Count && ValueCodec.ToRaw(d, echoed.Values).SequenceEqual(current, StringComparer.Ordinal)) continue;
                errors[d.Key] = $"You cannot change {d.Label}."; continue;
            }
            // A multiple-choice value may arrive as one text with the items joined by ';' (a choice code never contains one).
            IReadOnlyList<string?> items = FieldTypes.IsMulti(d.Type) && raw.Count == 1 && raw[0] is { } joined && joined.Contains(';') ? joined.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : raw;
            var parsed = ValueCodec.Parse(d, set, items, keep);
            if (!parsed.IsValid) { errors[d.Key] = parsed.Errors[0]; continue; }
            if (parsed.IsEmpty) after.Remove(d.Key); else after[d.Key] = parsed.Values.ToList();
        }
        if (applyDefaults)
            foreach (var d in set.Fields.Where(f => f.Default.Count > 0 && !f.IsCalculated && !after.ContainsKey(f.Key) && !input.ContainsKey(f.Key) && (canEdit is null || canEdit(f))))
            {
                var parsed = ValueCodec.Parse(d, set, d.Default);
                if (parsed.IsValid && !parsed.IsEmpty) after[d.Key] = parsed.Values.ToList();
            }
        if (enforceRequired)
            foreach (var d in set.Fields.Where(f => f.Required && !f.IsCalculated && (canEdit is null || canEdit(f))))
                if (!after.ContainsKey(d.Key) && !errors.ContainsKey(d.Key)) errors[d.Key] = $"{d.Label} is required.";
        // Person and file references must really exist for the caller (a person this tenant knows, a document of this contract).
        foreach (var (key, values) in after.Where(kv => input.ContainsKey(kv.Key)).ToList())
        {
            var d = set.Find(key)!;
            if (d.Type == FieldTypes.Person)
            {
                var names = await people.DisplayNamesAsync(values.Where(v => v.Ref is not null).Select(v => v.Ref!.Value), ct);
                if (values.Any(v => v.Ref is { } id && !names.ContainsKey(id))) errors[key] = $"{d.Label} must be a person from the list.";
            }
            else if (d.Type == FieldTypes.FileReference)
            {
                foreach (var v in values.Where(v => v.Ref is not null))
                    if (contractId is null || await documents.GetAsync(v.Ref!.Value, ct) is not { } doc || doc.ContractId != contractId) errors[key] = $"{d.Label} must be a document attached to this contract.";
            }
        }
        foreach (var d in set.Fields)
        {
            var before = stored.TryGetValue(d.Key, out var b) ? ValueCodec.ToRaw(d, b) : [];
            var now = after.TryGetValue(d.Key, out var a) ? ValueCodec.ToRaw(d, a) : [];
            if (!before.SequenceEqual(now, StringComparer.Ordinal)) changes.Add(new FieldChange(d.Key, before.Count == 0 ? null : string.Join("; ", before), now.Count == 0 ? null : string.Join("; ", now)));
        }
        return new Plan(after, changes, errors);
    }

    public async Task ValidateForNewAsync(Guid organisationId, string? contractType, string initialStatus, CustomFieldInput input, CancellationToken ct = default)
    {
        var set = await catalogue.PublishedAsync(organisationId, ct);
        var roles = await RolesAsync(organisationId, ct);
        var (_, _, today) = await LocaleAsync(organisationId, ct);
        var plan = await PlanAsync(set, [], input, d => FieldRules.Editable(d with { VisibleInStates = [], EditableInStates = [] }, new FieldContext(roles, initialStatus, contractType, today, false)), enforceRequired: true, applyDefaults: true, ct);
        if (plan.Errors.Count > 0) throw new CustomFieldValidationException(plan.Errors);
    }

    public async Task<IReadOnlyList<FieldChange>> SaveAsync(Guid contractId, CustomFieldInput input, CancellationToken ct = default)
    {
        var c0 = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(c0, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        await access.RequireAsync("contracts.edit_owned", descriptor, ct);
        var set = await catalogue.PublishedAsync(c0.OrganisationId, ct);
        var roles = await RolesAsync(c0.OrganisationId, ct);
        var (_, _, today) = await LocaleAsync(c0.OrganisationId, ct);
        var gate = await ClassificationGateAsync(descriptor, c0.Classification, ct);
        var scale = await access.ClassificationScaleAsync(ct);
        return await uow.ExecuteAsync(async cx =>
        {
            var c = await db.Contracts.FirstAsync(x => x.Id == contractId, cx);
            c.EnsureOpenForEdit(clock.UtcNow);
            var stored = await StoredAsync(contractId, set, cx);
            bool Can(FieldDefinition d) => gate(d) && FieldRules.Editable(d, new FieldContext(roles, c.Status, c.ContractType, today, stored.ContainsKey(d.Key)));
            var plan = await PlanAsync(set, stored, input, Can, enforceRequired: true, applyDefaults: false, cx, contractId);
            if (plan.Errors.Count > 0) throw new CustomFieldValidationException(plan.Errors);
            await WriteAsync(c, set, plan, input.Keys, Actor, "contract.custom_fields_updated", null, null, null, scale, cx);
            return (IReadOnlyList<FieldChange>)plan.Changes;
        }, ct);
    }

    private async Task WriteAsync(Contract c, CustomFieldSet set, Plan plan, IEnumerable<string> touchedKeys, string actor, string auditAction, string? authorityKind, Guid? authorityId, string? reason,
        ClassificationScale scale, CancellationToken ct, Guid? batch = null, string? sourceSystem = null, IReadOnlyDictionary<string, string>? sourceValues = null)
    {
        if (plan.Changes.Count == 0) return;
        var now = clock.UtcNow;
        var changedKeys = plan.Changes.Select(x => x.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = await db.CustomValues.Where(v => v.ContractId == c.Id && changedKeys.Contains(v.FieldKey)).ToListAsync(ct);
        db.CustomValues.RemoveRange(existing);
        foreach (var key in changedKeys)
        {
            if (!plan.After.TryGetValue(key, out var values)) continue;
            var d = set.Find(key)!;
            for (var i = 0; i < values.Count; i++)
                db.CustomValues.Add(ToRow(c.TenantId, c.OrganisationId, c.Id, d, values[i], i, set.ReleaseNumber, actor, now, batch, sourceSystem, sourceValues?.GetValueOrDefault(key)));
        }
        c.RecordChange(actor, now);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); throw new DomainException("concurrency_conflict", "The record was changed by someone else. Reload it and try again."); }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); throw new DomainException("concurrency_conflict", "The record was changed by someone else. Reload it and try again."); }
        // The event is held at the most sensitive level among the contract and the fields that changed, so the audit trail hides it from anyone who could not read the value.
        var level = changedKeys.Select(k => set.Find(k)!.Classification).Append(c.Classification).OrderByDescending(scale.Rank).First();
        audit.Record(new AuditEntry(auditAction, nameof(Contract), c.Id.ToString(), OrganisationId: c.OrganisationId, Classification: level, Reason: reason,
            OldValuesJson: AuditJson.Of(plan.Changes.ToDictionary(x => x.Field, x => x.Before)), NewValuesJson: AuditJson.Of(new { fields = plan.Changes.ToDictionary(x => x.Field, x => x.After), release = set.ReleaseNumber, authorityKind, authorityId })));
    }

    public async Task<IReadOnlyDictionary<string, string?>> ApprovalFactsAsync(Guid contractId, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct);
        if (c is null) return new Dictionary<string, string?>();
        var set = await catalogue.PublishedAsync(c.OrganisationId, ct);
        var (_, _, today) = await LocaleAsync(c.OrganisationId, ct);
        var stored = await StoredAsync(contractId, set, ct);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in set.Fields.Where(f => f.CanFeedApprovals && FieldRules.IsCurrent(f, today) && FieldRules.AppliesToType(f, c.ContractType)))
            if (stored.TryGetValue(d.Key, out var values) && values.Count > 0) result[d.Key] = ValueCodec.ToRaw(d, values).FirstOrDefault();
        return result;
    }

    public async Task<IReadOnlyList<string>> ApprovalFactKeysAsync(Guid organisationId, CancellationToken ct = default)
        => (await catalogue.PublishedAsync(organisationId, ct)).Fields.Where(f => f.CanFeedApprovals).Select(f => f.Key).ToList();

    // ---- trusted writes --------------------------------------------------------------------------------------------------------------------------------

    private async Task<(Contract Contract, CustomFieldSet Set, Plan Plan)> TrustedPlanAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, bool tracked, CancellationToken ct)
    {
        var c = tracked ? await db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId, ct) : await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct);
        if (c is null) throw new DomainException("contract_not_found", "The contract was not found.");
        var set = await catalogue.PublishedAsync(c.OrganisationId, ct);
        var (_, _, today) = await LocaleAsync(c.OrganisationId, ct);
        var stored = await StoredAsync(contractId, set, ct);
        var input = new CustomFieldInput();
        foreach (var (key, value) in changes)
        {
            var d = set.Find(key);
            input[key] = d is not null && FieldTypes.IsMulti(d.Type) ? (IReadOnlyList<string?>)(value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : (IReadOnlyList<string?>)[value];
        }
        bool Can(FieldDefinition d) => !d.IsCalculated && FieldRules.IsCurrent(d, today) && FieldRules.AppliesToType(d, c.ContractType);
        var plan = await PlanAsync(set, stored, input, Can, enforceRequired: false, applyDefaults: false, ct, contractId);
        return (c, set, plan);
    }

    public async Task<IReadOnlyList<FieldChange>> PreviewAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default)
    {
        var (_, _, plan) = await TrustedPlanAsync(contractId, changes, tracked: false, ct);
        if (plan.Errors.Count > 0) throw new CustomFieldValidationException(plan.Errors);
        return plan.Changes;
    }

    public async Task<IReadOnlyList<FieldChange>> ApplyAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, string actor, string authorityKind, Guid authorityId, string? reason, CancellationToken ct = default)
    {
        var scale = await access.ClassificationScaleAsync(ct);
        return await uow.ExecuteAsync(async cx =>
        {
            var (c, set, plan) = await TrustedPlanAsync(contractId, changes, tracked: true, cx);
            if (plan.Errors.Count > 0) throw new CustomFieldValidationException(plan.Errors);
            await WriteAsync(c, set, plan, changes.Keys, actor, "contract.custom_fields_applied", authorityKind, authorityId, reason, scale, cx);
            return (IReadOnlyList<FieldChange>)plan.Changes;
        }, ct);
    }

    public async Task WriteNewAsync(Guid contractId, CustomFieldInput input, string actor, CancellationToken ct = default)
    {
        var scale = await access.ClassificationScaleAsync(ct);
        await uow.ExecuteAsync(async cx =>
        {
            var c = await db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId, cx) ?? throw new DomainException("contract_not_found", "The contract was not found.");
            var set = await catalogue.PublishedAsync(c.OrganisationId, cx);
            var plan = await PlanAsync(set, [], input, d => !d.IsCalculated, enforceRequired: false, applyDefaults: true, cx, contractId);
            if (plan.Errors.Count > 0) throw new CustomFieldValidationException(plan.Errors);
            await WriteAsync(c, set, plan, input.Keys, actor, "contract.custom_fields_set", null, null, null, scale, cx);
            return true;
        }, ct);
    }
}

/// <summary>How much of the stored data depends on the published definitions, for the publish-time impact checks.</summary>
internal sealed class ContractCustomFieldUsage(ContractsDbContext db, ICustomFieldCatalogue catalogue) : ICustomFieldUsage
{
    public async Task<FieldUsage> LoadAsync(Guid organisationId, CancellationToken ct = default)
    {
        var contracts = await db.Contracts.AsNoTracking().CountAsync(c => c.OrganisationId == organisationId && !c.IsArchived, ct);
        var perKey = await db.CustomValues.AsNoTracking().Where(v => v.OrganisationId == organisationId).GroupBy(v => v.FieldKey).Select(g => new { g.Key, N = g.Select(x => x.ContractId).Distinct().Count() }).ToListAsync(ct);
        var set = await catalogue.PublishedAsync(organisationId, ct);
        var listKeys = set.Fields.Where(f => FieldTypes.UsesList(f.Type)).Select(f => f.Key).ToList();
        var members = listKeys.Count == 0 ? [] : await db.CustomValues.AsNoTracking().Where(v => v.OrganisationId == organisationId && listKeys.Contains(v.FieldKey) && v.TextValue != null)
            .GroupBy(v => new { v.FieldKey, v.TextValue }).Select(g => new { g.Key.FieldKey, g.Key.TextValue, N = g.Count() }).ToListAsync(ct);
        return new FieldUsage(contracts, perKey.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.N), members.ToDictionary(x => FieldUsage.MemberKey(x.FieldKey, x.TextValue!), x => x.N));
    }
}
