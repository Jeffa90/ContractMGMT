using Ecp.Configuration;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>S4: the terminology model. Keys are stable machine identifiers; labels are data in layers (organisation, pack, platform); the token language is constrained; documents are validated before publication.</summary>
[Trait("Category", "Unit")]
public sealed class TerminologyTests
{
    private static string PackDoc(string pack) => File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", pack, "terminology", "terminology.json"));
    private static TerminologyDocument Pack(string pack) => TerminologyDocument.Parse(PackDoc(pack));

    private static TerminologyDocument Overrides(params TermEntry[] terms) => new(TerminologyDocument.Overrides, terms);
    private static TermEntry T(string key, string singular, string plural, string locale = "en", string? @short = null, string? description = null, string? help = null)
        => new(key, locale, singular, plural, @short, description, help);

    // ---- the catalogue ----

    [Fact]
    public void The_catalogue_covers_every_key_the_brief_requires_and_marks_them_required()
    {
        string[] required =
        [
            "application.name", "contract", "contract.register", "contract.owner", "contract.administrator", "contract.reference", "contract.type", "contract.portfolio", "counterparty", "supplier", "category",
            "subcategory", "reviewer", "legal.review", "approval", "registration.approval", "primary.approver", "backup.approver", "external.execution", "variation", "renewal", "notification", "organisation",
            "legal.entity", "business.unit", "division", "site", "document", "executed.document", "solution.pack", "migration.project", "migration.batch"
        ];
        foreach (var key in required) { Assert.True(TermCatalogue.IsKnown(key), key); Assert.Contains(key, TermCatalogue.RequiredKeys); }
        Assert.Equal(TermCatalogue.All.Count, TermCatalogue.All.Select(d => d.Key).Distinct().Count());
    }

    [Fact]
    public void Neutral_wording_names_no_business_and_uses_the_neutral_labels_the_brief_lists()
    {
        var neutral = TermSet.Neutral();
        Assert.Equal("Contract Administrator", neutral.Singular("contract.administrator"));
        Assert.Equal("Contract Owner", neutral.Singular("contract.owner"));
        Assert.Equal("Reviewer", neutral.Singular("reviewer"));
        Assert.Equal("Approval Authority", neutral.Singular("primary.approver"));
        Assert.Equal("Backup Approver", neutral.Singular("backup.approver"));
        Assert.Equal("Contract Type", neutral.Singular("contract.type"));
        Assert.Equal("Contract Portfolio", neutral.Singular("contract.portfolio"));
        Assert.Equal("Contract Register", neutral.Singular("contract.register"));
        Assert.Equal("External Execution", neutral.Singular("external.execution"));
        Assert.Equal("Review", neutral.Singular("legal.review"));
        Assert.Equal("Counterparty", neutral.Singular("counterparty"));
        Assert.Equal("Counterparty", neutral.Singular("supplier"));
        foreach (var d in TermCatalogue.All)
            foreach (var text in new[] { d.Singular, d.Plural, d.Description })
                Assert.DoesNotMatch("(?i)jbs|procurement|group head|medius|qlik|docusign", text);
    }

    [Fact]
    public void An_unknown_key_resolves_to_its_own_text_as_a_diagnostic_only()
    {
        var set = TermSet.Neutral();
        Assert.Equal("no.such.key", set.Singular("no.such.key"));
        Assert.Equal(TermLayer.Unknown, set.LayerOf("no.such.key"));
        Assert.Equal(TermLayer.Platform, set.LayerOf("contract"));
    }

    [Fact]
    public void Roles_are_labelled_by_term_key_and_the_stable_role_codes_are_unchanged()
    {
        Assert.Equal("contract.administrator", TermCatalogue.KeyForRole("ProcurementAdministrator"));
        Assert.Equal("contract.owner", TermCatalogue.KeyForRole("CategoryOwner"));
        Assert.Equal("reviewer", TermCatalogue.KeyForRole("Legal"));
        Assert.Equal("primary.approver", TermCatalogue.KeyForRole("Approver"));
        Assert.Equal("backup.approver", TermCatalogue.KeyForRole("BackupApprover"));
        // Every role the platform knows has a label key, and that key is in the catalogue.
        foreach (var role in Ecp.Identity.Domain.RoleCodes.All)
        {
            var key = TermCatalogue.KeyForRole(role);
            Assert.True(TermCatalogue.IsKnown(key), $"{role} -> '{key}'");
        }
        // The persisted codes themselves are stable identifiers.
        Assert.Equal("ProcurementAdministrator", Ecp.Identity.Domain.RoleCodes.ProcurementAdministrator);
        Assert.Equal("CategoryOwner", Ecp.Identity.Domain.RoleCodes.CategoryOwner);
        Assert.Equal("Legal", Ecp.Identity.Domain.RoleCodes.Legal);
        Assert.Equal("BackupApprover", Ecp.Identity.Domain.RoleCodes.BackupApprover);
    }

