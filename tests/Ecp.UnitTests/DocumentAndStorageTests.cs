using System.Text;
using Ecp.Documents.Domain;
using Ecp.Documents.Storage;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

public class FileNameTests
{
    [Theory]
    [InlineData("contract.pdf", "contract.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\evil.docx", "evil.docx")]
    [InlineData("a<b>c:d\"e|f?g*.pdf", "a_b_c_d_e_f_g_.pdf")]
    [InlineData("report.pdf. ", "report.pdf")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("", "file")]
    [InlineData("   ", "file")]
    [InlineData("..", "file")]
    public void File_names_are_sanitised_for_display_and_never_form_paths(string input, string expected)
        => Assert.Equal(expected, FileNames.Sanitise(input));

    [Fact]
    public void Control_characters_are_replaced_and_length_is_bounded_keeping_the_extension()
    {
        Assert.DoesNotContain('\n', FileNames.Sanitise("a\nb\u0000c.pdf"));
        var longName = FileNames.Sanitise(new string('a', 400) + ".pdf");
        Assert.True(longName.Length <= 200);
        Assert.EndsWith(".pdf", longName);
    }
}

public class StorageKeyTests
{
    private static readonly string Hash = Hashing.Sha256Hex("hello");

    [Fact]
    public void Keys_are_tenant_prefixed_content_addresses()
    {
        var tenant = Guid.NewGuid();
        var key = StorageKey.For(tenant, Hash);
        Assert.Equal($"t/{tenant:N}/{Hash[..2]}/{Hash}", key);
        Assert.True(StorageKey.IsValidFor(tenant, key));
        Assert.False(StorageKey.IsValidFor(Guid.NewGuid(), key)); // another tenant cannot use it
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("t/../../etc/passwd")]
    [InlineData("")]
    [InlineData("file.pdf")]
    public void Anything_that_is_not_a_content_address_is_invalid(string key)
        => Assert.False(StorageKey.IsValidFor(Guid.NewGuid(), key));

    [Fact]
    public void A_non_hash_cannot_produce_a_key()
        => Assert.Equal("invalid_hash", Assert.Throws<DomainException>(() => StorageKey.For(Guid.NewGuid(), "../../x")).Code);
}

public class DocumentDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_document_without_a_contract_waits_in_restricted_staging()
    {
        var d = Document.Create(Guid.NewGuid(), Guid.NewGuid(), null, "Executed Contract", "t", "Internal", false);
        Assert.Equal(DocumentStatus.Quarantined, d.Status);
        Assert.Equal(DocumentStatus.Active, Document.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x", "t", "Internal", false).Status);
    }

    [Fact]
    public void Versions_are_numbered_and_the_latest_becomes_current_and_files_are_not_downloadable_until_clean()
    {
        var tenant = Guid.NewGuid();
        var d = Document.Create(tenant, Guid.NewGuid(), Guid.NewGuid(), "x", "Title", "Internal", true);
        var hash = Hashing.Sha256Hex("v1");
        var v1 = d.AddVersion("../a.pdf", "application/pdf", 10, hash, "local-fs", StorageKey.For(tenant, hash), "1.0", null, "u", Now);
        var v2 = d.AddVersion("b.pdf", "application/pdf", 12, Hashing.Sha256Hex("v2"), "local-fs", StorageKey.For(tenant, Hashing.Sha256Hex("v2")), null, null, "u", Now);
        Assert.Equal(1, v1.VersionNumber);
        Assert.Equal(2, v2.VersionNumber);
        Assert.Equal(v2.Id, d.CurrentVersionId);
        Assert.Equal("a.pdf", v1.FileName);
        Assert.False(v1.IsDownloadable(allowSkippedScan: false));
        v1.RecordScan(ScanStatus.Clean);
        Assert.True(v1.IsDownloadable(false));
        v2.RecordScan(ScanStatus.Skipped);
        Assert.False(v2.IsDownloadable(false));
        Assert.True(v2.IsDownloadable(true));
        v2.RecordScan(ScanStatus.Infected);
        Assert.False(v2.IsDownloadable(true));
    }

    [Fact]
    public void A_version_cannot_point_at_another_tenants_storage_key()
    {
        var d = Document.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x", "t", "Internal", false);
        var hash = Hashing.Sha256Hex("v");
        Assert.Equal("invalid_storage_key", Assert.Throws<DomainException>(() =>
            d.AddVersion("a.pdf", "application/pdf", 1, hash, "local-fs", StorageKey.For(Guid.NewGuid(), hash), null, null, "u", Now)).Code);
    }
}

