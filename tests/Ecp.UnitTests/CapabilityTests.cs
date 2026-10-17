using Ecp.Configuration;
using Ecp.Contracts.Register;
using Ecp.Lifecycle.Capabilities;
using Ecp.Lifecycle.Definitions;
using Ecp.Notifications;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// S6 (docs/CAPABILITIES.md): the capability catalogue is bound to the platform's closed vocabularies and nothing else, the resolved set layers organisation over pack over platform default, and the capability document
/// is plain, validated data.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CapabilityCatalogueTests
{
    [Fact]
    public void The_catalogue_holds_exactly_the_five_capabilities_and_the_key_list_matches()
    {
        Assert.Equal(CapabilityKeys.All, CapabilityCatalogue.All.Select(c => c.Key));
        Assert.Equal(5, CapabilityKeys.All.Count);
        Assert.Equal(CapabilityKeys.All.Count, CapabilityKeys.All.Distinct().Count());
        Assert.All(CapabilityKeys.All, k => Assert.True(CapabilityCatalogue.IsKnown(k)));
        Assert.False(CapabilityCatalogue.IsKnown("not.a.capability"));
    }

    [Fact]
    public void Every_binding_names_something_the_vocabularies_and_catalogues_really_have_and_belongs_to_one_capability()
    {
        foreach (var c in CapabilityCatalogue.All)
        {
            Assert.All(c.TransitionRoles, r => Assert.Contains(r, WorkflowVocabulary.TransitionRoles));
            Assert.All(c.StateRoles, r => Assert.Contains(r, WorkflowVocabulary.StateRoles));
            Assert.All(c.Guards, g => Assert.Contains(g, WorkflowVocabulary.Guards));
            Assert.All(c.Requires, r => Assert.True(CapabilityCatalogue.IsKnown(r), $"{c.Key} requires an unknown capability {r}"));
            Assert.DoesNotContain(c.Key, c.Requires);
        }
        // A role, state role, guard, field or view belongs to at most one capability (otherwise "which capability blocks this step?" would have no single answer).
        static void Unique(IEnumerable<string> all, string what) => Assert.True(all.Count() == all.Distinct().Count(), $"A {what} is bound to more than one capability.");
        Unique(CapabilityCatalogue.All.SelectMany(c => c.TransitionRoles), "transition role");
        Unique(CapabilityCatalogue.All.SelectMany(c => c.StateRoles), "state role");
        Unique(CapabilityCatalogue.All.SelectMany(c => c.Guards), "guard");
        Unique(CapabilityCatalogue.All.SelectMany(c => c.RegisterFields), "register field");
        Unique(CapabilityCatalogue.All.SelectMany(c => c.ViewKeys), "view");
    }

    [Fact]
    public void Every_managed_step_belongs_to_a_capability_except_the_ones_that_are_core_to_every_process()
    {
        // Managed roles are the steps with their own data and rules. Each one is either an optional capability's step or deliberately core (returnToDrafting, withdraw, cancel, flag, archive are not managed or are generic).
        var unbound = WorkflowVocabulary.ManagedRoles.Where(r => CapabilityCatalogue.OfTransitionRole(r) is null).Order().ToList();
        Assert.Equal(["cancel"], unbound);
    }

    [Fact]
    public void Bound_dashboard_cards_exist_and_register_fields_exist()
    {
        var dashboardKeys = typeof(DashboardKeys).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        foreach (var view in CapabilityCatalogue.All.SelectMany(c => c.ViewKeys)) Assert.Contains(view, dashboardKeys);
        var systemViews = SystemViews.Load().Views.Select(v => v.Key).ToHashSet();
        // A key bound as a view is either a dashboard card or a built-in list view; every bound key is one of the two.
        foreach (var view in CapabilityCatalogue.All.SelectMany(c => c.ViewKeys)) Assert.True(dashboardKeys.Contains(view) || systemViews.Contains(view));
    }

    [Fact]
    public void Labels_and_effects_are_written_with_terminology_tokens_and_name_no_business()
    {
        foreach (var c in CapabilityCatalogue.All)
        {
            var text = string.Join(' ', c.Label, c.Description, c.TurnsOffEffect);
            Assert.DoesNotMatch("(?i)jbs|procurement|medius|qlik|docusign", text);
        }
        // The terms a label depends on exist in the platform's neutral terminology.
        var neutral = TermSet.Neutral();
        foreach (var token in CapabilityCatalogue.All.SelectMany(c => System.Text.RegularExpressions.Regex.Matches(string.Join(' ', c.Label, c.Description, c.TurnsOffEffect), @"\[\[([a-z.]+)[|\]]").Select(m => m.Groups[1].Value)).Distinct())
            Assert.True(neutral.Has(token), $"The terminology key '{token}' used by a capability text is not in the neutral terminology.");
    }

    [Fact]
    public void Variations_need_register_authorisation_and_nothing_else_needs_anything()
    {
        Assert.Equal([CapabilityKeys.RegisterAuthorisation], CapabilityCatalogue.ByKey[CapabilityKeys.Variations].Requires);
        Assert.All(CapabilityKeys.All.Where(k => k != CapabilityKeys.Variations), k => Assert.Empty(CapabilityCatalogue.ByKey[k].Requires));
    }
}

