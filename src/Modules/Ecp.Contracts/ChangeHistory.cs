using System.Text.Json;
using Ecp.Audit;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

/// <summary>One change to a contract's details, as the signed-in person may see it.</summary>
public sealed record ContractChange(DateTime OccurredUtc, string Who, string What, string? Reason, IReadOnlyList<FieldChange> Fields);

/// <summary>
/// The history of changes to a contract's details (core and configured fields), taken from the audit trail and filtered for the person asking: an event held above their classification level is not
/// shown at all, and a configured field they cannot see is left out of the event. Needs the right to read the contract.
/// </summary>
public interface IContractChangeHistory
{
    Task<IReadOnlyList<ContractChange>> ListAsync(Guid contractId, int take = 100, CancellationToken ct = default);
}

internal sealed class ContractChangeHistory(ContractsDbContext db, IAuditEventSource events, IAccessService access, ContractAccessContext context, IContractCustomFields customFields, IPeopleDirectory people,
    ICustomFieldCatalogue catalogue) : IContractChangeHistory
{
    private static readonly string[] Actions =
        ["contract.updated", "contract.custom_fields_updated", "contract.custom_fields_applied", "contract.fields_changed", "migration.contract.updated", "migration.contract.custom_fields", "migration.contract.created", "migration.contract.legacy_loaded"];

    private static string Label(string action) => action switch
    {
        "contract.updated" => "Details edited",
        "contract.custom_fields_updated" => "Additional details edited",
        "contract.custom_fields_applied" => "Additional details changed by an authorised process",
        "contract.fields_changed" => "Changed by a Variation or an audited unlock",
        "migration.contract.updated" or "migration.contract.custom_fields" => "Updated by a data migration",
        _ => "Loaded by a data migration"
    };

    public async Task<IReadOnlyList<ContractChange>> ListAsync(Guid contractId, int take = 100, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(c, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        var id = contractId.ToString();
        var raw = await events.Events.Where(e => e.EntityType == nameof(Contract) && e.EntityId == id && Actions.Contains(e.Action)).OrderByDescending(e => e.Sequence).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);

        // An event is shown only if the person could read the contract at the level the event is held at.
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
        async Task<bool> MayAsync(string? level)
        {
            var l = level ?? c.Classification;
            if (!allowed.TryGetValue(l, out var ok)) allowed[l] = ok = (await access.CheckSilentAsync("contracts.read", descriptor with { Classification = l }, ct)).Allowed;
            return ok;
        }
        var visibleCustom = (await customFields.ForContractAsync(contractId, ct: ct)).Fields.Select(f => f.Definition).ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        var set = await catalogue.PublishedAsync(c.OrganisationId, ct);
        var actors = raw.Select(e => Guid.TryParse(e.ActorId, out var g) ? g : (Guid?)null).Where(g => g.HasValue).Select(g => g!.Value).Distinct().ToList();
        var names = actors.Count == 0 ? new Dictionary<Guid, string>() : await people.DisplayNamesAsync(actors, ct);

        var result = new List<ContractChange>();
        foreach (var e in raw)
        {
            if (!await MayAsync(e.Classification)) continue;
            var fields = Parse(e.Action, e.OldValuesJson, e.NewValuesJson).Where(f => !IsHiddenCustom(f.Field, visibleCustom, set)).Select(f => f with { Field = LabelOf(f.Field, visibleCustom) }).ToList();
            if (fields.Count == 0 && e.Action.StartsWith("migration.contract.", StringComparison.Ordinal) && e.Action is not ("migration.contract.created" or "migration.contract.legacy_loaded")) continue;
            var who = Guid.TryParse(e.ActorId, out var g) && names.TryGetValue(g, out var n) ? n : e.ActorId is { Length: > 0 } a && !Guid.TryParse(a, out _) ? a : "the platform";
            result.Add(new ContractChange(e.OccurredUtc, who, Label(e.Action), e.Reason, fields));
        }
        return result;
    }

    /// <summary>A configured field the person cannot see on this contract (by role, state or classification) is dropped from the event; a key no longer defined is dropped too.</summary>
    private static bool IsHiddenCustom(string field, Dictionary<string, FieldDefinition> visible, CustomFieldSet set)
        => field.StartsWith("cf.", StringComparison.Ordinal) && !visible.ContainsKey(field[3..]);

    private static string LabelOf(string field, Dictionary<string, FieldDefinition> visible)
    {
        var key = field.StartsWith("cf.", StringComparison.Ordinal) ? field[3..] : field;
        return visible.TryGetValue(key, out var d) ? d.Label : CoreFields.Find(field)?.Label ?? field switch { "ContractTitle" or "contractTitle" => "Contract Title", "Description" or "description" => "Description", _ => field };
    }

    private static IEnumerable<FieldChange> Parse(string action, string? oldJson, string? newJson)
    {
        Dictionary<string, string?> Flat(string? json)
        {
            var d = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return d;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
                foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => p.Value.GetString(), _ => p.Value.ToString() };
            }
            catch (JsonException) { }
            return d;
        }
        Dictionary<string, string?> Nested(string? json, string member)
        {
            var d = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return d;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(member, out var inner) && inner.ValueKind == JsonValueKind.Object)
                    foreach (var p in inner.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
            }
            catch (JsonException) { }
            return d;
        }
        var before = Flat(oldJson);
        var after = action switch
        {
            "contract.custom_fields_updated" or "contract.custom_fields_applied" or "migration.contract.custom_fields" => Nested(newJson, "fields"),
            "contract.fields_changed" => Nested(newJson, "changes"),
            _ => Flat(newJson)
        };
        if (action is "migration.contract.created" or "migration.contract.legacy_loaded") yield break;
        var custom = action is "contract.custom_fields_updated" or "contract.custom_fields_applied" or "migration.contract.custom_fields";
        foreach (var key in after.Keys.Union(before.Keys))
        {
            before.TryGetValue(key, out var b); after.TryGetValue(key, out var a);
            if (!string.Equals(b, a, StringComparison.Ordinal)) yield return new FieldChange(custom ? "cf." + key : key, b, a);
        }
    }
}
