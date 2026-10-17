using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Register;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Packs;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S9 readiness (docs/S9_READINESS.md, G8 and G11): every kind a pack may carry, in one throwaway pack, installed through the Pack Manager, assigned, and published in dependency order. Until this ran, the approval matrix, custom
/// fields and lookups, list views, classification policy and branding had never come from a pack. The pack is built at test time and committed nowhere; its wording is neutral and names no business.
/// What it pins down: a document that depends on the organisation (a matrix condition on a custom field, a view over a custom field) is accepted at upload with a warning and judged for real when the draft is validated for an
/// organisation, while a genuinely malformed document is still refused at upload.
/// </summary>
internal static class PackKindSmokeScenarios
{
    private const string MatrixCode = "smoke-tiers";

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];
    private static string Blank(params string[] parts) => Path.Combine([RepoRoot.Find(), "templates", "packs", "blank", .. parts]);

    // ---- the documents (neutral, synthetic) ----

    private static string Lifecycle()
    {
        var json = File.ReadAllText(Blank("workflows", "contract-lifecycle.json"));
        var named = Regex.Replace(json, "\"requireExecutedCopy\": true \\}", "\"requireExecutedCopy\": true, \"matrix\": \"" + MatrixCode + "\" }", RegexOptions.None, TimeSpan.FromSeconds(2));
        Assert.NotEqual(json, named);
        return named;
    }

    private static string VariationWorkflow()
    {
        var json = File.ReadAllText(Blank("workflows", "variation.json"));
        var named = Regex.Replace(json, "\"requireExecutedCopy\": true \\}", "\"requireExecutedCopy\": true, \"matrix\": \"" + MatrixCode + "\" }", RegexOptions.None, TimeSpan.FromSeconds(2));
        Assert.NotEqual(json, named);
        return named;
    }

    /// <summary>The Blank policy with its content levels stated for a scale that has no privileged level (docs/S9_READINESS.md, G7).</summary>
    private static string Policy()
    {
        var json = File.ReadAllText(Blank("notifications", "notification-policy.json"));
        var named = json.Replace("\"withholdEverythingFromLevel\": \"Legal Privileged\"", "\"withholdEverythingFromLevel\": \"Restricted\"");
        Assert.NotEqual(json, named);
        return named;
    }

    private const string Classification = """{ "levels": ["Public", "Internal", "Confidential", "Restricted"], "defaultLevel": "Internal", "privilegedLevels": [] }""";

    private const string Branding = """{ "productName": "Smoke Pack Manager", "accentColour": "#1f3a8a", "footerText": "Synthetic smoke pack" }""";

    private const string Fields = """
        { "schemaVersion": 1,
          "sections": [ { "key": "project", "label": "Project", "order": 1 } ],
          "lookups": [ { "name": "riskLevel", "label": "Risk level", "members": [ { "code": "Low", "label": "Low" }, { "code": "High", "label": "High" } ] } ],
          "fields": [
            { "key": "bond_required", "label": "Bond required", "type": "BOOLEAN", "section": "project", "usableInApprovals": true },
            { "key": "insurance_expiry", "label": "Insurance expiry", "type": "DATE", "section": "project", "filterable": true, "sortable": true } ] }
        """;

    /// <summary>Tiers on value, an exception on a core fact and on a custom field (the part that depends on the organisation having published that field).</summary>
    private const string Matrix = """
        { "schemaVersion": 1, "matrices": [ { "code": "smoke-tiers", "name": "Smoke tiers", "currency": "USD", "rows": [
            { "code": "small", "priority": 100, "when": { "valueMax": 100000 }, "steps": [ { "key": "lead", "display": "Lead", "approverGroup": "Approvers", "mode": "FirstResponse" } ] },
            { "code": "large", "priority": 100, "flow": "Sequential", "when": { "valueMin": 100000 },
              "steps": [ { "key": "lead", "display": "Lead", "approverGroup": "Approvers", "mode": "FirstResponse" }, { "key": "director", "display": "Director", "approverGroup": "Directors", "mode": "AllRequired" } ] },
            { "code": "bonded", "kind": "Exception", "effect": "AddSteps", "priority": 200,
              "when": { "riskLevels": [ "High" ], "custom": [ { "field": "bond_required", "operator": "Equals", "values": [ "true" ] } ] },
              "steps": [ { "key": "risk", "display": "Risk", "approverGroup": "RiskManagers", "mode": "FirstResponse" } ] } ] } ] }
        """;

    /// <summary>One view on core fields only, one that uses the custom field (columns, a filter and a sort).</summary>
    private const string Views = """
        { "schemaVersion": 1, "views": [
            { "key": "smoke-core", "name": "Smoke core view", "description": "Core fields only.", "columns": [ "agreementReference", "contractTitle", "status" ], "sort": [ { "field": "contractTitle", "descending": false } ] },
            { "key": "smoke-insurance", "name": "Smoke insurance view", "description": "Uses a custom field.", "columns": [ "agreementReference", "insurance_expiry" ],
              "filters": [ { "field": "insurance_expiry", "op": "IsNotEmpty", "values": [] } ], "sort": [ { "field": "insurance_expiry", "descending": false } ] } ] }
        """;

    private static readonly (string Source, string Path, string Kind, string Scope, int Schema, bool Required)[] Entries =
    [
        ("lifecycle.json", "content/contract-lifecycle.json", "WorkflowContractLifecycle", "organisation", 1, true),
        ("variation.json", "content/variation-workflow.json", "WorkflowVariation", "organisation", 1, true),
        ("notifications.json", "content/notification-policy.json", "NotificationPolicy", "organisation", 1, true),
        ("terminology.json", "content/terminology.json", "Terminology", "organisation", 1, false),
        ("capabilities.json", "content/capabilities.json", "Capabilities", "organisation", 1, false),
        ("matrix.json", "content/approval-matrix.json", "ApprovalMatrix", "organisation", 1, false),
        ("fields.json", "content/custom-fields.json", "CustomField", "organisation", 1, false),
        ("views.json", "content/list-views.json", "ContractListView", "organisation", 1, false),
        ("classification.json", "content/classification-policy.json", "ClassificationPolicy", "tenant", 1, false),
        ("branding.json", "content/branding.json", "Branding", "tenant", 1, false),
    ];

    private static Dictionary<string, string> Documents(Func<string, string, string>? change = null)
    {
        var docs = new Dictionary<string, string>
        {
            ["lifecycle.json"] = Lifecycle(), ["variation.json"] = VariationWorkflow(), ["notifications.json"] = Policy(), ["terminology.json"] = PackTestKit.ThirdPackTerminologyJson(),
            ["capabilities.json"] = File.ReadAllText(Blank("capabilities", "capabilities.json")), ["matrix.json"] = Matrix, ["fields.json"] = Fields, ["views.json"] = Views,
            ["classification.json"] = Classification, ["branding.json"] = Branding,
        };
        if (change is not null) foreach (var k in docs.Keys.ToList()) docs[k] = change(k, docs[k]);
        return docs;
    }

    private static byte[] Pack(string key, Func<string, string, string>? change = null)
        => PackTestKit.Build(key, "1.0.0", content: Entries, extraDocuments: Documents(change));

    // ---- helpers ----

    private static Task<PackUploadResult> UploadAsync(LifecycleWorld w, byte[] bytes) => w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().UploadAsync(new MemoryStream(bytes), "smoke.ecpkg"));

    /// <summary>One person who is both an administrator of the organisation (its drafts) and of the tenant (the tenant-wide kinds a pack may carry, which are separate drafts), as a small business's administrator is.</summary>
    private static async Task<string> AdministratorAsync(LifecycleWorld w, Guid org, string key = "smokeAdmin")
    {
        await using var scope = w.W.AsScopeless();
        var id = await scope.Get<IUserService>().CreateActiveTrustedAsync(key, key + "@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", key + "|" + w.W.TenantId, "test");
        await scope.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([org], "Internal"), null, EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        await scope.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.TenantAdministrator, null, ScopeSet.Tenant("Internal"), null, EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        w.W.Users[key] = id;
        return key;
    }

    private static async Task<string> PublishAsync(LifecycleWorld w, string admin, Guid org, string kind)
    {
        var draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().FindOpenDraftAsync(kind, kind is "ClassificationPolicy" or "Branding" ? Guid.Empty : org));
        Assert.True(draft is not null, $"{kind}: no draft was created by the assignment.");
        var validated = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ValidateAsync(draft!.Id));
        Assert.True(validated.IsPublishable, $"{kind}: " + string.Join("; ", validated.Validation!.Messages.Select(m => $"{m.Severity} {m.Code} {m.Path} {m.Message}")));
        await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().PublishAsync(validated.Id, "Smoke test"));
        return kind;
    }

    // ---- 1. every kind, from one pack, end to end ----

    public static async Task Every_packable_kind_comes_from_one_pack_and_publishes_in_dependency_order(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("smoke");
        var up = await UploadAsync(w, Pack(key));
        Assert.True(up.Report.CanInstall, string.Join("; ", up.Report.Issues.Where(i => i.IsError).Select(i => i.Code + " " + i.Message)));

        // The documents that depend on the organisation are reported, not refused.
        Assert.Contains(up.Report.Warnings, i => i.Message.Contains("bond_required", StringComparison.Ordinal));
        Assert.Contains(up.Report.Warnings, i => i.Message.Contains("insurance_expiry", StringComparison.Ordinal));

        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(up.Token));
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Smoke Org", "SMK", key);
        var admin = await AdministratorAsync(w, org.Org);
        var assigned = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().AssignAsync(org.Org, key, "1.0.0"));
        // A draft that only waits for something else to be published is not a problem with the draft.
        Assert.All(assigned.Kinds.Where(k => k.Kind != CapabilityKinds.Name), k => Assert.False(k.NeedsAttention, k.Kind + ": " + k.Outcome + " " + k.Detail));
        Assert.Contains(assigned.Kinds, k => k.Kind == "ApprovalMatrix" && k.Outcome.Contains("waits", StringComparison.Ordinal));
        Assert.Contains(assigned.Kinds, k => k.Kind == "ContractListView" && k.Outcome.Contains("waits", StringComparison.Ordinal));
        foreach (var kind in new[] { "ApprovalMatrix", "CustomField", "ContractListView", "ClassificationPolicy", "Branding", "WorkflowContractLifecycle", "WorkflowVariation", "NotificationPolicy" })
            Assert.Contains(assigned.Kinds, k => k.Kind == kind);

        // Dependency order: classification, fields, terminology, matrix, workflows (each names the matrix), policy, views, branding.
        var order = new[] { "ClassificationPolicy", "CustomField", "Terminology", "ApprovalMatrix", WorkflowKinds.Variation, WorkflowKinds.ContractLifecycle, Ecp.Notifications.Policy.NotificationPolicyKinds.Policy, "ContractListView", "Branding" };
        foreach (var kind in order) await PublishAsync(w, admin, org.Org, kind);

        // The organisation now runs on the pack: its lifecycle starts in the Blank pack's first state, its custom field is usable, its shared view is listed, its scale has no privileged level.
        var contract = await w.W.AsAsync(org.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(org.Org, "SMK-000001", "Smoke", org.Supplier, org.Category)));
        Assert.Equal("Open", (await w.W.AsAsync(org.User, s => s.Get<ILifecycleService>().StateAsync(contract))).Status);
        var views = await w.W.AsAsync(org.User, s => s.Get<IContractViews>().ListAsync(org.Org));
        Assert.Contains(views, v => v.Name == "Smoke insurance view");
        var scale = await w.W.AsAsync(admin, s => s.Get<Ecp.Identity.IAccessService>().ClassificationScaleAsync());
        Assert.Empty(scale.PrivilegedLevels);
        Assert.Equal(["Public", "Internal", "Confidential", "Restricted"], scale.Levels);
    }

    // ---- 2. dependency order is enforced, and the organisation-dependent findings become errors for a real organisation ----

    public static async Task A_draft_that_depends_on_something_unpublished_is_judged_for_the_organisation_and_refused_until_it_exists(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("order");
        var up = await UploadAsync(w, Pack(key));
        Assert.True(up.Report.CanInstall);
        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(up.Token));
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Order Org", "ORD", key);
        var admin = await AdministratorAsync(w, org.Org);
        await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().AssignAsync(org.Org, key, "1.0.0"));

        async Task<DraftDto> ValidateAsync(string kind)
        {
            var draft = (await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().FindOpenDraftAsync(kind, org.Org)))!;
            return await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        }

        // Before the custom fields are published, the matrix (custom condition), the views (custom columns) and the lifecycle (names the matrix) are refused for this organisation.
        var matrix = await ValidateAsync("ApprovalMatrix");
        Assert.False(matrix.IsPublishable);
        Assert.Contains(matrix.Validation!.Messages, m => m.Severity == ValidationSeverity.Error && m.Code == "fact_unavailable");
        var views = await ValidateAsync("ContractListView");
        Assert.False(views.IsPublishable);
        Assert.Contains(views.Validation!.Messages, m => m.Severity == ValidationSeverity.Error && m.Code is "column_unavailable" or "filter_unavailable");
        var lifecycle = await ValidateAsync(WorkflowKinds.ContractLifecycle);
        Assert.False(lifecycle.IsPublishable);
        Assert.Contains(lifecycle.Validation!.Messages, m => m.Code == "matrix_missing");

        // Publish in order and each becomes publishable.
        await PublishAsync(w, admin, org.Org, "CustomField");
        Assert.True((await ValidateAsync("ApprovalMatrix")).IsPublishable);
        Assert.True((await ValidateAsync("ContractListView")).IsPublishable);
        await PublishAsync(w, admin, org.Org, "ApprovalMatrix");
        Assert.True((await ValidateAsync(WorkflowKinds.ContractLifecycle)).IsPublishable);
    }

    // ---- 3. the fix is narrow: real mistakes are still refused at upload ----

    public static async Task Genuinely_malformed_documents_are_still_refused_when_the_pack_is_uploaded(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var cases = new (string File, string Break, string Why)[]
        {
            ("matrix.json", Matrix.Replace("\"valueMax\": 100000", "\"valueMax\": \"lots\""), "a malformed matrix document"),
            ("matrix.json", Matrix.Replace("\"riskLevels\": [ \"High\" ]", "\"colours\": [ \"Red\" ]"), "a condition the matrix does not know"),
            ("views.json", Views.Replace("\"contractTitle\", \"status\"", "\"contractTitle\", \"statusTypo\""), "a view column that is neither a core field nor a custom field name (a typo)"),
            ("fields.json", Fields.Replace("\"BOOLEAN\"", "\"NOT_A_TYPE\""), "a field type that does not exist"),
            ("classification.json", Classification.Replace("\"defaultLevel\": \"Internal\"", "\"defaultLevel\": \"Nowhere\""), "a default level that is not in the scale"),
            ("branding.json", Branding.Replace("#1f3a8a", "#ffffff"), "an accent colour that fails contrast"),
        };
        foreach (var (file, broken, why) in cases)
        {
            var key = Unique("bad");
            var up = await UploadAsync(w, Pack(key, (name, text) => name == file ? broken : text));
            Assert.False(up.Report.CanInstall, $"{why} was accepted at upload.");
            Assert.Contains(up.Report.Issues, i => i.IsError && i.Code == PackCodes.ContentInvalid);
        }
    }
}

