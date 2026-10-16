using System.Globalization;
using Ecp.Contracts.Domain;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts.Register;

// Filters are written as explicit queries over named columns (EF.Property with a name chosen from a fixed list in this file), never from text the caller supplied: the caller picks a field key
// from the catalogue and an operator from the grammar, and everything else is a parameter.
internal sealed partial class RegisterQueryEngine
{
    private async Task<(IQueryable<Contract> C, IQueryable<Supplier> S)> ApplyFilterAsync(IQueryable<Contract> cq, IQueryable<Supplier> sq, FilterClause f, RegisterCatalogue catalogue, CustomFieldSet set,
        ContractAccessFilter.Context listCtx, LevelVisibility vis, DateOnly today, DateTime now, CancellationToken ct)
    {
        if (f.Field == "set")
        {
            var provider = setProviders.First(s => string.Equals(s.Key, f.Values[0], StringComparison.Ordinal));
            var ids = (await provider.IdsAsync(f.Values.Count > 1 ? f.Values[1] : null, ct)).ToArray();
            return (cq.Where(c => ids.Contains(c.Id)), sq);
        }
        var field = catalogue.Find(f.Field)!;
        if (field.IsCustom) return (ApplyCustom(cq, set, set.Find(field.Key)!, f, vis, today), sq);

        switch (field.Key)
        {
            case "agreementReference": return (TextOn(cq, nameof(Contract.AgreementReference), f, v => v.ToUpperInvariant()), sq);
            case "contractTitle": return (TextOn(cq, nameof(Contract.ContractTitle), f, v => v), sq);
            case "supplier": return (cq, TextOn(sq, nameof(Supplier.NormalisedName), f, v => v.ToUpperInvariant()));
            case "supplierVendorNumber": return (cq, TextOn(sq, nameof(Supplier.VendorNumber), f, v => v));
            case "supplierAbn": return (cq, TextOn(sq, nameof(Supplier.Abn), f, v => new string(v.Where(char.IsDigit).ToArray())));
            case "supplierType": return (cq, TextOn(sq, nameof(Supplier.SupplierType), f, v => v));
            case "sapOutlineAgreementNumber": return (TextOn(cq, nameof(Contract.SapOutlineAgreementNumber), f, v => v), sq);
            case "contractType": return (TextOn(cq, nameof(Contract.ContractType), f, v => v), sq);
            case "agreementType": return (TextOn(cq, nameof(Contract.AgreementType), f, v => v), sq);
            case "riskLevel": return (TextOn(cq, nameof(Contract.RiskLevel), f, v => v), sq);
            case "functionCode": return (TextOn(cq, nameof(Contract.FunctionCode), f, v => v), sq);
            case "extensionType": return (TextOn(cq, nameof(Contract.ExtensionType), f, v => v), sq);
            case "currencyCode": return (TextOn(cq, nameof(Contract.CurrencyCode), f, v => v.ToUpperInvariant()), sq);
            case "classification": return (TextOn(cq, nameof(Contract.Classification), f, v => v), sq);
            case "status":
                {
                    var statuses = new List<string>();
                    foreach (var v in f.Values)
                        if (v == FilterTokens.Live) statuses.AddRange(await StatusGroupAsync("live", catalogue.OrganisationId, ct)); else statuses.Add(v);
                    var arr = statuses.Distinct().ToArray();
                    return (f.Op switch { FilterOps.NotEq => cq.Where(c => !arr.Contains(c.Status)), FilterOps.IsEmpty => cq.Where(c => false), FilterOps.IsNotEmpty => cq, _ => cq.Where(c => arr.Contains(c.Status)) }, sq);
                }
            case "category":
                {
                    var ids = f.Values.Select(Guid.Parse).SelectMany(id => listCtx.DescendantsOf.TryGetValue(id, out var d) ? d : [id]).Distinct().ToArray();
                    return (f.Op switch { FilterOps.NotEq => cq.Where(c => !ids.Contains(c.CategoryId)), FilterOps.IsEmpty => cq.Where(c => false), FilterOps.IsNotEmpty => cq, _ => cq.Where(c => ids.Contains(c.CategoryId)) }, sq);
                }
            case "legalEntity": return (GuidOn(cq, nameof(Contract.LegalEntityId), f), sq);
            case "businessUnit": return (GuidOn(cq, nameof(Contract.BusinessUnitId), f), sq);
            case "site": return (GuidOn(cq, nameof(Contract.SiteId), f), sq);
            case "owner":
                {
                    var uid = f.Values[0] == FilterTokens.Me ? access.CurrentUserId ?? Guid.Empty : Guid.Parse(f.Values[0]);
                    var owned = await db.CategoryAssignments.AsNoTracking().Where(a => a.UserId == uid && a.EffectiveFromUtc <= now && (a.EffectiveToUtc == null || now < a.EffectiveToUtc)).Select(a => a.CategoryId).ToListAsync(ct);
                    var ids = owned.SelectMany(id => listCtx.DescendantsOf.TryGetValue(id, out var d) ? d : [id]).Distinct().ToArray();
                    return (f.Op == FilterOps.NotEq ? cq.Where(c => !ids.Contains(c.CategoryId)) : cq.Where(c => ids.Contains(c.CategoryId)), sq);
                }
            case "estimatedValue": return (NumberOn(cq, nameof(Contract.EstimatedValue), f), sq);
            case "noticePeriodValue": return (IntOn(cq, nameof(Contract.NoticePeriodValue), f), sq);
            case "startDate": return (DateOn(cq, nameof(Contract.StartDate), f, today), sq);
            case "effectiveDate": return (DateOn(cq, nameof(Contract.EffectiveDate), f, today), sq);
            case "reviewDate": return (DateOn(cq, nameof(Contract.ReviewDate), f, today), sq);
            case "endDate": return (DateOn(cq, nameof(Contract.EndDate), f, today), sq);
            case "registeredDate": return (MomentOn(cq, nameof(Contract.RegisteredDateUtc), f, today), sq);
            case "createdUtc": return (MomentOn(cq, nameof(Contract.CreatedUtc), f, today), sq);
            case "updatedUtc": return (MomentOn(cq, nameof(Contract.UpdatedUtc), f, today), sq);
            case "criticalNotice": { var b = f.Values[0] == "true"; return (cq.Where(c => c.CriticalNotice == b), sq); }
            case "legalReviewRequired": { var b = f.Values[0] == "true"; return (cq.Where(c => c.LegalReviewRequired == b), sq); }
            case "recordLocked": { var b = f.Values[0] == "true"; return (cq.Where(c => c.RecordLocked == b), sq); }
            case "evergreen":
                return f.Values[0] == "true" ? (cq.Where(c => c.ExtensionType == ExtensionTypes.Evergreen), sq) : (cq.Where(c => c.ExtensionType == null || c.ExtensionType != ExtensionTypes.Evergreen), sq);
            case "legalReviewStatus":
                {
                    var provider = setProviders.FirstOrDefault(s => s.Key == "legalReviewStatus") ?? throw new RegisterQueryException([new RegisterIssue("filters", "set", "Legal review status is not available.")]);
                    var ids = new HashSet<Guid>();
                    foreach (var v in f.Values) ids.UnionWith(await provider.IdsAsync(v, ct));
                    var arr = ids.ToArray();
                    return (f.Op == FilterOps.NotEq ? cq.Where(c => !arr.Contains(c.Id)) : cq.Where(c => arr.Contains(c.Id)), sq);
                }
            default:
                throw new RegisterQueryException([new RegisterIssue("filters", "field", $"'{f.Field}' cannot be filtered.")]);
        }
    }