    // ---- layers ----

    [Fact]
    public void The_organisation_beats_the_pack_and_the_pack_beats_the_platform()
    {
        var pack = Overrides(T("supplier", "Vendor", "Vendors"), T("category", "Portfolio", "Portfolios"));
        var org = Overrides(T("supplier", "Partner", "Partners"));
        var set = TermSet.Build("en", org.Terms, pack.Terms, "r1", "p@1");
        Assert.Equal("Partner", set.Singular("supplier"));              // organisation
        Assert.Equal(TermLayer.Organisation, set.LayerOf("supplier"));
        Assert.Equal("Portfolios", set.Plural("category"));              // pack
        Assert.Equal(TermLayer.Pack, set.LayerOf("category"));
        Assert.Equal("Contract", set.Singular("contract"));              // platform
        Assert.Equal(TermLayer.Platform, set.LayerOf("contract"));
    }

    [Fact]
    public void Removing_an_override_resumes_inheritance()
    {
        var pack = Overrides(T("supplier", "Vendor", "Vendors"));
        var withOverride = TermSet.Build("en", Overrides(T("supplier", "Partner", "Partners")).Terms, pack.Terms, "r1", "p");
        var removed = TermSet.Build("en", Overrides().Terms, pack.Terms, "r2", "p");
        Assert.Equal("Partner", withOverride.Singular("supplier"));
        Assert.Equal("Vendor", removed.Singular("supplier"));
        Assert.Equal(TermLayer.Pack, removed.LayerOf("supplier"));
    }

    [Fact]
    public void An_inactive_entry_is_ignored_and_the_layer_beneath_shows_through()
    {
        var org = Overrides(T("supplier", "Partner", "Partners") with { Active = false });
        var set = TermSet.Build("en", org.Terms, Overrides(T("supplier", "Vendor", "Vendors")).Terms, null, null);
        Assert.Equal("Vendor", set.Singular("supplier"));
    }

    [Fact]
    public void A_regional_locale_falls_back_to_its_language_and_a_spelling_variant_applies()
    {
        Assert.Equal("Organisation", TermSet.Neutral("en-AU").Singular("organisation"));
        Assert.Equal("Organization", TermSet.Neutral("en-US").Singular("organisation"));
        Assert.Equal("Organizations", TermSet.Neutral("en-US").Plural("organisation"));
        // A pack term for 'en' still applies to en-AU, and a more specific en-AU term wins inside the same layer.
        var pack = Overrides(T("site", "Location", "Locations"), T("site", "Plant", "Plants", "en-AU"));
        Assert.Equal("Location", TermSet.Build("en", null, pack.Terms, null, null).Singular("site"));
        Assert.Equal("Plant", TermSet.Build("en-AU", null, pack.Terms, null, null).Singular("site"));
        Assert.Equal("Plant", TermSet.Build("en-AU", null, pack.Terms, null, null).Singular("site"));
        Assert.Equal("Location", TermSet.Build("en-GB", null, pack.Terms, null, null).Singular("site"));
    }

    [Fact]
    public void Optional_fields_fall_through_and_a_missing_optional_key_is_safe()
    {
        var pack = Overrides(T("supplier", "Vendor", "Vendors", description: "A company we buy from.", help: "Choose the company."));
        var org = Overrides(T("supplier", "Partner", "Partners", @short: "Prtnr"));
        var set = TermSet.Build("en", org.Terms, pack.Terms, null, null);
        Assert.Equal("Prtnr", set.Short("supplier"));
        Assert.Equal("A company we buy from.", set.Description("supplier"));   // description falls through from the pack
        Assert.Equal("Choose the company.", set.Help("supplier"));
        Assert.Equal("Contract", TermSet.Neutral().Short("contract"));         // no short label: the singular is used
        Assert.Null(TermSet.Neutral().Help("contract"));
    }

