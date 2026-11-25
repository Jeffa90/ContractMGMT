using System.Net.Mail;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

public sealed record Roles(bool ContractOwner, bool Legal, bool Approver, bool GroupHead, bool Admin, bool Auditor);

public sealed record PersonView(int Id, string Account, string Name, string Email, Roles Roles, bool IsActive, DateTime? LastSignInUtc, string RowVersion)
{
    public static PersonView From(Person p) => new(p.Id, p.WindowsAccount, p.Name, p.Email,
        new Roles(p.IsContractOwner, p.IsLegal, p.IsApprover, p.IsGroupHead, p.IsAdmin, p.IsAuditor), p.IsActive,
        p.LastSignInUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null, Convert.ToBase64String(p.RowVersion));
}

public sealed record SavePerson(string? Account, string? Name, string? Email, Roles? Roles, bool? IsActive, string? RowVersion);

public static class PeopleEndpoints
{
    private static readonly (string Label, Func<Roles, bool> Has)[] RoleLabels =
    [
        ("Contract owner", r => r.ContractOwner), ("Legal", r => r.Legal), ("Approver", r => r.Approver),
        ("Group Head", r => r.GroupHead), ("Admin", r => r.Admin), ("Auditor", r => r.Auditor),
    ];

    public static string Describe(Roles r)
    {
        var names = RoleLabels.Where(x => x.Has(r)).Select(x => x.Label).ToList();
        return names.Count == 0 ? "no roles (can look only)" : string.Join(", ", names);
    }

    public static void MapPeopleEndpoints(this IEndpointRouteBuilder app)
    {
        var people = app.MapGroup("/api/people");

        people.MapGet("/", async (CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsAdmin) return ApiResults.NotAllowed("Only admins can manage people. Ask your admin if you need someone added.");
            await using var connection = await db.OpenAsync();
            var rows = await connection.QueryAsync<Person>("SELECT * FROM contracts.Person ORDER BY IsActive DESC, Name");
            return Results.Ok(rows.Select(PersonView.From));
        });

        people.MapPost("/", async (SavePerson body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsAdmin) return ApiResults.NotAllowed("Only admins can add people.");

            var (input, invalid) = Validate(body, settings, requireRowVersion: false);
            if (invalid is not null) return invalid;

            await using var connection = await db.OpenAsync();
            await using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var clash = await connection.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE AccountKey = @key",
                new { key = WindowsAccount.Key(input.Account) }, tx);
            if (clash is not null)
                return ApiResults.Invalid("This person is already added.", new()
                {
                    ["account"] = clash.IsActive ? $"{clash.Name} already uses {clash.WindowsAccount}." : $"{clash.Name} ({clash.WindowsAccount}) is already here but switched off. Find them in the list and switch them back on.",
                });