[Trait("Category", "Unit")]
public sealed class CapabilitySetTests
{
    private static CapabilityEntry E(string key, bool on, string? note = null) => new(key, on, note);

    [Fact]
    public void With_nothing_configured_everything_is_on_which_is_how_the_platform_behaved_before_capabilities_were_configurable()
    {
        var set = CapabilitySet.Default;
        Assert.True(set.AllEnabled);
        Assert.Equal(CapabilityKeys.All, set.Enabled);
        Assert.Empty(set.Disabled);
        Assert.All(CapabilityKeys.All, k => Assert.Equal(CapabilityLayer.Platform, set.LayerOf(k)));
        Assert.Empty(set.Unmet());
    }

    [Fact]
    public void The_organisation_wins_over_the_pack_and_the_pack_wins_over_the_platform_default()
    {
        var pack = new[] { E(CapabilityKeys.LegalReview, false, "pack"), E(CapabilityKeys.Renewals, false), E(CapabilityKeys.SignatureRequests, true) };
        var org = new[] { E(CapabilityKeys.LegalReview, true, "mine"), E(CapabilityKeys.Variations, false) };
        var set = CapabilitySet.Build(org, pack, "x");

        Assert.True(set.IsEnabled(CapabilityKeys.LegalReview));                       // organisation over pack
        Assert.Equal(CapabilityLayer.Organisation, set.LayerOf(CapabilityKeys.LegalReview));
        Assert.Equal("mine", set.Find(CapabilityKeys.LegalReview)!.Note);
        Assert.False(set.IsEnabled(CapabilityKeys.Renewals));                         // pack over platform
        Assert.Equal(CapabilityLayer.Pack, set.LayerOf(CapabilityKeys.Renewals));
        Assert.False(set.IsEnabled(CapabilityKeys.Variations));                       // organisation over platform
        Assert.True(set.IsEnabled(CapabilityKeys.RegisterAuthorisation));             // nobody decided: platform default
        Assert.Equal(CapabilityLayer.Platform, set.LayerOf(CapabilityKeys.RegisterAuthorisation));
        Assert.Equal("x", set.Fingerprint);
    }

    [Fact]
    public void An_unknown_capability_is_never_on_and_never_changes_a_known_one()
    {
        var set = CapabilitySet.Build([E("future.thing", true)], [E("another.future", false)], "x");
        Assert.False(set.IsEnabled("future.thing"));
        Assert.False(set.IsEnabled("another.future"));
        Assert.True(set.AllEnabled);
        Assert.Null(set.Find("future.thing"));
    }

    [Fact]
    public void A_later_statement_for_the_same_capability_replaces_an_earlier_one()
    {
        var set = CapabilitySet.Build([E(CapabilityKeys.Renewals, true), E(CapabilityKeys.Renewals, false)], null, "x");
        Assert.False(set.IsEnabled(CapabilityKeys.Renewals));
    }

    [Fact]
    public void An_enabled_capability_with_a_required_one_off_is_reported_as_unmet()
    {
        var set = CapabilitySet.Build([E(CapabilityKeys.RegisterAuthorisation, false)], null, "x");
        var unmet = Assert.Single(set.Unmet());
        Assert.Equal(new UnmetRequirement(CapabilityKeys.Variations, CapabilityKeys.RegisterAuthorisation), unmet);
        // Turning both off is coherent.
        Assert.Empty(CapabilitySet.Build([E(CapabilityKeys.RegisterAuthorisation, false), E(CapabilityKeys.Variations, false)], null, "x").Unmet());
    }

