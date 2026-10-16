using System.Text.Json;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecp.Identity.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, DbContextServices services) : TenantDbContext(options, services)
{
    public DbSet<UserProfile> Users => Set<UserProfile>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<GroupRoleMapping> GroupMappings => Set<GroupRoleMapping>();
    public DbSet<IdentityProviderRegistration> Providers => Set<IdentityProviderRegistration>();
    protected override string SchemaName => IdentityModule.Schema;

    private static readonly JsonSerializerOptions Json = new();

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<IReadOnlyList<Guid>, string> ListConverter =
        new(v => JsonSerializer.Serialize(v, Json), v => (IReadOnlyList<Guid>)(JsonSerializer.Deserialize<List<Guid>>(v, Json) ?? new List<Guid>()));
    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<IReadOnlyList<Guid>?, string?> NullableListConverter =
        new(v => v == null ? null : JsonSerializer.Serialize(v, Json), v => v == null ? null : (IReadOnlyList<Guid>)(JsonSerializer.Deserialize<List<Guid>>(v, Json) ?? new List<Guid>()));

    private static ValueComparer<IReadOnlyList<Guid>> ListComparer() => new(
        (a, b) => (a ?? Array.Empty<Guid>()).SequenceEqual(b ?? Array.Empty<Guid>()), v => v.Aggregate(0, (h, g) => HashCode.Combine(h, g)), v => v.ToList());
    private static ValueComparer<IReadOnlyList<Guid>?> NullableListComparer() => new(
        (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b), v => v == null ? 0 : v.Aggregate(0, (h, g) => HashCode.Combine(h, g)), v => v == null ? null : v.ToList());

    protected override void ConfigureModule(ModelBuilder mb)
    {
        mb.Entity<UserProfile>(e =>
        {
            e.ToTable("UserProfile");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.EmailNormalised).HasMaxLength(254).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RowVersion);
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.EmailNormalised }).IsUnique();
        });

        mb.Entity<ExternalIdentity>(e =>
        {
            e.ToTable("ExternalIdentity");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ProviderKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.Issuer).HasMaxLength(450).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(450).IsRequired();
            e.HasOne<UserProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.UserId }).HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            // An (issuer, subject) pair identifies exactly one user in a tenant.
            e.HasIndex(x => new { x.TenantId, x.Issuer, x.Subject }).IsUnique();
        });

        mb.Entity<RoleAssignment>(e =>
        {
            e.ToTable("RoleAssignment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RoleCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.ViewerKind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ScopeLevel).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ClassificationCeiling).HasMaxLength(50).IsRequired();
            e.Property(x => x.ApproverGroupKey).HasMaxLength(100);
            e.Property(x => x.GrantedByLabel).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Ignore(x => x.Scope);
            ListProperty(e.Property(x => x.OrganisationIds));
            ListProperty(e.Property(x => x.BusinessUnitIds));
            ListProperty(e.Property(x => x.DivisionIds));
            ListProperty(e.Property(x => x.SiteIds));
            NullableListProperty(e.Property(x => x.CategoryIds));
            e.Property(x => x.RowVersion);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.UserId }).HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.UserId });
            e.HasIndex(x => new { x.TenantId, x.RoleCode });
        });

        mb.Entity<GroupRoleMapping>(e =>
        {
            e.ToTable("GroupRoleMapping");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ProviderKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.GroupKey).HasMaxLength(450).IsRequired();
            e.Property(x => x.RoleCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.ViewerKind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ScopeLevel).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ClassificationCeiling).HasMaxLength(50).IsRequired();
            e.Property(x => x.ApproverGroupKey).HasMaxLength(100);
            e.Ignore(x => x.Scope);
            ListProperty(e.Property(x => x.OrganisationIds));
            ListProperty(e.Property(x => x.BusinessUnitIds));
            ListProperty(e.Property(x => x.DivisionIds));
            ListProperty(e.Property(x => x.SiteIds));
            NullableListProperty(e.Property(x => x.CategoryIds));
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.ProviderKey, x.GroupKey });
        });

        mb.Entity<IdentityProviderRegistration>(e =>
        {
            e.ToTable("IdentityProvider");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Key).HasMaxLength(40).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Authority).HasMaxLength(500);
            e.Property(x => x.ClientId).HasMaxLength(200);
            e.Property(x => x.ClientSecretRef).HasMaxLength(500);
            e.Property(x => x.EntraTenantId).HasMaxLength(100);
            e.Property(x => x.Scopes).HasMaxLength(500).IsRequired();
            e.Property(x => x.GroupsClaim).HasMaxLength(100).IsRequired();
            e.Property(x => x.RowVersion);
            e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        });
    }

    private static void ListProperty(PropertyBuilder<IReadOnlyList<Guid>> p)
    {
        p.HasConversion(ListConverter, ListComparer());
    }

    private static void NullableListProperty(PropertyBuilder<IReadOnlyList<Guid>?> p)
    {
        p.HasConversion(NullableListConverter, NullableListComparer());
    }
}

public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var (options, services) = DesignTime.Create<IdentityDbContext>(IdentityModule.Schema);
        return new IdentityDbContext(options, services);
    }
}
