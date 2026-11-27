using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

public sealed record LayoutSection(string? Key, string Title, bool IsHidden);

public sealed record LayoutField(
    string? Key, string Kind, string Label, string? Help, string SectionKey, bool IsRequired, bool IsHidden, bool ShowInList, bool Filterable,
    bool InExport, string? AfterEffective, List<ChoiceOption>? Choices, int? MaxLength, decimal? MinValue, decimal? MaxValue);

/// <summary>Sections in order, and fields in order (their order within each section is their position).</summary>
public sealed record SaveLayout(int VersionId, List<LayoutSection> Sections, List<LayoutField> Fields);

/// <summary>
/// Admin, Contract fields (decisions D-15 to D-18): add, rename, move, hide and require fields and sections without code.
/// The admin edits with a live preview and saves the whole layout at once; every saved layout is kept and can be put back.
/// Rules: built-in fields can't be deleted or change kind; rule fields can't be hidden; a field's kind can't change once a contract
/// has a value in it; removing a field hides it and keeps its values. No formulas or code anywhere.
/// </summary>
public static partial class LayoutEndpoints
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    private static string Slug(string text)
    {
        var s = NonSlug().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return s.Length == 0 ? "item" : s.Length > 40 ? s[..40].Trim('-') : s;
    }

    public static async Task<object> ViewAsync(SqlConnection c, SqlTransaction? tx = null)
    {
        var sections = await Fields.LoadSectionsAsync(c, tx);
        var fields = await Fields.LoadAsync(c, tx);
        var versionId = await c.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(Id), 0) FROM contracts.LayoutVersion", transaction: tx);
        return new
        {
            versionId,
            titleFromDetails = await ContractTitles.FromDetailsAsync(c, tx), // D-132
            sections = sections.Select(s => new { s.Key, s.Title, s.IsHidden }),
            fields = fields.Select(f => new
            {
                f.Key, f.IsBuiltIn, f.IsRuleField, f.Kind, KindName = Fields.KindNames[f.Kind], f.Label, f.Help, f.SectionKey, f.IsRequired, f.IsHidden,
                f.ShowInList, f.Filterable, CanFilter = Fields.CanFilter(f), f.InExport, f.AfterEffective, f.Choices, f.MaxLength, f.MinValue, f.MaxValue, f.HasValues,
            }),
        };
    }

    public static void MapLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/layout", async (CurrentUser current, Db db) =>
        {
            var (_, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            return Results.Ok(await ViewAsync(c));
        });

        app.MapPut("/api/layout", async (SaveLayout body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ListsEndpoints.RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var latest = await c.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(Id), 0) FROM contracts.LayoutVersion", transaction: tx);
            if (body.VersionId != latest)
                return ApiResults.Changed("Another admin saved the fields while you were editing. Your changes weren't saved. Reload to see theirs, then make yours again.");
            // Keep the starting layout as version 1, so it can always be put back.
            if (latest == 0) await SaveVersionAsync(c, tx, me!, "The starting layout");
            var (summary, problems) = await ApplyAsync(c, tx, body, hideMissing: false);
            if (problems.Count > 0) return ApiResults.Invalid("Some changes can't be saved yet. Check the messages.", problems);
            if (summary.Count == 0) return Results.Ok(await ViewAsync(c, tx));
            await SaveVersionAsync(c, tx, me!, string.Join("; ", summary));
            await audit.WriteAsync(c, tx, me!.AsActor(), "layout.saved", "Layout", null, $"{me.Name} changed the contract fields: {string.Join("; ", summary)}.");
            var view = await ViewAsync(c, tx);
            await tx.CommitAsync();
            return Results.Ok(view);
        });

        app.MapGet("/api/layout/history", async (CurrentUser current, Db db) =>
        {
            var (_, refusal) = await ListsEndpoints.RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await c.QueryAsync("SELECT Id, SavedUtc, SavedByName, Summary FROM contracts.LayoutVersion ORDER BY Id DESC");
            return Results.Ok(rows.Select(r => new { r.Id, SavedUtc = DateTime.SpecifyKind((DateTime)r.SavedUtc, DateTimeKind.Utc), r.SavedByName, r.Summary }));
        });

        app.MapPost("/api/layout/history/{id:int}/restore", async (int id, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ListsEndpoints.RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var json = await c.QuerySingleOrDefaultAsync<string>("SELECT LayoutJson FROM contracts.LayoutVersion WHERE Id = @id", new { id }, tx);
            if (json is null) return ApiResults.NotFound("We couldn't find that saved version.");
            var saved = JsonSerializer.Deserialize<SaveLayout>(json, FieldRow.Json)!;
            // Fields added since that version stay, hidden, so no value is lost.
            var (summary, problems) = await ApplyAsync(c, tx, saved, hideMissing: true);
            if (problems.Count > 0) return ApiResults.Invalid("That version can't be put back as it is. " + string.Join(" ", problems.Values), problems);
            await SaveVersionAsync(c, tx, me!, $"Put back the layout saved as version {id}" + (summary.Count > 0 ? $": {string.Join("; ", summary)}" : ""));
            await audit.WriteAsync(c, tx, me!.AsActor(), "layout.restored", "Layout", id, $"{me.Name} put back the contract fields as saved in version {id}.");
            var view = await ViewAsync(c, tx);
            await tx.CommitAsync();
            return Results.Ok(view);
        });
    }

    private static async Task SaveVersionAsync(SqlConnection c, SqlTransaction tx, Person me, string summary)
    {
        var layout = await CurrentAsync(c, tx);
        await c.ExecuteAsync("INSERT INTO contracts.LayoutVersion (SavedById, SavedByName, Summary, LayoutJson) VALUES (@id, @name, @summary, @json)",
            new { id = me.Id, name = me.Name, summary = summary.Length > 2000 ? summary[..1997] + "..." : summary, json = JsonSerializer.Serialize(layout, FieldRow.Json) }, tx);
    }

    /// <summary>The layout as stored, in the same shape the admin saves.</summary>
    public static async Task<SaveLayout> CurrentAsync(SqlConnection c, SqlTransaction tx)
    {
        var sections = await Fields.LoadSectionsAsync(c, tx);
        var fields = await Fields.LoadAsync(c, tx);
        return new SaveLayout(0, sections.Select(s => new LayoutSection(s.Key, s.Title, s.IsHidden)).ToList(),
            fields.Select(f => new LayoutField(f.Key, f.Kind, f.Label, f.Help, f.SectionKey, f.IsRequired, f.IsHidden, f.ShowInList, f.Filterable, f.InExport,
                f.AfterEffective, f.Choices, f.MaxLength, f.MinValue, f.MaxValue)).ToList());
    }

    /// <summary>Checks and applies a whole layout. Returns a plain summary of what changed, or problems keyed by section or field.</summary>
    private static async Task<(List<string> Summary, Dictionary<string, string> Problems)> ApplyAsync(SqlConnection c, SqlTransaction tx, SaveLayout body, bool hideMissing)
    {
        var problems = new Dictionary<string, string>();
        var summary = new List<string>();
        var sections = await Fields.LoadSectionsAsync(c, tx);
        var fields = await Fields.LoadAsync(c, tx);
        var sectionsByKey = sections.ToDictionary(s => s.Key);
        var fieldsByKey = fields.ToDictionary(f => f.Key);

        // ---- Sections
        var newSectionKeys = new Dictionary<string, string>(); // temp key from client -> real key
        var seenSections = new HashSet<string>();
        for (var i = 0; i < body.Sections.Count; i++)
        {
            var s = body.Sections[i];
            var title = (s.Title ?? "").Trim();
            var id = s.Key is not null && sectionsByKey.ContainsKey(s.Key) ? s.Key : null;
            var problemKey = $"section.{s.Key ?? i.ToString()}";
            if (title.Length is 0 or > 100) { problems[problemKey] = "Give each section a title under 100 characters."; continue; }
            if (!seenSections.Add(title.ToUpperInvariant())) { problems[problemKey] = $"There are two sections called \"{title}\"."; continue; }
            if (id is null)
            {
                var key = "s-" + Slug(title);
                for (var n = 2; sectionsByKey.ContainsKey(key) || newSectionKeys.ContainsValue(key); n++) key = $"s-{Slug(title)}-{n}";
                if (s.Key is not null) newSectionKeys[s.Key] = key;
                await c.ExecuteAsync("INSERT INTO contracts.FormSection ([Key], Title, Position, IsHidden) VALUES (@key, @title, @i, @h)", new { key, title, i, h = s.IsHidden }, tx);
                summary.Add($"added the section {title}");
            }
            else
            {
                var before = sectionsByKey[id];
                if (before.Title != title) summary.Add($"renamed the section {before.Title} to {title}");
                if (before.IsHidden != s.IsHidden) summary.Add(s.IsHidden ? $"hid the section {title}" : $"showed the section {title} again");
                if (before.Position != i && before.Title == title) summary.Add($"moved the section {title}");
                await c.ExecuteAsync("UPDATE contracts.FormSection SET Title = @title, Position = @i, IsHidden = @h WHERE [Key] = @id", new { title, i, h = s.IsHidden, id }, tx);
            }
        }
        foreach (var missing in sections.Where(s => body.Sections.All(b => b.Key != s.Key)))
        {
            if (!hideMissing && fields.Any(f => f.SectionKey == missing.Key && body.Fields.Any(b => b.Key == f.Key && b.SectionKey == missing.Key)))
                problems[$"section.{missing.Key}"] = $"The section {missing.Title} still has fields. Move them first.";
            else if (!missing.IsHidden)
            {
                await c.ExecuteAsync("UPDATE contracts.FormSection SET IsHidden = 1 WHERE [Key] = @Key", new { missing.Key }, tx);
                summary.Add($"hid the section {missing.Title}");
            }
        }
        var sectionIds = (await Fields.LoadSectionsAsync(c, tx)).ToDictionary(s => s.Key, s => s.Id);
        string ResolveSection(string key) => newSectionKeys.TryGetValue(key, out var real) ? real : key;

        // ---- Fields
        var labels = new HashSet<string>();
        var positions = new Dictionary<string, int>();
        foreach (var f in body.Fields)
        {
            var label = (f.Label ?? "").Trim();
            var problemKey = $"field.{f.Key ?? label}";
            var existing = f.Key is not null && fieldsByKey.TryGetValue(f.Key, out var e) ? e : null;
            if (label.Length is 0 or > 100) { problems[problemKey] = "Give each field a label under 100 characters."; continue; }
            if (!labels.Add(label.ToUpperInvariant())) { problems[problemKey] = $"Two fields are called \"{label}\". Labels must be different (they're also the Excel column headings)."; continue; }
            var help = string.IsNullOrWhiteSpace(f.Help) ? null : f.Help.Trim();
            if (help is { Length: > 300 }) { problems[problemKey] = $"{label}: keep the help under 300 characters."; continue; }
            var sectionKey = ResolveSection(f.SectionKey);
            if (!sectionIds.TryGetValue(sectionKey, out var sectionId)) { problems[problemKey] = $"{label}: choose a section."; continue; }
            positions[sectionKey] = positions.GetValueOrDefault(sectionKey) + 1;
            var position = positions[sectionKey] - 1;

            if (existing is not null && existing.IsBuiltIn)
            {
                if (existing.IsRuleField && f.IsHidden) { problems[problemKey] = $"{label} is needed by the contract rules, so it can be renamed and moved but not hidden."; continue; }
                Describe(summary, existing, label, help, sectionKey, f);
                await c.ExecuteAsync("""
                    UPDATE contracts.FieldDefinition SET Label = @label, Help = @help, SectionId = @sectionId, Position = @position, IsRequired = @req,
                        IsHidden = @hid, ShowInList = @list, Filterable = @filter, InExport = @export WHERE [Key] = @key
                    """, new { label, help, sectionId, position, req = f.IsRequired || existing.IsRuleField && existing.IsRequired, hid = f.IsHidden, list = f.ShowInList, filter = f.Filterable, export = f.InExport, key = existing.Key }, tx);
                continue;
            }
            if (!Fields.AddedKinds.Contains(f.Kind)) { problems[problemKey] = $"{label}: choose what kind of field it is."; continue; }
            if (existing is not null && existing.Kind != f.Kind && existing.HasValues)
            { problems[problemKey] = $"{label} already has values on contracts, so its kind can't change. Add a new field instead (and hide this one)."; continue; }
            var afterEffective = f.AfterEffective == "AnyTime" ? "AnyTime" : "Variation";
            List<ChoiceOption>? choices = null;
            if (f.Kind is "Choice" or "MultiChoice")
            {
                choices = [];
                var seenChoice = new HashSet<string>();
                var oldChoices = existing?.Choices ?? [];
                foreach (var ch in f.Choices ?? [])
                {
                    var chLabel = (ch.Label ?? "").Trim();
                    if (chLabel.Length == 0) continue;
                    if (chLabel.Length > 100 || !seenChoice.Add(chLabel.ToUpperInvariant())) { problems[problemKey] = $"{label}: each option needs a different name under 100 characters."; break; }
                    var key = !string.IsNullOrEmpty(ch.Key) && oldChoices.Any(o => o.Key == ch.Key) ? ch.Key : Slug(chLabel);
                    while (choices.Any(x => x.Key == key) || (string.IsNullOrEmpty(ch.Key) && oldChoices.Any(o => o.Key == key))) key += "-x";
                    choices.Add(new ChoiceOption { Key = key, Label = chLabel, Hidden = ch.Hidden });
                }
                // An option in use is never dropped: it is kept, hidden.
                foreach (var old in oldChoices.Where(o => choices.All(x => x.Key != o.Key))) choices.Add(new ChoiceOption { Key = old.Key, Label = old.Label, Hidden = true });
                if (problems.ContainsKey(problemKey)) continue;
                if (choices.All(x => x.Hidden)) { problems[problemKey] = $"{label}: add at least one option to choose from."; continue; }
            }
            int? maxLength = f.Kind is "ShortText" or "LongText" ? f.MaxLength : null;
            if (maxLength is < 1 or > 4000) { problems[problemKey] = $"{label}: the longest answer must be between 1 and 4,000 characters."; continue; }
            decimal? minV = f.Kind is "WholeNumber" or "Decimal" or "Money" ? f.MinValue : null, maxV = f.Kind is "WholeNumber" or "Decimal" or "Money" ? f.MaxValue : null;
            if (minV is not null && maxV is not null && minV > maxV) { problems[problemKey] = $"{label}: the smallest allowed number is bigger than the largest."; continue; }
            var choicesJson = choices is null ? null : JsonSerializer.Serialize(choices, FieldRow.Json);
            var args = new { label, help, sectionId, position, req = f.IsRequired, hid = f.IsHidden, list = f.ShowInList, filter = f.Filterable, export = f.InExport, kind = f.Kind, afterEffective, choicesJson, maxLength, minV, maxV };
            if (existing is null)
            {
                var key = "f-" + Slug(label);
                for (var n = 2; fieldsByKey.ContainsKey(key) || await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.FieldDefinition WHERE [Key] = @key", new { key }, tx) > 0; n++)
                    key = $"f-{Slug(label)}-{n}";
                var p = new DynamicParameters(args);
                p.Add("key", key);
                await c.ExecuteAsync("""
                    INSERT INTO contracts.FieldDefinition ([Key], IsBuiltIn, Kind, Label, Help, SectionId, Position, IsRequired, IsHidden, ShowInList, Filterable, InExport, AfterEffective, ChoicesJson, MaxLength, MinValue, MaxValue)
                    VALUES (@key, 0, @kind, @label, @help, @sectionId, @position, @req, @hid, @list, @filter, @export, @afterEffective, @choicesJson, @maxLength, @minV, @maxV)
                    """, p, tx);
                summary.Add($"added the field {label} ({Fields.KindNames[f.Kind].ToLowerInvariant()})");
            }
            else
            {
                Describe(summary, existing, label, help, sectionKey, f);
                if (existing.Kind != f.Kind) summary.Add($"changed {label} to {Fields.KindNames[f.Kind].ToLowerInvariant()}");
                if (existing.ChoicesJson != choicesJson && choices is not null) summary.Add($"changed the options for {label}");
                var p = new DynamicParameters(args);
                p.Add("key", existing.Key);
                await c.ExecuteAsync("""
                    UPDATE contracts.FieldDefinition SET Kind = @kind, Label = @label, Help = @help, SectionId = @sectionId, Position = @position, IsRequired = @req, IsHidden = @hid,
                        ShowInList = @list, Filterable = @filter, InExport = @export, AfterEffective = @afterEffective, ChoicesJson = @choicesJson, MaxLength = @maxLength, MinValue = @minV, MaxValue = @maxV
                    WHERE [Key] = @key
                    """, p, tx);
            }
        }
        // Fields left out of the layout: built-in ones are refused; added ones are hidden (never deleted).
        foreach (var missing in fields.Where(f => body.Fields.All(b => b.Key != f.Key)))
        {
            if (missing.IsBuiltIn && !hideMissing) { problems[$"field.{missing.Key}"] = $"{missing.Label} is built in and can't be removed."; continue; }
            if (missing.IsBuiltIn || missing.IsHidden) continue;
            await c.ExecuteAsync("UPDATE contracts.FieldDefinition SET IsHidden = 1 WHERE [Key] = @Key", new { missing.Key }, tx);
            summary.Add($"removed {missing.Label} (its values are kept)");
        }
        return (summary, problems);
    }

    private static void Describe(List<string> summary, FieldRow before, string label, string? help, string sectionKey, LayoutField f)
    {
        if (before.Label != label) summary.Add($"renamed {before.Label} to {label}");
        if (before.SectionKey != sectionKey) summary.Add($"moved {label} to another section");
        if (before.IsHidden != f.IsHidden) summary.Add(f.IsHidden ? $"removed {label} (its values are kept)" : $"brought back {label}");
        if (before.IsRequired != f.IsRequired && !before.IsRuleField) summary.Add(f.IsRequired ? $"made {label} required" : $"made {label} optional");
        if (before.Help != help) summary.Add($"changed the help for {label}");
        if (before.ShowInList != f.ShowInList) summary.Add(f.ShowInList ? $"offered {label} as a list column" : $"stopped offering {label} as a list column");
        if (before.Filterable != f.Filterable) summary.Add(f.Filterable ? $"made {label} filterable" : $"stopped filtering on {label}");
        if (before.InExport != f.InExport) summary.Add(f.InExport ? $"included {label} in exports" : $"left {label} out of exports");
    }
}