    private async Task<IReadOnlyList<string>> StatusGroupAsync(string key, Guid? org, CancellationToken ct)
    {
        var all = new List<string>();
        foreach (var g in statusGroups.Where(g => g.Key == key)) all.AddRange(await g.StatusesAsync(org, ct));
        return all;
    }

    // ---- generic, name-based column filters ---------------------------------------------------------------------------------------------------------------------

    private static IQueryable<T> TextOn<T>(IQueryable<T> q, string prop, FilterClause f, Func<string, string> norm) where T : class
    {
        var vals = f.Values.Select(norm).ToArray();
        var v = vals.Length > 0 ? vals[0] : ""; var vu = v.ToUpperInvariant();
        return f.Op switch
        {
            FilterOps.Eq => q.Where(x => EF.Property<string?>(x, prop) == v),
            FilterOps.NotEq => q.Where(x => EF.Property<string?>(x, prop) == null || EF.Property<string?>(x, prop) != v),
            FilterOps.Contains => q.Where(x => EF.Property<string?>(x, prop)!.ToUpper().Contains(vu)),
            FilterOps.StartsWith => q.Where(x => EF.Property<string?>(x, prop)!.ToUpper().StartsWith(vu)),
            FilterOps.In => q.Where(x => vals.Contains(EF.Property<string?>(x, prop)!)),
            FilterOps.IsEmpty => q.Where(x => EF.Property<string?>(x, prop) == null || EF.Property<string?>(x, prop) == ""),
            FilterOps.IsNotEmpty => q.Where(x => EF.Property<string?>(x, prop) != null && EF.Property<string?>(x, prop) != ""),
            _ => q
        };
    }

