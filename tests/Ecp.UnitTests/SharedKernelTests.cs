using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Xunit;

namespace Ecp.UnitTests;

public class SharedKernelTests
{
    [Fact]
    public void Ids_are_unique_and_never_empty()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Ids.New()).ToList();
        Assert.Equal(1000, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
    }

    [Fact]
    public void Tenant_context_fails_closed_without_a_scope()
    {
        var ctx = new TenantContext();
        Assert.Null(ctx.TenantId);
        Assert.Throws<TenantScopeMissingException>(() => ctx.RequireTenantId());
    }

    [Fact]
    public void Tenant_context_use_sets_and_restores_scope_and_bumps_version()
    {
        var ctx = new TenantContext();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var v0 = ctx.Version;
        using (ctx.Use(a))
        {
            Assert.Equal(a, ctx.RequireTenantId());
            using (ctx.Use(b)) Assert.Equal(b, ctx.RequireTenantId());
            Assert.Equal(a, ctx.RequireTenantId());
        }
        Assert.Null(ctx.TenantId);
        Assert.True(ctx.Version > v0);
    }

    [Fact]
    public void Tenant_context_rejects_an_empty_tenant_and_double_dispose_is_harmless()
    {
        var ctx = new TenantContext();
        Assert.Throws<ArgumentException>(() => ctx.Use(Guid.Empty));
        var scope = ctx.Use(Guid.NewGuid());
        scope.Dispose(); scope.Dispose();
        Assert.Null(ctx.TenantId);
    }

    [Fact]
    public void Installation_scope_uses_the_system_tenant_and_is_flagged()
    {
        var ctx = new TenantContext();
        using (ctx.UseInstallationScope())
        {
            Assert.True(ctx.IsInstallationScope);
            Assert.Equal(SystemTenant.Id, ctx.RequireTenantId());
        }
        Assert.False(ctx.IsInstallationScope);
    }

    [Theory]
    [InlineData("env:ECP_DB", "env", "ECP_DB")]
    [InlineData("file:/run/secrets/db", "file", "/run/secrets/db")]
    [InlineData("KeyVault:ecp-db", "keyvault", "ecp-db")]
    public void Secret_references_parse(string text, string scheme, string target)
    {
        Assert.True(SecretReference.TryParse(text, out var r));
        Assert.Equal(scheme, r.Scheme);
        Assert.Equal(target, r.Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Server=x;Password=y")]
    [InlineData("http:thing")]
    [InlineData("env:")]
    public void Anything_that_is_not_a_reference_is_rejected(string? text)
        => Assert.False(SecretReference.TryParse(text, out _));

    [Fact]
    public void Environment_and_file_secret_providers_resolve_values()
    {
        var name = "ECP_UNIT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "from-env");
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "from-file\n");
        try
        {
            var p = new EnvironmentAndFileSecretProvider();
            Assert.Equal("from-env", p.Resolve(new SecretReference("env", name)));
            Assert.Equal("from-file", p.Resolve(new SecretReference("file", file)));
            Assert.Null(p.Resolve(new SecretReference("env", "NOT_SET_" + name)));
            Assert.Null(p.Resolve(new SecretReference("keyvault", "x")));
        }
        finally { Environment.SetEnvironmentVariable(name, null); File.Delete(file); }
    }

    [Fact]
    public void Redactor_masks_secret_values_but_keeps_other_settings()
    {
        var masked = Redactor.MaskConnectionString("Server=db;User Id=sa;Password=Sup3rSecret!;Encrypt=True;AccountKey=abc123");
        Assert.DoesNotContain("Sup3rSecret", masked);
        Assert.DoesNotContain("abc123", masked);
        Assert.Contains("Server=db", masked);
        Assert.Contains("Encrypt=True", masked);
    }

    [Fact]
    public void Guard_trims_and_never_truncates()
    {
        Assert.Equal("abc", Guard.NotBlank("  abc  ", "Field"));
        var ex = Assert.Throws<DomainException>(() => Guard.NotBlank(new string('x', 11), "Field", 10));
        Assert.Equal("too_long", ex.Code);
        Assert.Equal("required", Assert.Throws<DomainException>(() => Guard.NotBlank("  ", "Field")).Code);
        Assert.Null(Guard.OptionalBounded("   ", "Field", 5));
    }

    [Fact]
    public void Guard_requires_utc_instants()
    {
        Assert.Equal("not_utc", Assert.Throws<DomainException>(() => Guard.Utc(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), "t")).Code);
        Guard.Utc(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "t");
    }

    [Fact]
    public void Sha256_is_lowercase_hex_and_deterministic()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Hashing.Sha256Hex("abc"));
    }
}