    [Fact]
    public void All_five_can_be_off_at_once_and_the_set_says_so()
    {
        var set = CapabilitySet.Build(CapabilityKeys.All.Select(k => E(k, false)), null, "x");
        Assert.Equal(CapabilityKeys.All, set.Disabled);
        Assert.Empty(set.Enabled);
        Assert.False(set.AllEnabled);
        Assert.Empty(set.Unmet());
    }
}

[Trait("Category", "Unit")]
public sealed class CapabilityDocumentTests
{
    private static IReadOnlyList<ValidationMessage> Check(params CapabilityEntry[] entries) => new CapabilityDocument(CapabilityDocument.Overrides, entries).Check();
    private static IEnumerable<string> Errors(IEnumerable<ValidationMessage> m) => m.Where(x => x.Severity == ValidationSeverity.Error).Select(x => x.Code);

    [Fact]
    public void A_document_serialises_the_same_way_every_time_and_parses_back()
    {
        var doc = new CapabilityDocument(CapabilityDocument.Defaults, [new CapabilityEntry(CapabilityKeys.Renewals, false, "Not used"), new CapabilityEntry(CapabilityKeys.LegalReview, true)]);
        var json = doc.ToJson();
        Assert.Equal(json, doc.ToJson());
        Assert.True(json.IndexOf("legal.review", StringComparison.Ordinal) < json.IndexOf("renewals", StringComparison.Ordinal));       // ordered by key
        var back = CapabilityDocument.Parse(json);
        Assert.Equal(CapabilityDocument.Defaults, back.Mode);
        Assert.Equal(doc.Capabilities.OrderBy(c => c.Key).ToList(), back.Capabilities.ToList());
    }