    private static IQueryable<T> GuidOn<T>(IQueryable<T> q, string prop, FilterClause f) where T : class
    {
        var ids = f.Values.Where(v => Guid.TryParse(v, out _)).Select(Guid.Parse).ToArray();
        var one = ids.Length > 0 ? ids[0] : Guid.Empty;
        return f.Op switch
        {
            FilterOps.Eq => q.Where(x => EF.Property<Guid?>(x, prop) == one),
            FilterOps.NotEq => q.Where(x => EF.Property<Guid?>(x, prop) == null || EF.Property<Guid?>(x, prop) != one),
            FilterOps.In => q.Where(x => ids.Contains(EF.Property<Guid?>(x, prop)!.Value)),
            FilterOps.IsEmpty => q.Where(x => EF.Property<Guid?>(x, prop) == null),
            FilterOps.IsNotEmpty => q.Where(x => EF.Property<Guid?>(x, prop) != null),
            _ => q
        };
    }

    private static decimal Dec(string v) => decimal.Parse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static IQueryable<T> NumberOn<T>(IQueryable<T> q, string prop, FilterClause f) where T : class
    {
        var a = f.Values.Count > 0 ? Dec(f.Values[0]) : 0m; var b = f.Values.Count > 1 ? Dec(f.Values[1]) : 0m;
        return f.Op switch
        {
            FilterOps.Eq => q.Where(x => EF.Property<decimal?>(x, prop) == a),
            FilterOps.NotEq => q.Where(x => EF.Property<decimal?>(x, prop) == null || EF.Property<decimal?>(x, prop) != a),
            FilterOps.Gte => q.Where(x => EF.Property<decimal?>(x, prop) >= a),
            FilterOps.Lte => q.Where(x => EF.Property<decimal?>(x, prop) <= a),
            FilterOps.Between => q.Where(x => EF.Property<decimal?>(x, prop) >= a && EF.Property<decimal?>(x, prop) <= b),
            FilterOps.IsEmpty => q.Where(x => EF.Property<decimal?>(x, prop) == null),
            FilterOps.IsNotEmpty => q.Where(x => EF.Property<decimal?>(x, prop) != null),
            _ => q
        };
    }

    private static IQueryable<T> IntOn<T>(IQueryable<T> q, string prop, FilterClause f) where T : class
    {
        int I(decimal d) => (int)Math.Clamp(d, int.MinValue, int.MaxValue);
        var a = f.Values.Count > 0 ? I(Dec(f.Values[0])) : 0; var b = f.Values.Count > 1 ? I(Dec(f.Values[1])) : 0;
        return f.Op switch
        {
            FilterOps.Eq => q.Where(x => EF.Property<int?>(x, prop) == a),
            FilterOps.NotEq => q.Where(x => EF.Property<int?>(x, prop) == null || EF.Property<int?>(x, prop) != a),
            FilterOps.Gte => q.Where(x => EF.Property<int?>(x, prop) >= a),
            FilterOps.Lte => q.Where(x => EF.Property<int?>(x, prop) <= a),
            FilterOps.Between => q.Where(x => EF.Property<int?>(x, prop) >= a && EF.Property<int?>(x, prop) <= b),
            FilterOps.IsEmpty => q.Where(x => EF.Property<int?>(x, prop) == null),
            FilterOps.IsNotEmpty => q.Where(x => EF.Property<int?>(x, prop) != null),
            _ => q
        };
    }