    // ---- tokens ----

    [Theory]
    [InlineData("[[contract]]", "Contract")]
    [InlineData("[[contract|plural]]", "Contracts")]
    [InlineData("Open the [[contract.register|lower]]", "Open the contract register")]
    [InlineData("[[contract.register|sentence]] page", "Contract register page")]
    [InlineData("Add [[supplier|a|lower]]", "Add a counterparty")]
    [InlineData("Add [[organisation|a|lower]]", "Add an organisation")]
    [InlineData("[[contract|lower|plural]] you can see", "contracts you can see")]
    [InlineData("No [[category|plural|lower]] yet", "No categories yet")]
    [InlineData("Nothing here", "Nothing here")]
    public void Tokens_expand_with_constrained_modifiers(string input, string expected) => Assert.Equal(expected, TermTokens.ExpandNeutral(input));

    [Fact]
    public void Tokens_use_the_set_in_force_and_never_re_expand_a_label()
    {
        var set = TermSet.Build("en", Overrides(T("contract", "Agreement", "Agreements"), T("supplier", "[[contract]]", "[[contract|plural]]")).Terms, null, null, null);
        Assert.Equal("Agreements", set.Expand("[[contract|plural]]"));
        // A label that itself looks like a token is data and is output as text once; it is not expanded a second time.
        Assert.Equal("[[contract]]", set.Expand("[[supplier]]"));
    }

    [Theory]
    [InlineData("[[nope]]", true)]
    [InlineData("[[contract|bogus]]", true)]
    [InlineData("[[contract", true)]
    [InlineData("]] stray", true)]
    [InlineData("[[contract|plural]] and [[supplier|a|lower]]", false)]
    [InlineData("A plain message", false)]
    public void Unknown_tokens_modifiers_and_stray_brackets_are_reported(string text, bool hasProblems) => Assert.Equal(hasProblems, TermTokens.Problems(text).Count > 0);

    [Fact]
    public void An_unknown_token_is_left_as_written_so_nothing_executes()
    {
        Assert.Equal("[[contract|bogus]]", TermTokens.ExpandNeutral("[[contract|bogus]]"));
        Assert.Equal("[[ contract ]]", TermTokens.ExpandNeutral("[[ contract ]]"));
        Assert.Equal("{{contract}}", TermTokens.ExpandNeutral("{{contract}}"));
    }

    [Theory]
    [InlineData("agreement", "an")]
    [InlineData("Owner", "an")]
    [InlineData("contract", "a")]
    [InlineData("unit", "a")]
    [InlineData("hour", "an")]
    [InlineData("", "a")]
    public void The_article_follows_the_first_sound(string word, string article) => Assert.Equal(article, TermTokens.Article(word));

    // ---- validation ----

