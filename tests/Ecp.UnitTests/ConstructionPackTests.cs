using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.Approvals.Definitions;
using Ecp.Approvals.Resolution;
using Ecp.Configuration;
using Ecp.Contracts.Register;
using Ecp.Identity.Domain;
using Ecp.Lifecycle.Definitions;
using Ecp.Identity.Access;
using Ecp.Metadata;
using Ecp.Notifications.Policy;
using Ecp.Packs;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// S9 (docs/SECOND_BUSINESS_PROOF.md, SBP-AC-02, 03, 17): the shipped pack construction-subcontracts for the synthetic business Harborline Construction. Every document goes through the validator of the module that owns its kind,
/// the package builds the same twice, signs and verifies with an ephemeral key, and no document names the reference business, an Australian identifier or a tax authority. The pack is data under templates/packs and nothing else.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConstructionPackTests
{
    private const string Key = "construction-subcontracts";
    private static string Dir => Path.Combine(RepoRoot.Find(), "templates", "packs", Key);
    private static string Read(string relative) => File.ReadAllText(Path.Combine(Dir, relative));
    private static readonly string[] Levels = ["Public", "Internal", "Confidential", "Restricted"];
    private static readonly ClassificationScale Scale = new(Levels, "Internal", []);

    // ---- the package ----

    [Fact]
    public void The_pack_builds_to_a_valid_package_with_every_packable_kind_and_the_same_bytes_twice()
    {
        var a = PackBuilder.Build(Dir); var b = PackBuilder.Build(Dir);
        Assert.True(a.Succeeded, string.Join("; ", a.Issues.Select(i => i.Code + " " + i.Message)));
        Assert.True(a.Package!.AsSpan().SequenceEqual(b.Package!));
        var m = a.Opened!.Manifest!;
        Assert.Equal((Key, "1.1.0", true), (m.PackKey, m.Version, m.Synthetic));
        Assert.Equal(PackKinds.Packable.Order(StringComparer.Ordinal).ToArray(), m.Content.Select(c => c.Kind).Where(k => k != "SolutionPack").Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(m.Content, c => c.Kind == "ClassificationPolicy" && c.Scope == PackScopes.Tenant);
        Assert.Contains(m.Content, c => c.Kind == "Branding" && c.Scope == PackScopes.Tenant);
        Assert.Equal(PackReader.Open(a.Package!).ContentDigest, PackReader.Open(b.Package!).ContentDigest);
    }

    [Fact]
    public async Task The_pack_signs_with_the_existing_tooling_and_only_a_trusted_signature_is_trusted_in_production()
    {
        var built = PackBuilder.Build(Dir);
        using var key = new TestSigningKey();
        var signed = PackBuilder.Sign(built.Opened!, key.Signer, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        async Task<TrustResult> Evaluate(byte[] bytes, IEnumerable<TrustedKey> keys, bool production)
        {
            var opened = PackReader.Open(bytes);
            return await new PackTrustPolicy(new StaticKeyResolver(keys)).EvaluateAsync(opened.Manifest!, opened.CanonicalManifest, opened.SignatureFile, new TrustContext(production, false, new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc)), PackLimits.Default);
        }
        Assert.Equal(TrustStatus.Trusted, (await Evaluate(signed, [key.AsTrusted()], production: true)).Status);
        Assert.False((await Evaluate(built.Package!, [key.AsTrusted()], production: true)).IsTrusted);                       // unsigned: refused in Production
        using var other = new TestSigningKey("other-key");
        Assert.False((await Evaluate(signed, [other.AsTrusted()], production: true)).IsTrusted);                            // wrong key
        // Changing one byte of one document is caught by the hashes before trust is asked.
        var entries = PackTestKit.Entries(signed);
        var path = built.Opened!.Manifest!.Content.Single(c => c.Kind == "Branding").Path;
        entries[path] = PackTestKit.Utf8(System.Text.Encoding.UTF8.GetString(entries[path]).Replace("#0b5d4b", "#0b5d4c"));
        Assert.False(PackReader.Open(PackTestKit.Rezip(entries)).IsStructurallyValid);
    }

    // ---- the documents ----

    private static IReadOnlyList<ValidationMessage> Workflow(string relative, string subject, out WorkflowDefinition def)
    {
        var (d, parse) = WorkflowDefinitionParser.Parse(Read(relative));
        Assert.NotNull(d); Assert.Empty(parse);
        def = d!;
        return WorkflowDefinitionParser.Validate(d!, subject, PermissionMatrix.Instance);
    }

    [Fact]
    public void The_lifecycle_and_the_change_order_workflow_are_valid_and_use_each_state_role_once()
    {
        var messages = Workflow("workflows/contract-lifecycle.json", "Contract", out var life);
        Assert.Empty(PackFiles.Errors(messages));
        Assert.All(messages.Where(m => m.Code == "role_missing"), m => Assert.Equal(ValidationSeverity.Warning, m.Severity));
        Assert.Equal(11, life.States.Count);
        Assert.Equal("Draft", life.Initial.Code);
        Assert.All(life.States.Where(s => s.Role is not null).GroupBy(s => s.Role), g => Assert.Single(g));
        Assert.True(life.State("Active")!.Locked);
        Assert.Equal(3, life.States.Count(s => s.Terminal));
        // Practical completion is a fact recorded by a change order, never a state of its own (owner decision D1).
        Assert.DoesNotContain(life.States, s => s.Code.Contains("Defects", StringComparison.OrdinalIgnoreCase) || s.Code.Contains("Practical", StringComparison.OrdinalIgnoreCase));
        // The one state without a role is an unlocked step before activation, where no reminder is due.
        Assert.Equal(["InternalReview"], life.States.Where(s => s.Role is null).Select(s => s.Code).ToArray());
        Assert.False(life.State("InternalReview")!.Locked);

        Assert.Empty(PackFiles.Errors(Workflow("workflows/variation.json", "Variation", out var change)));
        Assert.Equal("applied", change.State(Assert.Single(change.WithRole("approve")).To)!.Role);
        Assert.Equal("None", change.Settings.Variation.LegalReview);
        Assert.Equal("subcontract-activation", life.Settings.Approval.Matrix);
        Assert.Equal("change-order-approval", change.Settings.Approval.Matrix);
    }

    [Fact]
    public void The_lifecycle_has_no_formal_review_no_renewal_no_disposition_and_the_capabilities_agree()
    {
        _ = Workflow("workflows/contract-lifecycle.json", "Contract", out var life);
        _ = Workflow("workflows/variation.json", "Variation", out var change);
        var doc = CapabilityDocument.Parse(Read("capabilities/capabilities.json"));
        Assert.Equal(CapabilityDocument.Defaults, doc.Mode);
        Assert.Empty(PackFiles.Errors(doc.Check()));
        Assert.Empty(doc.CheckCoherence());
        var off = doc.Capabilities.Where(e => !e.Enabled).Select(e => e.Key).ToHashSet();
        Assert.Equal([CapabilityKeys.LegalReview, CapabilityKeys.Renewals], off.Order().ToArray());
        foreach (var def in new[] { life, change })
        {
            Assert.DoesNotContain(def.States, s => CapabilityCatalogue.OfStateRole(s.Role) is { } c && off.Contains(c));
            Assert.DoesNotContain(def.Transitions, t => CapabilityCatalogue.OfTransitionRole(t.Role) is { } c && off.Contains(c));
            Assert.DoesNotContain(def.Transitions, t => t.Guards.Any(g => CapabilityCatalogue.OfGuard(g) is { } c && off.Contains(c)));
        }
        // The capabilities the pack turns on are the ones the lifecycle uses.
        Assert.NotEmpty(life.WithRole("sendSignature")); Assert.NotEmpty(life.WithRole("submitRegister")); Assert.NotEmpty(life.WithRole("openVariation"));
        Assert.Empty(life.WithRole("startRenewal"));
    }

    [Fact]
    public void The_notification_policy_is_valid_states_its_content_levels_and_uses_neither_formal_review_nor_extension_events()
    {
        var (policy, parse) = NotificationPolicyParser.Parse(Read("notifications/notification-policy.json"));
        Assert.NotNull(policy); Assert.Empty(parse);
        Assert.Empty(NotificationPolicyParser.Validate(policy!, Scale));
        Assert.DoesNotContain(policy!.Events, e => e.Event.StartsWith("legal.", StringComparison.Ordinal) || e.Event.StartsWith("renewal.", StringComparison.Ordinal) || e.Event.StartsWith("disposition.", StringComparison.Ordinal));
        Assert.Contains(Levels, l => l == policy.Content.WithholdEverythingFromLevel);
        Assert.Contains(Levels, l => l == policy.Content.WithholdTitleFromLevel);
        Assert.NotEqual(ClassificationScale.LegalPrivilegedLevel, policy.Content.WithholdEverythingFromLevel);
        foreach (var e in new[] { "approval.submitted", "approval.approved", "approval.rejected", "variation.raised", "variation.applied", "variation.rejected", "variation.cancelled", "reminder.endDate", "reminder.reviewDate", "reminder.criticalNotice" })
            Assert.NotNull(policy.RuleFor(e));
        Assert.Equal([14, 30, 60, 90], policy.Reminders.AllowedLeadDays.Order().ToArray());
        Assert.Equal(["OrganisationAdministrators"], policy.Reminders.UnownedFallback);
    }

    [Theory]
    [InlineData("Public", true, true)]
    [InlineData("Internal", true, true)]
    [InlineData("Confidential", false, true)]      // the title is withheld from Confidential, the reference stays
    [InlineData("Restricted", false, false)]       // nothing about the record is in the message
    public void Messages_withhold_content_at_the_levels_the_pack_states_and_the_scale_has_no_privileged_level(string level, bool titleShown, bool referenceShown)
    {
        var (policy, parse) = NotificationPolicyParser.Parse(Read("notifications/notification-policy.json"));
        Assert.Empty(parse);
        var template = new Ecp.Notifications.Policy.MessageTemplate("{{contract.reference}}: {{contract.title}}", "{{actor.name}} acted on {{contract.title}}. Open {{link}}.");
        var context = new Ecp.Notifications.RenderContext("HBL-000001", "Foundation scope PELICAN", level, "Pat", "/Contracts/Details/1", null, null, null, null);
        var (subject, body) = new Ecp.Notifications.NotificationRenderer().Render(policy!, template, context, Scale, TermSet.Neutral());
        Assert.Equal(titleShown, (subject + body).Contains("PELICAN", StringComparison.Ordinal));
        Assert.Equal(referenceShown, (subject + body).Contains("HBL-000001", StringComparison.Ordinal));
        if (!referenceShown) Assert.DoesNotContain("Pat", subject + body);
    }

    [Fact]
    public void The_classification_scale_has_four_levels_and_no_privileged_level()
    {
        using var doc = JsonDocument.Parse(Read("classification/classification-policy.json"));
        var levels = doc.RootElement.GetProperty("levels").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(Levels, levels);
        Assert.Empty(doc.RootElement.GetProperty("privilegedLevels").EnumerateArray());
        Assert.Contains(doc.RootElement.GetProperty("defaultLevel").GetString(), levels);
        Assert.DoesNotContain("Legal Privileged", Read("classification/classification-policy.json"));
        Assert.Empty(Branding.Parse(Read("branding/branding.json")).Check());
    }

    [Fact]
    public void The_fields_lookups_and_views_are_valid_and_the_views_use_only_fields_the_pack_defines_or_the_platform_has()
    {
        var (set, parse) = FieldParser.Parse(Read("fields/custom-fields.json"));
        Assert.NotNull(set); Assert.Empty(parse);
        var errors = FieldParser.Validate(set!, new FieldValidationContext(Levels, RoleCodes.All.ToHashSet())).Where(m => m.Severity == ValidationSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Code + " " + e.Path + " " + e.Message)));
        foreach (var key in new[] { "project_number", "bond_required", "insurance_expiry", "practical_completion_date", "defects_liability_end_date", "change_order_type", "time_extension_days", "revised_completion_date", "retention_percentage" })
            Assert.Contains(set!.Fields, f => f.Key == key);
        Assert.Contains(set.Lookups, l => l.Name == "riskLevel");
        Assert.All(set.Fields.Where(f => f.UsableInApprovals), f => Assert.NotEqual(FieldTypes.LongText, f.Type));
        Assert.All(set.Fields, f => Assert.True(FieldTypes.All.Contains(f.Type), f.Type));

        var (views, viewParse) = ViewParser.Parse(Read("views/list-views.json"));
        Assert.NotNull(views); Assert.Empty(viewParse);
        var known = set.Fields.Select(f => f.Key).Concat(CoreFields.All.Select(c => c.Key)).Append("set").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in views!.Views)
            foreach (var name in v.Columns.Concat(v.Sort.Select(s => s.Field)).Concat(v.Filters.Select(f => f.Field)))
                Assert.True(known.Contains(name), $"{v.Key}: '{name}' is neither a core field nor a field of this pack.");
        Assert.True(views.Views.Count >= 10);
    }

    [Fact]
    public void The_approval_matrices_are_valid_and_resolve_the_agreed_tiers_at_every_boundary()
    {
        var (set, messages) = MatrixParser.Parse(Read("matrices/approval-matrices.json"));
        Assert.NotNull(set);
        Assert.Empty(PackFiles.Errors(messages));
        Assert.Equal(["change-order-approval", "subcontract-activation"], set!.Matrices.Select(m => m.Code).Order().ToArray());
        var asOf = new DateOnly(2026, 10, 16);
        ApprovalFacts Facts(decimal v, string? risk = null, string currency = "USD", Dictionary<string, string?>? custom = null)
            => new(v, currency, ["CONCRETE"], "LUMP_SUM", null, null, null, "Internal", risk, null, custom ?? []);
        string Shape(string matrix, ApprovalFacts f)
        {
            var s = MatrixEvaluator.Select(set, matrix, f, asOf, new ResolutionTrace());
            Assert.True(s.Success, s.BlockMessage);
            return string.Join(",", s.Steps.OrderBy(x => x.Stage).ThenBy(x => x.Step.Key, StringComparer.Ordinal).Select(x => $"{x.Step.Key}@{x.Stage}"));
        }
        // Subcontract activation tiers: below 250,000; 250,000 to below 2,000,000; 2,000,000 and above.
        Assert.Equal("project-director@0", Shape("subcontract-activation", Facts(249_999.99m)));
        Assert.Equal("project-director@0,commercial-manager@1", Shape("subcontract-activation", Facts(250_000m)));
        Assert.Equal("project-director@0,commercial-manager@1", Shape("subcontract-activation", Facts(1_999_999.99m)));
        Assert.Equal("commercial-manager@0,project-director@0,regional-director@0", Shape("subcontract-activation", Facts(2_000_000m)));
        // Exceptions: high risk adds the safety step after the base steps; bonded work of 250,000 or more adds a bond check.
        Assert.Equal("project-director@0,safety-risk@1", Shape("subcontract-activation", Facts(100_000m, "High")));
        Assert.Equal("project-director@0", Shape("subcontract-activation", Facts(100_000m, "Low")));
        Assert.Equal("project-director@0", Shape("subcontract-activation", Facts(100_000m, custom: new() { ["bond_required"] = "true" })));
        Assert.Contains("bond-check", Shape("subcontract-activation", Facts(300_000m, custom: new() { ["bond_required"] = "true" })));
        // Change orders are judged on the resulting value, with a lighter path for an administrative one.
        Assert.Equal("project-director@0", Shape("change-order-approval", Facts(999_999.99m)));
        Assert.Equal("project-director@0,commercial-manager@1", Shape("change-order-approval", Facts(1_000_000m)));
        Assert.Equal("commercial-manager@0,project-director@0,regional-director@0", Shape("change-order-approval", Facts(5_000_000m)));
        Assert.Equal("commercial-manager@0", Shape("change-order-approval", Facts(7_500_000m, custom: new() { ["change_order_type"] = "ADMINISTRATIVE" })));
        // A currency with a dated rate is converted (synthetic rate, stated as such in the matrix description); one without blocks.
        Assert.Equal("project-director@0,commercial-manager@1", Shape("subcontract-activation", Facts(400_000m, currency: "CAD")));
        Assert.False(MatrixEvaluator.Select(set, "subcontract-activation", Facts(400_000m, currency: "EUR"), asOf, new ResolutionTrace()).Success);
        Assert.Contains("synthetic", set.Matrices.First(m => m.Code == "subcontract-activation").Description, StringComparison.OrdinalIgnoreCase);
        // The matrices' groups are the groups the terminology document names, and no JBS group appears.
        var named = JsonDocument.Parse(Read("terminology/terminology.json")).RootElement.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("key").GetString()!).ToHashSet();
        foreach (var m in set.Matrices)
            foreach (var row in m.Rows)
                foreach (var step in row.Steps)
                {
                    Assert.Contains(step.ApproverGroup, named);
                    if (step.BackupGroup is { } backup) Assert.Contains(backup, named);
                }
        Assert.DoesNotContain("GroupHeadProcurement", string.Join(" ", set.Matrices.SelectMany(m => m.Rows).SelectMany(r => r.Steps).Select(s => s.ApproverGroup)));
    }

    [Fact]
    public void The_terminology_sets_every_required_key_names_the_groups_and_hides_the_unused_role()
    {
        var doc = TerminologyDocument.Parse(Read("terminology/terminology.json"));
        Assert.Empty(PackFiles.Errors(doc.Check()));
        var en = doc.Terms.Where(t => t.Locale == "en").ToDictionary(t => t.Key);
        foreach (var key in TermCatalogue.RequiredKeys) Assert.True(en.ContainsKey(key), key);
        Assert.Equal("Subcontract", en["contract"].Singular); Assert.Equal("Subcontractor", en["supplier"].Singular); Assert.Equal("Trade Package", en["category"].Singular);
        Assert.Equal("Change Order", en["variation"].Singular); Assert.Equal("Project", en["site"].Singular); Assert.Equal("Package Manager", en["contract.owner"].Singular);
        Assert.Equal("Project Director", en["primary.approver"].Singular); Assert.Equal("Activation Approval", en["registration.approval"].Singular);
        Assert.Equal("Division", en["business.unit"].Singular); Assert.Equal("Executed Subcontract", en["executed.document"].Singular);
        Assert.Contains(doc.Roles!, r => r.Code == RoleCodes.Legal && !r.Offered);
        Assert.Equal(["CommercialManagerDeputies", "CommercialManagers", "ProjectDirectorDeputies", "ProjectDirectors", "RegionalDirectors", "SafetyRisk"], doc.Groups!.Select(g => g.Key).Order().ToArray());
        // The label keys added for contract fields and identifiers are set where the business has its own word, and the identifier terms name no tax authority or ERP product.
        Assert.Equal("Scheduled Completion Date", en["field.end.date"].Singular);
        Assert.DoesNotMatch(@"\b(ABN|SAP|GST|EIN|VAT)\b", string.Join(" ", en.Values.Select(t => t.Singular + " " + t.Plural)));
    }

    [Fact]
    public void No_document_of_the_pack_names_the_reference_business_an_Australian_identifier_or_a_privileged_level()
    {
        var banned = new Regex(@"\b(JBS|JBSAUS|Medius|Qlik|DocuSign|ABN|SAP|AUD|Brisbane|Sydney|Melbourne|Australia|Australian)\b|procurement|en-AU|Legal Privileged|GroupHead|\bAgreement Reference\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
        foreach (var file in Directory.EnumerateFiles(Dir, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var hit = banned.Match(text);
            Assert.False(hit.Success, $"{Path.GetRelativePath(Dir, file)} contains '{hit.Value}'");
        }
        // Synthetic and honest about it.
        Assert.Contains("synthetic", Read("pack.json"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not legal or industry advice", Read("pack.json"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_pack_defaults_describe_the_business_and_its_locale()
    {
        using var doc = JsonDocument.Parse(Read("pack.json"));
        var d = doc.RootElement.GetProperty("defaults");
        Assert.Equal(("en-US", "America/Chicago", "USD", "HBL"), (d.GetProperty("culture").GetString(), d.GetProperty("timeZoneId").GetString(), d.GetProperty("currencyCode").GetString(), d.GetProperty("referencePrefix").GetString()));
        Assert.Equal(8, d.GetProperty("categories").GetArrayLength());
        Assert.Equal(5, d.GetProperty("contractTypes").GetArrayLength());
        Assert.True(TimeZones.TryFind("America/Chicago", out _)); Assert.True(TimeZones.TryFind("America/Denver", out _));
        Assert.Matches(@"^[A-Z][A-Z0-9]{1,15}$", "HBL");
    }
}