            var previousHead = input.Roles.GroupHead ? await ClearGroupHeadAsync(connection, tx, null) : null;
            var id = await connection.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Person (WindowsAccount, Name, Email, IsContractOwner, IsLegal, IsApprover, IsGroupHead, IsAdmin, IsAuditor, CreatedById, UpdatedById)
                OUTPUT INSERTED.Id
                VALUES (@Account, @Name, @Email, @ContractOwner, @Legal, @Approver, @GroupHead, @Admin, @Auditor, @me, @me)
                """, new { input.Account, input.Name, input.Email, input.Roles.ContractOwner, input.Roles.Legal, input.Roles.Approver, input.Roles.GroupHead, input.Roles.Admin, input.Roles.Auditor, me = me.Id }, tx);
            await audit.WriteAsync(connection, tx, me.AsActor(), "person.added", "Person", id,
                $"{me.Name} added {input.Name} ({input.Account}) as {Describe(input.Roles)}.", new { input.Account, input.Name, input.Email, input.Roles });
            if (previousHead is not null)
                await audit.WriteAsync(connection, tx, me.AsActor(), "person.group-head-moved", "Person", previousHead.Id,
                    $"{me.Name} made {input.Name} the Group Head in place of {previousHead.Name}, who stays an approver (backup).");
            await tx.CommitAsync();
            var saved = await connection.QuerySingleAsync<Person>("SELECT * FROM contracts.Person WHERE Id = @id", new { id });
            return Results.Created($"/api/people/{id}", PersonView.From(saved));
        });

        people.MapPut("/{id:int}", async (int id, SavePerson body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsAdmin) return ApiResults.NotAllowed("Only admins can change people.");

            var (input, invalid) = Validate(body, settings, requireRowVersion: true);
            if (invalid is not null) return invalid;

            await using var connection = await db.OpenAsync();
            await using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var before = await connection.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WITH (UPDLOCK) WHERE Id = @id", new { id }, tx);
            if (before is null) return ApiResults.NotFound("We couldn't find that person.");
            if (Convert.ToBase64String(before.RowVersion) != input.RowVersion)
                return ApiResults.Changed($"Someone else changed {before.Name} while you were editing. Your changes weren't saved. Close this and open {before.Name} again to see the latest.");

            var active = input.IsActive ?? before.IsActive;
            if (id == me.Id && (!input.Roles.Admin || !active))
                return ApiResults.NotAllowed("You can't remove your own admin access or switch yourself off. Ask another admin to do it.");
            var clash = await connection.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE AccountKey = @key AND Id <> @id",
                new { key = WindowsAccount.Key(input.Account), id }, tx);
            if (clash is not null)
                return ApiResults.Invalid("That Windows logon belongs to someone else.", new() { ["account"] = $"{clash.Name} already uses {clash.WindowsAccount}." });
            if (before.IsAdmin && before.IsActive && (!input.Roles.Admin || !active))
            {
                var otherAdmins = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person WHERE IsAdmin = 1 AND IsActive = 1 AND Id <> @id", new { id }, tx);
                if (otherAdmins == 0) return ApiResults.NotAllowed("JBS Contracts needs at least one admin. Make someone else an admin first.");
            }

            var previousHead = input.Roles.GroupHead && !before.IsGroupHead ? await ClearGroupHeadAsync(connection, tx, id) : null;
            await connection.ExecuteAsync("""
                UPDATE contracts.Person SET WindowsAccount = @Account, Name = @Name, Email = @Email,
                    IsContractOwner = @ContractOwner, IsLegal = @Legal, IsApprover = @Approver, IsGroupHead = @GroupHead, IsAdmin = @Admin, IsAuditor = @Auditor,
                    IsActive = @active, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                WHERE Id = @id
                """, new { input.Account, input.Name, input.Email, input.Roles.ContractOwner, input.Roles.Legal, input.Roles.Approver, input.Roles.GroupHead, input.Roles.Admin, input.Roles.Auditor, active, me = me.Id, id }, tx);

            var beforeView = PersonView.From(before);
            var changes = new List<string>();
            if (before.WindowsAccount != input.Account) changes.Add($"Windows logon from {before.WindowsAccount} to {input.Account}");
            if (before.Name != input.Name) changes.Add($"name from {before.Name} to {input.Name}");
            if (before.Email != input.Email) changes.Add("email");
            var added = RoleLabels.Where(r => r.Has(input.Roles) && !r.Has(beforeView.Roles)).Select(r => r.Label).ToList();
            var removed = RoleLabels.Where(r => !r.Has(input.Roles) && r.Has(beforeView.Roles)).Select(r => r.Label).ToList();
            if (added.Count > 0) changes.Add("added " + string.Join(", ", added));
            if (removed.Count > 0) changes.Add("removed " + string.Join(", ", removed));
            string summary;
            if (before.IsActive && !active) summary = $"{me.Name} switched off {before.Name}'s access.";
            else if (!before.IsActive && active) summary = $"{me.Name} switched {input.Name}'s access back on" + (changes.Count > 0 ? $" and changed: {string.Join("; ", changes)}." : ".");
            else summary = changes.Count == 0 ? $"{me.Name} saved {input.Name} with no changes." : $"{me.Name} changed {input.Name}: {string.Join("; ", changes)}.";
            await audit.WriteAsync(connection, tx, me.AsActor(), "person.changed", "Person", id, summary,
                new { before = new { beforeView.Account, beforeView.Name, beforeView.Email, beforeView.Roles, beforeView.IsActive }, after = new { input.Account, input.Name, input.Email, input.Roles, IsActive = active } });
            if (previousHead is not null)
                await audit.WriteAsync(connection, tx, me.AsActor(), "person.group-head-moved", "Person", previousHead.Id,
                    $"{me.Name} made {input.Name} the Group Head in place of {previousHead.Name}, who stays an approver (backup).");
            await tx.CommitAsync();
            var saved = await connection.QuerySingleAsync<Person>("SELECT * FROM contracts.Person WHERE Id = @id", new { id });
            return Results.Ok(PersonView.From(saved));
        });
    }

    private sealed record Input(string Account, string Name, string Email, Roles Roles, bool? IsActive, string? RowVersion);

    private static (Input Input, IResult? Invalid) Validate(SavePerson body, AppSettings settings, bool requireRowVersion)
    {
        var fields = new Dictionary<string, string>();
        var domain = settings.DefaultDomain;
        if (!WindowsAccount.TryNormalise(body.Account, domain, out var account))
            fields["account"] = string.IsNullOrWhiteSpace(body.Account)
                ? "Add their Windows logon."
                : domain is null ? "Type the Windows logon as DOMAIN\\username, for example AMH\\jsmith." : $"Type just their username (for example jsmith) or {domain}\\username.";
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0) fields["name"] = "Add their name.";
        else if (name.Length > 200) fields["name"] = "Keep the name under 200 characters.";
        var email = (body.Email ?? "").Trim();
        if (email.Length == 0) fields["email"] = "Add their work email, so they get reminders and approvals.";
        else if (email.Length > 320 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !email.Contains('.', StringComparison.Ordinal))
            fields["email"] = "That doesn't look like an email address. Check it, for example jane.smith@jbssa.com.au.";
        var roles = body.Roles ?? new Roles(false, false, false, false, false, false);
        if (roles.GroupHead && !roles.Approver) roles = roles with { Approver = true };
        if (requireRowVersion && string.IsNullOrEmpty(body.RowVersion))
            return (null!, ApiResults.Changed("This page is out of date. Close it and open the person again."));
        if (fields.Count > 0) return (null!, ApiResults.Invalid("Check the highlighted details.", fields));
        return (new Input(account, name, email, roles, body.IsActive, body.RowVersion), null);
    }

    /// <summary>There is one Group Head. Making someone Group Head turns it off for the previous one, who stays an approver (a backup).</summary>
    private static async Task<Person?> ClearGroupHeadAsync(SqlConnection connection, SqlTransaction tx, int? exceptId)
    {
        var previous = await connection.QuerySingleOrDefaultAsync<Person>(
            "SELECT * FROM contracts.Person WITH (UPDLOCK) WHERE IsGroupHead = 1 AND (@exceptId IS NULL OR Id <> @exceptId)", new { exceptId }, tx);
        if (previous is not null)
            await connection.ExecuteAsync("UPDATE contracts.Person SET IsGroupHead = 0, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @id", new { id = previous.Id }, tx);
        return previous;
    }
}
