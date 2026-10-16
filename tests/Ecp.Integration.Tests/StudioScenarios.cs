using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Ecp.Integration.Tests.CustomFieldWorld;

namespace Ecp.Integration.Tests;

/// <summary>The Configuration Studio's custom field editing: draft, edit, order, validate, impact, publish, retire, rollback, history and who may do each.</summary>
internal static class StudioScenarios
{
    private static FieldDefinition New(string key, string type, string section = "general", string label = "")
        => new(key, label.Length == 0 ? key + " label" : label, "", "", "", type, section, 0, false, [], FieldValidation.None, [], null, null, false, false, false, false, "Internal", [], [], [], [], [], FieldStatuses.Active, null, null);

    public static async Task A_draft_is_edited_ordered_validated_published_retired_and_rolled_back_through_the_studio(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = w.W.Org1;
        Task<T> Studio<T>(string user, Func<ICustomFieldStudio, Task<T>> f) => w.As(user, s => f(s.Get<ICustomFieldStudio>()));
        Task Studio_(string user, Func<ICustomFieldStudio, Task> f) => w.As(user, s => f(s.Get<ICustomFieldStudio>()));

        // A contract owner cannot open a draft; an administrator can. A draft starts empty when nothing has been published.
        await Assert.ThrowsAsync<ForbiddenException>(() => Studio_("ownerPackaging", s => s.StartDraftAsync(org)));
        await Studio_("orgAdmin", s => s.StartDraftAsync(org));
        var state = await Studio("orgAdmin", s => s.LoadAsync(org));
        Assert.True(state.HasDraft);
        Assert.Empty(state.Published.Fields);

        // Edits are refused until the draft is valid: a bad key and a duplicate are caught by validation, not by the page.
        await Studio_("orgAdmin", s => s.SaveFieldAsync(org, New("Bad Key", FieldTypes.Text)));
        var bad = await Studio("orgAdmin", s => s.ValidateAsync(org));
        Assert.False(bad.IsValid);
        await Assert.ThrowsAsync<DomainException>(() => Studio("orgAdmin", s => s.PublishAsync(org, "should not publish")));
        Assert.Null((await Studio("orgAdmin", s => s.LoadAsync(org))).Release);       // nothing was published
        await Studio_("orgAdmin", s => s.RemoveDraftFieldAsync(org, "Bad Key"));       // never published, so it can be deleted

        // Three fields, added in order, then reordered; a section and a lookup list are authored too.
        await Studio_("orgAdmin", s => s.SaveSectionAsync(org, new FieldSection("commercial", "Commercial terms", 20)));
        await Studio_("orgAdmin", s => s.SaveListAsync(org, new LookupList("regions", "Regions", [new("NSW", "New South Wales"), new("QLD", "Queensland")])));
        await Studio_("orgAdmin", s => s.SaveFieldAsync(org, New("first_field", FieldTypes.Text, "commercial")));
        await Studio_("orgAdmin", s => s.SaveFieldAsync(org, New("second_field", FieldTypes.Integer, "commercial")));
        await Studio_("orgAdmin", s => s.SaveFieldAsync(org, New("third_field", FieldTypes.Lookup, "commercial") with { Lookup = "regions", Required = true }));
        await Studio_("orgAdmin", s => s.MoveFieldAsync(org, "third_field", -1));
        await Studio_("orgAdmin", s => s.MoveFieldAsync(org, "third_field", -1));
        await Studio_("orgAdmin", s => s.MoveFieldAsync(org, "third_field", -1));       // already first: no change
        await Studio_("orgAdmin", s => s.MoveFieldAsync(org, "first_field", 1));
        state = await Studio("orgAdmin", s => s.LoadAsync(org));
        Assert.Equal(["third_field", "second_field", "first_field"], state.DraftSet!.Fields.OrderBy(f => f.Order).Select(f => f.Key).ToArray());
        Assert.Equal(3, state.DraftSet.Fields.Select(f => f.Order).Distinct().Count());
        Assert.All(state.Impact, i => Assert.Equal(ImpactLevel.Info, i.Level));        // all new
        Assert.True((await Studio("orgAdmin", s => s.ValidateAsync(org))).IsValid);

        // Publish: it takes effect at once for forms and the catalogue, and leaves history.
        var v1 = await Studio("orgAdmin", s => s.PublishAsync(org, "first fields"));
        Assert.Equal(1, v1.Number);
        Assert.Equal(3, (await w.W.AsScopeless().Run(s => s.Get<ICustomFieldCatalogue>().PublishedAsync(org))).Fields.Count);
        Assert.False((await Studio("orgAdmin", s => s.LoadAsync(org))).HasDraft);

        // A published field with values cannot be deleted from the draft; retiring it is allowed and keeps the values.
        var id = await w.CreateWithFieldsAsync("ownerPackaging", new() { ["first_field"] = "kept", ["third_field"] = "NSW" });
        await Studio_("orgAdmin", s => s.StartDraftAsync(org));
        var e = await Assert.ThrowsAsync<DomainException>(() => Studio_("orgAdmin", s => s.RemoveDraftFieldAsync(org, "first_field")));
        Assert.Equal("field_published", e.Code);
        await Studio_("orgAdmin", s => s.SetRetiredAsync(org, "first_field", true));
        state = await Studio("orgAdmin", s => s.LoadAsync(org));
        Assert.Contains(state.Impact, i => i.Key == "first_field" && i.Level == ImpactLevel.Warning && i.AffectedValues == 1);
        Assert.Equal(1, state.Usage.Of("first_field"));
        // Changing the type of a field in use is a blocking impact and the draft cannot be published.
        await Studio_("orgAdmin", s => s.SaveFieldAsync(org, New("second_field", FieldTypes.Text)));
        await Studio_("orgAdmin", s => s.SaveFieldAsync(org, New("third_field", FieldTypes.Text)));
        state = await Studio("orgAdmin", s => s.LoadAsync(org));
        Assert.Contains(state.Impact, i => i.Key == "third_field" && i.Level == ImpactLevel.Blocking);
        Assert.False((await Studio("orgAdmin", s => s.ValidateAsync(org))).IsValid);
        await Assert.ThrowsAsync<DomainException>(() => Studio("orgAdmin", s => s.PublishAsync(org, "type change")));
        Assert.Equal(1, (await Studio("orgAdmin", s => s.LoadAsync(org))).Release!.Number);        // still release 1

        // Put the draft right (undo the type changes) and publish the retirement.
        await Studio_("orgAdmin", s => s.DiscardDraftAsync(org));
        await Studio_("orgAdmin", s => s.StartDraftAsync(org));
        await Studio_("orgAdmin", s => s.SetRetiredAsync(org, "first_field", true));
        var v2 = await Studio("orgAdmin", s => s.PublishAsync(org, "retire first_field"));
        Assert.Equal(2, v2.Number);
        var form = await w.FormAsync("ownerPackaging", id);
        Assert.Equal("kept", form.Entry("first_field")!.Display);
        Assert.False(form.Entry("first_field")!.Editable);

        // Rollback publishes release 1 again as release 3; the person needs the rollback right, and history keeps all three.
        await Assert.ThrowsAsync<ForbiddenException>(() => Studio_("ownerPackaging", s => s.RollbackAsync(org, 1, "undo")));
        await Studio_("orgAdmin", s => s.RollbackAsync(org, 1, "retirement was premature"));
        state = await Studio("orgAdmin", s => s.LoadAsync(org, includeHistory: true));
        Assert.Equal(3, state.Release!.Number);
        Assert.Equal(1, state.Release.RollbackOfNumber);
        Assert.Equal(3, state.Releases!.Count);
        Assert.True((await w.FormAsync("ownerPackaging", id)).Entry("first_field")!.Editable);

        // Every step left an audit event.
        var actions = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action.StartsWith("config.") || e.Action.StartsWith("customfield.")).Select(e => e.Action).ToListAsync());
        Assert.Contains("config.draft.created", actions);
        Assert.Contains("config.draft.saved", actions);
        Assert.Contains("config.draft.validated", actions);
        Assert.Contains("config.published", actions);
        Assert.Contains("config.rolled_back", actions);
        Assert.Contains("customfield.definitions.published", actions);
    }
}