public sealed class PackKindSmokeScenarioTests_Sqlite
{
    [Fact] public Task All_kinds() => ProviderRunner.SqliteAsync(PackKindSmokeScenarios.Every_packable_kind_comes_from_one_pack_and_publishes_in_dependency_order);
    [Fact] public Task Order() => ProviderRunner.SqliteAsync(PackKindSmokeScenarios.A_draft_that_depends_on_something_unpublished_is_judged_for_the_organisation_and_refused_until_it_exists);
    [Fact] public Task Narrow() => ProviderRunner.SqliteAsync(PackKindSmokeScenarios.Genuinely_malformed_documents_are_still_refused_when_the_pack_is_uploaded);
}

[Trait("Category", "SqlServer")]
public sealed class PackKindSmokeScenarioTests_SqlServer
{
    [SqlServerFact] public Task All_kinds() => ProviderRunner.SqlServerAsync(PackKindSmokeScenarios.Every_packable_kind_comes_from_one_pack_and_publishes_in_dependency_order);
    [SqlServerFact] public Task Order() => ProviderRunner.SqlServerAsync(PackKindSmokeScenarios.A_draft_that_depends_on_something_unpublished_is_judged_for_the_organisation_and_refused_until_it_exists);
    [SqlServerFact] public Task Narrow() => ProviderRunner.SqlServerAsync(PackKindSmokeScenarios.Genuinely_malformed_documents_are_still_refused_when_the_pack_is_uploaded);
}
