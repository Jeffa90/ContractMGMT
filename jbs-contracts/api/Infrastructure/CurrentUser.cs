using Dapper;

namespace JbsContracts.Api.Infrastructure;

public sealed class Person
{
    public int Id { get; set; }
    public string WindowsAccount { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsContractOwner { get; set; }
    public bool IsLegal { get; set; }
    public bool IsApprover { get; set; }
    public bool IsGroupHead { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsAuditor { get; set; }
    public bool IsProcurement { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastSignInUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    /// <summary>On QA, the admin who is trying the app as this person ("Try as", D-81). Null normally.</summary>
    public int? TriedById { get; set; }
    public string? TriedByName { get; set; }

    /// <summary>Who did something, for the audit: when an admin is trying the app as someone, the admin, plainly marked.</summary>
    public AuditLog.Actor AsActor() => TriedById is null ? new(Id, Name) : new(TriedById, Name);

    /// <summary>Legal, approvers, admins and auditors see every contract, including Confidential ones.</summary>
    public bool SeesAllConfidential => IsLegal || IsApprover || IsAdmin || IsAuditor;
}

/// <summary>
/// The signed-in person for this request: the Windows account, matched to someone an admin has added. On QA only (AllowDemoData), an
/// admin can "Try as" another person: the app then treats the request as that person, and their name carries "[tried by ...]" so every
/// audit line and message shows who really did it (D-81).
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor http, Db db, TimeProvider clock, AppSettings settings)
{
    public const string TryAsCookie = "jbs-contracts-try-as";

    private Person? person;
    private bool loaded;

    /// <summary>The admin's own record while they try the app as someone else; otherwise the same as the person.</summary>
    public Person? Real { get; private set; }

    public string? Account => http.HttpContext?.User.Identity is { IsAuthenticated: true } id ? id.Name : null;

    /// <summary>The active person, or null when the account isn't signed in, hasn't been added, or has been deactivated.</summary>
    public async Task<Person?> GetAsync()
    {
        if (loaded) return person;
        loaded = true;
        if (Account is not { Length: > 0 } account) return null;
        await using var connection = await db.OpenAsync();
        person = await connection.QuerySingleOrDefaultAsync<Person>(
            "SELECT * FROM contracts.Person WHERE AccountKey = @key AND IsActive = 1", new { key = WindowsAccount.Key(account) });
        if (person is not null && (person.LastSignInUtc is null || clock.GetUtcNow().UtcDateTime - person.LastSignInUtc > TimeSpan.FromMinutes(15)))
        {
            // Last seen, at most every 15 minutes. Not audited: it is not a change anyone made.
            await connection.ExecuteAsync("UPDATE contracts.Person SET LastSignInUtc = SYSUTCDATETIME() WHERE Id = @id", new { id = person.Id });
        }
        Real = person;
        if (person is { IsAdmin: true } && settings.AllowDemoData
            && int.TryParse(http.HttpContext?.Request.Cookies[TryAsCookie], out var tryAs) && tryAs != person.Id)
        {
            var target = await connection.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE Id = @tryAs AND IsActive = 1", new { tryAs });
            if (target is not null)
            {
                target.TriedById = person.Id;
                target.TriedByName = person.Name;
                target.Name = $"{target.Name} [tried by {person.Name}]";
                person = target;
            }
        }
        return person;
    }
}