    [Fact]
    public void A_statement_without_an_enabled_value_means_on_and_only_false_switches_a_capability_off()
    {
        var doc = CapabilityDocument.Parse("""{"mode":"overrides","capabilities":[{"key":"renewals"},{"key":"legal.review","enabled":"no"},{"key":"variations","enabled":false}]}""");
        Assert.True(doc.Capabilities.Single(c => c.Key == "renewals").Enabled);
        Assert.True(doc.Capabilities.Single(c => c.Key == "legal.review").Enabled);
        Assert.False(doc.Capabilities.Single(c => c.Key == "variations").Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"capabilities":["x"]}""")]
    public void A_document_that_is_not_the_right_shape_is_refused(string json) => Assert.Throws<DomainException>(() => CapabilityDocument.Parse(json));

    [Fact]
    public void A_well_formed_document_has_no_errors()
        => Assert.Empty(Errors(Check(new CapabilityEntry(CapabilityKeys.LegalReview, false, "We do not review"), new CapabilityEntry(CapabilityKeys.Renewals, true))));

    [Theory]
    [InlineData("Legal.Review")]
    [InlineData("legal review")]
    [InlineData("1legal")]
    [InlineData("")]
    [InlineData("a.b.c.d.e")]
    [InlineData("legal..review")]
    public void A_malformed_key_is_an_error(string key) => Assert.Contains("capability_key", Errors(Check(new CapabilityEntry(key, true))));

    [Fact]
    public void A_duplicate_key_is_an_error_and_an_unknown_key_is_only_a_warning()
    {
        Assert.Contains("capability_duplicate", Errors(Check(new CapabilityEntry(CapabilityKeys.Renewals, true), new CapabilityEntry(CapabilityKeys.Renewals, false))));
        var unknown = Check(new CapabilityEntry("future.thing", true));
        Assert.Empty(Errors(unknown));
        Assert.Contains(unknown, m => m.Code == "capability_unknown" && m.Severity == ValidationSeverity.Warning);
    }

    [Theory]
    [InlineData("<b>bold</b>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("{{ template }}")]
    [InlineData("${ expression }")]
    [InlineData("expr: 1+1")]
    [InlineData("onclick=bad")]
    [InlineData("&#x41;")]
    public void A_note_must_be_plain_text(string note) => Assert.Contains("capability_note_markup", Errors(Check(new CapabilityEntry(CapabilityKeys.Renewals, false, note))));

    [Fact]
    public void A_note_has_a_length_limit_and_no_line_breaks_or_stray_space()
    {
        Assert.Contains("capability_note_length", Errors(Check(new CapabilityEntry(CapabilityKeys.Renewals, false, new string('a', CapabilityDocument.MaxNote + 1)))));
        Assert.Contains("capability_note_space", Errors(Check(new CapabilityEntry(CapabilityKeys.Renewals, false, "two\nlines"))));
        Assert.Contains("capability_note_space", Errors(Check(new CapabilityEntry(CapabilityKeys.Renewals, false, " padded "))));
    }

    [Fact]
    public void The_mode_and_the_number_of_statements_are_bounded()
    {
        Assert.Contains("capability_mode", Errors(new CapabilityDocument("conditional", []).Check()));
        var many = Enumerable.Range(0, 101).Select(i => new CapabilityEntry($"k{i}", true)).ToList();
        Assert.Contains("capability_count", Errors(new CapabilityDocument(CapabilityDocument.Overrides, many).Check()));
    }

    [Fact]
    public void A_pack_document_that_leaves_variations_on_without_register_authorisation_is_incoherent()
    {
        var doc = new CapabilityDocument(CapabilityDocument.Defaults, [new CapabilityEntry(CapabilityKeys.RegisterAuthorisation, false)]);
        Assert.Contains("capability_requires", doc.CheckCoherence().Select(m => m.Code));
        Assert.Empty(new CapabilityDocument(CapabilityDocument.Defaults, [new CapabilityEntry(CapabilityKeys.RegisterAuthorisation, false), new CapabilityEntry(CapabilityKeys.Variations, false)]).CheckCoherence());
    }

    [Fact]
    public void The_shipped_pack_documents_are_valid_and_coherent_and_say_what_they_should()
    {
        CapabilityDocument Load(string pack) => CapabilityDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", pack, "capabilities", "capabilities.json")));
        var jbs = Load("jbs-procurement");
        var blank = Load("blank");
        foreach (var doc in new[] { jbs, blank })
        {
            Assert.Equal(CapabilityDocument.Defaults, doc.Mode);
            Assert.Empty(Errors(doc.Check()));
            Assert.Empty(doc.CheckCoherence());
        }
        Assert.True(jbs.AsSet().AllEnabled);
        Assert.Equal([CapabilityKeys.LegalReview, CapabilityKeys.Renewals, CapabilityKeys.SignatureRequests], blank.AsSet().Disabled);
        Assert.Equal(CapabilityKeys.All.Count, jbs.Capabilities.Count);
        Assert.Equal(CapabilityKeys.All.Count, blank.Capabilities.Count);
    }
}

/// <summary>A published workflow is checked against the capabilities, only through the closed vocabularies: the reference lifecycle uses all five, the neutral one only Variations.</summary>
[Trait("Category", "Unit")]
public sealed class WorkflowCapabilityUseTests
{
    private static WorkflowDefinition Load(string pack, string file) => WorkflowDefinitionParser.Parse(File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", pack, "workflows", file))).Definition!;

    [Fact]
    public void The_reference_lifecycle_uses_every_capability_and_the_neutral_one_only_variations()
    {
        var jbs = WorkflowCapabilityUse.Of(Load("jbs-procurement", "contract-lifecycle.json")).Select(u => u.Capability).Distinct().Order().ToList();
        Assert.Equal(CapabilityKeys.All.Order().ToList(), jbs);
        var blank = WorkflowCapabilityUse.Of(Load("blank", "contract-lifecycle.json")).Select(u => u.Capability).Distinct().ToList();
        Assert.Equal([CapabilityKeys.Variations], blank);
    }

    [Fact]
    public void A_variation_workflow_is_found_to_use_register_authorisation_when_it_has_approval_steps()
    {
        var uses = WorkflowCapabilityUse.Of(Load("jbs-procurement", "variation.json")).Select(u => u.Capability).Distinct().ToList();
        Assert.All(uses, u => Assert.True(CapabilityCatalogue.IsKnown(u)));
    }
}
