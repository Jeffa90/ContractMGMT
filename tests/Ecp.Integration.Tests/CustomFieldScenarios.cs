using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Documents;
using Ecp.Lifecycle;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecp.Integration.Tests.CustomFieldWorld;

namespace Ecp.Integration.Tests;

/// <summary>Configured fields on contracts: every type, validation, who sees and changes what, locking, versions and retirement, tenant isolation and the published-configuration cache.</summary>
internal static class CustomFieldScenarios
{
    private static string AllTypes => string.Join(",",
        Field("txt", "TEXT"), Field("notes", "LONG_TEXT"), Field("qty", "INTEGER"), Field("rate", "DECIMAL"), Field("price", "CURRENCY"), Field("share", "PERCENTAGE"), Field("signed", "DATE"), Field("at", "DATETIME"),
        Field("flag", "BOOLEAN"), Field("tier", "SINGLE_CHOICE", Choices), Field("tags", "MULTI_CHOICE", Choices), Field("region", "LOOKUP", "\"lookup\": \"regions\""), Field("lead", "PERSON"), Field("mail", "EMAIL"),
        Field("site_url", "URL"), Field("doc", "FILE_REFERENCE"), Field("shown", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Sum\", \"operands\": [\"qty\", \"rate\"] }"));
    private const string Regions = "\"lookups\": [ { \"name\": \"regions\", \"members\": [ { \"code\": \"NSW\", \"label\": \"New South Wales\" }, { \"code\": \"QLD\", \"label\": \"Queensland\" } ] } ]";

    public static async Task Every_field_type_is_stored_read_changed_and_cleared_through_the_secured_path(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var release = await w.PublishFieldsAsync(Doc(AllTypes, Regions));
        Assert.Equal(1, release.Number);
        var lead = w.W.Users["legalA"];
        var id = await w.CreateWithFieldsAsync("ownerPackaging", new()
        {
            ["txt"] = "Hello", ["notes"] = "Line one\nLine two", ["qty"] = "4", ["rate"] = "2.5", ["price"] = "1,234.50", ["share"] = "12.5", ["signed"] = "2026-03-01", ["at"] = "2026-03-01T10:00:00Z", ["flag"] = "yes",
            ["tier"] = "silver", ["tags"] = "GOLD;Bronze", ["region"] = "Queensland", ["lead"] = lead.ToString(), ["mail"] = "Someone@Example.com", ["site_url"] = "https://example.com/a"
        });

        var form = await w.FormAsync("ownerPackaging", id);
        string Raw(string k) => string.Join("|", form.Entry(k)!.Raw);
        Assert.Equal("Hello", Raw("txt"));
        Assert.Equal("Line one\nLine two", Raw("notes"));
        Assert.Equal("4", Raw("qty")); Assert.Equal("2.5", Raw("rate")); Assert.Equal("1234.5", Raw("price")); Assert.Equal("12.5", Raw("share"));
        Assert.Equal("2026-03-01", Raw("signed")); Assert.Equal("2026-03-01T10:00:00Z", Raw("at")); Assert.Equal("true", Raw("flag"));
        Assert.Equal("SILVER", Raw("tier")); Assert.Equal("GOLD|BRONZE", Raw("tags")); Assert.Equal("QLD", Raw("region")); Assert.Equal(lead.ToString(), Raw("lead"));
        Assert.Equal("Someone@Example.com", Raw("mail")); Assert.Equal("https://example.com/a", Raw("site_url"));
        Assert.Equal("Silver", form.Entry("tier")!.Display); Assert.Equal("Queensland", form.Entry("region")!.Display); Assert.Equal("Gold; Bronze", form.Entry("tags")!.Display);
        Assert.Equal("6.5", form.Entry("shown")!.Display);
        Assert.All(form.Fields.Where(f => !f.Definition.IsCalculated), f => Assert.True(f.Editable));
        Assert.False(form.Entry("shown")!.Editable);
        Assert.Equal(1, form.Release);

        // A document of this contract can be referenced; one of another contract cannot.
        await w.AddDocumentAsync(id, "FinalAgreement", "Contract text");
        var docId = (await w.As("ownerPackaging", s => s.Get<ISecuredContractDocuments>().ListAsync(id))).Single().DocumentId;
        var other = await w.NewDraftAsync();
        await w.AddDocumentAsync(other, "FinalAgreement", "Another text");
        var otherDoc = (await w.As("ownerPackaging", s => s.Get<ISecuredContractDocuments>().ListAsync(other))).Single().DocumentId;
        var refused = await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.SaveFieldsAsync("ownerPackaging", id, ("doc", [otherDoc.ToString()])));
        Assert.Contains("doc", refused.Errors.Keys);
        var changes = await w.SaveFieldsAsync("ownerPackaging", id, ("doc", [docId.ToString()]), ("qty", ["6"]), ("txt", [""]), ("tags", ["SILVER"]));
        Assert.Equal(["doc", "qty", "tags", "txt"], changes.Select(c => c.Field).Order().ToArray());
        Assert.Equal(("4", "6"), (changes.Single(c => c.Field == "qty").Before!, changes.Single(c => c.Field == "qty").After!));
        Assert.Null(changes.Single(c => c.Field == "txt").After);
        form = await w.FormAsync("ownerPackaging", id);
        Assert.False(form.Entry("txt")!.HasValue);
        Assert.Equal("SILVER", string.Join("|", form.Entry("tags")!.Raw));
        Assert.Equal("8.5", form.Entry("shown")!.Display);