    internal static DateOnly ParseDate(string v, DateOnly today)
        => v.StartsWith(FilterTokens.Today, StringComparison.Ordinal) && TryRelative(v, today, out var d) ? d : DateOnly.ParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IQueryable<T> DateOn<T>(IQueryable<T> q, string prop, FilterClause f, DateOnly today) where T : class
    {
        var a = f.Values.Count > 0 ? ParseDate(f.Values[0], today) : default; var b = f.Values.Count > 1 ? ParseDate(f.Values[1], today) : default;
        return f.Op switch
        {
            FilterOps.Eq => q.Where(x => EF.Property<DateOnly?>(x, prop) == a),
            FilterOps.Gte => q.Where(x => EF.Property<DateOnly?>(x, prop) >= a),
            FilterOps.Lte => q.Where(x => EF.Property<DateOnly?>(x, prop) <= a),
            FilterOps.Between => q.Where(x => EF.Property<DateOnly?>(x, prop) >= a && EF.Property<DateOnly?>(x, prop) <= b),
            FilterOps.IsEmpty => q.Where(x => EF.Property<DateOnly?>(x, prop) == null),
            FilterOps.IsNotEmpty => q.Where(x => EF.Property<DateOnly?>(x, prop) != null),
            _ => q
        };
    }

    private static DateTime StartOf(DateOnly d) => DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    private static IQueryable<T> MomentOn<T>(IQueryable<T> q, string prop, FilterClause f, DateOnly today) where T : class
    {
        // A date given for a moment means the whole day (UTC): "on or after" starts at its first instant, "on or before" runs to its last.
        var from = f.Values.Count > 0 ? StartOf(ParseDate(f.Values[0], today)) : default; var to = f.Values.Count > 1 ? StartOf(ParseDate(f.Values[1], today)).AddDays(1) : from.AddDays(1);
        var next = from.AddDays(1);
        return f.Op switch
        {
            FilterOps.Eq => q.Where(x => EF.Property<DateTime?>(x, prop) >= from && EF.Property<DateTime?>(x, prop) < next),
            FilterOps.Gte => q.Where(x => EF.Property<DateTime?>(x, prop) >= from),
            FilterOps.Lte => q.Where(x => EF.Property<DateTime?>(x, prop) < next),
            FilterOps.Between => q.Where(x => EF.Property<DateTime?>(x, prop) >= from && EF.Property<DateTime?>(x, prop) < to),
            FilterOps.IsEmpty => q.Where(x => EF.Property<DateTime?>(x, prop) == null),
            FilterOps.IsNotEmpty => q.Where(x => EF.Property<DateTime?>(x, prop) != null),
            _ => q
        };
    }

    // ---- configured fields -------------------------------------------------------------------------------------------------------------------------------

    // A configured-field condition is an uncorrelated "contract id in (matching values)" subquery, never one lookup per contract: the database finds the matching values through the field's own index and
    // joins them once, so the cost follows the number of matching values, not the size of the register times the number of matches.
    private static IQueryable<Contract> Matching(IQueryable<Contract> cq, IQueryable<ContractCustomValue> values)
    {
        var ids = values.Select(v => v.ContractId);
        return cq.Where(c => ids.Contains(c.Id));
    }

    private static IQueryable<Contract> NotMatching(IQueryable<Contract> cq, IQueryable<ContractCustomValue> values)
    {
        var ids = values.Select(v => v.ContractId);
        return cq.Where(c => !ids.Contains(c.Id));
    }

