using Ecp.Configuration;
using Ecp.Identity.Domain;
using Ecp.Setup.Onboarding;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public class OnboardingCsvTests
{
    [Fact]
    public void Every_list_round_trips_through_its_csv_form()
    {
        var d = Sample();
        Assert.Equal(d.Categories, OnboardingCsv.ReadCategories(OnboardingCsv.WriteCategories(d.Categories)).Items);
        Assert.Equal(d.ContractTypes, OnboardingCsv.ReadTypes(OnboardingCsv.WriteTypes(d.ContractTypes)).Items);
        Assert.Equal(d.CategoryOwners, OnboardingCsv.ReadOwners(OnboardingCsv.WriteOwners(d.CategoryOwners)).Items);
        var viewers = OnboardingCsv.ReadViewers(OnboardingCsv.WriteViewers(d.ViewerScopes)).Items;
        Assert.Equal(d.ViewerScopes.Select(Canonical.Viewer), viewers.Select(Canonical.Viewer));
        var mappings = OnboardingCsv.ReadMappings(OnboardingCsv.WriteMappings(d.GroupMappings)).Items;
        Assert.Equal(d.GroupMappings.Select(Canonical.Mapping), mappings.Select(Canonical.Mapping));
        var structure = OnboardingCsv.ToSection(OnboardingCsv.ReadStructure(OnboardingCsv.WriteStructure(d.Structure)).Items);
        Assert.Equal(d.Structure.Sites, structure.Sites); Assert.Equal(d.Structure.BusinessUnits, structure.BusinessUnits); Assert.Equal(d.Structure.Divisions, structure.Divisions);
    }

    [Fact]
    public void Quoted_values_with_commas_and_quotes_survive()
    {
        var items = new[] { new CategoryEntry("A", "Packaging, \"flexible\" and rigid", null) };
        var back = OnboardingCsv.ReadCategories(OnboardingCsv.WriteCategories(items)).Items;
        Assert.Equal("Packaging, \"flexible\" and rigid", Assert.Single(back).Name);
    }

    [Fact]
    public void Headers_comments_and_blank_lines_are_skipped()
    {
        var r = OnboardingCsv.ReadCategories("code,name,parentCode\n\n# a comment\nA,Alpha,\r\nB,Beta,A\r\n");
        Assert.True(r.IsClean); Assert.Equal(["A", "B"], r.Items.Select(i => i.Code));
    }

    [Theory]
    [InlineData("A")]                        // too few cells
    [InlineData("\"A,Alpha")]                // unclosed quote
    public void Bad_lines_are_reported_with_their_line_number_and_nothing_is_accepted_silently(string line)
    {
        var r = OnboardingCsv.ReadCategories("code,name\nGOOD,Good\n" + line);
        Assert.False(r.IsClean);
        Assert.Equal(3, r.Issues.Single().Line);
    }

    [Fact]
    public void Unknown_viewer_kinds_and_booleans_are_refused()
    {
        Assert.Contains("Galactic", OnboardingCsv.ReadViewers("a@b.invalid,Galactic,,,,Internal").Issues.Single().Message);
        Assert.NotEmpty(OnboardingCsv.ReadOwners("A,a@b.invalid,perhaps").Issues);
        Assert.NotEmpty(OnboardingCsv.ReadStructure("Planet,X,Mars").Issues);
    }

    [Fact]
    public void Oversized_input_is_refused_rather_than_processed()
    {
        var many = string.Join('\n', Enumerable.Range(0, OnboardingCsv.MaxLines + 1).Select(i => $"C{i},Name"));
        Assert.Contains("Too many lines", OnboardingCsv.ReadCategories(many).Issues.Single().Message);
        var longLine = "A," + new string('x', OnboardingCsv.MaxLineLength);
        Assert.Contains("longer than", OnboardingCsv.ReadCategories(longLine).Issues.Single().Message);
    }

    [Fact]
    public void Formula_like_cells_are_just_text()
    {
        var item = Assert.Single(OnboardingCsv.ReadCategories("=SUM(A1:A9),@cmd|' /C calc'!A0,").Items);
        Assert.Equal("=SUM(A1:A9)", item.Code);   // validation rejects the code; nothing is ever evaluated
        var report = OnboardingValidator.Validate(OnboardingDocument.Empty with { Categories = [item] }, OnboardingFacts.None());
        Assert.Contains(report.Messages, m => m.Code == "invalid_key");
    }

    internal static OnboardingDocument Sample() => new(1, "blank",
        new StructureSection([new("LE1", "Legal One", null, null, "ABN 1")], [new("OPS", "Operations")], [new("PKG", "Packaging", "OPS")], [new("BNE", "Brisbane", null, "Australia/Brisbane")]),
        [new("PACKAGING", "Packaging"), new("PACKAGING-FLEX", "Flexible, packaging", "PACKAGING")], [new("MSA", "Master services agreement")],
        [new("PACKAGING", "owner@example.invalid", true)],
        [new("viewer@example.invalid", ViewerKind.Restricted, ["PACKAGING"], ["PKG"], ["BNE"], "Internal")],
        [new("corp-sso", "Procurement-Viewers", RoleCodes.Viewer, ViewerKind.BusinessUnit, [], ["PKG"], [], "Internal")]);
}