        // Saving what is already stored changes nothing and writes no audit event.
        var events = async () => await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.CountAsync(e => e.Action == "contract.custom_fields_updated"));
        var before = await events();
        Assert.Empty(await w.SaveFieldsAsync("ownerPackaging", id, ("qty", ["6"])));
        Assert.Equal(before, await events());
        var audit = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "contract.custom_fields_updated").OrderByDescending(e => e.OccurredUtc).Select(e => e.NewValuesJson + "|" + e.OldValuesJson).FirstAsync());
        Assert.Contains("\"qty\":\"6\"", audit); Assert.Contains("\"qty\":\"4\"", audit);
    }

    public static async Task Invalid_values_defaults_and_requiredness_are_enforced_on_the_server_and_entered_values_are_kept(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(string.Join(",", Field("must", "TEXT", "\"required\": true"), Field("count", "INTEGER", "\"validation\": { \"min\": 1, \"max\": 10 }"),
            Field("tier", "SINGLE_CHOICE", Choices + ", \"default\": \"BRONZE\""), Field("code", "TEXT", "\"validation\": { \"pattern\": \"^[A-Z]{2}[0-9]{3}$\", \"patternMessage\": \"Use two letters and three digits.\" }"))));

        // Required is enforced at creation, before a reference number is used or anything is written.
        var missing = await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.CreateWithFieldsAsync("ownerPackaging", new() { ["count"] = "3" }));
        Assert.Contains("must", missing.Errors.Keys);
        var bad = await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.CreateWithFieldsAsync("ownerPackaging", new() { ["must"] = "x", ["count"] = "11", ["code"] = "nope" }));
        Assert.Equal(["code", "count"], bad.Errors.Keys.Order().ToArray());
        Assert.Equal("Use two letters and three digits.", bad.Errors["code"]);
        // The form is shown again with what was typed and each message beside its field.
        var again = await w.As("ownerPackaging", s => s.Get<IContractCustomFields>().ForNewAsync(w.W.Org1, null, new CustomFieldInput { ["count"] = ["11"] }, bad.Errors));
        Assert.Equal(["11"], again.Entry("count")!.Raw);
        Assert.Equal(bad.Errors["count"], again.Entry("count")!.Error);

        var id = await w.CreateWithFieldsAsync("ownerPackaging", new() { ["must"] = "present" });
        var form = await w.FormAsync("ownerPackaging", id);
        Assert.Equal("BRONZE", string.Join("|", form.Entry("tier")!.Raw));                 // the default was applied at creation
        // On edit, blanking a required field is refused; so is a value outside the choices or a crafted unknown key.
        var cleared = await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.SaveFieldsAsync("ownerPackaging", id, ("must", [""])));
        Assert.Contains("must", cleared.Errors.Keys);
        Assert.Contains("tier", (await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.SaveFieldsAsync("ownerPackaging", id, ("tier", ["PLATINUM"])))).Errors.Keys);
        Assert.Contains("nothing", (await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.SaveFieldsAsync("ownerPackaging", id, ("nothing", ["x"])))).Errors.Keys);
        Assert.Equal("present", string.Join("|", (await w.FormAsync("ownerPackaging", id)).Entry("must")!.Raw));     // nothing was half-saved
    }

    public static async Task A_field_is_shown_and_editable_only_to_the_roles_states_and_contract_types_it_names(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(string.Join(",",
            Field("legal_note", "TEXT", "\"visibleToRoles\": [\"Legal\", \"CategoryOwner\"], \"editableByRoles\": [\"ProcurementAdministrator\"]"),
            Field("draft_only", "TEXT", "\"editableInStates\": [\"Draft\"]"),
            Field("other_state", "TEXT", "\"visibleInStates\": [\"NoSuchState\"]"),
            Field("services_only", "TEXT", "\"contractTypes\": [\"Services\"]"))));
        var services = await w.CreateWithFieldsAsync("ownerPackaging", new() { ["services_only"] = "S" }, contractType: "Services");
        var goods = await w.CreateWithFieldsAsync("ownerPackaging", new() { }, contractType: "Goods");

        var owner = await w.FormAsync("ownerPackaging", services);
        Assert.True(owner.Entry("legal_note") is { Editable: false });                      // the owner may read it, but only a Procurement Administrator may change it
        Assert.True(owner.Entry("services_only") is { Editable: true });
        Assert.True(owner.Entry("draft_only") is { Editable: true });                        // the contract is a Draft
        Assert.Null(owner.Entry("other_state"));                                             // visible only in a state this contract is not in
        var viewer = await w.FormAsync("viewerAll", services);
        Assert.Null(viewer.Entry("legal_note"));                                             // hidden altogether: not blanked
        Assert.All(viewer.Fields, f => Assert.False(f.Editable));
        Assert.NotNull((await w.FormAsync("legalA", services)).Entry("legal_note"));          // Legal may read it
        // The goods contract has no place for a services-only field, and it is not offered.
        Assert.Null((await w.FormAsync("ownerPackaging", goods)).Entry("services_only"));
        await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.SaveFieldsAsync("ownerPackaging", goods, ("services_only", ["x"])));
        // A crafted post of a field the person cannot change is refused, however it is posted; nothing is half saved.
        var crafted = await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.SaveFieldsAsync("ownerPackaging", services, ("legal_note", ["Tampering"]), ("draft_only", ["fine"])));
        Assert.Equal(["legal_note"], crafted.Errors.Keys.ToArray());
        Assert.False((await w.FormAsync("ownerPackaging", services)).Entry("draft_only")!.HasValue);
        await Assert.ThrowsAnyAsync<Exception>(() => w.SaveFieldsAsync("viewerAll", services, ("draft_only", ["x"])));       // a viewer cannot edit the contract at all
        await Assert.ThrowsAnyAsync<Exception>(() => w.SaveFieldsAsync("legalA", services, ("draft_only", ["x"])));        // nor can Legal: it does not edit commercial facts

        // Once the contract has moved on, a field that is editable only in Draft is no longer offered for change.
        var inLegal = await w.NewDraftAsync(legal: true);
        await w.As("ownerPackaging", s => s.Get<Ecp.Lifecycle.ILegalReviewService>().SubmitAsync(new Ecp.Lifecycle.SubmitLegalRequest(Ecp.Lifecycle.Domain.SubjectKinds.Contract, inLegal, "Please review")));
        var frozen = await w.FormAsync("ownerPackaging", inLegal);
        Assert.All(frozen.Fields, f => Assert.False(f.Editable));                            // content is frozen while Legal reviews
    }

    public static async Task Classified_and_privileged_fields_are_hidden_from_people_without_the_level(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.UserAsync("privLegal", Ecp.Identity.Domain.RoleCodes.Legal, null, "Legal Privileged", privileged: true);
        await w.PublishFieldsAsync(Doc(string.Join(",", Field("plain", "TEXT"), Field("secret", "TEXT", "\"classification\": \"Restricted\""), Field("advice", "LONG_TEXT", "\"classification\": \"Legal Privileged\""))));
        var id = await w.CreateWithFieldsAsync("ownerPackaging", new() { ["plain"] = "visible" });
        // Higher-level values are written by a trusted process (a migration, or a person who holds the level); the owner, with a Confidential ceiling, could not enter them.
        await w.W.AsScopeless().Run(s => s.Get<IContractCustomFieldWriter>().ApplyAsync(id, new Dictionary<string, string?> { ["secret"] = "restricted-word", ["advice"] = "privileged advice" }, "test", "Migration", Guid.NewGuid(), null));
        Assert.Equal(3, await w.StoredValueCountAsync(id));

        var owner = await w.FormAsync("ownerPackaging", id);
        Assert.Equal(["plain"], owner.Fields.Select(f => f.Definition.Key).ToArray());
        Assert.DoesNotContain("restricted-word", string.Join("", owner.Fields.SelectMany(f => f.Raw.Append(f.Display))));
        var legal = await w.FormAsync("legalA", id);                                               // ceiling Restricted, not privileged
        Assert.Equal(["plain", "secret"], legal.Fields.Select(f => f.Definition.Key).Order().ToArray());
        Assert.Equal(["advice", "plain", "secret"], (await w.FormAsync("privLegal", id)).Fields.Select(f => f.Definition.Key).Order().ToArray());
        var crafted = await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.SaveFieldsAsync("ownerPackaging", id, ("secret", ["overwrite"]), ("advice", ["overwrite"])));
        Assert.Equal(["advice", "secret"], crafted.Errors.Keys.Order().ToArray());                  // refused as "cannot change", never "exists but hidden"
        Assert.Equal("restricted-word", string.Join("", (await w.FormAsync("legalA", id)).Entry("secret")!.Raw));
        // The audit event for the trusted write is held at the most sensitive level changed, so the audit trail hides it from the owner too.
        var level = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "contract.custom_fields_applied").Select(e => e.Classification).FirstAsync());
        Assert.Equal("Legal Privileged", level);
    }

    public static async Task A_locked_contract_keeps_its_configured_values_until_a_Variation_or_audited_unlock_changes_them(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(Field("note", "TEXT")));
        var id = await w.ToEffectiveInOrderAsync();
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractCustomFieldWriter>().WriteNewAsync(id, new CustomFieldInput { ["note"] = ["kept"] }, "test"); return 0; });
        await Assert.ThrowsAnyAsync<Exception>(() => w.SaveFieldsAsync("ownerPackaging", id, ("note", ["changed"])));
        var form = await w.FormAsync("ownerPackaging", id);
        Assert.Equal("kept", string.Join("", form.Entry("note")!.Raw));
        Assert.False(form.Entry("note")!.Editable);
    }

    public static async Task Definitions_are_versioned_retired_never_deleted_and_the_values_they_hold_are_protected(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var v1 = await w.PublishFieldsAsync(Doc(Field("tier", "SINGLE_CHOICE", Choices) + "," + Field("memo", "TEXT")));
        var id = await w.CreateWithFieldsAsync("ownerPackaging", new() { ["tier"] = "BRONZE", ["memo"] = "kept" });

        // The choice in use cannot be removed; the field in use cannot be removed or changed in type; a duplicate key and an unsafe pattern are refused.
        string Rewrite(string choices, string memo) => Doc(Field("tier", "SINGLE_CHOICE", choices) + "," + memo);
        var gold = "\"choices\": [ { \"code\": \"GOLD\", \"label\": \"Gold\" }, { \"code\": \"SILVER\", \"label\": \"Silver\" } ]";
        var e1 = await Assert.ThrowsAsync<DomainException>(() => w.PublishFieldsAsync(Rewrite(gold, Field("memo", "TEXT"))));
        Assert.Contains("BRONZE", e1.Message);
        var e2 = await Assert.ThrowsAsync<DomainException>(() => w.PublishFieldsAsync(Doc(Field("tier", "SINGLE_CHOICE", Choices))));
        Assert.Contains("memo", e2.Message);
        var e3 = await Assert.ThrowsAsync<DomainException>(() => w.PublishFieldsAsync(Rewrite(Choices, Field("memo", "INTEGER"))));
        Assert.Contains("type", e3.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<DomainException>(() => w.PublishFieldsAsync(Doc(Field("dup", "TEXT") + "," + Field("dup", "TEXT"))));
        await Assert.ThrowsAsync<DomainException>(() => w.PublishFieldsAsync(Doc(Field("pat", "TEXT", "\"validation\": { \"pattern\": \"(a)\\\\1\" }"))));

        // Marking the choice inactive, and retiring the field, are allowed and keep every stored value.
        var inactive = "\"choices\": [ { \"code\": \"GOLD\", \"label\": \"Gold\" }, { \"code\": \"SILVER\", \"label\": \"Silver\" }, { \"code\": \"BRONZE\", \"label\": \"Bronze\", \"active\": false } ]";
        var v2 = await w.PublishFieldsAsync(Rewrite(inactive, Field("memo", "TEXT", "\"status\": \"Retired\"")));
        Assert.Equal(2, v2.Number);
        var form = await w.FormAsync("ownerPackaging", id);
        Assert.Equal("Bronze", form.Entry("tier")!.Display);                                        // the old choice still shows its label
        Assert.Equal("kept", form.Entry("memo")!.Display);                                          // the retired field still shows its value ...
        Assert.False(form.Entry("memo")!.Editable);                                                 // ... read-only
        Assert.True(form.Entry("tier")!.Editable);
        await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.SaveFieldsAsync("ownerPackaging", id, ("memo", ["new"])));
        Assert.Contains("tier", (await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.CreateWithFieldsAsync("ownerPackaging", new() { ["tier"] = "BRONZE" }))).Errors.Keys);   // an inactive choice is not offered to new entry
        await w.SaveFieldsAsync("ownerPackaging", id, ("tier", ["BRONZE"]), ("memo", ["kept"]));      // ... but an unchanged stored one is accepted again
        var other = await w.CreateWithFieldsAsync("ownerPackaging", new() { ["tier"] = "GOLD" });
        Assert.Equal(1, await w.StoredValueCountAsync(other));
        Assert.Null((await w.As("ownerPackaging", s => s.Get<IContractCustomFields>().ForNewAsync(w.W.Org1, null))).Entry("memo"));      // a retired field is not on the Create page

        // Rolling back is a new release carrying the earlier content, and takes effect at once.
        var v3 = await w.W.AsScopeless().Run(s => s.Get<IConfigurationService>().LatestReleaseAsync(MetadataModule.CustomFieldKind, w.W.Org1));
        Assert.Equal(2, v3!.Number);
        var ids = await w.W.AsScopeless().Run(s => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(MetadataModule.CustomFieldKind, w.W.Org1, v1.ContentJson, "test", "back to release 1"));
        Assert.Equal(3, ids.Number);
        Assert.True((await w.FormAsync("ownerPackaging", id)).Entry("memo")!.Editable);
        var history = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "customfield.definitions.published").CountAsync());
        Assert.Equal(3, history);
    }

    public static async Task Definitions_and_values_belong_to_one_tenant_and_the_published_cache_never_crosses_or_goes_stale(TestApp app)
    {
        var a = await LifecycleWorld.CreateAsync(app);
        var b = await LifecycleWorld.CreateAsync(app);
        var cache = app.Services.GetRequiredService<IPublishedConfigurationCache>();
        await a.PublishFieldsAsync(Doc(Field("shared_key", "TEXT", "\"label\": \"Tenant A label\"").Replace("shared_key label", "Tenant A label")));
        await b.PublishFieldsAsync(Doc(Field("shared_key", "INTEGER") + "," + Field("only_b", "TEXT")));
        string[] Keys(IReadOnlyList<FieldDefinition> f) => f.Select(x => x.Key).Order().ToArray();
        for (var round = 0; round < 3; round++)             // repeated reads are served from memory and must stay separate
        {
            var setA = await a.W.AsScopeless().Run(s => s.Get<ICustomFieldCatalogue>().PublishedAsync(a.W.Org1));
            var setB = await b.W.AsScopeless().Run(s => s.Get<ICustomFieldCatalogue>().PublishedAsync(b.W.Org1));
            Assert.Equal(["shared_key"], Keys(setA.Fields));
            Assert.Equal(["only_b", "shared_key"], Keys(setB.Fields));
            Assert.Equal(FieldTypes.Text, setA.Find("shared_key")!.Type);
            Assert.Equal(FieldTypes.Integer, setB.Find("shared_key")!.Type);
        }
        Assert.True(cache.Statistics.Hits > 0);

        // A tenant cannot see or reach another's contract values or definitions.
        var idA = await a.CreateWithFieldsAsync("ownerPackaging", new() { ["shared_key"] = "A's value" });
        Assert.Equal(0, await b.W.AsScopeless().Run(s => s.Get<Ecp.Contracts.Persistence.ContractsDbContext>().CustomValues.CountAsync()));
        await Assert.ThrowsAnyAsync<Exception>(() => b.As("ownerPackaging", s => s.Get<IContractCustomFields>().ForContractAsync(idA)));
        await Assert.ThrowsAnyAsync<Exception>(() => b.W.AsScopeless().Run(s => s.Get<IContractCustomFieldWriter>().PreviewAsync(idA, new Dictionary<string, string?> { ["shared_key"] = "x" })));

        // A publish is visible immediately, not after the cache expires.
        var before = cache.Statistics.Invalidations;
        await a.PublishFieldsAsync(Doc(Field("shared_key", "TEXT") + "," + Field("added_later", "TEXT")));
        Assert.True(cache.Statistics.Invalidations > before);
        Assert.Equal(["added_later", "shared_key"], Keys((await a.W.AsScopeless().Run(s => s.Get<ICustomFieldCatalogue>().PublishedAsync(a.W.Org1))).Fields));
        Assert.Equal(2, (await a.W.AsScopeless().Run(s => s.Get<ICustomFieldCatalogue>().PublishedAsync(a.W.Org1))).ReleaseNumber);
        Assert.Equal(["only_b", "shared_key"], Keys((await b.W.AsScopeless().Run(s => s.Get<ICustomFieldCatalogue>().PublishedAsync(b.W.Org1))).Fields));
    }

    public static async Task A_failed_publish_leaves_the_published_definitions_and_the_cache_unchanged(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(Field("keep", "TEXT")));
        Assert.Single((await w.W.AsScopeless().Run(s => s.Get<ICustomFieldCatalogue>().PublishedAsync(w.W.Org1))).Fields);
        await Assert.ThrowsAsync<DomainException>(() => w.PublishFieldsAsync(Doc(Field("keep", "TEXT") + "," + Field("bad key", "TEXT"))));
        var set = await w.W.AsScopeless().Run(s => s.Get<ICustomFieldCatalogue>().PublishedAsync(w.W.Org1));
        Assert.Equal(1, set.ReleaseNumber);
        Assert.Equal(["keep"], set.Fields.Select(f => f.Key).ToArray());
    }
}

