using System.Text.Json;
using Ecp.Access.Persistence;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Access;

/// <summary>The demonstration identity provider's fixed identifiers. Synthetic test people sign in through it; it exists only in Demo and Test.</summary>
public static class TestIdentity
{
    public const string ProviderKey = "demo";
    public const string Issuer = "https://demo.ecp.invalid";
}

/// <summary>One row of the People list.</summary>
public sealed record PersonRow(Guid Id, string DisplayName, string Email, UserStatusView Status, bool IsTestPerson, IReadOnlyList<ProfileBadge> Profiles, string Roles, int OwnedCategories);

public sealed record NewPerson(string DisplayName, string Email, bool AllowDemonstrationSignIn);

public sealed record NewPersonResult(Guid UserId, bool IsTestPerson, AccessResult? Access);

/// <summary>The People & Access list and the Add person flow. Adding a person and giving them access are <b>one transaction</b>: if the access is refused, the person is not created either.</summary>
public interface IPersonAccessService
{
    /// <summary>Whether this installation offers demonstration sign-in for new people (Demo or Test only).</summary>
    bool DemonstrationSignInAllowed { get; }

    Task<IReadOnlyList<PersonRow>> ListAsync(CancellationToken ct = default);

    /// <summary>What adding this person with this access would do. Nothing is kept.</summary>
    Task<AccessPlan> PreviewNewAsync(NewPerson person, AccessChange access, CancellationToken ct = default);

    Task<NewPersonResult> AddAsync(NewPerson person, AccessChange access, CancellationToken ct = default);
}

internal sealed class PersonAccessService(
    IPeopleDirectory people, IUserAdministration admin, IUserService users, IAccessProfileService profiles, IEffectiveAccess effective, ICategoryAdministration categoryAdmin,
    IAccessService access, IUnitOfWork uow, IAuditWriter audit, IdentityOptions identity, AccessDbContext db) : IPersonAccessService
{
    public bool DemonstrationSignInAllowed => identity.Mode != EcpMode.Production;

    public async Task<IReadOnlyList<PersonRow>> ListAsync(CancellationToken ct = default)
    {
        var persons = await people.ManageableUsersAsync(ct);
        var test = DemonstrationSignInAllowed ? await people.LinkedToProviderAsync(TestIdentity.ProviderKey, ct) : new HashSet<Guid>();
        var ids = persons.Select(p => p.Id).ToList();
        var rows = await db.ProfileApplications.AsNoTracking().Where(a => ids.Contains(a.UserId) && a.EndedUtc == null).OrderBy(a => a.AppliedUtc).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var owned = (await categoryAdmin.ListAllAsync(ct)).SelectMany(c => c.Owners.Where(o => o.EffectiveToUtc is null || o.EffectiveToUtc > now).Select(o => o.UserId)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        return persons.Select(p => new PersonRow(p.Id, p.DisplayName, p.Email, p.Status switch { UserStatus.Active => UserStatusView.Active, UserStatus.Disabled => UserStatusView.Disabled, _ => UserStatusView.Invited },
            test.Contains(p.Id),
            rows.Where(r => r.UserId == p.Id).Select(r => new ProfileBadge(r.ProfileKey, AccessProfiles.Find(r.ProfileKey)?.Label ?? r.ProfileKey, r.Id, r.AppliedUtc, r.AppliedByLabel, r.OrganisationId)).ToList(),
            string.Join(", ", p.Roles.Select(RoleText)), owned.GetValueOrDefault(p.Id))).ToList();
    }

    private static string RoleText(string roleWithKind)
    {
        var code = roleWithKind.Contains(" (") ? roleWithKind[..roleWithKind.IndexOf(" (", StringComparison.Ordinal)] : roleWithKind;
        return EffectiveAccessService.RoleToken(code);
    }

    public async Task<AccessPlan> PreviewNewAsync(NewPerson person, AccessChange accessChange, CancellationToken ct = default)
    {
        var steps = new List<PlannedStep>(); var problems = new List<string>(); AccessPlan? inner = null;
        try
        {
            await uow.ExecuteAsync<bool>(async c =>
            {
                try
                {
                    var id = await CreatePersonAsync(person, c);
                    steps.Add(new PlannedStep("person", person.AllowDemonstrationSignIn ? $"Add {person.DisplayName} as a test person who can sign in through demonstration sign-in." : $"Invite {person.DisplayName}. They become active at their first sign-in."));
                    if (accessChange.ProfileKeys.Count > 0)
                    {
                        // The nested preview rolls back only its own work if it were a separate transaction; here it joins ours, which is rolled back at the end either way.
                        inner = await profiles.PreviewAsync(accessChange with { UserId = id }, c);
                    }
                    else inner = new AccessPlan(true, [], [], await effective.ForUserAsync(id, c), null);
                }
                catch (DomainException ex) when (ex.Code == "user_exists") { problems.Add("That email address already has an account. Change that person's access instead."); }
                catch (Exception ex) when (ex is DomainException or ForbiddenException) { problems.Add(ex is DomainException d ? d.RawMessage : "You are not allowed to add people here."); }
                throw new RolledBackForPreview();
            }, ct);
        }
        catch (RolledBackForPreview) { }
        if (inner is not null) { steps.AddRange(inner.Steps); problems.AddRange(inner.Problems); }
        return new AccessPlan(problems.Count == 0, steps, problems, problems.Count == 0 ? inner?.After : null, null);
    }

    public async Task<NewPersonResult> AddAsync(NewPerson person, AccessChange accessChange, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var id = await CreatePersonAsync(person, c);
            AccessResult? result = null;
            if (accessChange.ProfileKeys.Count > 0) result = await profiles.ApplyAsync(accessChange with { UserId = id }, c);
            return new NewPersonResult(id, person.AllowDemonstrationSignIn, result);
        }, ct);

    private async Task<Guid> CreatePersonAsync(NewPerson person, CancellationToken ct)
    {
        var name = Guard.NotBlank(person.DisplayName, "Name", 200);
        var email = Guard.NotBlank(person.Email, "Email", 254);
        if (!person.AllowDemonstrationSignIn) return await admin.InviteAsync(name, email, ct);   // Production path, unchanged: a real invitation, linked at the first verified sign-in

        // A synthetic test person. Refused in Production here, at the service, whatever the page showed.
        if (!DemonstrationSignInAllowed) throw new DomainException("test_only", "Demonstration sign-in is available only in Demo and Test. Invite the person instead.");
        if ((await access.GrantsAsync("users.manage", ct)).Count == 0) throw new ForbiddenException("You cannot manage users.");
        var id = await users.CreateActiveTrustedAsync(name, email, TestIdentity.ProviderKey, TestIdentity.Issuer, email, "test-person", ct);
        audit.Record(new AuditEntry("access.test_person.created", "User", id.ToString(), Reason: "Synthetic test person with demonstration sign-in (no password)",
            NewValuesJson: JsonSerializer.Serialize(new { provider = TestIdentity.ProviderKey, mode = identity.Mode.ToString() })));
        return id;
    }
}
