using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Mapping;

namespace Ecp.DataMigration.Planning;

/// <summary>A supplier as the planner sees it: an existing one, or one an earlier row of the same batch will create.</summary>
internal sealed class WorkingSupplier(Guid? id, string name, string normalised, string? vendorNumber)
{
    /// <summary>The tax identifier the first row that creates this counterparty carries (as the source gave it, already checked against the organisation's primary identifier type).</summary>
    public string? TaxIdentifier { get; set; }
    public Guid? Id { get; set; } = id;
    public string Name { get; } = name;
    public string Normalised { get; } = normalised;
    public string? VendorNumber { get; } = vendorNumber;
    public bool IsNew => Id is null;
    public string Key => VendorNumber is { Length: > 0 } v ? "v:" + v.ToUpperInvariant() : "n:" + Normalised;
}

internal sealed class WorkingCategory(Guid? id, string code, string name, string normalised, WorkingCategory? parent, bool active)
{
    public Guid? Id { get; set; } = id;
    public string Code { get; } = code;
    public string Name { get; } = name;
    public string Normalised { get; } = normalised;
    public WorkingCategory? Parent { get; } = parent;
    public bool IsActive { get; } = active;
    public bool IsNew => Id is null;
}

public sealed record ContractValues(
    string SourceRecordId, string SourceReference, string Title, string? Description, string Status, string Classification, DateOnly? Start, DateOnly? Effective, DateOnly? Review,
    DateOnly? End, DateTime? RegisteredUtc, string? RegisteredSource, string? Currency, decimal? Value, string? ExtensionType, bool LegalReview, string? DocumentFolder,
    string? ContractType = null, string? AgreementType = null, string? RiskLevel = null, string? FunctionCode = null, string? SapOutlineAgreementNumber = null, int? NoticePeriodValue = null,
    string? NoticePeriodUnit = null, bool CriticalNotice = false);

/// <summary>What a commit will do for one row, derived from the mapped values and the state of the target. The same plan is computed at validation (to preview) and at commit (to act).</summary>
internal sealed class RowPlan
{
    public RowOutcome Outcome { get; set; } = RowOutcome.Accepted;
    public PlannedAction Action { get; set; } = PlannedAction.None;
    public bool NeedsReview { get; set; }
    /// <summary>The duplicate strategy is manual review and this row is one it applies to (decided or not).</summary>
    public bool ReviewApplicable { get; set; }
    public Guid? ExistingContractId { get; set; }
    public ExistingContractRef? Existing { get; set; }
    public WorkingSupplier? Supplier { get; set; }
    public WorkingCategory? Category { get; set; }
    public ContractValues? Values { get; set; }
    public string TargetReference { get; set; } = "";
    public bool GenerateReference { get; set; }
    public bool LockAsLegacy { get; set; }
    public IReadOnlyDictionary<string, string?>? UpdateFields { get; set; }
    /// <summary>Additional field values in their canonical stored text (key to value), and the value as the source gave it, for provenance.</summary>
    public IReadOnlyDictionary<string, string?>? CustomValues { get; set; }
    public IReadOnlyDictionary<string, string>? CustomSourceValues { get; set; }
    public string? IdempotencyKey { get; set; }
    public string Summary { get; set; } = "";
    public List<RowMessage> Messages { get; } = [];
    public bool HasErrors => Messages.Any(m => m.IsError);
}

