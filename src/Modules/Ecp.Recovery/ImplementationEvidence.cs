using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Identity;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Documents;
using Ecp.Formats;

namespace Ecp.Recovery;

/// <summary>One thing the pack states, in words a reviewer can read. Never a secret, never contract or document content.</summary>
public sealed record EvidenceFact(string Name, string Value);

/// <summary>
/// One section of the pack. <see cref="Code"/> is stable (<c>EVP-nn</c>). <see cref="Status"/> is always one of Verified, Warning, Failed or Not Verified; <see cref="CountsAs"/> is what that counts as under the platform's
/// evidence rules (D14.13, D14.14): only Verified is Passed, Warning is not a pass until accepted, Failed is Failed and Not Verified stays Not Verified.
/// </summary>
public sealed record EvidenceSection(string Code, string Title, OpsStatus Status, string CountsAs, string Summary, IReadOnlyList<EvidenceFact> Facts, string? Reference);

public sealed record EvidenceRisk(string Code, string Title, OpsStatus Status, string Why, string NextStep);

public sealed record SignOffLine(string Role, string Name, string Date, string Signature);

public sealed record ImplementationEvidencePack(int FormatVersion, DateTime GeneratedUtc, string CorrelationId, string PlatformVersion, string EnvironmentMode, string DeploymentModel, string Governance, OpsStatus Overall,
    IReadOnlyList<EvidenceSection> Sections, IReadOnlyList<AcceptedExceptionDto> AcceptedExceptions, IReadOnlyList<EvidenceRisk> OpenRisks, IReadOnlyList<SignOffLine> SignOff, IReadOnlyList<string> Statements);

public sealed record EvidenceFile(string Name, string Sha256, long Bytes);

public sealed record EvidencePackage(byte[] Zip, string ZipSha256, IReadOnlyList<EvidenceFile> Files, ImplementationEvidencePack Pack);

/// <summary>
/// The company implementation evidence pack (docs/IMPLEMENTATION_REHEARSAL.md, section on evidence). It collects what the platform can honestly state about an implementation: the environment, the governance in force, the pack
/// and organisations, identity, email, scanner and masked-data readiness, configuration publication, migration, backup, the upgrade pre-flight and operational readiness, with every accepted exception and every open risk. It states
/// facts and their evidence status; it never says an environment is ready for Production, and a person must sign it off outside the platform. It holds no secret, no contract and no document content.
/// </summary>
public interface IImplementationEvidence
{
    Task<ImplementationEvidencePack> BuildAsync(CancellationToken ct = default);
    /// <summary>The pack as a ZIP of JSON, CSV (formula-safe) and Markdown with a hash manifest. Needs the evidence-export permission; the export is audited with its hash.</summary>
    Task<EvidencePackage> ExportAsync(CancellationToken ct = default);
}