internal static class VariationFieldScenarios
{
    public static async Task A_Variation_changes_configured_fields_only_the_person_may_see_and_only_when_approved(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(string.Join(",", Field("tier", "SINGLE_CHOICE", Choices), Field("secret", "TEXT", "\"classification\": \"Restricted\""), Field("memo", "TEXT", "\"usableInApprovals\": true"))));
        var id = await w.ToEffectiveInOrderAsync();
        await w.W.AsScopeless().Run(async s =>
        {
            await s.Get<IContractCustomFieldWriter>().WriteNewAsync(id, new CustomFieldInput { ["tier"] = ["GOLD"], ["secret"] = ["restricted-word"], ["memo"] = ["before"] }, "test");
            return 0;
        });

        // The owner is offered the fields they can see; the restricted one is neither offered nor accepted.
        var offered = await w.As("ownerPackaging", s => s.Get<IVariationService>().ChangeableCustomFieldsAsync(id));
        Assert.Equal(["memo", "tier"], offered.Select(d => d.Key).Order().ToArray());
        var bad = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, Proposal(("cf.secret", "overwrite")))));
        Assert.Equal("field_not_changeable", bad.Code);
        await Assert.ThrowsAsync<CustomFieldValidationException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, Proposal(("cf.tier", "PLATINUM")))));   // not a choice: refused when drafted
        await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(id, Proposal(("cf.nosuch", "x")))));

        // A valid proposal leaves the stored values and the locked record alone until it is approved.
        var (variation, request) = await w.VariationToApprovalAsync(id, Proposal(("cf.tier", "SILVER"), ("cf.memo", "after")));
        Assert.Equal("GOLD", string.Join("", (await w.FormAsync("ownerPackaging", id)).Entry("tier")!.Raw));
        var pending = await w.As("ownerPackaging", s => s.Get<IVariationService>().GetAsync(variation));
        var row = pending.Comparison.Single(c => c.Field == "cf.tier");
        Assert.Equal(("GOLD", "SILVER"), (row.Before, row.After));
        Assert.Equal("tier label", row.Label);

        // The approver sees only what an administrator has made usable in approvals; the other configured field is not in the snapshot they read.
        var snapshot = await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().GetAsync(request));
        Assert.Contains("cf.memo", snapshot.Snapshot!.ProposedChanges!.Keys);
        Assert.DoesNotContain("cf.tier", snapshot.Snapshot.ProposedChanges.Keys);

        w.App.Clock.Advance(TimeSpan.FromHours(1));
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "Approved"));
        var form = await w.FormAsync("ownerPackaging", id);
        Assert.Equal("SILVER", string.Join("", form.Entry("tier")!.Raw));
        Assert.Equal("after", string.Join("", form.Entry("memo")!.Raw));
        Assert.Equal("restricted-word", string.Join("", (await w.FormAsync("legalA", id)).Entry("secret")!.Raw));       // untouched
        var done = await w.As("ownerPackaging", s => s.Get<IVariationService>().GetAsync(variation));
        var applied = done.Comparison.Single(c => c.Field == "cf.tier");
        Assert.Equal(("GOLD", "SILVER"), (applied.Before, applied.After));
        var audit = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "contract.custom_fields_applied").Select(e => e.NewValuesJson).ToListAsync());
        Assert.Contains(audit, a => a!.Contains("variation", StringComparison.Ordinal) && a.Contains("SILVER", StringComparison.Ordinal));
    }

    private static VariationInput Proposal(params (string Key, string Value)[] changes)
        => new("Change details", "Configured fields", "Because", null, false, null, null, null, changes.ToDictionary(c => c.Key, c => (string?)c.Value));
}