[Trait("Category", "Unit")]
public class OnboardingValidatorTests
{
    private static OnboardingFacts Facts(params string[] users) => OnboardingFacts.None() with
    {
        KnownEmails = users.Select(u => u.ToLowerInvariant()).ToHashSet(), ProviderKeys = new HashSet<string> { "corp-sso" }, PackIds = new HashSet<string> { "blank" },
        Categories = new HashSet<string> { "EXISTING" }
    };

    private static ValidationReport Run(OnboardingDocument d, params string[] users) => OnboardingValidator.Validate(d, Facts(users));
    private static bool Has(ValidationReport r, string code, ValidationSeverity s = ValidationSeverity.Error) => r.Messages.Any(m => m.Code == code && m.Severity == s);

    [Fact]
    public void A_well_formed_document_is_valid()
    {
        var r = Run(OnboardingCsvTests.Sample(), "owner@example.invalid", "viewer@example.invalid");
        Assert.True(r.IsValid, string.Join("; ", r.Messages.Where(m => m.Severity == ValidationSeverity.Error).Select(m => m.Message)));
    }

    [Fact]
    public void The_empty_document_is_valid_but_warns_that_nobody_is_given_access()
    {
        var r = Run(OnboardingDocument.Empty);
        Assert.True(r.IsValid); Assert.True(Has(r, "no_access", ValidationSeverity.Warning));
    }

    [Fact]
    public void Structure_rules()
    {
        var d = OnboardingDocument.Empty with { Structure = new StructureSection([], [new("OPS", "Ops"), new("ops", "Ops again")],
            [new("PKG", "Packaging", "NOPE")], [new("S1", "Site", null, "Mars/Olympus"), new("bad key!", "Bad")]) };
        var r = Run(d);
        Assert.True(Has(r, "duplicate")); Assert.True(Has(r, "division_not_found")); Assert.True(Has(r, "invalid_time_zone")); Assert.True(Has(r, "invalid_key"));
    }