public sealed class LocalStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ecp-storage-" + Guid.NewGuid().ToString("N"));
    private LocalFileSystemStorageProvider Provider() => new(new LocalFileSystemOptions { RootPath = _root });
    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task Content_round_trips_with_a_platform_computed_hash()
    {
        var p = Provider(); var tenant = Guid.NewGuid();
        var stored = await p.PutAsync(tenant, Bytes("hello"), 1024);
        Assert.Equal(Hashing.Sha256Hex("hello"), stored.Sha256);
        Assert.Equal(5, stored.SizeBytes);
        Assert.False(stored.AlreadyExisted);
        using var read = await p.OpenReadAsync(tenant, stored.StorageKey);
        Assert.Equal("hello", await new StreamReader(read).ReadToEndAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "_incoming")) && Directory.EnumerateFiles(Path.Combine(_root, "_incoming")).Any());
    }

    [Fact]
    public async Task Identical_content_deduplicates_within_a_tenant_but_never_across_tenants()
    {
        var p = Provider(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var first = await p.PutAsync(a, Bytes("same"), 1024);
        var second = await p.PutAsync(a, Bytes("same"), 1024);
        var other = await p.PutAsync(b, Bytes("same"), 1024);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.StorageKey, second.StorageKey);
        Assert.False(other.AlreadyExisted);
        Assert.NotEqual(first.StorageKey, other.StorageKey);
    }

    [Fact]
    public async Task A_tenant_cannot_read_exists_or_delete_another_tenants_key()
    {
        var p = Provider(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var stored = await p.PutAsync(a, Bytes("secret"), 1024);
        await Assert.ThrowsAsync<DomainException>(() => p.OpenReadAsync(b, stored.StorageKey));
        await Assert.ThrowsAsync<DomainException>(() => p.ExistsAsync(b, stored.StorageKey));
        await Assert.ThrowsAsync<DomainException>(() => p.DeleteAsync(b, stored.StorageKey));
        Assert.True(await p.ExistsAsync(a, stored.StorageKey));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("t/00000000000000000000000000000001/../../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task Traversal_style_keys_are_refused(string key)
    {
        var p = Provider();
        await Assert.ThrowsAsync<DomainException>(() => p.OpenReadAsync(Guid.NewGuid(), key));
    }

    [Fact]
    public async Task Oversize_and_empty_content_are_refused_and_leave_no_temporary_files()
    {
        var p = Provider(); var tenant = Guid.NewGuid();
        Assert.Equal("file_too_large", (await Assert.ThrowsAsync<DomainException>(() => p.PutAsync(tenant, Bytes(new string('x', 100)), 10))).Code);
        Assert.Equal("empty_file", (await Assert.ThrowsAsync<DomainException>(() => p.PutAsync(tenant, new MemoryStream(), 10))).Code);
        var incoming = Path.Combine(_root, "_incoming");
        Assert.True(!Directory.Exists(incoming) || !Directory.EnumerateFiles(incoming).Any());
    }

    [Fact]
    public async Task Putting_without_a_tenant_fails_closed()
        => await Assert.ThrowsAsync<TenantScopeMissingException>(() => Provider().PutAsync(Guid.Empty, Bytes("x"), 10));
}