    [Fact]
    public void A_valid_overrides_document_validates()
    {
        var doc = Overrides(T("supplier", "Partner", "Partners", description: "Someone we work with."));
        Assert.DoesNotContain(doc.Check(), m => m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void A_complete_document_missing_a_required_key_is_refused()
    {
        var all = TermCatalogue.RequiredKeys.Where(k => k != "supplier").Select(k => T(k, "X", "Xs")).ToList();
        var doc = new TerminologyDocument(TerminologyDocument.Complete, all);
        var errors = doc.Check().Where(m => m.Severity == ValidationSeverity.Error).ToList();
        Assert.Contains(errors, m => m.Code == "term_required_missing" && m.Message.Contains("'supplier'"));
        Assert.DoesNotContain(new TerminologyDocument(TerminologyDocument.Complete, [.. all, T("supplier", "X", "Xs")]).Check(), m => m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void A_complete_document_must_declare_a_locale()
        => Assert.Contains(new TerminologyDocument(TerminologyDocument.Complete, []).Check(), m => m.Code == "term_locale_missing");

    [Fact]
    public void A_duplicate_key_in_one_locale_is_refused_but_the_same_key_in_two_locales_is_not()
    {
        var dup = Overrides(T("supplier", "A", "As"), T("supplier", "B", "Bs"));
        Assert.Contains(dup.Check(), m => m.Code == "term_duplicate");
        var fine = Overrides(T("supplier", "A", "As"), T("supplier", "B", "Bs", "fr"));
        Assert.DoesNotContain(fine.Check(), m => m.Code == "term_duplicate");
    }

    [Theory]
    [InlineData("<b>Bold</b>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("a &amp; b")]
    [InlineData("{{7*7}}")]
    [InlineData("${user}")]
    [InlineData("<%= x %>")]
    [InlineData("onclick=steal()")]
    public void Markup_script_and_template_syntax_in_a_label_is_refused(string label)
    {
        var doc = Overrides(T("supplier", label, "Suppliers"));
        Assert.Contains(doc.Check(), m => m.Severity == ValidationSeverity.Error && m.Code == "term_singular_markup");
    }

    [Fact]
    public void Excessive_lengths_and_surrounding_spaces_are_refused()
    {
        Assert.Contains(Overrides(T("supplier", new string('x', TerminologyDocument.MaxLabel + 1), "Ys")).Check(), m => m.Code == "term_singular_length");
        Assert.Contains(Overrides(T("supplier", "A", "As", @short: new string('x', TerminologyDocument.MaxShort + 1))).Check(), m => m.Code == "term_short_length");
        Assert.Contains(Overrides(T("supplier", "A", "As", description: new string('x', TerminologyDocument.MaxDescription + 1))).Check(), m => m.Code == "term_description_length");
        Assert.Contains(Overrides(T("supplier", "A", "As", help: new string('x', TerminologyDocument.MaxHelp + 1))).Check(), m => m.Code == "term_help_length");
        Assert.Contains(Overrides(T("supplier", " padded", "As")).Check(), m => m.Code == "term_singular_space");
        Assert.Contains(Overrides(T("supplier", "A\u0007B", "As")).Check(), m => m.Code == "term_singular_control");
        Assert.Contains(Overrides(T("supplier", "", "As")).Check(), m => m.Code == "term_singular_missing");
    }

    [Theory]
    [InlineData("Supplier")]
    [InlineData("supplier..x")]
    [InlineData("1supplier")]
    [InlineData("supplier-x")]
    [InlineData("a.b.c.d.e.f.g")]
    public void A_key_must_be_dotted_lower_case_words(string key)
        => Assert.Contains(Overrides(T(key, "A", "As")).Check(), m => m.Code == "term_key");

    [Fact]
    public void An_unknown_key_is_a_warning_not_an_error_and_a_bad_locale_is_an_error()
    {
        Assert.Contains(Overrides(T("some.future.key", "A", "As")).Check(), m => m.Severity == ValidationSeverity.Warning && m.Code == "term_unknown_key");
        Assert.Contains(Overrides(T("supplier", "A", "As", "English")).Check(), m => m.Severity == ValidationSeverity.Error && m.Code == "term_locale");
    }

    [Fact]
    public void A_document_round_trips_and_keeps_pack_provenance()
    {
        var doc = Overrides(T("supplier", "Partner", "Partners", @short: "Ptr", description: "d", help: "h")).WithSource("some-pack", "1.2.3", TerminologyDocument.Overrides);
        var back = TerminologyDocument.Parse(doc.ToJson());
        Assert.Equal(doc.Terms[0], back.Terms[0]);
        Assert.Equal("some-pack", back.Terms[0].Source!.PackKey);
        Assert.Equal(doc.ToJson(), back.ToJson());
    }

    [Fact]
    public void Not_json_or_not_an_object_is_refused_with_a_message()
    {
        Assert.Throws<Ecp.SharedKernel.DomainException>(() => TerminologyDocument.Parse("not json"));
        Assert.Throws<Ecp.SharedKernel.DomainException>(() => TerminologyDocument.Parse("[]"));
    }

    // ---- the shipped packs ----

    [Fact]
    public void The_blank_pack_terminology_is_complete_valid_and_neutral()
    {
        var blank = Pack("blank");
        Assert.DoesNotContain(blank.Check(), m => m.Severity == ValidationSeverity.Error);
        var set = TermSet.Build("en", null, blank.Terms, null, null);
        foreach (var d in TermCatalogue.All.Where(d => d.Required)) { Assert.Equal(d.Singular, set.Singular(d.Key)); Assert.Equal(d.Plural, set.Plural(d.Key)); }
        Assert.DoesNotMatch("(?i)jbs|procurement|group head|medius|qlik|docusign|supplier", PackDoc("blank").Replace("\"key\": \"supplier\"", ""));
    }

    [Fact]
    public void The_jbs_pack_terminology_is_complete_valid_and_carries_the_procurement_wording_only_inside_the_pack()
    {
        var jbs = Pack("jbs-procurement");
        Assert.DoesNotContain(jbs.Check(), m => m.Severity == ValidationSeverity.Error);
        var set = TermSet.Build("en", null, jbs.Terms, null, null);
        Assert.Equal("Procurement Administrator", set.Singular("contract.administrator"));
        Assert.Equal("Category Owner", set.Singular("contract.owner"));
        Assert.Equal("Legal Review", set.Singular("legal.review"));
        Assert.Equal("Group Head of Procurement", set.Singular("primary.approver"));
        Assert.Equal("Supplier", set.Singular("supplier"));
        Assert.Equal("Procurement Contract Register", set.Singular("contract.register"));
        Assert.Equal(TermLayer.Pack, set.LayerOf("contract.administrator"));
        // The platform's own layer stays neutral whatever pack is in force.
        Assert.Equal("Contract Administrator", TermSet.Neutral().Singular("contract.administrator"));
    }

    [Fact]
    public void Both_shipped_terminology_documents_use_only_known_keys()
    {
        foreach (var pack in new[] { "blank", "jbs-procurement" })
            Assert.All(Pack(pack).Terms, t => Assert.True(TermCatalogue.IsKnown(t.Key), $"{pack}: {t.Key}"));
    }
}

/// <summary>
/// A+ follow-ups (ADR-0029): approver-group display names and role visibility live in the terminology document next to the labels. The group key and the role code stay the identifiers; names and visibility are presentation.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GroupNamesAndRoleVisibilityTests
{
    private static GroupDisplayName G(string key, string name, string locale = "en", string? description = null) => new(key, locale, name, description);
    private static TerminologyDocument Doc(IReadOnlyList<GroupDisplayName>? groups = null, IReadOnlyList<RoleOffer>? roles = null) => new(TerminologyDocument.Overrides, [], groups, roles);
    private static string[] Errors(TerminologyDocument d) => d.Check().Where(m => m.Severity == ValidationSeverity.Error).Select(m => m.Code).ToArray();

    [Fact]
    public void A_document_without_the_new_sections_serialises_exactly_as_before_so_pack_hashes_do_not_move()
    {
        var json = new TerminologyDocument(TerminologyDocument.Complete, [new TermEntry("contract", "en", "Contract", "Contracts")]).ToJson();
        Assert.DoesNotContain("groups", json);
        Assert.DoesNotContain("roles", json);
        foreach (var pack in new[] { "blank", "jbs-procurement" })
        {
            var raw = File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", pack, "terminology", "terminology.json"));
            Assert.Empty(Errors(TerminologyDocument.Parse(raw)));
        }
    }

    [Fact]
    public void Groups_and_role_choices_round_trip_and_serialise_deterministically()
    {
        var doc = Doc([G("Zeta", "Last"), G("Approvers", "Registration Approvers", description: "Who approves.")], [new RoleOffer("Legal", false), new RoleOffer("Viewer", true)]);
        var json = doc.ToJson();
        var back = TerminologyDocument.Parse(json);
        Assert.Equal(["Approvers", "Zeta"], back.Groups!.Select(g => g.Key));
        Assert.Equal("Who approves.", back.Groups![0].Description);
        Assert.Equal([("Legal", false), ("Viewer", true)], back.Roles!.Select(r => (r.Code, r.Offered)));
        Assert.Equal(json, back.ToJson());
    }

    [Theory]
    [InlineData("", "Name", "group_key")]
    [InlineData("bad key", "Name", "group_key")]
    [InlineData("-leading", "Name", "group_key")]
    [InlineData("Key", "", "group_name_missing")]
    [InlineData("Key", "<b>bold</b>", "group_name_markup")]
    [InlineData("Key", "{{ template }}", "group_name_markup")]
    [InlineData("Key", " padded ", "group_name_space")]
    public void A_group_name_must_be_plain_text_for_a_well_formed_key(string key, string name, string code)
        => Assert.Contains(code, Errors(Doc([G(key, name)])));

    [Fact]
    public void Group_limits_and_duplicates_are_refused_and_a_valid_group_passes()
    {
        Assert.Empty(Errors(Doc([G("GroupHeadProcurement", "Group Head of Procurement", description: "Approves registration."), G("GroupHeadProcurement", "Chef du groupe", "fr")])));
        Assert.Contains("group_duplicate", Errors(Doc([G("A", "One"), G("A", "Two")])));
        Assert.Contains("group_name_length", Errors(Doc([G("A", new string('x', 81))])));
        Assert.Contains("group_key", Errors(Doc([G(new string('k', 101), "Name")])));
        Assert.Contains("group_locale", Errors(Doc([G("A", "Name", "not a locale")])));
        Assert.Contains("group_count", Errors(Doc(Enumerable.Range(0, TerminologyDocument.MaxGroups + 1).Select(i => G("K" + i, "N")).ToList())));
    }

    [Fact]
    public void Role_choices_are_validated_against_the_catalogue_and_the_administrative_roles_cannot_be_hidden()
    {
        Assert.Empty(Errors(Doc(roles: [new RoleOffer("Legal", false), new RoleOffer("DivisionSiteAdministrator", false), new RoleOffer("OrganisationAdministrator", true)])));
        Assert.Contains("role_unknown", Errors(Doc(roles: [new RoleOffer("ContractAdministrator", false)])));      // a label is not a role code
        Assert.Contains("role_duplicate", Errors(Doc(roles: [new RoleOffer("Legal", false), new RoleOffer("Legal", true)])));
        foreach (var role in TermSet.RolesNeverHidden) Assert.Contains("role_not_hideable", Errors(Doc(roles: [new RoleOffer(role, false)])));
        Assert.Equal(["SystemAdministrator", "TenantAdministrator", "OrganisationAdministrator"], TermSet.RolesNeverHidden);
    }

    [Fact]
    public void An_unnamed_group_shows_its_key_and_a_named_one_shows_its_name_the_organisation_over_the_pack_and_the_locale_chain_applies()
    {
        var pack = new TermExtras([G("Approvers", "Pack name"), G("Approvers", "Nom du paquet", "fr"), G("OnlyPack", "Only the pack")]);
        var org = new TermExtras([G("Approvers", "Org name")]);
        var set = TermSet.Build("en-AU", null, null, null, null, org, pack);
        Assert.Equal("Org name", set.Group("Approvers"));
        Assert.Equal("Only the pack", set.Group("OnlyPack"));
        Assert.Equal("Unnamed", set.Group("Unnamed"));              // the key itself: a diagnostic, never an error
        Assert.False(set.HasGroupName("Unnamed"));
        Assert.Equal("Pack name", TermSet.Build("en", null, null, null, null, null, pack).Group("Approvers"));
        Assert.Equal("Nom du paquet", TermSet.Build("fr", null, null, null, null, null, pack).Group("Approvers"));
        Assert.Equal("Approvers", TermSet.Neutral().Group("Approvers"));
    }

    [Fact]
    public void Role_visibility_the_organisations_choice_wins_over_the_packs_and_nothing_is_hidden_by_default()
    {
        Assert.Empty(TermSet.Neutral().HiddenRoles);
        Assert.True(TermSet.Neutral().IsRoleOffered("Legal"));
        var pack = new TermExtras(null, [new RoleOffer("Legal", false), new RoleOffer("Auditor", false)]);
        var packOnly = TermSet.Build("en", null, null, null, null, null, pack);
        Assert.False(packOnly.IsRoleOffered("Legal"));
        Assert.True(packOnly.IsRoleOffered("Viewer"));
        var org = new TermExtras(null, [new RoleOffer("Legal", true), new RoleOffer("Viewer", false)]);
        var both = TermSet.Build("en", null, null, null, null, org, pack);
        Assert.True(both.IsRoleOffered("Legal"));                    // the organisation brings it back
        Assert.False(both.IsRoleOffered("Auditor"));                 // the pack's choice stands where the organisation says nothing
        Assert.False(both.IsRoleOffered("Viewer"));
    }

    [Fact]
    public void The_resolver_ignores_an_attempt_to_hide_an_administrative_role_even_if_a_document_somehow_carries_one()
    {
        var set = TermSet.Build("en", null, null, null, null, new TermExtras(null, TermSet.RolesNeverHidden.Select(r => new RoleOffer(r, false)).ToList()));
        Assert.Empty(set.HiddenRoles);
        foreach (var role in TermSet.RolesNeverHidden) Assert.True(set.IsRoleOffered(role));
    }

    [Fact]
    public void Hiding_a_role_or_naming_a_group_changes_no_permission()
    {
        var before = PermissionMatrix.Instance.Of(RoleCodes.Legal, "legal.clear");
        _ = TermSet.Build("en", null, null, null, null, new TermExtras([G("Approvers", "Whatever")], [new RoleOffer("Legal", false)]));
        Assert.Equal(before, PermissionMatrix.Instance.Of(RoleCodes.Legal, "legal.clear"));
        Assert.NotEqual(Grant.None, before);
    }

    [Fact]
    public void The_shipped_packs_name_their_approver_group_and_hide_no_role()
    {
        var jbs = TerminologyDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", "jbs-procurement", "terminology", "terminology.json")));
        var blank = TerminologyDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", "blank", "terminology", "terminology.json")));
        Assert.Equal("Group Head of Procurement", TermSet.Build("en", null, null, null, null, null, jbs.Extras).Group("GroupHeadProcurement"));
        Assert.Equal("Approval Authorities", TermSet.Build("en", null, null, null, null, null, blank.Extras).Group("Approvers"));
        Assert.Empty(TermSet.Build("en", null, null, null, null, null, jbs.Extras).HiddenRoles);
        Assert.Empty(TermSet.Build("en", null, null, null, null, null, blank.Extras).HiddenRoles);
        // The group key stays out of the other pack: no leak of one business's group into the neutral pack.
        Assert.False(TermSet.Build("en", null, null, null, null, null, blank.Extras).HasGroupName("GroupHeadProcurement"));
    }

    // ---- contract field labels (G1a, G1b): every label the core fields show is a terminology key ----

    [Fact]
    public void Every_core_field_label_and_group_is_made_of_catalogue_tokens_and_a_few_structural_words()
    {
        var structural = new[] { "Title", "Type", "Status", "Required", "Unit" };
        foreach (var f in Ecp.Metadata.CoreFields.All)
            foreach (var text in new[] { f.Label, f.Group })
            {
                var tokens = System.Text.RegularExpressions.Regex.Matches(text, @"\[\[([a-z.]+)(?:\|[a-z|]+)?\]\]").Select(m => m.Groups[1].Value).ToList();
                foreach (var t in tokens) Assert.True(TermCatalogue.IsKnown(t), $"{f.Key}: '{t}' is not a catalogue key.");
                var rest = System.Text.RegularExpressions.Regex.Replace(text, @"\[\[[^\]]*\]\]", " ");
                foreach (var word in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    Assert.True(structural.Contains(word), $"{f.Key}: the literal word '{word}' in '{text}' is not a terminology key.");
            }
    }

    [Fact]
    public void The_field_label_keys_are_optional_and_the_country_and_ERP_wording_is_neutral()
    {
        var neutral = TermSet.Build("en", null, null, null, null, null);
        Assert.Equal("Tax Identifier", neutral.Singular("supplier.tax.id"));
        Assert.Equal("Vendor Number", neutral.Singular("supplier.number"));
        Assert.Equal("ERP Reference", neutral.Singular("erp.reference"));
        Assert.Equal(32, TermCatalogue.RequiredKeys.Count);
        foreach (var k in new[] { "supplier.tax.id", "supplier.number", "erp.reference", "field.end.date", "field.group.dates" }) Assert.DoesNotContain(k, TermCatalogue.RequiredKeys);
        // Nothing neutral names a country, a tax authority or an ERP product.
        foreach (var d in TermCatalogue.All) Assert.DoesNotMatch(@"\b(ABN|SAP|GST|VAT|EIN)\b", d.Singular + " " + d.Plural);
    }
}