    [Fact]
    public void Category_rules_cover_duplicates_missing_parents_cycles_and_depth()
    {
        Assert.True(Has(Run(OnboardingDocument.Empty with { Categories = [new("A", "A"), new("a", "A again")] }), "duplicate"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { Categories = [new("A", "A", "MISSING")] }), "parent_not_found"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { Categories = [new("A", "A", "EXISTING")] }), "parent_not_found") is false);   // an existing parent is fine
        Assert.True(Has(Run(OnboardingDocument.Empty with { Categories = [new("A", "A", "B"), new("B", "B", "A")] }), "cycle"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { Categories = [new("A", "A", "A")] }), "cycle"));
        var deep = Enumerable.Range(0, 8).Select(i => new CategoryEntry($"L{i}", "Level", i == 0 ? null : $"L{i - 1}")).ToList();
        Assert.True(Has(Run(OnboardingDocument.Empty with { Categories = deep }), "depth"));
    }

    [Fact]
    public void People_must_already_exist_and_owners_must_name_a_known_category()
    {
        var d = OnboardingDocument.Empty with { Categories = [new("A", "A")], CategoryOwners = [new("A", "ghost@example.invalid", true), new("ZZZ", "real@example.invalid", true)] };
        var r = Run(d, "real@example.invalid");
        Assert.True(Has(r, "user_not_found")); Assert.True(Has(r, "category_not_found"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { CategoryOwners = [new("EXISTING", "not an address", true)] }), "email"));
    }

    [Theory]
    [InlineData(ViewerKind.AllContracts, "PACKAGING", "", "", "viewer_not_narrowable")]
    [InlineData(ViewerKind.Category, "", "", "", "category_required")]
    [InlineData(ViewerKind.BusinessUnit, "", "", "", "business_unit_required")]
    [InlineData(ViewerKind.Site, "", "", "", "site_required")]
    [InlineData(ViewerKind.Restricted, "", "", "", "restricted_needs_scope")]
    public void Viewer_kinds_must_be_narrowed_the_way_their_kind_requires(ViewerKind kind, string cats, string bus, string sites, string expected)
    {
        string[] L(string v) => v.Length == 0 ? [] : v.Split(';');
        var d = OnboardingDocument.Empty with { Categories = [new("PACKAGING", "P")], ViewerScopes = [new("v@example.invalid", kind, L(cats), L(bus), L(sites), "Internal")] };
        Assert.True(Has(Run(d, "v@example.invalid"), expected));
    }

    [Fact]
    public void A_restricted_viewer_with_a_single_narrowing_is_warned_and_unknown_keys_and_ceilings_are_errors()
    {
        var one = OnboardingDocument.Empty with { Categories = [new("P", "P")], ViewerScopes = [new("v@example.invalid", ViewerKind.Restricted, ["P"], [], [], "Internal")] };
        Assert.True(Has(Run(one, "v@example.invalid"), "restricted_single", ValidationSeverity.Warning));
        var bad = OnboardingDocument.Empty with { ViewerScopes = [new("v@example.invalid", ViewerKind.Site, [], [], ["NOWHERE"], "Galactic")] };
        var r = Run(bad, "v@example.invalid");
        Assert.True(Has(r, "site_not_found")); Assert.True(Has(r, "invalid_classification"));
    }

    [Fact]
    public void The_identity_modules_assignment_rules_are_applied_so_a_draft_cannot_pass_validation_and_fail_at_publication()
    {
        // A Restricted Viewer's ceiling may not exceed Confidential without an explicit decision, which onboarding cannot make.
        var viewer = OnboardingDocument.Empty with { Categories = [new("P", "P")], ViewerScopes = [new("v@example.invalid", ViewerKind.Restricted, ["P"], ["PKG"], [], "Restricted")] };
        var withUnit = viewer with { Structure = new StructureSection([], [], [new("PKG", "Packaging")], []) };
        Assert.True(Has(Run(withUnit, "v@example.invalid"), "ceiling_too_high"));
        var ok = withUnit with { ViewerScopes = [withUnit.ViewerScopes[0] with { Ceiling = "Confidential" }] };
        Assert.True(Run(ok, "v@example.invalid").IsValid);
        var mapped = OnboardingDocument.Empty with { Structure = new StructureSection([], [], [new("PKG", "Packaging")], [new("BNE", "Brisbane")]),
            GroupMappings = [new("corp-sso", "grp", RoleCodes.Viewer, ViewerKind.Restricted, [], ["PKG"], ["BNE"], "Restricted")] };
        Assert.True(Has(Run(mapped), "ceiling_too_high"));
    }

    [Fact]
    public void The_highest_classification_ceiling_is_flagged_for_attention()
    {
        var d = OnboardingDocument.Empty with { ViewerScopes = [new("v@example.invalid", ViewerKind.AllContracts, [], [], [], "Restricted")] };
        Assert.True(Has(Run(d, "v@example.invalid"), "top_ceiling", ValidationSeverity.Warning));
    }

    [Theory]
    [InlineData(RoleCodes.SystemAdministrator)]
    [InlineData(RoleCodes.TenantAdministrator)]
    [InlineData(RoleCodes.OrganisationAdministrator)]
    [InlineData(RoleCodes.BusinessConfigurationAdministrator)]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.BackupApprover)]
    public void Group_mappings_cannot_grant_administrator_or_approver_roles(string role)
    {
        var d = OnboardingDocument.Empty with { GroupMappings = [new("corp-sso", "grp", role, null, [], [], [], "Internal")] };
        Assert.True(Has(Run(d), "role_not_mappable"));
    }

    [Fact]
    public void Group_mapping_rules()
    {
        Assert.True(Has(Run(OnboardingDocument.Empty with { GroupMappings = [new("corp-sso", "grp", "Wizard", null, [], [], [], "Internal")] }), "unknown_role"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { GroupMappings = [new("corp-sso", "grp", RoleCodes.Viewer, null, [], [], [], "Internal")] }), "viewer_kind_required"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { GroupMappings = [new("corp-sso", "grp", RoleCodes.Legal, ViewerKind.Site, [], [], [], "Internal")] }), "viewer_kind_only_for_viewer"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { GroupMappings = [new("corp-sso", "grp", RoleCodes.DivisionSiteAdministrator, null, [], [], [], "Internal")] }), "site_required"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { GroupMappings = [new("other-idp", "grp", RoleCodes.Legal, null, [], [], [], "Internal")] }), "provider_unknown", ValidationSeverity.Warning));
        var twice = new MappingEntry("corp-sso", "grp", RoleCodes.Legal, null, [], [], [], "Internal");
        Assert.True(Has(Run(OnboardingDocument.Empty with { GroupMappings = [twice, twice] }), "duplicate"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { GroupMappings = [new("corp-sso", "grp", RoleCodes.ProcurementAdministrator, null, [], [], [], "Internal")] }), "privileged_mapping", ValidationSeverity.Warning));
    }

    [Fact]
    public void Schema_version_and_pack_are_checked()
    {
        Assert.True(Has(Run(OnboardingDocument.Empty with { SchemaVersion = 7 }), "schema_version"));
        Assert.True(Has(Run(OnboardingDocument.Empty with { PackId = "no-such-pack" }), "unknown_pack"));
        Assert.True(Run(OnboardingDocument.Empty with { PackId = "blank" }).IsValid);
    }

    [Fact]
    public void Documents_survive_json_and_tolerate_missing_lists_but_not_garbage()
    {
        var d = OnboardingCsvTests.Sample();
        Assert.Equal(d.ToJson(), OnboardingDocument.Parse(d.ToJson()).ToJson());
        var sparse = OnboardingDocument.Parse("""{"schemaVersion":1,"categories":[{"code":"A","name":"A"}]}""");
        Assert.Empty(sparse.ViewerScopes); Assert.Empty(sparse.Structure.Sites); Assert.Single(sparse.Categories);
        Assert.Throws<DomainException>(() => OnboardingDocument.Parse("{ not json"));
        Assert.Same(OnboardingDocument.Empty, OnboardingDocument.Parse("{}"));
    }
}

