using System.Text.Json;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Identity.Windows;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.Setup;
using Ecp.SharedKernel;

namespace Ecp.Host;

/// <summary>One failsafe administrator named in host configuration (Ecp:Security:FailsafeAdmins). Not a secret; never a role from Windows.</summary>
public sealed record FailsafeAdmin(string Account, string DisplayName, string Email);

/// <summary>
/// Unattended deployment commands run by the deploy pipeline on the web server (ADR-0045):
/// <c>deploy-migrate</c> creates the schema on a fresh database and is a no-op on a current one; <c>setup-apply</c> drives the same setup stages the
/// wizard does (same validation, same audit) from an answers file, and keeps the failsafe administrators in place on every deploy.
/// </summary>
public static class DeploymentCli
{
    public static readonly string[] Commands = ["deploy-migrate", "setup-apply"];
    public const string Usage = "deploy-migrate | setup-apply --answers <file.json>";

    /// <summary>Exit code meaning "nothing failed, but something still needs a person": the pipeline shows a warning, not a failure.</summary>
    public const int NeedsAttention = 3;

    public const string FailsafeSection = "Ecp:Security:FailsafeAdmins";

    public static IReadOnlyList<FailsafeAdmin> ReadFailsafeAdmins(IConfiguration configuration)
    {
        var list = new List<FailsafeAdmin>();
        foreach (var entry in configuration.GetSection(FailsafeSection).GetChildren().OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue))
        {
            var account = WindowsAccountName.Normalise(entry["Account"])
                ?? throw new DomainException("failsafe_account", $"{FailsafeSection}:{entry.Key}:Account must be a Windows account as DOMAIN\\name.");
            var email = entry["Email"]?.Trim();
            if (string.IsNullOrEmpty(email) || !System.Net.Mail.MailAddress.TryCreate(email, out _))
                throw new DomainException("failsafe_email", $"{FailsafeSection}:{entry.Key}:Email must be a valid email address.");
            var name = string.IsNullOrWhiteSpace(entry["DisplayName"]) ? account : entry["DisplayName"]!.Trim();
            list.Add(new FailsafeAdmin(account, name, email));
        }
        return list;
    }

    /// <summary>
    /// Builds the values for one stage: the stage's own defaults (what the wizard pre-fills), then the answers file, then, for the administrator step, the first failsafe
    /// administrator. Nothing here is a secret: secret settings are references (env:, file:), as in the wizard.
    /// </summary>
    public static Dictionary<string, string> ValuesFor(StageDescriptor stage, JsonElement? answers, FailsafeAdmin? firstAdmin)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in stage.Fields) if (!string.IsNullOrEmpty(f.Value)) values[f.Name] = f.Value!;
        if (answers is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(stage.Id, out var section) && section.ValueKind == JsonValueKind.Object)
            foreach (var p in section.EnumerateObject())
                values[p.Name] = p.Value.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.String => p.Value.GetString() ?? "", _ => p.Value.GetRawText() };
        if (stage.Id == "admin" && firstAdmin is not null)
        {
            values["displayName"] = firstAdmin.DisplayName; values["email"] = firstAdmin.Email; values["windowsAccount"] = firstAdmin.Account;
        }
        // The email test goes to the first failsafe administrator unless the answers name someone else.
        if (stage.Id == "notifications" && firstAdmin is not null && string.IsNullOrWhiteSpace(values.GetValueOrDefault("testRecipient")))
            values["testRecipient"] = firstAdmin.Email;
        return values;
    }

    public static async Task<int> RunAsync(string command, string[] args, IServiceProvider services, IConfiguration configuration, EcpMode mode, CancellationToken ct = default)
    {
        try
        {
            return command switch
            {
                "deploy-migrate" => await MigrateAsync(services, mode, ct),
                "setup-apply" => await SetupApplyAsync(args, services, configuration, ct),
                _ => 64,
            };
        }
        catch (DomainException ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static async Task<int> MigrateAsync(IServiceProvider services, EcpMode mode, CancellationToken ct)
    {
        var runner = services.GetRequiredService<MigrationRunner>();
        if (await UpgradeCli.GuardMigrateAsync(services, mode, ct) is null)
        {
            await runner.RunAsync(ct);
            Console.WriteLine("Migrations applied.");
            return 0;
        }
        // An installed Production database is upgraded only by the upgrade procedure (backup, maintenance mode). Nothing is applied here; the deploy stops before copying files.
        var status = await runner.GetStatusAsync(ct);
        if (status.IsCurrent) { Console.WriteLine("The database schema is current. Nothing to apply."); return 0; }
        Console.Error.WriteLine("This release changes the database schema of an existing installation (pending: " + string.Join(", ", status.Pending) + "). "
            + "Apply it with the upgrade procedure in docs/UPGRADE_RUNBOOK.md (recovery point, upgrade-preflight, upgrade-apply), then re-run the deployment. Nothing was changed.");
        return 2;
    }

    private static async Task<int> SetupApplyAsync(string[] args, IServiceProvider services, IConfiguration configuration, CancellationToken ct)
    {
        var answersPath = Option(args, "--answers") ?? throw new DomainException("answers", "Usage: setup-apply --answers <file.json>");
        using var answersDoc = JsonDocument.Parse(await File.ReadAllTextAsync(answersPath, ct));
        JsonElement? answers = answersDoc.RootElement.TryGetProperty("stages", out var s) ? s : null;
        var admins = ReadFailsafeAdmins(configuration);
        if (admins.Count == 0) throw new DomainException("failsafe_missing", $"No failsafe administrator is configured ({FailsafeSection}:0:Account, :DisplayName, :Email).");

        var setup = services.GetRequiredService<ISetupService>();
        await setup.EnsureInitialisedAsync(ct);
        var actor = new SetupActor("pipeline");
        if (!await setup.IsCompleteAsync(ct))
        {
            var snapshot = await setup.SnapshotAsync(ct: ct);
            foreach (var summary in snapshot.Stages)
            {
                var stage = await setup.DescribeAsync(summary.Id, ct: ct);
                if (stage is null) continue;
                if (stage.Status is StageStatus.Complete or StageStatus.Skipped && stage.Id is not ("readiness" or "complete")) { Console.WriteLine($"  [done]    {stage.Id}"); continue; }
                if (!stage.Available) { Console.WriteLine($"  [waiting] {stage.Id}: {stage.UnavailableReason}"); continue; }
                // An optional step (demonstration data outside Production) runs only if the answers ask for it; "skip" records the decision as the wizard's Skip does.
                var wantsSkip = answers is { } a && a.TryGetProperty(stage.Id, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == "skip";
                var result = wantsSkip ? await setup.SkipAsync(stage.Id, actor, ct) : await setup.ExecuteAsync(stage.Id, ValuesFor(stage, answers, admins[0]), actor, ct: ct);
                Console.WriteLine($"  [{(result.Succeeded ? "ok" : "FAILED")}]{(result.Succeeded ? "      " : "  ")}{stage.Id}: {result.Message}");
                foreach (var d in result.Details) Console.WriteLine("              " + d);
            }
        }

        var after = await setup.SnapshotAsync(ct: ct);
        if (after.TenantId is { } tenantId && after.Stages.Any(x => x.Id == "identity" && x.Status == StageStatus.Complete))
            await EnsureFailsafeAdminsAsync(services, tenantId, admins, ct);

        if (await setup.IsCompleteAsync(ct)) { Console.WriteLine("Setup is complete."); return 0; }
        Console.WriteLine("Setup is not complete yet. If the readiness step says the administrator has not signed in, each failsafe administrator opens "
            + "<site>/Account/WindowsSignIn?testProvider=windows once from their own PC, then the deployment is run again.");
        return NeedsAttention;
    }

    /// <summary>
    /// Every configured failsafe administrator is invited (by Windows account), holds an active Tenant Administrator role and an installation administrator account.
    /// Idempotent and audited by the services it calls. A disabled account is reported and left alone: disabling is a deliberate act this command does not undo.
    /// </summary>
    private static async Task EnsureFailsafeAdminsAsync(IServiceProvider services, Guid tenantId, IReadOnlyList<FailsafeAdmin> admins, CancellationToken ct)
    {
        foreach (var (scopeId, role) in new[] { (tenantId, RoleCodes.TenantAdministrator), (SystemTenant.Id, RoleCodes.SystemAdministrator) })
        {
            foreach (var admin in admins)
            {
                await using var scope = services.CreateAsyncScope();
                var p = scope.ServiceProvider;
                var actor = p.GetRequiredService<ActorContext>();
                actor.Type = ActorType.System; actor.ActorId = "cli:failsafe-admins"; actor.Source = "Cli";
                using var _ = p.GetRequiredService<TenantContext>().Use(scopeId);
                var users = p.GetRequiredService<IUserService>();
                var existing = await users.FindByEmailAsync(admin.Email, ct);
                if (existing?.Status == UserStatus.Disabled) { Console.WriteLine($"  [warning] {admin.Account} is disabled in {(scopeId == SystemTenant.Id ? "the installation" : "the tenant")}; left as it is."); continue; }
                var userId = existing?.Id ?? await users.InviteAsync(admin.DisplayName, admin.Email, ct);
                var windows = p.GetRequiredService<IWindowsAccounts>();
                if (await windows.GetAsync(userId, ct) is not { Linked: true }) await windows.SetInvitedAccountAsync(userId, admin.Account, "failsafe-admins", ct);
                var assignments = p.GetRequiredService<IRoleAssignmentService>();
                if ((await assignments.ListForUserAsync(userId, includeEnded: false, ct)).Any(a => a.RoleCode == role && a.IsActive)) continue;
                var request = scopeId == SystemTenant.Id
                    ? new GrantRequest(userId, role, null, ScopeSet.Installation("Public"), Reason: $"Failsafe administrator ({FailsafeSection})")
                    : new GrantRequest(userId, role, null, ScopeSet.Tenant((await p.GetRequiredService<IClassificationScaleProvider>().GetAsync(ct)).Levels[^1]),
                        Reason: $"Failsafe administrator ({FailsafeSection})", ExplicitHighCeiling: true);
                await assignments.GrantTrustedAsync(request, AssignmentSource.Setup, "failsafe-admins", null, ct);
                Console.WriteLine($"  [ok]      {admin.Account} holds {role}.");
            }
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