    private IQueryable<Contract> ApplyCustom(IQueryable<Contract> cq, CustomFieldSet set, FieldDefinition d, FilterClause f, LevelVisibility vis, DateOnly today)
    {
        var key = d.Key;
        var states = d.VisibleInStates.ToArray();
        // Only rows where the caller could see this field's value take part, so no result can reveal a value that is hidden from them. Where every grant allows the field's level the contracts in the query
        // all qualify already and the test is left out (LevelVisibility.IsOpen).
        if (!vis.IsOpen(d.Classification))
        {
            var allowed = vis.Ids(d.Classification); var higher = vis.Above(d.Classification);
            cq = cq.Where(c => higher.Contains(c.Classification) || allowed.Contains(c.Id));
        }
        if (states.Length > 0) cq = cq.Where(c => states.Contains(c.Status));
        var cv = db.CustomValues.AsNoTracking().Where(v => v.FieldKey == key);
        if (f.Op == FilterOps.IsEmpty) return NotMatching(cq, cv);
        if (f.Op == FilterOps.IsNotEmpty) return Matching(cq, cv);
        switch (FieldTypes.StorageOf(d.Type))
        {
            case StorageKind.Text:
                {
                    // A choice or lookup is stored as its code; what was asked for may be the label.
                    var vals = f.Values.Select(v => FieldTypes.UsesList(d.Type) ? set.FindChoice(d, v)?.Code ?? v : v).ToArray();
                    var one = vals[0]; var oneUpper = one.ToUpperInvariant();
                    return f.Op switch
                    {
                        FilterOps.Eq => Matching(cq, cv.Where(v => v.TextValue == one)),
                        FilterOps.NotEq => NotMatching(cq, cv.Where(v => v.TextValue == one)),
                        FilterOps.Contains => Matching(cq, cv.Where(v => v.TextValue!.ToUpper().Contains(oneUpper))),
                        FilterOps.StartsWith => Matching(cq, cv.Where(v => v.TextValue!.ToUpper().StartsWith(oneUpper))),
                        FilterOps.In => Matching(cq, cv.Where(v => vals.Contains(v.TextValue!))),
                        _ => cq
                    };
                }
            case StorageKind.LongText:
                {
                    var one = f.Values[0];
                    var oneUpper = one.ToUpperInvariant();
                    return f.Op == FilterOps.Contains ? Matching(cq, cv.Where(v => v.LongTextValue!.ToUpper().Contains(oneUpper))) : cq;
                }
            case StorageKind.Number:
                {
                    var a = f.Values.Count > 0 ? Dec(f.Values[0]) : 0m; var b = f.Values.Count > 1 ? Dec(f.Values[1]) : 0m;
                    return f.Op switch
                    {
                        FilterOps.Eq => Matching(cq, cv.Where(v => v.NumberValue == a)),
                        FilterOps.NotEq => NotMatching(cq, cv.Where(v => v.NumberValue == a)),
                        FilterOps.Gte => Matching(cq, cv.Where(v => v.NumberValue >= a)),
                        FilterOps.Lte => Matching(cq, cv.Where(v => v.NumberValue <= a)),
                        FilterOps.Between => Matching(cq, cv.Where(v => v.NumberValue >= a && v.NumberValue <= b)),
                        _ => cq
                    };
                }
            case StorageKind.Date:
                {
                    var a = f.Values.Count > 0 ? ParseDate(f.Values[0], today) : default; var b = f.Values.Count > 1 ? ParseDate(f.Values[1], today) : default;
                    return f.Op switch
                    {
                        FilterOps.Eq => Matching(cq, cv.Where(v => v.DateValue == a)),
                        FilterOps.Gte => Matching(cq, cv.Where(v => v.DateValue >= a)),
                        FilterOps.Lte => Matching(cq, cv.Where(v => v.DateValue <= a)),
                        FilterOps.Between => Matching(cq, cv.Where(v => v.DateValue >= a && v.DateValue <= b)),
                        _ => cq
                    };
                }
            case StorageKind.DateTime:
                {
                    var from = f.Values.Count > 0 ? StartOf(ParseDate(f.Values[0], today)) : default; var next = from.AddDays(1); var to = f.Values.Count > 1 ? StartOf(ParseDate(f.Values[1], today)).AddDays(1) : next;
                    return f.Op switch
                    {
                        FilterOps.Eq => Matching(cq, cv.Where(v => v.DateTimeValue >= from && v.DateTimeValue < next)),
                        FilterOps.Gte => Matching(cq, cv.Where(v => v.DateTimeValue >= from)),
                        FilterOps.Lte => Matching(cq, cv.Where(v => v.DateTimeValue < next)),
                        FilterOps.Between => Matching(cq, cv.Where(v => v.DateTimeValue >= from && v.DateTimeValue < to)),
                        _ => cq
                    };
                }
            case StorageKind.Bool:
                {
                    var b = f.Values[0] == "true";
                    return Matching(cq, cv.Where(v => v.BoolValue == b));
                }
            case StorageKind.Ref:
                {
                    var ids = f.Values.Where(v => Guid.TryParse(v, out _)).Select(Guid.Parse).ToArray(); var one = ids.Length > 0 ? ids[0] : Guid.Empty;
                    return f.Op switch
                    {
                        FilterOps.Eq => Matching(cq, cv.Where(v => v.RefValue == one)),
                        FilterOps.NotEq => NotMatching(cq, cv.Where(v => v.RefValue == one)),
                        FilterOps.In => Matching(cq, cv.Where(v => ids.Contains(v.RefValue!.Value))),
                        _ => cq
                    };
                }
            default:
                return cq;
        }
    }

    // ---- sorting -----------------------------------------------------------------------------------------------------------------------------------------