[Trait("Category", "Unit")]
public class OnboardingSimulatorTests
{
    private static readonly ClassificationScale Scale = ClassificationScale.Default;

    [Fact]
    public void A_category_viewer_sees_only_its_category_and_never_above_its_ceiling()
    {
        var d = OnboardingDocument.Empty with
        {
            Structure = new StructureSection([], [], [], [new("BNE", "Brisbane"), new("MEL", "Melbourne")]),
            Categories = [new("PACKAGING", "P"), new("PACKAGING-FLEX", "F", "PACKAGING"), new("ENERGY", "E")],
            ViewerScopes = [new("v@example.invalid", ViewerKind.Category, ["PACKAGING"], [], [], "Internal")]
        };
        var preview = OnboardingSimulator.Run(d, Scale);
        var row = Assert.Single(preview.Rows);
        Assert.True(row.Visible > 0 && row.Visible < row.Total);
        // 3 categories x 2 sites x 3 classification samples (lowest, highest ordinary, privileged) = 18; packaging and its child at the lowest level = 2 x 2 sites = 4 visible
        Assert.Equal(18, row.Total); Assert.Equal(4, row.Visible);
        Assert.All(row.HiddenExamples, h => Assert.True(h.Contains("ENERGY") || h.Contains("Restricted") || h.Contains("Privileged")));
    }

