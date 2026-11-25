using Dapper;

namespace JbsContracts.Api.Infrastructure;

/// <summary>
/// Makes sure the first admins named in the settings (APPENV_Ecp__Security__FailsafeAdmins__N__Account / __DisplayName / __Email
/// on the GitHub environment) exist, are active and are admins, every time the app starts. This is how the very first person
/// gets in, and the way back in if every admin is removed by mistake.
/// </summary>
public static class FirstAdmins
{
    public static async Task EnsureAsync(Db db, AuditLog audit, AppSettings settings, ILogger logger)
    {
        foreach (var admin in settings.FirstAdmins)
        {
            if (!WindowsAccount.TryNormalise(admin.Account, null, out var account))
            {
                logger.LogError("First admin setting {Index} is not a DOMAIN\\user logon; ignored.", settings.FirstAdmins.ToList().IndexOf(admin));
                continue;
            }
            await using var connection = await db.OpenAsync();
            await using var tx = connection.BeginTransaction();
            await Db.LockAsync(connection, tx, "people");
            var existing = await connection.QuerySingleOrDefaultAsync<Person>(
                "SELECT * FROM contracts.Person WITH (UPDLOCK, HOLDLOCK) WHERE AccountKey = @key", new { key = WindowsAccount.Key(account) }, tx);
            if (existing is null)
            {
                var id = await connection.ExecuteScalarAsync<int>("""
                    INSERT INTO contracts.Person (WindowsAccount, Name, Email, IsAdmin, IsContractOwner) OUTPUT INSERTED.Id
                    VALUES (@account, @name, @email, 1, 1)
                    """, new { account, name = admin.Name, email = admin.Email }, tx);
                await audit.WriteAsync(connection, tx, AuditLog.Actor.System, "person.first-admin", "Person", id,
                    $"{admin.Name} ({account}) was added as an admin from the server settings.", new { account, admin = true, contractOwner = true });
                logger.LogInformation("First admin {PersonId} added from settings.", id);
            }
            else if (!existing.IsAdmin || !existing.IsActive)
            {
                await connection.ExecuteAsync("UPDATE contracts.Person SET IsAdmin = 1, IsActive = 1, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @id", new { id = existing.Id }, tx);
                await audit.WriteAsync(connection, tx, AuditLog.Actor.System, "person.first-admin-restored", "Person", existing.Id,
                    $"{existing.Name}'s admin access was restored from the server settings.", new { wasAdmin = existing.IsAdmin, wasActive = existing.IsActive });
                logger.LogWarning("First admin {PersonId} restored from settings.", existing.Id);
            }
            await tx.CommitAsync();
        }
    }
}