    private IQueryable<Row> ApplySorts(IQueryable<Row> rows, IReadOnlyList<SortClause> sorts, RegisterCatalogue catalogue, CustomFieldSet set, LevelVisibility vis)
    {
        IOrderedQueryable<Row>? ordered = null;
        void Add<K>(System.Linq.Expressions.Expression<Func<Row, K>> key, bool desc)
            => ordered = ordered is null ? (desc ? rows.OrderByDescending(key) : rows.OrderBy(key)) : (desc ? ordered.ThenByDescending(key) : ordered.ThenBy(key));

        foreach (var s in sorts)
        {
            var d = s.Descending;
            var field = catalogue.Find(s.Field)!;
            if (field.IsCustom)
            {
                var def = set.Find(field.Key)!;
                var key = def.Key; var states = def.VisibleInStates.ToArray();
                var cv = db.CustomValues.AsNoTracking().Where(v => v.FieldKey == key && v.Ordinal == 0);
                if (vis.IsOpen(def.Classification))
                {
                    // Every grant allows this field's level, so the access test would be true for every row in the query and is left out (LevelVisibility.IsOpen).
                    switch (FieldTypes.StorageOf(def.Type))
                    {
                        case StorageKind.Text: Add(r => (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.TextValue).FirstOrDefault() : null, d); break;
                        case StorageKind.Number: Add(r => (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.NumberValue).FirstOrDefault() : null, d); break;
                        case StorageKind.Date: Add(r => (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.DateValue).FirstOrDefault() : null, d); break;
                        case StorageKind.DateTime: Add(r => (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.DateTimeValue).FirstOrDefault() : null, d); break;
                        case StorageKind.Bool: Add(r => (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.BoolValue).FirstOrDefault() : null, d); break;
                    }
                    continue;
                }
                var allowed = vis.Ids(def.Classification); var higher = vis.Above(def.Classification);
                switch (FieldTypes.StorageOf(def.Type))
                {
                    case StorageKind.Text: Add(r => (higher.Contains(r.C.Classification) || allowed.Contains(r.C.Id)) && (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.TextValue).FirstOrDefault() : null, d); break;
                    case StorageKind.Number: Add(r => (higher.Contains(r.C.Classification) || allowed.Contains(r.C.Id)) && (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.NumberValue).FirstOrDefault() : null, d); break;
                    case StorageKind.Date: Add(r => (higher.Contains(r.C.Classification) || allowed.Contains(r.C.Id)) && (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.DateValue).FirstOrDefault() : null, d); break;
                    case StorageKind.DateTime: Add(r => (higher.Contains(r.C.Classification) || allowed.Contains(r.C.Id)) && (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.DateTimeValue).FirstOrDefault() : null, d); break;
                    case StorageKind.Bool: Add(r => (higher.Contains(r.C.Classification) || allowed.Contains(r.C.Id)) && (states.Length == 0 || states.Contains(r.C.Status)) ? cv.Where(v => v.ContractId == r.C.Id).Select(v => v.BoolValue).FirstOrDefault() : null, d); break;
                }
                continue;
            }
            switch (field.Key)
            {
                case "agreementReference": Add(r => r.C.AgreementReference, d); break;
                case "contractTitle": Add(r => r.C.ContractTitle, d); break;
                case "supplier": Add(r => r.S.NormalisedName, d); break;
                case "supplierVendorNumber": Add(r => r.S.VendorNumber, d); break;
                case "supplierAbn": Add(r => r.S.Abn, d); break;
                case "supplierType": Add(r => r.S.SupplierType, d); break;
                case "category": Add(r => r.G.Name, d); break;
                case "status": Add(r => r.C.Status, d); break;
                case "classification": Add(r => r.C.Classification, d); break;
                case "contractType": Add(r => r.C.ContractType, d); break;
                case "agreementType": Add(r => r.C.AgreementType, d); break;
                case "riskLevel": Add(r => r.C.RiskLevel, d); break;
                case "functionCode": Add(r => r.C.FunctionCode, d); break;
                case "legalEntity": Add(r => r.C.LegalEntityId, d); break;
                case "businessUnit": Add(r => r.C.BusinessUnitId, d); break;
                case "site": Add(r => r.C.SiteId, d); break;
                case "currencyCode": Add(r => r.C.CurrencyCode, d); break;
                case "estimatedValue": Add(r => r.C.EstimatedValue, d); break;
                case "extensionType": Add(r => r.C.ExtensionType, d); break;
                case "noticePeriodValue": Add(r => r.C.NoticePeriodDays, d); break;
                case "criticalNotice": Add(r => r.C.CriticalNotice, d); break;
                case "legalReviewRequired": Add(r => r.C.LegalReviewRequired, d); break;
                case "sapOutlineAgreementNumber": Add(r => r.C.SapOutlineAgreementNumber, d); break;
                case "startDate": Add(r => r.C.StartDate, d); break;
                case "effectiveDate": Add(r => r.C.EffectiveDate, d); break;
                case "registeredDate": Add(r => r.C.RegisteredDateUtc, d); break;
                case "reviewDate": Add(r => r.C.ReviewDate, d); break;
                case "endDate": Add(r => r.C.EndDate, d); break;
                case "createdUtc": Add(r => r.C.CreatedUtc, d); break;
                case "updatedUtc": Add(r => r.C.UpdatedUtc, d); break;
            }
        }
        // Always finish on the reference and id so a page boundary never repeats or skips a row.
        return ordered is null ? rows.OrderBy(r => r.C.AgreementReference).ThenBy(r => r.C.Id) : ordered.ThenBy(r => r.C.AgreementReference).ThenBy(r => r.C.Id);
    }

