using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecp.Documents.Storage;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>Building blocks of backup, restore, validation and evidence (docs/BACKUP_AND_RESTORE.md). The end-to-end proof against a real SQL Server is in the integration suite.</summary>
[Trait("Category", "Unit")]
public sealed class RecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ecp-recovery-unit-" + Guid.NewGuid().ToString("N"));
    public RecoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // ---- paths, names and links ----

    [Theory]
    [InlineData("database/x.bak", true)]
    [InlineData("documents/t/0123456789abcdef0123456789abcdef/ab/abcdef", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("a/../b", false)]
    [InlineData("/absolute", false)]
    [InlineData("C:/windows", false)]
    [InlineData("a\\b", false)]
    [InlineData("a//b", false)]
    [InlineData("", false)]
    [InlineData("a/./b", false)]
    [InlineData("name with space", false)]
    [InlineData(".hidden", false)]
    public void A_backup_file_name_must_be_a_plain_relative_name(string name, bool expected) => Assert.Equal(expected, SafeFiles.IsRelativeName(name));

    [Fact]
    public void Resolve_refuses_a_name_that_leaves_the_backup_and_accepts_one_inside()
    {
        Assert.StartsWith(Path.GetFullPath(_root), SafeFiles.Resolve(_root, "database/x.bak"));
        Assert.Throws<DomainException>(() => SafeFiles.Resolve(_root, "../outside"));
        Assert.Throws<DomainException>(() => SafeFiles.Resolve(_root, "/etc/passwd"));
    }

    [Fact]
    public void A_link_inside_a_backup_is_never_followed()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var backup = Path.Combine(_root, "backup"); Directory.CreateDirectory(backup);
        Directory.CreateSymbolicLink(Path.Combine(backup, "documents"), outside);
        File.CreateSymbolicLink(Path.Combine(backup, "linked.bak"), Path.Combine(outside, "secret.txt"));
        Assert.Throws<DomainException>(() => SafeFiles.Resolve(backup, "documents/secret.txt"));
        Assert.Throws<DomainException>(() => SafeFiles.Resolve(backup, "linked.bak"));
    }

    [Fact]
    public void Backup_ids_database_names_and_hashes_are_validated()
    {
        var id = SafeFiles.NewBackupId(new DateTime(2026, 10, 17, 3, 15, 0, DateTimeKind.Utc));
        Assert.True(SafeFiles.IsBackupId(id)); Assert.StartsWith("bk-20261017T031500Z-", id);
        Assert.NotEqual(id, SafeFiles.NewBackupId(new DateTime(2026, 10, 17, 3, 15, 0, DateTimeKind.Utc)));      // immutable ids never repeat
        foreach (var bad in new[] { "", "bk-1", "../bk-20261017T031500Z-abcdef12", "bk-20261017T031500Z-ABCDEF12", id + "/x" }) Assert.False(SafeFiles.IsBackupId(bad), bad);
        Assert.True(SafeFiles.IsTargetDatabaseName("ecp_restore_01"));
        foreach (var bad in new[] { "master", "MASTER", "tempdb", "msdb", "model", "1abc", "a b", "a;DROP", "a]b", "", new string('a', 65) }) Assert.False(SafeFiles.IsTargetDatabaseName(bad), bad);
        Assert.True(SafeFiles.IsSha256(new string('a', 64))); Assert.False(SafeFiles.IsSha256(new string('A', 64))); Assert.False(SafeFiles.IsSha256("abc"));
    }

    [Fact]
    public void A_backup_directory_inside_a_served_path_is_refused()
    {
        Assert.False(SafeFiles.IsOutsideWebRoot("/app/wwwroot/backups", ["/app/wwwroot"]));
        Assert.False(SafeFiles.IsOutsideWebRoot("/app", ["/app/wwwroot"]));       // a parent of a served path would expose it
        Assert.True(SafeFiles.IsOutsideWebRoot("/var/backups/ecp", ["/app/wwwroot", "/app"]));
    }

    [Fact]
    public void The_database_servers_view_of_a_backup_path_uses_its_own_separators()
    {
        var linux = new RecoveryOptions { BackupDirectory = "/mnt/b", SqlServerBackupDirectory = "/ecp-backups/" };
        Assert.Equal("/ecp-backups/bk-1/database/x.bak", RecoveryPaths.ToSqlPath(linux, "bk-1", "database/x.bak"));
        var windows = new RecoveryOptions { BackupDirectory = "/mnt/b", SqlServerBackupDirectory = "D:\\Backups" };
        Assert.Equal("D:\\Backups\\bk-1\\database\\x.bak", RecoveryPaths.ToSqlPath(windows, "bk-1", "database/x.bak"));
        Assert.Equal("/mnt/b/bk-1/x", RecoveryPaths.ToSqlPath(new RecoveryOptions { BackupDirectory = "/mnt/b" }, "bk-1", "x"));
    }

    // ---- the manifest ----

    private static BackupManifest Sample(string id = "bk-20261017T031500Z-0a1b2c3d", Func<List<BackupFile>, List<BackupFile>>? files = null)
    {
        var db = new BackupFile("database/ecp.bak", "database", 10, new string('a', 64));
        var list = files?.Invoke([db, new BackupFile("inventory.json", "inventory", 5, new string('b', 64))]) ?? [db, new BackupFile("inventory.json", "inventory", 5, new string('b', 64))];
        var empty = new InventorySnapshot(new DateTime(2026, 10, 17, 3, 15, 0, DateTimeKind.Utc), [], []);
        return new BackupManifest(1, id, "installation", empty.TakenUtc, empty.TakenUtc, "1.0.0", "Test", new OperatorEntry("op", "Operator"), "reason", "corr",
            new DatabaseSection("SQL Server", "16.0", "ecp", "Full", true, "database/ecp.bak", 10, new string('a', 64), true, true, false, null), new SchemaSection([], 0, []),
            new DocumentSection("local-fs", true, "documents", null, null, 0, 0, 0, 0, 0, 0, 0, ""), new PackSection(null, 0, 0, [], [], [], ""), empty, empty, true,
            new ProtectionSection(false, null, "stated", ""), list, "Completed", []);
    }

    [Fact]
    public void A_manifest_serialises_to_the_same_bytes_every_time_and_reads_back_equal()
    {
        var a = ManifestJson.Serialise(Sample()); var b = ManifestJson.Serialise(Sample());
        Assert.Equal(a, b);
        Assert.Equal(ManifestJson.Sha256(a), ManifestJson.Sha256(b));
        var read = ManifestJson.Parse(a, "bk-20261017T031500Z-0a1b2c3d", 1 << 20);
        Assert.Equal("SQL Server", read.Database.Engine);
        Assert.Equal(2, read.Files.Count);
    }

    [Fact]
    public void A_manifest_that_is_not_valid_is_refused_with_a_plain_reason()
    {
        const string id = "bk-20261017T031500Z-0a1b2c3d";
        string Text(BackupManifest m) => Encoding.UTF8.GetString(ManifestJson.Serialise(m));
        Assert.Throws<DomainException>(() => ManifestJson.Parse([], id, 1 << 20));                                                     // empty
        Assert.Throws<DomainException>(() => ManifestJson.Parse(Encoding.UTF8.GetBytes("{not json"), id, 1 << 20));                   // garbage
        Assert.Throws<DomainException>(() => ManifestJson.Parse(ManifestJson.Serialise(Sample()), id, 10));                            // larger than the limit
        Assert.Throws<DomainException>(() => ManifestJson.Parse(ManifestJson.Serialise(Sample()), "bk-20261017T031500Z-ffffffff", 1 << 20));   // belongs to a different folder
        Assert.Throws<DomainException>(() => ManifestJson.Parse(Encoding.UTF8.GetBytes(Text(Sample()).Replace("\"formatVersion\": 1", "\"formatVersion\": 99")), id, 1 << 20));
        Assert.Throws<DomainException>(() => ManifestJson.Parse(Encoding.UTF8.GetBytes(Text(Sample()).Replace("\"scope\": \"installation\"", "\"scope\": \"tenant\"")), id, 1 << 20));
        Assert.Throws<DomainException>(() => ManifestJson.Parse(Encoding.UTF8.GetBytes(Text(Sample()).Replace("\"outcome\": \"Completed\"", "\"outcome\": \"Failed\"")), id, 1 << 20));
        Assert.Throws<DomainException>(() => ManifestJson.Parse(Encoding.UTF8.GetBytes(Text(Sample()).Replace("\"formatVersion\": 1,", "\"formatVersion\": 1, \"surprise\": true,")), id, 1 << 20));   // unknown members are refused
        // paths: traversal, duplicates, absolute, a missing database file, bad hashes
        Assert.Throws<DomainException>(() => ManifestJson.Validate(Sample(files: f => [.. f, new BackupFile("../escape", "document", 1, new string('c', 64))]), id));
        Assert.Throws<DomainException>(() => ManifestJson.Validate(Sample(files: f => [.. f, new BackupFile("/etc/passwd", "document", 1, new string('c', 64))]), id));
        Assert.Throws<DomainException>(() => ManifestJson.Validate(Sample(files: f => [.. f, f[0]]), id));
        Assert.Throws<DomainException>(() => ManifestJson.Validate(Sample(files: f => [f[1]]), id));
        Assert.Throws<DomainException>(() => ManifestJson.Validate(Sample(files: f => [.. f, new BackupFile("x", "document", 1, "not-a-hash")]), id));
        Assert.Throws<DomainException>(() => ManifestJson.Validate(Sample(files: f => [.. f, new BackupFile("y", "mystery", 1, new string('c', 64))]), id));
    }

    // ---- secrets and log injection ----

    [Fact]
    public void Secrets_are_masked_before_text_leaves_the_platform_and_a_finished_document_is_scanned_again()
    {
        var text = "Server=db.internal,1433;Database=ecp;User Id=sa;Password=Sup3rS3cret!;TrustServerCertificate=True and Bearer abcdefghijklmnop1234 and client_secret=abc123 and https://user:pw@host/path and jane.doe@example.test";
        var clean = Redaction.Clean(text);
        foreach (var leaked in new[] { "Sup3rS3cret", "abcdefghijklmnop1234", "abc123", "user:pw", "jane.doe", "db.internal" }) Assert.DoesNotContain(leaked, clean);
        Assert.False(Redaction.ContainsSecret(clean));
        Assert.True(Redaction.ContainsSecret(text));
        Assert.True(Redaction.ContainsSecret("-----BEGIN PRIVATE KEY-----\nMIIE\n-----END PRIVATE KEY-----"));
        Assert.DoesNotContain("PRIVATE KEY", Redaction.Clean("a -----BEGIN RSA PRIVATE KEY-----\nMIIE\n-----END RSA PRIVATE KEY----- b"));
        Assert.False(Redaction.ContainsSecret("{\"secretReference\": \"secret:env/EXAMPLE\", \"passwordPolicy\": \"strict\"}"));      // names of fields are not values
    }

    [Fact]
    public void Line_breaks_are_flattened_so_a_value_cannot_forge_a_log_or_report_line()
        => Assert.DoesNotContain('\n', Redaction.Clean("first\r\nFAKE 2026-10-17 INFO operator approved\nline\t3"));

    // ---- the document store as a backup sees it ----

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private async Task<(LocalFileSystemStorageProvider Store, Guid Tenant, string Key, byte[] Bytes)> StoreWithOneObjectAsync()
    {
        var store = new LocalFileSystemStorageProvider(new LocalFileSystemOptions { RootPath = Path.Combine(_root, "store") });
        var tenant = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("synthetic document " + Guid.NewGuid());
        var put = await store.PutAsync(tenant, new MemoryStream(bytes), 1 << 20);
        return (store, tenant, put.StorageKey, bytes);
    }

    [Fact]
    public async Task The_document_store_is_listed_through_its_abstraction_and_every_object_is_copied_and_hashed()
    {
        var (store, tenant, key, bytes) = await StoreWithOneObjectAsync();
        var other = Guid.NewGuid(); var otherBytes = Encoding.UTF8.GetBytes("another tenant's document");
        var otherPut = await store.PutAsync(other, new MemoryStream(otherBytes), 1 << 20);
        var backup = Path.Combine(_root, "backup");
        var copy = await DocumentStoreCopier.CopyAsync(store, store, Path.Combine(backup, "documents"), CancellationToken.None);
        Assert.Equal(2, copy.Lines.Count);
        Assert.Equal(0, copy.Skipped);
        Assert.All(copy.Lines, l => Assert.True(l.HashMatchesKey));
        var line = copy.Lines.Single(l => l.Key == key);
        Assert.Equal(Hash(bytes), line.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(backup, "documents", key.Replace('/', Path.DirectorySeparatorChar))));    // the content address is preserved
        Assert.StartsWith($"t/{tenant:N}/", key); Assert.StartsWith($"t/{other:N}/", otherPut.StorageKey);                                            // tenants stay apart

        var index = DocumentStoreCopier.Serialise(copy.Lines);
        var path = Path.Combine(backup, "index.ndjson"); await File.WriteAllBytesAsync(path, index);
        Assert.Equal(copy.Lines, DocumentStoreCopier.ReadIndex(path).ToList());
        Assert.Equal(tenant, DocumentStoreCopier.TenantOf(key));
    }

    [Fact]
    public async Task A_stored_object_that_no_longer_matches_its_address_is_copied_as_found_and_flagged()
    {
        var (store, _, key, _) = await StoreWithOneObjectAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "store", key.Replace('/', Path.DirectorySeparatorChar)), "damaged");
        var copy = await DocumentStoreCopier.CopyAsync(store, store, Path.Combine(_root, "backup", "documents"), CancellationToken.None);
        Assert.False(copy.Lines.Single().HashMatchesKey);
    }

    [Fact]
    public async Task A_link_or_an_unexpected_entry_in_the_document_store_is_skipped_and_never_read()
    {
        var (store, tenant, key, _) = await StoreWithOneObjectAsync();
        var folder = Path.Combine(_root, "store", "t", tenant.ToString("N"), key.Split('/')[2]);
        File.WriteAllText(Path.Combine(folder, "not-a-content-address.txt"), "stray");
        File.WriteAllText(Path.Combine(_root, "store", "t", "stray-file"), "stray");
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(_root, "outside.txt"), "outside the store");
            File.CreateSymbolicLink(Path.Combine(folder, new string('f', 64)), Path.Combine(_root, "outside.txt"));
        }
        var skipped = new List<string>();
        var found = new List<StoredObjectRef>();
        await foreach (var o in store.EnumerateAsync(skipped.Add)) found.Add(o);
        Assert.Single(found);
        Assert.True(skipped.Count >= 2);
    }

    [Fact]
    public void A_document_index_with_a_bad_line_is_refused()
    {
        var path = Path.Combine(_root, "index.ndjson");
        File.WriteAllText(path, "{\"key\":\"../../etc/passwd\",\"bytes\":1,\"sha256\":\"" + new string('a', 64) + "\",\"hashMatchesKey\":true}\n");
        Assert.Throws<DomainException>(() => DocumentStoreCopier.ReadIndex(path).ToList());
        File.WriteAllText(path, "not json\n");
        Assert.Throws<DomainException>(() => DocumentStoreCopier.ReadIndex(path).ToList());
        File.WriteAllText(path, "{\"key\":\"t/" + new string('a', 32) + "/aa/" + new string('a', 64) + "\",\"bytes\":-1,\"sha256\":\"" + new string('a', 64) + "\",\"hashMatchesKey\":true}\n");
        Assert.Throws<DomainException>(() => DocumentStoreCopier.ReadIndex(path).ToList());
    }

    [Fact]
    public async Task Package_files_are_copied_by_hash_name_only_and_quarantined_uploads_are_left_behind()
    {
        var tenant = Guid.NewGuid(); var bytes = Encoding.UTF8.GetBytes("package bytes"); var sha = Hash(bytes);
        var packages = Path.Combine(_root, "packs", "tenants", tenant.ToString("N"), "packages"); Directory.CreateDirectory(packages);
        await File.WriteAllBytesAsync(Path.Combine(packages, sha + ".ecpkg"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(packages, "notahash.ecpkg"), bytes);
        var quarantine = Path.Combine(_root, "packs", "tenants", tenant.ToString("N"), "quarantine"); Directory.CreateDirectory(quarantine);
        await File.WriteAllBytesAsync(Path.Combine(quarantine, "upload.ecpkg"), bytes);
        var copied = await PackStoreCopier.CopyAsync(Path.Combine(_root, "packs"), Path.Combine(_root, "backup"), CancellationToken.None);
        var one = Assert.Single(copied);
        Assert.Equal(sha, one.Sha256); Assert.True(one.HashMatchesName); Assert.Equal(tenant, one.TenantId);
        Assert.False(Directory.Exists(Path.Combine(_root, "backup", "packs", "tenants", tenant.ToString("N"), "quarantine")));
    }

    // ---- evidence ----

    [Fact]
    public void An_evidence_download_is_safe_to_open_in_a_spreadsheet_and_holds_no_secret()
    {
        var hostile = "=cmd|' /C calc'!A0";
        var report = new EvidenceReport(DateTime.UtcNow, "1.0.0", "Test", true, false, true, "Off", [new EvidenceBackup("bk-1", "Verified", null, "Completed", 1, 0, "abc", null)],
            [new EvidenceRun("Backup", "Completed", hostile, "+SUM(1+1)", DateTime.UtcNow, null, "@operator"), new EvidenceRun("Restore", "Failed", "-2+3", "ok", DateTime.UtcNow, null, "\tx")],
            [], DeploymentModels.All, ["note"]);
        var csv = Encoding.UTF8.GetString(EvidenceFormats.ToCsv(report));
        foreach (var line in csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            foreach (var cell in line.Split(',')) Assert.False(cell.Length > 0 && cell.TrimStart('"')[0] is '=' or '+' or '-' or '@' or '\t', $"formula-looking cell: {cell}");
        Assert.Contains("'=cmd", csv);
        var json = Encoding.UTF8.GetString(EvidenceFormats.ToJson(report));
        Assert.False(Redaction.ContainsSecret(json));
        Assert.Throws<DomainException>(() => EvidenceFormats.ToJson(report with { Notices = ["password=Sup3rS3cretValue"] }));
    }

    [Fact]
    public void The_deployment_models_name_what_is_not_verified()
    {
        Assert.Contains(DeploymentModels.All, d => d.Model.Contains("IIS") && d.Status.Contains("Not Verified"));
        Assert.Contains(DeploymentModels.All, d => d.Model.Contains("Azure") && d.Status.Contains("Not Verified"));
        Assert.Contains(DeploymentModels.All, d => d.Model.Contains("Container + external SQL Server") && d.Status == "Verified");
        Assert.Contains(DeploymentModels.All, d => d.Model.Contains("SharePoint") && d.Status == "Not implemented");
    }

    // ---- validation outcomes and inventory comparison ----

    private static ValidationCheck Check(CheckOutcome o) => new("VAL-000", "t", o, CheckSeverity.Info, "d", "", "", "c");

    [Fact]
    public void Validation_outcomes_are_Passed_Passed_with_warnings_Failed_or_Not_Verified_and_only_the_first_two_allow_a_release()
    {
        Assert.Equal(ValidationOutcome.Passed, ValidationReport.Summarise([Check(CheckOutcome.Passed)]));
        Assert.Equal(ValidationOutcome.PassedWithWarnings, ValidationReport.Summarise([Check(CheckOutcome.Passed), Check(CheckOutcome.Warning)]));
        Assert.Equal(ValidationOutcome.Failed, ValidationReport.Summarise([Check(CheckOutcome.Warning), Check(CheckOutcome.Failed), Check(CheckOutcome.NotVerified)]));
        Assert.Equal(ValidationOutcome.NotVerified, ValidationReport.Summarise([Check(CheckOutcome.Passed), Check(CheckOutcome.NotVerified)]));
        var report = (ValidationOutcome o) => new ValidationReport("s", DateTime.UtcNow, DateTime.UtcNow, o, [], "c");
        Assert.True(report(ValidationOutcome.Passed).AllowsRelease); Assert.True(report(ValidationOutcome.PassedWithWarnings).AllowsRelease);
        Assert.False(report(ValidationOutcome.Failed).AllowsRelease); Assert.False(report(ValidationOutcome.NotVerified).AllowsRelease);
    }

    [Fact]
    public void Inventories_are_compared_ignoring_operational_tables_and_the_restores_own_events()
    {
        var tenant = Guid.NewGuid();
        InventorySnapshot Snapshot(long contracts, long checksum, long leaseRows, long systemEvents) => new(DateTime.UtcNow, [],
            [new("contracts.Contract", tenant, contracts, checksum), new("ops.OperationLease", SystemTenant.Id, leaseRows, 7), new("audit.AuditEvent", SystemTenant.Id, systemEvents, 1)]);
        var baseline = Snapshot(3, 100, 1, 4);
        Assert.True(InventoryComparer.Equivalent(baseline, Snapshot(3, 100, 5, 4)));                                    // lease churn is not a difference
        Assert.False(InventoryComparer.Equivalent(baseline, Snapshot(4, 100, 1, 4)));                                   // a row more
        Assert.Contains("content differs", Assert.Single(InventoryComparer.Differences(baseline, Snapshot(3, 101, 1, 4))));   // same count, different content
        Assert.False(InventoryComparer.Equivalent(baseline, Snapshot(3, 100, 1, 5)));
        Assert.True(InventoryComparer.Differences(baseline, Snapshot(3, 100, 1, 5), allowRestoreEvents: true).Count == 0);        // a restore appends to the installation's own chain
        Assert.False(InventoryComparer.Differences(baseline, Snapshot(3, 100, 1, 3), allowRestoreEvents: true).Count == 0);       // but never removes
    }

    [Fact]
    public void The_confirmation_phrase_names_the_backup_and_the_dangerous_path_needs_other_words()
    {
        Assert.Equal("RESTORE bk-1", RestorePhrases.For("bk-1", false));
        Assert.Equal("RESTORE bk-1 OVER THE ACTIVE DATABASE", RestorePhrases.For("bk-1", true));
        Assert.NotEqual(RestorePhrases.For("bk-1", false), RestorePhrases.For("bk-2", false));
    }

    // ---- maintenance mode ----

    [Fact]
    public void A_maintenance_snapshot_is_short_lived_and_can_be_dropped_at_once()
    {
        var snapshot = new MaintenanceSnapshot();
        Assert.False(snapshot.TryGet(out _));
        snapshot.Set(new MaintenanceStatus(true, MaintenanceCauses.Manual, "r", DateTime.UtcNow, "op"));
        Assert.True(snapshot.TryGet(out var status)); Assert.True(status.Enabled);
        snapshot.Invalidate();
        Assert.False(snapshot.TryGet(out _));
        Assert.True(MaintenanceCauses.IsRestoreRelated(MaintenanceCauses.RestoreFailed)); Assert.False(MaintenanceCauses.IsRestoreRelated(MaintenanceCauses.Manual));
    }

    [Fact]
    public void The_support_bundle_log_buffer_keeps_only_warnings_and_errors_redacted_and_without_exception_text()
    {
        var buffer = new RecentLogBuffer();
        var log = buffer.CreateLogger("Test");
        log.LogInformation("not kept");
        log.LogWarning(new InvalidOperationException("secret detail Password=abc"), "Connection failed for Server=x;Password=hunter2;");
        var line = Assert.Single(buffer.Snapshot());
        Assert.DoesNotContain("hunter2", line.Message); Assert.Equal(nameof(InvalidOperationException), line.ExceptionType);
        Assert.DoesNotContain("secret detail", System.Text.Json.JsonSerializer.Serialize(line));
    }
}