internal static class ChangeHistoryScenarios
{
    public static async Task The_change_history_shows_each_edit_to_the_person_who_may_read_it_and_never_a_hidden_value(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(string.Join(",", Field("plain", "TEXT"), Field("secret", "TEXT", "\"classification\": \"Restricted\""), Field("legal_note", "TEXT", "\"visibleToRoles\": [\"Legal\"]"))));
        var id = await w.CreateWithFieldsAsync("ownerPackaging", new() { ["plain"] = "first" });
        await w.SaveFieldsAsync("ownerPackaging", id, ("plain", ["second"]));
        await w.As("ownerPackaging", s => s.Get<ISecuredContractService>().UpdateAllAsync(id, new Dictionary<string, string?> { ["riskLevel"] = "High", ["contractTitle"] = "Renamed contract" }, null));
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractCustomFieldWriter>().ApplyAsync(id, new Dictionary<string, string?> { ["secret"] = "classified-value", ["legal_note"] = "legal-only-value" }, "test", "Migration", Guid.NewGuid(), null); return 0; });

        var owner = await w.As("ownerPackaging", s => s.Get<IContractChangeHistory>().ListAsync(id));
        var text = string.Join("|", owner.SelectMany(c => c.Fields.Select(f => $"{f.Field}:{f.Before}>{f.After}")));
        Assert.Contains("plain label:first>second", text.Replace("plain", "plain label").Replace("plain label label", "plain label"));
        Assert.Contains("Risk Level:>High", text);
        Assert.Contains("Contract Title", text);
        Assert.DoesNotContain("classified-value", text); Assert.DoesNotContain("legal-only-value", text);          // a Restricted value and a Legal-only value never reach the owner
        Assert.Equal(owner.Count, owner.Select(c => c.OccurredUtc).Count());
        Assert.All(owner, c => Assert.False(string.IsNullOrWhiteSpace(c.Who)));
        // Legal (cleared to Restricted, and in the role) sees the restricted event, but not an event held at Legal Privileged level.
        var legal = await w.As("legalA", s => s.Get<IContractChangeHistory>().ListAsync(id));
        var legalText = string.Join("|", legal.SelectMany(c => c.Fields.Select(f => f.After)));
        Assert.Contains("classified-value", legalText); Assert.Contains("legal-only-value", legalText);
        // A person who cannot read the contract learns nothing, not even that it exists.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("otherApprover", s => s.Get<IContractChangeHistory>().ListAsync(id)));      // the contract's own read rule decides, as on the detail page
    }
}