    // ---- cells -------------------------------------------------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<RegisterRow>> BuildRowsAsync(List<Row> page, List<RegisterColumn> columns, CustomFieldSet set, ClassificationScale scale, LevelVisibility vis, CancellationToken ct)
    {
        var ids = page.Select(r => r.C.Id).ToArray();
        var names = new Dictionary<Guid, (Dictionary<Guid, string> Bu, Dictionary<Guid, string> Site, Dictionary<Guid, string> Le)>();
        foreach (var org in page.Select(r => r.C.OrganisationId).Distinct())
        {
            var st = await structure.GetAsync(org, ct);
            names[org] = (st.BusinessUnits.ToDictionary(x => x.Id, x => x.Name), st.Sites.ToDictionary(x => x.Id, x => x.Name), st.LegalEntities.ToDictionary(x => x.Id, x => x.Name));
        }
        // Owners (only when asked for): the active owners of the contract's category and its ancestors.
        Dictionary<Guid, string>? ownerNames = null; List<Ecp.Contracts.Domain.CategoryAssignment>? assignments = null; Dictionary<Guid, Guid?>? parents = null;
        if (columns.Any(c => c.Key == "owner"))
        {
            var now = clock.UtcNow;
            assignments = (await db.CategoryAssignments.AsNoTracking().ToListAsync(ct)).Where(a => a.IsActiveAt(now)).ToList();
            parents = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentCategoryId, ct);
            ownerNames = new Dictionary<Guid, string>(await people.DisplayNamesAsync(assignments.Select(a => a.UserId).Distinct(), ct));
        }
        var customColumns = columns.Where(c => c.IsCustom).Select(c => set.Find(c.Key)!).Where(d => d is not null && !d.IsCalculated).ToList();
        var stored = new Dictionary<(Guid, string), List<TypedValue>>();
        var visibleAt = new Dictionary<string, HashSet<Guid>>();
        if (customColumns.Count > 0)
        {
            var keys = customColumns.Select(d => d.Key).ToArray();
            foreach (var v in await db.CustomValues.AsNoTracking().Where(v => ids.Contains(v.ContractId) && keys.Contains(v.FieldKey)).OrderBy(v => v.Ordinal).ToListAsync(ct))
            {
                var def = set.Find(v.FieldKey)!;
                (stored.TryGetValue((v.ContractId, v.FieldKey), out var list) ? list : stored[(v.ContractId, v.FieldKey)] = []).Add(Contracts.ContractCustomFieldService.ToTyped(v, FieldTypes.StorageOf(def.Type)));
            }
            foreach (var level in customColumns.Select(d => d.Classification).Distinct()) visibleAt[level] = vis.IsOpen(level) ? ids.ToHashSet() : (await vis.Ids(level).Where(id => ids.Contains(id)).ToListAsync(ct)).ToHashSet();
        }
        var rolesByOrg = new Dictionary<Guid, IReadOnlySet<string>>();
        foreach (var org in page.Select(r => r.C.OrganisationId).Distinct()) rolesByOrg[org] = customColumns.Count == 0 ? new HashSet<string>() : await RolesAsync(org, ct);
        var todayByOrg = new Dictionary<Guid, DateOnly>();
        foreach (var org in page.Select(r => r.C.OrganisationId).Distinct()) todayByOrg[org] = TodayFor(await structure.GetOrganisationAsync(org, ct));