internal sealed class ImplementationEvidence(IOperationsDashboard dashboard, IEnablementAdministration enablement, IAccessService access, TenantContext tenant, IClock clock, IActorContext actor, StartupFacts startup,
    OperationalFactCollector collector, ReadinessService readiness, Ecp.Audit.IAuditWriter audit, IUnitOfWork uow, IScannerReadiness scanner, IMockScannerAcceptance mockAcceptance,
    IEmailConfigurationService email, GovernanceMode? governance = null) : IImplementationEvidence
{
    private const int Format = 1;

    private static OpsStatus Map(ReadinessStatus s) => s switch { ReadinessStatus.Healthy => OpsStatus.Verified, ReadinessStatus.Degraded => OpsStatus.Warning, _ => OpsStatus.Failed };
    private static string Counts(OpsStatus s) => EvidenceSemantics.Label(EvidenceSemantics.Judge(s).Verdict);

    private static EvidenceSection Section(string code, string title, OpsStatus status, string summary, string? reference = null, params (string, string)[] facts)
        => new(code, title, status, Counts(status), Redaction.Clean(summary, keepEmail: false), [.. facts.Select(f => new EvidenceFact(f.Item1, Redaction.Clean(f.Item2)))], reference);

    private static long Sum(IReadOnlyList<FactTotal>? facts, string area, string key) => facts?.FirstOrDefault(f => f.Area == area && f.Key == key)?.Value ?? 0;

    public async Task<ImplementationEvidencePack> BuildAsync(CancellationToken ct = default)
    {
        if (tenant.TenantId != SystemTenant.Id || !await access.HoldsAnywhereAsync(OperationsPermissions.EvidenceExport)) throw new ForbiddenException("You do not have permission to do this.");
        var now = clock.UtcNow;
        var d = await dashboard.BuildAsync(ct);
        var facts = await collector.CollectAsync(now, ct);
        var report = await readiness.RunAsync(ct);
        var sections = new List<EvidenceSection>();

        // EVP-01 Environment
        var mode = startup.Mode;
        sections.Add(Section("EVP-01", "Environment summary", mode == EcpMode.Production ? OpsStatus.Verified : OpsStatus.Warning,
            mode == EcpMode.Production ? "A Production-mode installation." : $"A {mode}-mode installation. {mode} is a supported mode for supervised rehearsal; it is not Production.", "/Admin/Operations",
            ("Mode", mode.ToString()), ("Platform version", d.PlatformVersion), ("Database engine", d.DatabaseEngine), ("Deployment model", DeploymentModelOf(d.DatabaseEngine)),
            ("Organisations", Sum(facts, "Tenancy", "Organisations.Active").ToString()), ("Generated (UTC)", now.ToString("yyyy-MM-dd HH:mm:ss"))));

        // EVP-02 Governance
        sections.Add(governance is null
            ? Section("EVP-02", "Governance mode", OpsStatus.NotVerified, "The governance mode was not available to the evidence pack.", "/Admin/Operations/Dashboard")
            : Section("EVP-02", "Governance mode", governance.IsProductionEquivalent ? OpsStatus.Verified : OpsStatus.Warning,
                governance.IsProductionEquivalent ? "A second person must publish configuration, as in Production." : "The simpler single-person rule applies. This is not the Production rule.", "/Admin/Operations/Dashboard",
                ("Mode", governance.Name), ("Second person required", governance.SecondPersonRequired ? "Yes" : "No"), ("Meaning", governance.Label),
                ("Releases published with a second person", Sum(facts, "Configuration", "Releases.SecondPerson").ToString()), ("Releases published under the single-person rule", Sum(facts, "Configuration", "Releases.SinglePerson").ToString())));

        // EVP-03 Pack and organisations
        var packs = facts?.Where(f => f.Area == "Packs" && f.Key.StartsWith("Assigned.", StringComparison.Ordinal)).Select(f => (f.Key["Assigned.".Length..], f.Value.ToString())).ToList() ?? [];
        sections.Add(Section("EVP-03", "Solution pack and organisation set-up", packs.Count == 0 ? OpsStatus.NotVerified : OpsStatus.Verified,
            packs.Count == 0 ? "No solution pack assignment was found." : $"{packs.Sum(p => int.Parse(p.Item2))} organisation(s) use {packs.Count} pack version(s).", "/Admin/Packs",
            [.. packs.Select(p => ($"Pack {p.Item1}", $"{p.Item2} organisation(s)")), ("Installed packs", Sum(facts, "Packs", "Installed").ToString()), ("Open configuration drafts", Sum(facts, "Configuration", "Drafts.Open").ToString())]));

        // EVP-04 Identity
        var idChecks = report.Results.Where(r => r.Id.StartsWith("R-ID", StringComparison.Ordinal)).ToList();
        sections.Add(idChecks.Count == 0
            ? Section("EVP-04", "Identity provider readiness", OpsStatus.NotVerified, "Identity-provider readiness is checked in the setup wizard with a test sign-in and by the start-up guard. The platform has not exercised a customer identity provider in this environment.", "/Admin/Setup")
            : Section("EVP-04", "Identity provider readiness", OpsStatuses.Worst(idChecks.Select(r => Map(r.Status))), string.Join(" ", idChecks.Select(r => $"{r.Id}: {r.Message}")), "/Admin/Health"));

        // EVP-05 Email
        var em = await email.ReadinessAsync(ct);
        sections.Add(Section("EVP-05", "Email readiness", em.State switch { EmailState.Healthy => OpsStatus.Verified, EmailState.Failed => OpsStatus.Failed, _ => OpsStatus.NotVerified },
            em.Summary, "/Admin/Operations/Email", ("Provider", em.ProviderKey), ("State", em.State.ToString()), ("Counts as", em.Verdict),
            ("Last success (UTC)", em.LastSuccessUtc?.ToString("yyyy-MM-dd HH:mm") ?? "never"), ("Last failure (UTC)", em.LastFailureUtc?.ToString("yyyy-MM-dd HH:mm") ?? "none"), ("A pass expires (UTC)", em.ExpiresUtc?.ToString("yyyy-MM-dd HH:mm") ?? "-"),
            ("Reference", em.LastTest?.CorrelationId ?? "-")));

        // EVP-06 Scanner
        var sc = await scanner.EvaluateAsync(ct);
        var accepted = await mockAcceptance.CurrentAsync(ct);
        sections.Add(Section("EVP-06", "Malware scanner readiness", sc.State switch { ScannerState.Healthy => OpsStatus.Verified, ScannerState.Failed or ScannerState.Refused => OpsStatus.Failed, _ => OpsStatus.NotVerified },
            sc.Summary, "/Admin/Operations/Scanner", ("Scanner", sc.ProviderName), ("State", sc.State.ToString()), ("Counts as", sc.Verdict),
            ("Demonstration scanner", sc.DemonstrationOnly ? "Yes (not protection)" : "No"),
            ("Mock scanner accepted for the rehearsal", accepted is null ? "No" : $"Yes, by {accepted.AcceptedBy} until {accepted.ExpiresUtc:yyyy-MM-dd}: {accepted.Reason} ({accepted.Scope})"),
            ("Last self-test", sc.LastTest is null ? "never (or since the last restart)" : $"{sc.LastTest.Outcome} on {sc.LastTest.TestedUtc:yyyy-MM-dd HH:mm} UTC, reference {sc.LastTest.CorrelationId}")));

        // EVP-07 Masked data
        var current = Sum(facts, "DataMigration", "Masked.Declarations.Current");
        var expired = Sum(facts, "DataMigration", "Masked.Declarations.Expired");
        var maskedBatches = Sum(facts, "DataMigration", "Batches.MaskedCompanyData");
        var unclassified = Sum(facts, "DataMigration", "Batches.Unclassified");
        sections.Add(Section("EVP-07", "Masked-data declarations", maskedBatches == 0 && current + expired == 0 ? OpsStatus.NotVerified : expired > 0 ? OpsStatus.Warning : OpsStatus.Verified,
            maskedBatches == 0 && current + expired == 0 ? "No masked company data was declared or loaded. The platform does not mask or inspect data and cannot say whether any data is safe: masking is the customer's responsibility."
            : "The declarations record what the customer said about its own masking. They are not a statement by the platform that the data is safe.", "/Migration/MaskedData",
            ("Current declarations", current.ToString()), ("Expired declarations", expired.ToString()), ("Revoked declarations", Sum(facts, "DataMigration", "Masked.Declarations.Revoked").ToString()),
            ("Declarations stating personal data remains", Sum(facts, "DataMigration", "Masked.Declarations.PersonalData").ToString()), ("Batches marked Masked Company Data", maskedBatches.ToString()),
            ("Batches marked Synthetic", Sum(facts, "DataMigration", "Batches.Synthetic").ToString()), ("Loaded batches with no classification", unclassified.ToString())));

        // EVP-08 Configuration publication
        var cfg = d.Panel("configuration");
        sections.Add(Section("EVP-08", "Configuration publication", cfg?.Status ?? OpsStatus.NotVerified, cfg?.Summary ?? "Configuration facts were not available.", "/Admin/Configuration",
            ("Releases in force", Sum(facts, "Configuration", "Releases.InForce").ToString()), ("Releases published, all time", Sum(facts, "Configuration", "Releases.Published").ToString()),
            ("Published with a second person", Sum(facts, "Configuration", "Releases.SecondPerson").ToString()), ("Published under the single-person rule", Sum(facts, "Configuration", "Releases.SinglePerson").ToString())));

        // EVP-09 Migration and reconciliation
        var committed = Sum(facts, "DataMigration", "Batches.Committed") + Sum(facts, "DataMigration", "Batches.Reconciled") + Sum(facts, "DataMigration", "Batches.Accepted");
        var failedBatches = Sum(facts, "DataMigration", "Batches.Failed");
        sections.Add(Section("EVP-09", "Migration and reconciliation", failedBatches > 0 ? OpsStatus.Failed : committed == 0 ? OpsStatus.NotVerified : Sum(facts, "DataMigration", "Batches.Accepted") > 0 ? OpsStatus.Verified : OpsStatus.Warning,
            committed == 0 ? "No migration batch has been committed." : Sum(facts, "DataMigration", "Batches.Accepted") > 0 ? "At least one batch was committed, reconciled and accepted by a named person." : "Batches were committed but none has been accepted yet.", "/Migration",
            ("Committed", Sum(facts, "DataMigration", "Batches.Committed").ToString()), ("Reconciled", Sum(facts, "DataMigration", "Batches.Reconciled").ToString()), ("Accepted", Sum(facts, "DataMigration", "Batches.Accepted").ToString()),
            ("Rolled back", Sum(facts, "DataMigration", "Batches.RolledBack").ToString()), ("Failed", failedBatches.ToString())));

        // EVP-10 UAT
        sections.Add(Section("EVP-10", "User acceptance testing", Sum(facts, "DataMigration", "Batches.Accepted") > 0 ? OpsStatus.Warning : OpsStatus.NotVerified,
            "The platform records the business acceptance of a migration batch (who, when, a note). It does not record the customer's wider UAT scripts or results; those are kept by the customer's own UAT process and attached to the sign-off.", "/Migration",
            ("Batches with a recorded business acceptance", Sum(facts, "DataMigration", "Batches.Accepted").ToString())));

        // EVP-11 Backup, EVP-12 Upgrade pre-flight, EVP-13 Operational readiness: from the dashboard's own judgements
        var rec = d.Panel("recovery"); var upg = d.Panel("upgrade");
        sections.Add(Section("EVP-11", "Backup evidence", rec?.Status ?? OpsStatus.NotVerified, rec?.Summary ?? "Backup facts were not available.", "/Admin/Operations/Backups",
            [.. (rec?.Items ?? []).Select(i => ($"{i.Code} {i.Title}", $"{OpsStatuses.Label(i.Status)}: {i.Explanation}"))]));
        sections.Add(Section("EVP-12", "Upgrade pre-flight outcome", upg?.Status ?? OpsStatus.NotVerified, upg?.Summary ?? "Upgrade facts were not available.", "/Admin/Operations/UpgradeReadiness",
            [.. (upg?.Items ?? []).Select(i => ($"{i.Code} {i.Title}", $"{OpsStatuses.Label(i.Status)}: {i.Explanation}"))]));
        sections.Add(Section("EVP-13", "Operational readiness", d.Overall, $"The operations dashboard overall is {OpsStatuses.Label(d.Overall)}. A Blocked or Not Verified item is never counted as a pass.", "/Admin/Operations/Dashboard",
            [.. report.Results.Select(r => ($"{r.Id} {r.Description}", $"{OpsStatuses.Label(Map(r.Status))}: {r.Message}"))]));

        var exceptions = await enablement.AcceptedExceptionsAsync(false, ct);
        var risks = new List<EvidenceRisk>();
        foreach (var s in sections.Where(s => s.Status != OpsStatus.Verified))
            risks.Add(new(s.Code, s.Title, s.Status, s.Summary, s.Reference is null ? "Resolve or accept this on the record." : $"Open {s.Reference}."));
        foreach (var r in report.Results.Where(r => r.Status != ReadinessStatus.Healthy))
            risks.Add(new(r.Id, r.Description, Map(r.Status), r.Message, "See Administration, Health."));

        var overall = OpsStatuses.Worst(sections.Select(s => s.Status));
        var signOff = new[] { "Implementation lead", "Business owner", "Security reviewer", "System Administrator", "Data owner" }.Select(r => new SignOffLine(r, "", "", "")).ToList();
        var statements = new[]
        {
            "This pack records what the platform could observe on the date shown. It is not an approval, a certification or a declaration that the environment is ready for Production. Production approval is external governance.",
            "Verified means the platform exercised and confirmed the item. Warning is not a pass until a named person has accepted it on the record. Failed (including Blocked) is a failure. Not Verified means nobody could check it here; it is never a pass.",
            "The pack contains no secret, no contract and no document content, and no personal data beyond the names of the people who performed or accepted an action.",
            "Sign-off lines are placeholders. They are completed by people outside the platform.",
        };
        return new ImplementationEvidencePack(Format, now, actor.CorrelationId, d.PlatformVersion, mode.ToString(), DeploymentModelOf(d.DatabaseEngine), governance is null ? "" : $"{governance.Name}: {governance.Label}", overall, sections, exceptions, risks, signOff, statements);
    }

    private static string DeploymentModelOf(string engine) => engine.Contains("SQL", StringComparison.OrdinalIgnoreCase) ? "Application host with SQL Server (container, IIS or Azure; see the evidence report for which models were exercised)" : $"Application host with {engine} (not a Production database)";

    // ---- formats -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static string Label(OpsStatus s) => OpsStatuses.Label(s);

    private static byte[] Json(ImplementationEvidencePack pack)
    {
        var text = JsonSerializer.Serialize(new
        {
            evidencePackFormat = pack.FormatVersion, generatedUtc = pack.GeneratedUtc, correlationId = pack.CorrelationId, platformVersion = pack.PlatformVersion, environmentMode = pack.EnvironmentMode, deploymentModel = pack.DeploymentModel,
            governance = pack.Governance, overall = Label(pack.Overall),
            sections = pack.Sections.Select(s => new { s.Code, s.Title, status = Label(s.Status), countsAs = s.CountsAs, s.Summary, facts = s.Facts.Select(f => new { f.Name, f.Value }), reference = s.Reference }),
            acceptedExceptions = pack.AcceptedExceptions.Select(e => new { e.Kind, e.Code, e.Scope, e.Reason, acceptedBy = e.AcceptedByName, acceptedUtc = e.AcceptedUtc, expiresUtc = e.ExpiresUtc, state = e.RevokedUtc is not null ? "Revoked" : e.Active ? "Active" : "Expired", correlationId = e.CorrelationId }),
            openRisks = pack.OpenRisks.Select(r => new { r.Code, r.Title, status = Label(r.Status), r.Why, r.NextStep }),
            signOff = pack.SignOff, statements = pack.Statements,
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n";
        if (Redaction.ContainsSecret(text)) throw new DomainException("secret_in_report", "The evidence pack was not produced because it appears to contain a secret.");
        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] Csv(ImplementationEvidencePack pack)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "Code", "Section", "Item", "Status", "Counts as", "Detail" } };
        foreach (var s in pack.Sections)
        {
            rows.Add([s.Code, s.Title, "Summary", Label(s.Status), s.CountsAs, s.Summary]);
            foreach (var f in s.Facts) rows.Add([s.Code, s.Title, f.Name, "", "", f.Value]);
        }
        foreach (var e in pack.AcceptedExceptions) rows.Add(["EXCEPTION", e.Kind, e.Code, e.RevokedUtc is not null ? "Revoked" : e.Active ? "Active" : "Expired", "", $"{e.Scope}; accepted by {e.AcceptedByName} on {e.AcceptedUtc:yyyy-MM-dd} until {e.ExpiresUtc:yyyy-MM-dd}: {e.Reason}"]);
        foreach (var r in pack.OpenRisks) rows.Add(["RISK", r.Code, r.Title, Label(r.Status), "", $"{r.Why} Next: {r.NextStep}"]);
        foreach (var l in pack.SignOff) rows.Add(["SIGN-OFF", l.Role, "(name)", "", "", "(to be completed outside the platform)"]);
        return CsvWriter.Write(rows);      // every cell a spreadsheet could read as a formula is neutralised
    }

    private static byte[] Markdown(ImplementationEvidencePack pack)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Company implementation evidence pack").AppendLine();
        sb.AppendLine($"- Generated (UTC): {pack.GeneratedUtc:yyyy-MM-dd HH:mm:ss}  ·  correlation id `{pack.CorrelationId}`");
        sb.AppendLine($"- Platform version: {pack.PlatformVersion}  ·  mode: {pack.EnvironmentMode}  ·  deployment: {pack.DeploymentModel}");
        sb.AppendLine($"- Governance: {pack.Governance}");
        sb.AppendLine($"- Overall (worst section): **{Label(pack.Overall)}**").AppendLine();
        foreach (var st in pack.Statements) sb.AppendLine("> " + st);
        sb.AppendLine().AppendLine("| Code | Section | Status | Counts as | Summary |").AppendLine("|---|---|---|---|---|");
        foreach (var s in pack.Sections) sb.AppendLine($"| {s.Code} | {Md(s.Title)} | {Label(s.Status)} | {s.CountsAs} | {Md(s.Summary)} |");
        foreach (var s in pack.Sections)
        {
            if (s.Facts.Count == 0) continue;
            sb.AppendLine().AppendLine($"## {s.Code} {Md(s.Title)}").AppendLine();
            foreach (var f in s.Facts) sb.AppendLine($"- **{Md(f.Name)}**: {Md(f.Value)}");
        }
        sb.AppendLine().AppendLine("## Accepted exceptions").AppendLine();
        if (pack.AcceptedExceptions.Count == 0) sb.AppendLine("None."); 
        foreach (var e in pack.AcceptedExceptions) sb.AppendLine($"- `{e.Code}` ({e.Kind}) {Md(e.Scope)}: accepted by {Md(e.AcceptedByName)} until {e.ExpiresUtc:yyyy-MM-dd}, {(e.RevokedUtc is not null ? "revoked" : e.Active ? "active" : "expired")}. Reason: {Md(e.Reason)}");
        sb.AppendLine().AppendLine("## Open risks").AppendLine();
        if (pack.OpenRisks.Count == 0) sb.AppendLine("None.");
        foreach (var r in pack.OpenRisks) sb.AppendLine($"- `{r.Code}` {Md(r.Title)} ({Label(r.Status)}): {Md(r.Why)} {Md(r.NextStep)}");
        sb.AppendLine().AppendLine("## Sign-off (placeholders, completed outside the platform)").AppendLine().AppendLine("| Role | Name | Date | Signature |").AppendLine("|---|---|---|---|");
        foreach (var l in pack.SignOff) sb.AppendLine($"| {l.Role} |  |  |  |");
        var text = sb.ToString();
        if (Redaction.ContainsSecret(text)) throw new DomainException("secret_in_report", "The evidence pack was not produced because it appears to contain a secret.");
        return Encoding.UTF8.GetBytes(text);
    }

    private static string Md(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    public async Task<EvidencePackage> ExportAsync(CancellationToken ct = default)
    {
        var pack = await BuildAsync(ct);
        var files = new List<(string Name, byte[] Bytes)> { ("evidence-pack.json", Json(pack)), ("evidence-pack.csv", Csv(pack)), ("evidence-pack.md", Markdown(pack)) };
        var manifest = files.Select(f => new EvidenceFile(f.Name, Convert.ToHexStringLower(SHA256.HashData(f.Bytes)), f.Bytes.Length)).ToList();
        var hashes = Encoding.UTF8.GetBytes(string.Join('\n', manifest.Select(m => $"{m.Sha256}  {m.Name}")) + "\n");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files.Append(("SHA256SUMS", hashes)))
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(pack.GeneratedUtc, TimeSpan.Zero);
                await using var s = entry.Open();
                await s.WriteAsync(bytes, ct);
            }
        }
        var zipBytes = ms.ToArray();
        var zipHash = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
        using var _ = tenant.UseInstallationScope();
        await uow.ExecuteAsync(c =>
        {
            audit.Record(new Ecp.Audit.AuditEntry("operations.evidence_pack.exported", "EvidencePack", pack.CorrelationId, NewValuesJson: JsonSerializer.Serialize(new { sha256 = zipHash, files = manifest.Select(m => m.Name), overall = Label(pack.Overall) })));
            return Task.CompletedTask;
        }, ct);
        return new EvidencePackage(zipBytes, zipHash, manifest, pack);
    }
}