    [Fact]
    public void An_all_contracts_viewer_sees_every_record_up_to_the_ceiling_and_the_ceiling_hides_the_rest()
    {
        var d = OnboardingDocument.Empty with
        {
            Categories = [new("A", "A")],
            ViewerScopes = [new("low@example.invalid", ViewerKind.AllContracts, [], [], [], "Internal"), new("high@example.invalid", ViewerKind.AllContracts, [], [], [], "Restricted")]
        };
        var rows = OnboardingSimulator.Run(d, Scale).Rows;
        var low = rows.Single(r => r.Who.StartsWith("low")); var high = rows.Single(r => r.Who.StartsWith("high"));
        Assert.Equal(high.Total - 1, high.Visible);              // everything but the privileged sample: a ceiling alone never opens a privileged record
        Assert.True(low.Visible < high.Visible);                 // the highest ordinary sample stays hidden under a lower ceiling although it is inside the person's scope
    }

    [Fact]
    public void Mappings_are_simulated_as_members_of_the_group_and_the_preview_is_deterministic()
    {
        var d = OnboardingCsvTests.Sample();
        var a = OnboardingSimulator.Run(d, Scale); var b = OnboardingSimulator.Run(d, Scale);
        Assert.Equal(a.Rows.Select(r => (r.Who, r.Visible)), b.Rows.Select(r => (r.Who, r.Visible)));
        Assert.Contains(a.Rows, r => r.Who.StartsWith("members of Procurement-Viewers"));
    }

    [Fact]
    public void Unknown_ceilings_are_skipped_rather_than_guessed()
    {
        var d = OnboardingDocument.Empty with { ViewerScopes = [new("v@example.invalid", ViewerKind.AllContracts, [], [], [], "Galactic")] };
        Assert.Empty(OnboardingSimulator.Run(d, Scale).Rows);
    }

    [Fact]
    public void Role_scopes_built_from_keys_use_the_requested_organisation_and_narrowings()
    {
        var org = Guid.NewGuid();
        var scope = OnboardingScopes.For(org, "Internal", ["A"], ["PKG"], [], k => OnboardingSimulator.Synthetic("c", k), k => OnboardingSimulator.Synthetic("b", k), k => OnboardingSimulator.Synthetic("s", k));
        Assert.Equal([org], scope.OrganisationIds); Assert.NotNull(scope.CategoryIds); Assert.Single(scope.BusinessUnitIds); Assert.Empty(scope.SiteIds);
        Assert.Null(OnboardingScopes.For(org, "Internal", [], [], [], k => Guid.Empty, k => Guid.Empty, k => Guid.Empty).CategoryIds);   // no category list means all categories
    }
}