        string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        string Moment(DateTime? d) => d is null ? "" : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var result = new List<RegisterRow>();
        foreach (var r in page)
        {
            var c = r.C; var n = names[c.OrganisationId];
            var cells = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var col in columns)
            {
                if (col.IsCustom)
                {
                    var def = set.Find(col.Key);
                    if (def is null || def.IsCalculated) { cells[col.Key] = null; continue; }
                    var has = stored.ContainsKey((c.Id, def.Key));
                    var okLevel = scale.Rank(c.Classification) > scale.Rank(def.Classification) || visibleAt[def.Classification].Contains(c.Id);
                    var shown = okLevel && FieldRules.Visible(def, new FieldContext(rolesByOrg[c.OrganisationId], c.Status, c.ContractType, todayByOrg[c.OrganisationId], has));
                    cells[col.Key] = shown && has ? string.Join("; ", ValueCodec.ToRaw(def, stored[(c.Id, def.Key)])) : null;
                    continue;
                }
                cells[col.Key] = col.Key switch
                {
                    "agreementReference" => c.AgreementReference, "contractTitle" => c.ContractTitle, "description" => c.Description, "supplier" => r.S.Name, "supplierVendorNumber" => r.S.VendorNumber,
                    "supplierAbn" => r.S.Abn, "supplierType" => r.S.SupplierType, "category" => r.G.Name, "status" => c.Status, "classification" => c.Classification, "contractType" => c.ContractType,
                    "agreementType" => c.AgreementType, "riskLevel" => c.RiskLevel, "functionCode" => c.FunctionCode,
                    "legalEntity" => c.LegalEntityId is { } le && n.Le.TryGetValue(le, out var lname) ? lname : null,
                    "businessUnit" => c.BusinessUnitId is { } bu && n.Bu.TryGetValue(bu, out var bname) ? bname : null,
                    "site" => c.SiteId is { } si && n.Site.TryGetValue(si, out var sname) ? sname : null,
                    "currencyCode" => c.CurrencyCode, "estimatedValue" => c.EstimatedValue is { } v ? Ecp.Metadata.ValueCodec.Plain(v) : null, "extensionType" => c.ExtensionType,
                    "evergreen" => c.ExtensionType == ExtensionTypes.Evergreen ? "true" : "false",
                    "noticePeriodValue" => c.NoticePeriodValue is { } npv ? $"{npv} {c.NoticePeriodUnit}" : null, "noticePeriodUnit" => c.NoticePeriodUnit,
                    "criticalNotice" => c.CriticalNotice ? "true" : "false", "legalReviewRequired" => c.LegalReviewRequired ? "true" : "false", "recordLocked" => c.RecordLocked ? "true" : "false",
                    "sapOutlineAgreementNumber" => c.SapOutlineAgreementNumber, "startDate" => Iso(c.StartDate), "effectiveDate" => Iso(c.EffectiveDate), "reviewDate" => Iso(c.ReviewDate), "endDate" => Iso(c.EndDate),
                    "registeredDate" => Moment(c.RegisteredDateUtc), "createdUtc" => Moment(c.CreatedUtc), "updatedUtc" => Moment(c.UpdatedUtc),
                    "owner" => OwnersOf(c.CategoryId, assignments, parents, ownerNames),
                    _ => null
                };
            }
            result.Add(new RegisterRow(c.Id, c.OrganisationId, c.AgreementReference, c.ContractTitle, c.Status, c.Classification, cells));
        }
        return result;
    }

    private static string? OwnersOf(Guid categoryId, List<Ecp.Contracts.Domain.CategoryAssignment>? assignments, Dictionary<Guid, Guid?>? parents, Dictionary<Guid, string>? names)
    {
        if (assignments is null || parents is null || names is null) return null;
        var path = new HashSet<Guid>(); Guid? id = categoryId;
        while (id is { } x && path.Add(x) && path.Count < 50) id = parents.GetValueOrDefault(x);
        var owners = assignments.Where(a => path.Contains(a.CategoryId)).OrderByDescending(a => a.IsPrimary).Select(a => names.GetValueOrDefault(a.UserId)).Where(n => n is not null).Distinct().ToList();
        return owners.Count == 0 ? null : string.Join("; ", owners);
    }
}
