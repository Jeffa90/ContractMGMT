using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// S5 (ADR-0029, docs/ROLE_CATALOGUE_ADR.md, owner decision "Option A+"): persisted role codes, permission codes and approver-group keys are permanent identifiers. They are never renamed, never aliased and never
/// reused; what people read comes from terminology. These tests freeze them, so a rename is a deliberate, visible act that has to defeat a test and an accepted decision.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FrozenIdentifierTests
{
    /// <summary>The twelve role codes, in the order of the permission matrix. Order matters: the matrix header is checked against it.</summary>
    private static readonly string[] FrozenRoles =
    [
        "SystemAdministrator", "TenantAdministrator", "OrganisationAdministrator", "DivisionSiteAdministrator", "BusinessConfigurationAdministrator",
        "ProcurementAdministrator", "CategoryOwner", "Legal", "Approver", "BackupApprover", "Auditor", "Viewer"
    ];

    /// <summary>Every permission code in the catalogue when the identifiers were frozen. A code may be added (add it here deliberately); a code may never be removed or renamed.</summary>
    private static readonly string[] FrozenPermissions =
    [
            "ai.use", "approvals.action", "approvals.delegate", "approvals.delegate.administer", "approvals.matrix.read", "approvals.simulate", "audit.export",
            "audit.read", "audit.read_admin", "audit.read_system", "capabilities.audit.read", "capabilities.draft", "capabilities.publish", "capabilities.read",
            "capabilities.rollback", "categories.assign", "categories.manage", "classification.lower", "config.draft", "config.history.read", "config.publish",
            "config.rollback", "contracts.close", "contracts.create", "contracts.edit_owned", "contracts.read", "contracts.submit_approval", "contracts.submit_legal",
            "contracts.suspend", "contracts.unlock_emergency", "disposition.administer", "disposition.approve", "disposition.request", "documents.download",
            "documents.upload", "identity.configure", "integrations.manage", "legal.clear", "legal.markup", "legal.respond", "legal.review", "legalhold.manage",
            "migration.accept", "migration.commit", "migration.legacy_load", "migration.rollback", "migration.stage", "notifications.administer", "onboarding.run",
            "ops.manage", "org.structure.manage", "organisations.manage", "packages.export", "packages.import", "packs.assign", "packs.audit.read", "packs.install",
            "packs.read", "packs.retire", "packs.trust.manage", "packs.upload", "packs.validate", "records.export", "records.read_privileged", "reminders.configure",
            "renewals.create", "reporting.external", "retention.manage", "rolemappings.manage", "roles.delegate", "secrets.manage", "settings.operational", "setup.run",
            "suppliers.manage", "suppliers.read", "support.elevate", "system.configure", "templates.manage", "tenant.configure", "tenants.manage",
            "terminology.audit.read", "terminology.draft", "terminology.publish", "terminology.read", "terminology.rollback", "users.manage", "variations.create"
    ];

    [Fact]
    public void The_twelve_role_codes_are_frozen_in_name_and_order()
    {
        Assert.Equal(FrozenRoles, RoleCodes.All);
        Assert.Equal("ProcurementAdministrator", RoleCodes.ProcurementAdministrator);
        Assert.Equal("CategoryOwner", RoleCodes.CategoryOwner);
        Assert.Equal("Legal", RoleCodes.Legal);
        Assert.Equal("Approver", RoleCodes.Approver);
        Assert.Equal("BackupApprover", RoleCodes.BackupApprover);
    }

    [Fact]
    public void No_alias_exists_a_neutral_looking_name_is_not_a_role_code()
    {
        // Decision 9: no alias migrations. The names people read are labels, not accepted spellings of a role code.
        foreach (var label in new[] { "ContractAdministrator", "ContractOwner", "Reviewer", "RegistrationApprover", "Contract Administrator", "Procurement Administrator", "Category Owner", "GroupHeadApprover", "procurementadministrator" })
            Assert.False(RoleCodes.IsKnown(label), label);
        foreach (var code in FrozenRoles) Assert.True(RoleCodes.IsKnown(code), code);
    }

    [Fact]
    public void The_permission_matrix_columns_are_the_frozen_role_codes_in_the_same_order()
    {
        var header = File.ReadLines(Path.Combine(RepoRoot.Find(), "requirements", "02-reference", "role-permission-matrix.csv")).First().Split(',');
        Assert.Equal(["Permission", .. FrozenRoles, "Basis"], header);
    }

    [Fact]
    public void Every_frozen_permission_code_still_exists_and_a_new_one_has_to_be_added_to_the_frozen_list_on_purpose()
    {
        var current = PermissionMatrix.Instance.Permissions.ToHashSet(StringComparer.Ordinal);
        var removed = FrozenPermissions.Where(p => !current.Contains(p)).ToList();
        Assert.True(removed.Count == 0, "A permission code was removed or renamed; permission codes are permanent identifiers: " + string.Join(", ", removed));
        var added = current.Except(FrozenPermissions, StringComparer.Ordinal).Order().ToList();
        Assert.True(added.Count == 0, "A permission code was added: add it to FrozenPermissions on purpose. " + string.Join(", ", added));
        Assert.Equal(FrozenPermissions.Length, FrozenPermissions.Distinct().Count());
    }

    [Fact]
    public void The_permission_catalogue_file_and_the_embedded_matrix_agree_on_the_codes()
    {
        var catalogue = File.ReadLines(Path.Combine(RepoRoot.Find(), "requirements", "02-reference", "permission-catalogue.csv")).Skip(1).Select(l => l.Split(',')[0]).Where(c => c.Length > 0).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(FrozenPermissions.Order(StringComparer.Ordinal).ToList(), catalogue);
    }

    [Fact]
    public void Each_role_maps_to_one_terminology_key_and_the_mapping_is_frozen()
    {
        var expected = new Dictionary<string, string>
        {
            ["SystemAdministrator"] = "role.system.administrator", ["TenantAdministrator"] = "role.tenant.administrator", ["OrganisationAdministrator"] = "role.organisation.administrator",
            ["DivisionSiteAdministrator"] = "role.division.site.administrator", ["BusinessConfigurationAdministrator"] = "role.configuration.administrator",
            ["ProcurementAdministrator"] = "contract.administrator", ["CategoryOwner"] = "contract.owner", ["Legal"] = "reviewer", ["Approver"] = "primary.approver", ["BackupApprover"] = "backup.approver",
            ["Auditor"] = "role.auditor", ["Viewer"] = "role.viewer"
        };
        foreach (var role in FrozenRoles) Assert.Equal(expected[role], TermCatalogue.KeyForRole(role));
    }

    [Fact]
    public void The_shipped_packs_keep_their_approver_group_keys()
    {
        string Group(string pack, string file)
        {
            var json = File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", pack, "workflows", file));
            return System.Text.RegularExpressions.Regex.Match(json, "\"approverGroupKey\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        }
        Assert.Equal("GroupHeadProcurement", Group("jbs-procurement", "contract-lifecycle.json"));
        Assert.Equal("GroupHeadProcurement", Group("jbs-procurement", "variation.json"));
        Assert.Equal("Approvers", Group("blank", "contract-lifecycle.json"));
        Assert.Equal("Approvers", Group("blank", "variation.json"));
    }

    [Fact]
    public void An_approver_group_key_is_free_text_for_approvers_and_nothing_else_and_the_platform_gives_no_key_a_meaning()
    {
        var scale = ClassificationScale.Default;
        var scope = ScopeSet.ForOrganisations([Guid.NewGuid()], "Internal");
        foreach (var key in new[] { "GroupHeadProcurement", "Approvers", "Anything-Else_1", new string('k', 100) })
        {
            AssignmentRules.Validate(RoleCodes.Approver, null, scope, key, scale);
            AssignmentRules.Validate(RoleCodes.BackupApprover, null, scope, key, scale);
        }
        Assert.Throws<DomainException>(() => AssignmentRules.Validate(RoleCodes.Approver, null, scope, new string('k', 101), scale));
        Assert.Throws<DomainException>(() => AssignmentRules.Validate(RoleCodes.Approver, null, scope, null, scale));
        Assert.Throws<DomainException>(() => AssignmentRules.Validate(RoleCodes.Legal, null, scope, "GroupHeadProcurement", scale));
    }

    [Fact]
    public void Audit_action_codes_for_role_changes_are_frozen()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", "Modules", "Ecp.Identity", "IdentityApi.cs"));
        foreach (var action in new[] { "role.granted", "role.revoked", "rolemapping.created", "rolemapping.deactivated" })
            Assert.Contains($"new AuditEntry(\"{action}\"", source);
    }

    [Fact]
    public void Role_assignment_and_mapping_columns_keep_their_names_and_sizes_so_no_alias_migration_is_needed()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", "Modules", "Ecp.Identity", "Persistence", "IdentityDbContext.cs"));
        Assert.Contains("e.Property(x => x.RoleCode).HasMaxLength(50).IsRequired();", source);
        Assert.Contains("e.Property(x => x.ApproverGroupKey).HasMaxLength(100);", source);
        Assert.DoesNotContain("RoleAlias", source);
        var migrations = Directory.EnumerateFiles(Path.Combine(RepoRoot.Find(), "src", "Modules", "Ecp.Identity", "Persistence", "Migrations"), "*.cs").Where(f => !f.EndsWith(".Designer.cs") && !f.EndsWith("Snapshot.cs"));
        foreach (var file in migrations)
            Assert.DoesNotMatch("(?i)RenameColumn\\([^)]*RoleCode|UPDATE\\s+\\[?(RoleAssignment|GroupRoleMapping)", File.ReadAllText(file));
    }
}
