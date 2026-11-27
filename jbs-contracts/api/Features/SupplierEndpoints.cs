using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public sealed class SupplierRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Abn { get; set; }
    public string? VendorNumber { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public int ContractCount { get; set; }
    /// <summary>The group it's in, if any (D-116): the parent supplier, for example a franchise's head company.</summary>
    public int? ParentSupplierId { get; set; }
    public string? ParentSupplierName { get; set; }
    public int MemberCount { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed record SaveSupplier(string? Name, string? Abn, string? VendorNumber, string? Notes, bool? IsActive, string? RowVersion);

public static class SupplierEndpoints
{
    public static void MapSupplierEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/suppliers", async (CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            // Contract counts include only contracts this person can see (Confidential ones are not revealed).
            var rows = await c.QueryAsync<SupplierRow>($"""
                SELECT s.Id, s.Name, s.Abn, s.VendorNumber, s.Notes, s.IsActive, s.RowVersion, s.ParentSupplierId, ps.Name AS ParentSupplierName,
                       (SELECT COUNT(*) FROM contracts.Supplier m WHERE m.ParentSupplierId = s.Id) AS MemberCount,
                       -- A group member is covered by its group's contracts too (D-116).
                       (SELECT COUNT(*) FROM contracts.Contract k WHERE (k.SupplierId = s.Id OR k.SupplierId = s.ParentSupplierId) AND k.Stage <> N'Withdrawn' AND {ContractAccess.VisibleSql("k")}) AS ContractCount
                FROM contracts.Supplier s LEFT JOIN contracts.Supplier ps ON ps.Id = s.ParentSupplierId ORDER BY s.Name
                """, ContractAccess.Params(me!));
            return Results.Ok(rows.Select(View));
        });

        app.MapPost("/api/suppliers", async (SaveSupplier body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsContractOwner && !me.IsAdmin) return ApiResults.NotAllowed("Only contract owners and admins can add suppliers.");
            var (input, invalid) = Validate(body);
            if (invalid is not null) return invalid;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var clash = await c.QuerySingleOrDefaultAsync<SupplierRow>("SELECT Id, Name, IsActive FROM contracts.Supplier WITH (UPDLOCK, HOLDLOCK) WHERE NameKey = UPPER(LTRIM(RTRIM(@Name)))", new { input.Name }, tx);
            if (clash is not null) return ApiResults.Invalid("That supplier is already here.", new() { ["name"] = clash.IsActive ? $"{clash.Name} is already a supplier. Choose it from the list." : $"{clash.Name} is already here but hidden. An admin can show it again." });
            var id = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Supplier (Name, Abn, VendorNumber, Notes, CreatedById, UpdatedById) OUTPUT INSERTED.Id
                VALUES (@Name, @Abn, @VendorNumber, @Notes, @me, @me)
                """, new { input.Name, input.Abn, input.VendorNumber, input.Notes, me = me.Id }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "supplier.added", "Supplier", id, $"{me.Name} added the supplier {input.Name}.", input);
            await tx.CommitAsync();
            var saved = await c.QuerySingleAsync<SupplierRow>("SELECT *, 0 AS ContractCount FROM contracts.Supplier WHERE Id = @id", new { id });
            return Results.Created($"/api/suppliers/{id}", View(saved));
        });

        app.MapPut("/api/suppliers/{id:int}", async (int id, SaveSupplier body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsContractOwner && !me.IsAdmin) return ApiResults.NotAllowed("Only contract owners and admins can change suppliers.");
            var (input, invalid) = Validate(body);
            if (invalid is not null) return invalid;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await c.QuerySingleOrDefaultAsync<SupplierRow>("SELECT *, 0 AS ContractCount FROM contracts.Supplier WITH (UPDLOCK) WHERE Id = @id", new { id }, tx);
            if (before is null) return ApiResults.NotFound("We couldn't find that supplier.");
            if (Convert.ToBase64String(before.RowVersion) != body.RowVersion)
                return ApiResults.Changed($"Someone else changed {before.Name} while you were editing. Your changes weren't saved. Close this and open it again to see the latest.");
            var active = body.IsActive ?? before.IsActive;
            if (active != before.IsActive && !me.IsAdmin) return ApiResults.NotAllowed("Only admins can hide or show suppliers.");
            var clash = await c.QuerySingleOrDefaultAsync<string>("SELECT Name FROM contracts.Supplier WITH (UPDLOCK, HOLDLOCK) WHERE NameKey = UPPER(LTRIM(RTRIM(@Name))) AND Id <> @id", new { input.Name, id }, tx);
            if (clash is not null) return ApiResults.Invalid("Another supplier has that name.", new() { ["name"] = $"{clash} is already a supplier." });
            await c.ExecuteAsync("""
                UPDATE contracts.Supplier SET Name = @Name, Abn = @Abn, VendorNumber = @VendorNumber, Notes = @Notes, IsActive = @active,
                    UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id
                """, new { input.Name, input.Abn, input.VendorNumber, input.Notes, active, me = me.Id, id }, tx);
            var changes = new List<string>();
            if (before.Name != input.Name) changes.Add($"name from {before.Name} to {input.Name}");
            if (before.Abn != input.Abn) changes.Add("ABN");
            if (before.VendorNumber != input.VendorNumber) changes.Add("vendor number");
            if (before.Notes != input.Notes) changes.Add("notes");
            var summary = before.IsActive && !active ? $"{me.Name} hid the supplier {before.Name}."
                : !before.IsActive && active ? $"{me.Name} showed the supplier {input.Name} again."
                : changes.Count == 0 ? $"{me.Name} saved {input.Name} with no changes." : $"{me.Name} changed the supplier {input.Name}: {string.Join("; ", changes)}.";
            await audit.WriteAsync(c, tx, me.AsActor(), "supplier.changed", "Supplier", id, summary,
                new { before = new { before.Name, before.Abn, before.VendorNumber, before.Notes, before.IsActive }, after = new { input.Name, input.Abn, input.VendorNumber, input.Notes, IsActive = active } });
            await tx.CommitAsync();
            var saved = await c.QuerySingleAsync<SupplierRow>("SELECT *, 0 AS ContractCount FROM contracts.Supplier WHERE Id = @id", new { id });
            return Results.Ok(View(saved));
        });
    }

    private static object View(SupplierRow s) => new
    {
        s.Id, s.Name, s.Abn, s.VendorNumber, s.Notes, s.IsActive, s.ContractCount, s.ParentSupplierId, s.ParentSupplierName, s.MemberCount, RowVersion = Convert.ToBase64String(s.RowVersion),
    };

    private sealed record Input(string Name, string? Abn, string? VendorNumber, string? Notes);

    private static (Input Input, IResult? Invalid) Validate(SaveSupplier body)
    {
        var fields = new Dictionary<string, string>();
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0) fields["name"] = "Add the supplier's name.";
        else if (name.Length > 200) fields["name"] = "Keep the name under 200 characters.";
        string? abn = null;
        if (!string.IsNullOrWhiteSpace(body.Abn))
        {
            abn = new string(body.Abn.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            if (!Abn.IsValid(abn)) fields["abn"] = "That ABN doesn't check out. It has 11 digits, for example 51 824 753 556. Leave it blank for an overseas supplier.";
        }
        var vendor = string.IsNullOrWhiteSpace(body.VendorNumber) ? null : body.VendorNumber.Trim();
        if (vendor is { Length: > 50 }) fields["vendorNumber"] = "Keep the vendor number under 50 characters.";
        var notes = string.IsNullOrWhiteSpace(body.Notes) ? null : body.Notes.Trim();
        if (notes is { Length: > 2000 }) fields["notes"] = "Keep the notes under 2,000 characters.";
        if (fields.Count > 0) return (null!, ApiResults.Invalid("Check the highlighted details.", fields));
        return (new Input(name, abn, vendor, notes), null);
    }
}

/// <summary>Australian Business Number: 11 digits with a check (weights 10,1,3,...,19 after subtracting 1 from the first digit). Format only; no registry lookup.</summary>
public static class Abn
{
    private static readonly int[] Weights = [10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19];

    public static bool IsValid(string abn)
    {
        if (abn.Length != 11 || !abn.All(char.IsAsciiDigit)) return false;
        var sum = 0;
        for (var i = 0; i < 11; i++) sum += ((abn[i] - '0') - (i == 0 ? 1 : 0)) * Weights[i];
        return sum % 89 == 0;
    }
}