internal sealed class PlanContext
{
    public required Guid TenantId { get; init; }
    public required Guid OrganisationId { get; init; }
    public required string SourceSystem { get; init; }
    public required DateTime? SourceExtractedUtc { get; init; }
    public required MigrationProject Project { get; init; }
    public required MigrationBatch Batch { get; init; }
    public required MappingDefinition Definition { get; init; }
    public required string InitialStatusCode { get; init; }
    public required Func<string, bool> IsKnownClassification { get; init; }
    public required string DefaultClassification { get; init; }
    public required DateTime NowUtc { get; init; }
    /// <summary>Checks additional field values against the published definitions; null when the batch maps none.</summary>
    public IMigrationCustomChecker? CustomChecker { get; init; }
    /// <summary>Checks the tax identifier column against the organisation's primary identifier type; null when the batch maps none.</summary>
    public IMigrationIdentifierChecker? IdentifierChecker { get; init; }
    public List<WorkingSupplier> Suppliers { get; } = [];
    public List<WorkingCategory> Categories { get; } = [];
    public Dictionary<string, ExistingContractRef> ExistingBySource { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ExistingContractRef> ExistingByReference { get; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, IReadOnlyDictionary<string, string?>> ExistingFields { get; } = [];
    /// <summary>Source record ids already seen in this file (first wins) and references already taken by an earlier row.</summary>
    public HashSet<string> SeenSourceIds { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ReferenceOwners { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The business rules of a contract load, in one place: required values, typed fields, supplier and category resolution, status and legacy-load policy, the Agreement
/// Reference rules and the duplicate strategies (MIGRATION_SUPPLEMENT sections 2, 3, 7 and 8). It never writes anything.
/// </summary>
internal static class RowPlanner
{
    public static string IdempotencyKey(Guid tenantId, Guid organisationId, string sourceSystem, string sourceRecordId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}|{organisationId:N}|{sourceSystem}|{sourceRecordId}")));

    public static RowPlan Plan(MappedRow mapped, StagingRow row, PlanContext ctx)
    {
        var suppliers = ctx.Suppliers.Count; var categories = ctx.Categories.Count;
        var plan = PlanCore(mapped, row, ctx);
        if (plan.Outcome != RowOutcome.Accepted)
        {
            // A row that will not load must not leave behind a supplier or category that a later row would then "find".
            if (ctx.Suppliers.Count > suppliers) ctx.Suppliers.RemoveRange(suppliers, ctx.Suppliers.Count - suppliers);
            if (ctx.Categories.Count > categories) ctx.Categories.RemoveRange(categories, ctx.Categories.Count - categories);
        }
        return plan;
    }

    private static RowPlan PlanCore(MappedRow mapped, StagingRow row, PlanContext ctx)
    {
        var plan = new RowPlan();
        plan.Messages.AddRange(mapped.Messages);
        if (mapped.HasErrors) return Reject(plan, "The row has values that could not be mapped.");

        var v = Typed(mapped, ctx, plan);
        if (v is null || plan.HasErrors) return Reject(plan, "The row has values that could not be understood.");
        plan.Values = v;
        CheckCustom(mapped, ctx, plan);
        if (plan.HasErrors) return Reject(plan, "An additional field value could not be accepted.");
        plan.IdempotencyKey = IdempotencyKey(ctx.TenantId, ctx.OrganisationId, ctx.SourceSystem, v.SourceRecordId);

        // 1. Duplicate source record in the same file: the first occurrence is kept, later ones are rejected (MIG-E005).
        if (!ctx.SeenSourceIds.Add(v.SourceRecordId))
        {
            plan.Messages.Add(new(true, "MIG-E005", "sourceRecordId", $"The source record '{v.SourceRecordId}' appears more than once in the file. The first occurrence is used."));
            return Reject(plan, "Duplicate source record in the file.");
        }

        // 2. Status and the legacy-load policy.
        var initial = ctx.InitialStatusCode;
        if (v.Status.Equals(initial, StringComparison.Ordinal)) plan.LockAsLegacy = false;
        else if (!ctx.Batch.IsLegacyLoad)
        {
            plan.Messages.Add(new(true, "MIG-E008", "status", $"The status '{v.Status}' is not valid for a standard load: only the initial state '{initial}' can be loaded without a legacy-load policy."));
            return Reject(plan, "Status not allowed.");
        }
        else if (!ctx.Project.LegacyAllowedStatuses.Contains(v.Status, StringComparer.Ordinal))
        {
            plan.Messages.Add(new(true, "MIG-E008", "status", $"The status '{v.Status}' is not one of the states the legacy-load policy allows ({string.Join(", ", ctx.Project.LegacyAllowedStatuses)})."));
            return Reject(plan, "Status not allowed by the legacy-load policy.");
        }
        else if (!ctx.Project.LegacyLoadAllowedAt(ctx.NowUtc))
        {
            plan.Messages.Add(new(true, "MIG-E012", "status", "The legacy-load policy has expired or been closed."));
            return Reject(plan, "Legacy-load policy inactive.");
        }
        else plan.LockAsLegacy = true;

        // 3. Registered date: honoured only for a locked legacy record under a policy that allows it; never fabricated.
        if (v.RegisteredUtc is { } registered)
        {
            if (!plan.LockAsLegacy || !ctx.Batch.AllowRegisteredDateImport)
                plan.Messages.Add(new(false, "MIG-W005", "registeredDate", "The source Registered Date was not imported: the legacy-load policy does not allow it for this record."));
            else if (v.Effective is { } eff && DateOnly.FromDateTime(registered) < eff)
                plan.Messages.Add(new(false, "MIG-W004", "registeredDate", "The Registered Date is earlier than the Effective Date."));
        }
        if (v.End is null && v.ExtensionType is { } ext && (ctx.Definition.Options.OpenEndedExtensionTypes ?? []).Contains(ext, StringComparer.OrdinalIgnoreCase))
            plan.Messages.Add(new(false, "MIG-W003", "endDate", "The End Date is blank and the extension type is open-ended (expected)."));

        // 4. Supplier and category (find, or plan to create).
        ResolveSupplier(mapped, ctx, plan);
        ResolveCategory(mapped, ctx, plan);
        if (plan.HasErrors) return Reject(plan, "[[supplier]] or [[category|lower]] could not be resolved.");

        // 5. Agreement Reference, existing records and the duplicate strategy.
        ctx.ExistingBySource.TryGetValue(v.SourceRecordId, out var byKey);
        var normalisedRef = Contract.NormaliseReference(v.SourceReference);
        ctx.ExistingByReference.TryGetValue(normalisedRef, out var byRef);
        ctx.ReferenceOwners.TryGetValue(normalisedRef, out var earlierRowOwner);
        var keep = ctx.Project.ReferencePolicy == ReferencePolicy.KeepSource;
        plan.TargetReference = keep ? normalisedRef : "";
        plan.GenerateReference = !keep;
        var strategy = ctx.Batch.DuplicateStrategy;
        var decision = row.ReviewDecision;

        if (byKey is not null)
        {
            plan.Existing = byKey; plan.ExistingContractId = byKey.Id;
            if (byKey.IsArchived)
            {
                plan.Messages.Add(new(true, "MIG-E006", "sourceRecordId", "An archived record already exists for this source record, so it cannot be loaded again."));
                return Reject(plan, "Archived record exists.");
            }
            plan.TargetReference = byKey.AgreementReference; plan.GenerateReference = false;
            plan.ReviewApplicable = strategy == DuplicateStrategy.ManualReview;
            if (plan.ReviewApplicable && decision is ReviewDecision.None or ReviewDecision.Required) return Review(plan, "This source record was loaded before. Choose skip, update or reject.");
            var effective = strategy == DuplicateStrategy.ManualReview ? decision : strategy switch
            {
                DuplicateStrategy.Reject => ReviewDecision.Reject,
                DuplicateStrategy.SkipExisting or DuplicateStrategy.CreateNewReference => ReviewDecision.Skip,
                DuplicateStrategy.UpdateMapped => ReviewDecision.Update,
                _ => ReviewDecision.Skip
            };
            switch (effective)
            {
                case ReviewDecision.Reject:
                    plan.Messages.Add(new(true, "MIG-E006", "sourceRecordId", "This source record was loaded before and the duplicate strategy is Reject."));
                    return Reject(plan, "Already loaded; strategy Reject.");
                case ReviewDecision.Skip:
                    plan.Messages.Add(new(false, "MIG-W002", "sourceRecordId", "This source record was loaded before; it is skipped and the existing record is unchanged."));
                    return Skip(plan, $"Already loaded as {byKey.AgreementReference}; no change.");
                case ReviewDecision.Update:
                    return PlanUpdate(plan, ctx, byKey);
                default:
                    return Skip(plan, "Skipped.");
            }
        }

        var collision = byRef is not null || (earlierRowOwner is not null && earlierRowOwner != v.SourceRecordId);
        if (keep && collision)
        {
            var detail = $"The agreement reference '{normalisedRef}' is already used by another record in this tenant.";
            plan.ReviewApplicable = strategy == DuplicateStrategy.ManualReview;
            if (plan.ReviewApplicable && decision is ReviewDecision.None or ReviewDecision.Required)
            { plan.Messages.Add(new(true, "MIG-E006", "agreementReference", detail + " Choose skip, create with a new reference, or reject.")); return Review(plan, detail); }
            var effective = plan.ReviewApplicable ? decision : strategy switch
            {
                DuplicateStrategy.SkipExisting => ReviewDecision.Skip,
                DuplicateStrategy.CreateNewReference => ReviewDecision.CreateNew,
                _ => ReviewDecision.Reject // Reject, and UpdateMapped (the colliding record did not come from this source record, so it is never updated)
            };
            switch (effective)
            {
                case ReviewDecision.Skip:
                    plan.Messages.Add(new(false, "MIG-E006", "agreementReference", detail + " The row is skipped."));
                    return Skip(plan, "Reference already in use; skipped.");
                case ReviewDecision.CreateNew:
                    plan.Messages.Add(new(false, "MIG-E006", "agreementReference", detail + " A new reference will be generated; the source reference is kept as the source agreement reference."));
                    plan.GenerateReference = true; plan.TargetReference = "";
                    break;
                default:
                    plan.Messages.Add(new(true, "MIG-E006", "agreementReference", detail + (strategy == DuplicateStrategy.UpdateMapped ? " It was not created from this source record, so it is not updated." : "")));
                    return Reject(plan, "Agreement reference in use.");
            }
        }
        if (!plan.GenerateReference) ctx.ReferenceOwners.TryAdd(normalisedRef, v.SourceRecordId);

        plan.Action = PlannedAction.Create; plan.Outcome = RowOutcome.Accepted;
        plan.Summary = $"Create [[contract|lower]] {(plan.GenerateReference ? "(new reference generated)" : plan.TargetReference)}; {Describe(plan.Supplier)}; {Describe(plan.Category)}"
            + (plan.LockAsLegacy ? "; loaded as a locked legacy record" : "");
        return plan;
    }

    /// <summary>Additional field values: each mapped <c>custom.key</c> is checked against the published definition (type, limits, defined choices, retired fields); the canonical text is what is stored and compared.</summary>
    private static void CheckCustom(MappedRow mapped, PlanContext ctx, RowPlan plan)
    {
        var raw = mapped.Values.Where(kv => MappingCatalogue.IsCustomTarget(kv.Key)).ToDictionary(kv => MappingCatalogue.CustomKeyOf(kv.Key), kv => (string?)kv.Value, StringComparer.OrdinalIgnoreCase);
        if (raw.Count == 0) return;
        if (ctx.CustomChecker is null) { plan.Messages.Add(new(true, "MIG-E020", "custom", "Additional fields cannot be loaded in this run.")); return; }
        var check = ctx.CustomChecker.Check(raw);
        foreach (var p in check.Problems) plan.Messages.Add(new(true, p.Code, MappingCatalogue.CustomPrefix + p.Key, p.Message));
        if (!check.IsValid) return;
        plan.CustomValues = check.Canonical;
        plan.CustomSourceValues = raw.Where(kv => check.Canonical.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => (kv.Value ?? "").Length > 450 ? kv.Value![..450] : kv.Value ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private static string Describe(WorkingSupplier? s) => s is null ? "" : s.IsNew ? $"create [[supplier|lower]] '{s.Name}'" : $"link [[supplier|lower]] '{s.Name}'";
    private static string Describe(WorkingCategory? c) => c is null ? "" : c.IsNew ? $"create [[category|lower]] {c.Code}" : $"link [[category|lower]] {c.Code}";

    private static RowPlan Reject(RowPlan plan, string summary) { plan.Outcome = RowOutcome.Rejected; plan.Action = PlannedAction.None; plan.Summary = summary; return plan; }
    private static RowPlan Skip(RowPlan plan, string summary) { plan.Outcome = RowOutcome.Skipped; plan.Action = PlannedAction.Skip; plan.Summary = summary; return plan; }
    private static RowPlan Review(RowPlan plan, string summary) { plan.Outcome = RowOutcome.Pending; plan.Action = PlannedAction.None; plan.NeedsReview = true; plan.Summary = "Needs a decision: " + summary; return plan; }

    /// <summary>Values are compared as the target stores them: money by amount (1000 equals 1000.00), everything else exactly.</summary>
    private static bool SameValue(string field, string? current, string? wanted)
    {
        if (field == "estimatedValue" && decimal.TryParse(current, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(wanted, NumberStyles.Number, CultureInfo.InvariantCulture, out var b)) return a == b;
        return string.Equals(current, wanted, StringComparison.Ordinal);
    }

    private static RowPlan PlanUpdate(RowPlan plan, PlanContext ctx, ExistingContractRef existing)
    {
        var v = plan.Values!;
        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Want(string target, string field, string? value) { if (ctx.Definition.Maps(target) && !string.IsNullOrEmpty(value)) wanted[field] = value; }
        Want("contractTitle", "contractTitle", v.Title); Want("description", "description", v.Description); Want("status", "status", v.Status);
        Want("currencyCode", "currencyCode", v.Currency); Want("estimatedValue", "estimatedValue", v.Value?.ToString(CultureInfo.InvariantCulture)); Want("extensionType", "extensionType", v.ExtensionType);
        Want("legalReviewRequired", "legalReviewRequired", v.LegalReview ? "true" : "false");
        Want("contractType", "contractType", v.ContractType); Want("agreementType", "agreementType", v.AgreementType); Want("riskLevel", "riskLevel", v.RiskLevel); Want("functionCode", "functionCode", v.FunctionCode);
        Want("sapOutlineAgreementNumber", "sapOutlineAgreementNumber", v.SapOutlineAgreementNumber); Want("noticePeriodValue", "noticePeriodValue", v.NoticePeriodValue?.ToString(CultureInfo.InvariantCulture));
        Want("noticePeriodUnit", "noticePeriodUnit", v.NoticePeriodUnit); Want("criticalNotice", "criticalNotice", ctx.Definition.Maps("criticalNotice") ? (v.CriticalNotice ? "true" : "false") : null);
        foreach (var (key, value) in plan.CustomValues ?? new Dictionary<string, string?>()) if (!string.IsNullOrEmpty(value)) wanted["cf." + key] = value;
        Want("startDate", "startDate", v.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); Want("effectiveDate", "effectiveDate", v.Effective?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Want("reviewDate", "reviewDate", v.Review?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); Want("endDate", "endDate", v.End?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (plan.Supplier?.Id is { } sid) wanted["supplierId"] = sid.ToString("D");
        if (plan.Category?.Id is { } cid) wanted["categoryId"] = cid.ToString("D");
        if (wanted.TryGetValue("status", out var status) && status != existing.Status && !plan.LockAsLegacy && !ctx.Batch.IsLegacyLoad)
            wanted.Remove("status"); // an ordinary load never changes workflow state

        ctx.ExistingFields.TryGetValue(existing.Id, out var current);
        var changed = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (field, value) in wanted)
            if (current is null || !SameValue(field, current.GetValueOrDefault(field), value)) changed[field] = value;
        // A planned (not yet created) supplier or category is applied at commit; the change cannot be compared now.
        if (plan.Supplier is { IsNew: true } || plan.Category is { IsNew: true }) { if (plan.Supplier is { IsNew: true }) changed["supplierId"] = null; if (plan.Category is { IsNew: true }) changed["categoryId"] = null; }
        if (changed.Count == 0)
        {
            plan.Messages.Add(new(false, "MIG-W002", "sourceRecordId", "No mapped field differs from the existing record; nothing is updated."));
            return Skip(plan, "Unchanged.");
        }
        if (existing.RecordLocked && !(ctx.Batch.IsLegacyLoad && ctx.Project.LegacyAllowOverwriteLocked))
        {
            plan.Messages.Add(new(true, "MIG-E012", "sourceRecordId", "The existing record is locked. A migration may overwrite it only under a legacy-load policy that explicitly allows it."));
            return Reject(plan, "Locked record; overwrite not allowed.");
        }
        plan.UpdateFields = changed; plan.Action = PlannedAction.Update; plan.Outcome = RowOutcome.Accepted;
        plan.Summary = $"Update {existing.AgreementReference}: {string.Join(", ", changed.Keys.OrderBy(k => k, StringComparer.Ordinal))}";
        return plan;
    }

    private static ContractValues? Typed(MappedRow mapped, PlanContext ctx, RowPlan plan)
    {
        string? Get(string name) => mapped.Values.TryGetValue(name, out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        string Need(string name, string label)
        {
            var s = Get(name);
            if (s is null) plan.Messages.Add(new(true, "MIG-E001", name, $"{label} is required."));
            return s ?? "";
        }
        string? Limit(string? s, string name, int max)
        {
            if (s is not null && s.Length > max) { plan.Messages.Add(new(true, "MIG-E009", name, $"{name} is {s.Length} characters; the most allowed is {max}. The value is not shortened.")); return null; }
            return s;
        }
        DateOnly? Date(string name)
        {
            if (Get(name) is not { } s) return null;
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            plan.Messages.Add(new(true, "MIG-E002", name, $"'{s}' is not a date. Use a ParseDate transformation with an explicit format."));
            return null;
        }
        var id = Limit(Need("sourceRecordId", "The source record id"), "sourceRecordId", 200);
        var reference = Limit(Need("agreementReference", "The agreement reference"), "agreementReference", 100);
        var title = Limit(Need("contractTitle", "The [[contract|lower]] title"), "contractTitle", 500);
        var status = Limit(Need("status", "The status"), "status", 100);
        var description = Limit(Get("description"), "description", 4000);
        var extension = Limit(Get("extensionType"), "extensionType", 50);
        var classification = Get("classification") ?? ctx.DefaultClassification;
        if (!ctx.IsKnownClassification(classification)) plan.Messages.Add(new(true, "MIG-E004", "classification", $"The classification '{classification}' is not on the tenant's scale."));
        DateTime? registered = null;
        if (Get("registeredDate") is { } rd)
        {
            if (DateTime.TryParseExact(rd, ["yyyy-MM-dd'T'HH:mm:ss'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) registered = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            else plan.Messages.Add(new(true, "MIG-E002", "registeredDate", $"'{rd}' is not a date and time. Use a ParseDateTime transformation."));
        }
        decimal? value = null;
        if (Get("estimatedValue") is { } ev)
        {
            if (decimal.TryParse(ev, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0) value = d;
            else plan.Messages.Add(new(true, "MIG-E003", "estimatedValue", $"'{ev}' is not a valid estimated value (it must be a number, zero or more)."));
        }
        var currency = Get("currencyCode")?.ToUpperInvariant();
        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))) plan.Messages.Add(new(true, "MIG-E015", "currencyCode", $"'{currency}' is not a three-letter currency code."));
        if (value.HasValue && currency is null) plan.Messages.Add(new(true, "MIG-E001", "currencyCode", "A currency is required when an estimated value is given."));
        bool legal = false;
        if (Get("legalReviewRequired") is { } lr)
        {
            if (lr.Equals("true", StringComparison.Ordinal)) legal = true;
            else if (!lr.Equals("false", StringComparison.Ordinal)) plan.Messages.Add(new(true, "MIG-E004", "legalReviewRequired", $"'{lr}' is not yes or no. Use a MapBoolean transformation."));
        }
        var contractType = Limit(Get("contractType"), "contractType", 100); var agreementType = Limit(Get("agreementType"), "agreementType", 100); var riskLevel = Limit(Get("riskLevel"), "riskLevel", 50);
        var functionCode = Limit(Get("functionCode"), "functionCode", 100); var sap = Limit(Get("sapOutlineAgreementNumber"), "sapOutlineAgreementNumber", 100);
        int? noticeValue = null;
        if (Get("noticePeriodValue") is { } nv)
        {
            if (int.TryParse(nv, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) noticeValue = n;
            else plan.Messages.Add(new(true, "MIG-E003", "noticePeriodValue", $"'{nv}' is not a whole number of days, weeks or months. Use a ParseInteger transformation."));
        }
        var noticeUnit = Get("noticePeriodUnit") is { } nu ? NoticePeriods.Canonical(nu) : null;
        if (Get("noticePeriodUnit") is { } rawUnit && noticeUnit is null) plan.Messages.Add(new(true, "MIG-E004", "noticePeriodUnit", $"'{rawUnit}' is not Days, Weeks or Months."));
        if ((noticeValue is null) != (noticeUnit is null) && !plan.HasErrors) plan.Messages.Add(new(true, "MIG-E001", "noticePeriodValue", "Give both the notice period and its unit, or neither."));
        var critical = false;
        if (Get("criticalNotice") is { } cn)
        {
            if (cn.Equals("true", StringComparison.Ordinal)) critical = true;
            else if (!cn.Equals("false", StringComparison.Ordinal)) plan.Messages.Add(new(true, "MIG-E004", "criticalNotice", $"'{cn}' is not yes or no. Use a MapBoolean transformation."));
        }
        var start = Date("startDate"); var effective = Date("effectiveDate"); var review = Date("reviewDate"); var end = Date("endDate");
        if (start.HasValue && end.HasValue && end < start) plan.Messages.Add(new(true, "MIG-E002", "endDate", "The End Date is before the Start Date."));
        if (plan.HasErrors) return null;
        return new ContractValues(id!, reference!, title!, description, status!, classification, start, effective, review, end, registered, Get("registeredDate"), currency, value, extension, legal, Get("staging.documentFolder"),
            contractType, agreementType, riskLevel, functionCode, sap, noticeValue, noticeUnit, critical);
    }

    private static void ResolveSupplier(MappedRow mapped, PlanContext ctx, RowPlan plan)
    {
        var name = mapped.Values.GetValueOrDefault("supplier.name")?.Trim();
        if (string.IsNullOrEmpty(name)) { plan.Messages.Add(new(true, "MIG-E001", "supplier.name", "The [[supplier|lower]] name is required.")); return; }
        if (name.Length > 300) { plan.Messages.Add(new(true, "MIG-E009", "supplier.name", $"The [[supplier|lower]] name is {name.Length} characters; the most allowed is 300. It is not shortened.")); return; }
        var vendor = mapped.Values.GetValueOrDefault("supplier.vendorNumber")?.Trim();
        if (vendor is { Length: > 50 }) { plan.Messages.Add(new(true, "MIG-E009", "supplier.vendorNumber", "The [[supplier.number]] is longer than 50 characters. It is not shortened.")); return; }
        var normalised = Supplier.NormaliseName(name);
        var taxIdentifier = mapped.Values.GetValueOrDefault("supplier.taxIdentifier")?.Trim();
        if (!string.IsNullOrEmpty(taxIdentifier))
        {
            // A structural check against the organisation's own identifier profile; nothing is looked up anywhere and a valid format proves nothing about registration.
            if (ctx.IdentifierChecker is not { TypeKey: not null } checker) { plan.Messages.Add(new(true, "MIG-E028", "supplier.taxIdentifier", "No [[supplier.tax.id]] type is set up for this [[organisation|lower]], so the column cannot be loaded.")); return; }
            var check = checker.Check(taxIdentifier);
            if (!check.Valid) { plan.Messages.Add(new(true, "MIG-E027", "supplier.taxIdentifier", $"The {checker.Label ?? "identifier"} '{taxIdentifier}' does not fit its type: {check.Message}")); return; }
        }

        WorkingSupplier? chosen = null;
        if (!string.IsNullOrEmpty(vendor))
        {
            var byVendor = ctx.Suppliers.Where(s => string.Equals(s.VendorNumber, vendor, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byVendor.Count == 1)
            {
                chosen = byVendor[0];
                if (chosen.Normalised != normalised)
                    plan.Messages.Add(new(false, "MIG-W006", "supplier.name", $"The [[supplier|lower]] name differs from the existing [[supplier|lower]] '{chosen.Name}' that has [[supplier.number]] {vendor}. The existing [[supplier|lower]] is used."));
            }
            else if (byVendor.Count > 1) { plan.Messages.Add(new(true, "MIG-E010", "supplier.vendorNumber", $"More than one [[supplier|lower]] has [[supplier.number]] {vendor}. A person must resolve this.")); return; }
        }
        if (chosen is null)
        {
            var byName = ctx.Suppliers.Where(s => s.Normalised == normalised).ToList();
            if (byName.Count > 1) { plan.Messages.Add(new(true, "MIG-E010", "supplier.name", $"More than one [[supplier|lower]] is named '{name}'. A person must resolve this.")); return; }
            if (byName.Count == 1)
            {
                var s = byName[0];
                if (!string.IsNullOrEmpty(vendor) && !string.IsNullOrEmpty(s.VendorNumber) && !string.Equals(s.VendorNumber, vendor, StringComparison.OrdinalIgnoreCase))
                { plan.Messages.Add(new(true, "MIG-E010", "supplier.vendorNumber", $"[[supplier|lower|a|cap]] named '{name}' exists with a different [[supplier.number]]. A person must resolve this.")); return; }
                chosen = s;
                if (!string.IsNullOrEmpty(vendor) && string.IsNullOrEmpty(s.VendorNumber))
                    plan.Messages.Add(new(false, "MIG-W006", "supplier.name", $"The existing [[supplier|lower]] '{s.Name}' has no [[supplier.number]]; it was matched on its exact name."));
            }
        }
        if (chosen is null)
        {
            chosen = new WorkingSupplier(null, name, normalised, string.IsNullOrEmpty(vendor) ? null : vendor) { TaxIdentifier = string.IsNullOrEmpty(taxIdentifier) ? null : taxIdentifier };
            ctx.Suppliers.Add(chosen); // later rows in this batch find it
        }
        plan.Supplier = chosen;
    }

    private static void ResolveCategory(MappedRow mapped, PlanContext ctx, RowPlan plan)
    {
        var categoryValue = mapped.Values.GetValueOrDefault("category.name")?.Trim();
        var subcategoryValue = mapped.Values.GetValueOrDefault("subcategory.name")?.Trim();
        if (string.IsNullOrEmpty(categoryValue)) { plan.Messages.Add(new(true, "MIG-E001", "category.name", "The [[category|lower]] is required.")); return; }
        var category = FindCategory(categoryValue, null, ctx, plan, "category.name");
        if (category is null) return;
        var chosen = category;
        if (!string.IsNullOrEmpty(subcategoryValue))
        {
            var sub = FindCategory(subcategoryValue, category, ctx, plan, "subcategory.name");
            if (sub is null) return;
            chosen = sub;
        }
        plan.Category = chosen;
    }

    private static WorkingCategory? FindCategory(string value, WorkingCategory? parent, PlanContext ctx, RowPlan plan, string field)
    {
        var code = value.ToUpperInvariant();
        var normalised = Supplier.NormaliseName(value);
        var matches = ctx.Categories.Where(c => c.Code == code).ToList();
        if (matches.Count == 0) matches = ctx.Categories.Where(c => c.Normalised == normalised && (parent is null ? c.Parent is null : ReferenceEquals(c.Parent, parent))).ToList();
        if (matches.Count > 1) { plan.Messages.Add(new(true, "MIG-E011", field, $"'{value}' matches more than one [[category|lower]].")); return null; }
        if (matches.Count == 1)
        {
            var found = matches[0];
            if (!found.IsActive) { plan.Messages.Add(new(true, "MIG-E011", field, $"The [[category|lower]] {found.Code} is inactive.")); return null; }
            if (parent is not null && !ReferenceEquals(found.Parent, parent)) { plan.Messages.Add(new(true, "MIG-E011", field, $"{found.Code} is not [[subcategory|lower|a]] of {parent.Code}.")); return null; }
            return found;
        }
        if (!ctx.Definition.Options.CreateMissingCategories)
        { plan.Messages.Add(new(true, "MIG-E011", field, $"'{value}' is not [[category|lower|a]] in this organisation. Add [[category|lower|a]] lookup or let the profile create missing [[category|plural|lower]].")); return null; }
        var newCode = DeriveCode(value);
        if (ctx.Categories.Any(c => c.Code == newCode)) { plan.Messages.Add(new(true, "MIG-E011", field, $"[[category|lower|a|cap]] with the derived code {newCode} already exists under a different name.")); return null; }
        var created = new WorkingCategory(null, newCode, value, normalised, parent, active: true);
        ctx.Categories.Add(created);
        plan.Messages.Add(new(false, "MIG-W002", field, $"The [[category|lower]] '{value}' will be created with code {newCode}."));
        return created;
    }

    /// <summary>A category code for a created category: the value upper-cased, runs of other characters becoming one dash.</summary>
    public static string DeriveCode(string value)
    {
        var sb = new StringBuilder(); var dash = false;
        foreach (var ch in value.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        var code = sb.ToString().Trim('-');
        return code.Length == 0 ? "CATEGORY" : code.Length > 100 ? code[..100] : code;
    }
}
