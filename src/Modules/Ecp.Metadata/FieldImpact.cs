namespace Ecp.Metadata;

public enum ImpactLevel { Info, Warning, Blocking }

/// <summary>One thing a draft would change for the people and the data that already exist.</summary>
public sealed record ImpactItem(ImpactLevel Level, string Key, string Message, int AffectedValues = 0);

/// <summary>
/// What publishing a draft would do to what already exists, worked out from the published definitions, the draft and how much stored data depends on them. Pure and deterministic, so the Studio shows the
/// same analysis the validator enforces. A <see cref="ImpactLevel.Blocking"/> item is also an error when the draft is validated.
/// </summary>
public static class FieldImpact
{
    public static IReadOnlyList<ImpactItem> Analyse(CustomFieldSet? published, CustomFieldSet draft, FieldUsage usage)
    {
        var items = new List<ImpactItem>();
        var before = published ?? CustomFieldSet.Empty;
        foreach (var f in draft.Fields)
        {
            var old = before.Find(f.Key);
            var used = usage.Of(f.Key);
            if (old is null)
            {
                items.Add(new(ImpactLevel.Info, f.Key, $"New field '{f.Label}'. Existing agreements have no value for it until someone enters one.{(f.Required ? " Because it is required, it is asked for the next time each agreement is edited." : "")}"));
                continue;
            }
            if (!old.IsRetired && f.IsRetired) items.Add(new(ImpactLevel.Warning, f.Key, $"'{f.Label}' is retired. {used} stored value(s) are kept and still shown, but no new values can be entered.", used));
            if (old.IsRetired && !f.IsRetired) items.Add(new(ImpactLevel.Info, f.Key, $"'{f.Label}' is reinstated and takes new values again.", used));
            if (!string.Equals(old.Type, f.Type, StringComparison.Ordinal)) items.Add(new(used > 0 ? ImpactLevel.Blocking : ImpactLevel.Warning, f.Key, used > 0 ? $"The type of '{f.Label}' cannot change while {used} agreement(s) hold a value. Retire it and add a new field." : $"The type of '{f.Label}' changes from {FieldTypes.Label(old.Type)} to {FieldTypes.Label(f.Type)}. No values are stored yet.", used));
            if (!string.Equals(old.Classification, f.Classification, StringComparison.Ordinal)) items.Add(new(ImpactLevel.Warning, f.Key, $"The classification of '{f.Label}' changes from {old.Classification} to {f.Classification}. It decides who can read {used} stored value(s), in the register, exports and approvals.", used));
            if (!old.VisibleToRoles.SequenceEqual(f.VisibleToRoles) || !old.VisibleInStates.SequenceEqual(f.VisibleInStates) || !old.ContractTypes.SequenceEqual(f.ContractTypes))
                items.Add(new(ImpactLevel.Warning, f.Key, $"Who sees '{f.Label}' (roles, states or contract types) changes. Stored values are kept; a value that no longer applies stays visible only where it was already entered.", used));
            if (!old.EditableByRoles.SequenceEqual(f.EditableByRoles) || !old.EditableInStates.SequenceEqual(f.EditableInStates))
                items.Add(new(ImpactLevel.Info, f.Key, $"Who can change '{f.Label}' (roles or states) changes."));
            if (!old.Required && f.Required) items.Add(new(ImpactLevel.Warning, f.Key, $"'{f.Label}' becomes required. Agreements without a value are asked for it the next time they are edited; nothing is changed now.", Math.Max(0, usage.Contracts - used)));
            if (old.UsableInApprovals != f.UsableInApprovals) items.Add(new(ImpactLevel.Warning, f.Key, f.UsableInApprovals ? $"'{f.Label}' becomes available to approval matrices. A matrix explanation then shows the values it judged." : $"'{f.Label}' is no longer available to approval matrices. A matrix that names it stops resolving until it is changed.", used));
            if (old.Searchable != f.Searchable || old.Filterable != f.Filterable || old.Sortable != f.Sortable || old.Reportable != f.Reportable)
                items.Add(new(ImpactLevel.Info, f.Key, $"Where '{f.Label}' appears changes: search {Flag(f.Searchable)}, filter {Flag(f.Filterable)}, sort {Flag(f.Sortable)}, exports and reports {Flag(f.Reportable)}."));
            foreach (var removed in ChoicesOf(old, before).Where(c => ChoicesOf(f, draft).All(n => !string.Equals(n.Code, c.Code, StringComparison.OrdinalIgnoreCase))))
            {
                var n = usage.Of(f.Key, removed.Code);
                items.Add(new(n > 0 ? ImpactLevel.Blocking : ImpactLevel.Warning, f.Key, n > 0 ? $"The choice '{removed.Label}' of '{f.Label}' is held by {n} agreement(s). Make it inactive instead of removing it." : $"The choice '{removed.Label}' of '{f.Label}' is removed. No agreement holds it.", n));
            }
            foreach (var off in ChoicesOf(f, draft).Where(c => !c.Active && ChoicesOf(old, before).FirstOrDefault(o => string.Equals(o.Code, c.Code, StringComparison.OrdinalIgnoreCase))?.Active == true))
            {
                var n = usage.Of(f.Key, off.Code);
                items.Add(new(ImpactLevel.Warning, f.Key, $"The choice '{off.Label}' of '{f.Label}' is made inactive. {n} stored value(s) keep it and still show; it cannot be chosen again.", n));
            }
        }
        foreach (var f in before.Fields.Where(o => draft.Find(o.Key) is null))
        {
            var used = usage.Of(f.Key);
            items.Add(new(ImpactLevel.Blocking, f.Key, $"'{f.Label}' is published and cannot be deleted{(used > 0 ? $" ({used} agreement(s) hold a value)" : "")}. Retire it instead; retiring keeps every stored value.", used));
        }
        return items;
    }

    private static string Flag(bool on) => on ? "on" : "off";
    private static IReadOnlyList<FieldChoice> ChoicesOf(FieldDefinition d, CustomFieldSet set) => set.ChoicesOf(d);
}
