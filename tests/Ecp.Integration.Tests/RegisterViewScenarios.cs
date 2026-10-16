using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Register;
using Ecp.Formats;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecp.Integration.Tests.CustomFieldWorld;

namespace Ecp.Integration.Tests;

/// <summary>List views (shipped, shared, personal) and exports: the same column and filter model, what each person may use, and what an export records and refuses.</summary>
internal static class RegisterViewScenarios
{
    private static Task<T> As<T>(LifecycleWorld w, string user, Func<IContractViews, Task<T>> f) => w.As(user, s => f(s.Get<IContractViews>()));
    private static async Task<int> Void(Task t) { await t; return 0; }
    private static string[] Refs(RegisterPage p) => p.Rows.Select(r => r.AgreementReference).ToArray();

    public static async Task The_eleven_shipped_views_run_and_select_what_their_names_promise(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var today = w.Today;
        var expiring = await w.ToEffectiveInOrderAsync(end: today.AddDays(30), review: today.AddDays(10), critical: true, notice: 60);
        var distant = await w.ToEffectiveInOrderAsync(end: today.AddDays(500), review: today.AddDays(400));
        var evergreen = await w.ToEffectiveInOrderAsync(extension: "Evergreen", review: today.AddDays(20));
        var draft = await w.NewDraftAsync(legal: true);
        string RefOf(Guid id) => w.FactsAsync(id).GetAwaiter().GetResult().AgreementReference;
        var (rExpiring, rDistant, rEvergreen, rDraft) = (RefOf(expiring), RefOf(distant), RefOf(evergreen), RefOf(draft));

        var views = await As(w, "ownerPackaging", v => v.ListAsync());
        Assert.Equal(11, views.Count(v => v.Scope == ViewScope.System));
        foreach (var v in views.Where(v => v.Scope == ViewScope.System))
        {
            var page = await As(w, "ownerPackaging", s => s.RunAsync(v.Ref));     // every shipped view runs for an ordinary owner
            Assert.True(page.Total >= 0 && page.Columns.Count > 0, v.Ref);
        }
        var all = await As(w, "ownerPackaging", v => v.RunAsync("system:all-contracts"));
        Assert.Contains(rDraft, Refs(all)); Assert.Contains(rExpiring, Refs(all));

        // The views select by facts, not by name: expiry window and Evergreen, review date, Critical Notice, owner, and the drafts waiting for Legal.
        var expiringSoon = Refs(await As(w, "ownerPackaging", v => v.RunAsync("system:expiring-soon")));
        Assert.Contains(rExpiring, expiringSoon); Assert.DoesNotContain(rDistant, expiringSoon); Assert.DoesNotContain(rEvergreen, expiringSoon); Assert.DoesNotContain(rDraft, expiringSoon);
        var reviews = Refs(await As(w, "ownerPackaging", v => v.RunAsync("system:reviews-due")));
        Assert.Contains(rExpiring, reviews); Assert.Contains(rEvergreen, reviews); Assert.DoesNotContain(rDistant, reviews);
        var critical = Refs(await As(w, "ownerPackaging", v => v.RunAsync("system:critical-notice")));
        Assert.Contains(rExpiring, critical); Assert.DoesNotContain(rDistant, critical);
        Assert.Contains(rExpiring, Refs(await As(w, "ownerPackaging", v => v.RunAsync("system:my-contracts"))));
        Assert.Empty(Refs(await As(w, "legalA", v => v.RunAsync("system:my-contracts"))));          // Legal owns no category
        var pendingApproval = Refs(await As(w, "ownerPackaging", v => v.RunAsync("system:pending-approval")));
        Assert.DoesNotContain(rDistant, pendingApproval);
        // A person only ever sees their own organisation's views and a view that does not exist is a plain not-found.
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => As(w, "ownerPackaging", v => v.GetAsync("system:no-such-view")));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => As(w, "ownerPackaging", v => v.GetAsync("shared:" + Guid.NewGuid().ToString("N") + ":x")));
    }

    public static async Task Personal_views_are_private_validated_and_safe_to_save_and_delete(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(Field("plain", "TEXT", "\"filterable\": true, \"sortable\": true") + "," + Field("secret", "TEXT", "\"classification\": \"Restricted\", \"filterable\": true, \"sortable\": true")));
        await RegisterScenarios.Make(w, "Viewed one", custom: new() { ["plain"] = "alpha" });
        await RegisterScenarios.Make(w, "Viewed two", custom: new() { ["plain"] = "beta" });
        ViewDefinition Def(string name, params string[] columns) => new("x", name, "mine", columns, [new SortClause("agreementReference")], [], 25);

        var mine = await As(w, "ownerPackaging", v => v.SaveMineAsync(null, Def("My plain view", "agreementReference", "contractTitle", "plain")
            with { Filters = [new FilterClause("plain", "Eq", ["alpha"])] }));
        Assert.StartsWith("personal:", mine);
        var page = await As(w, "ownerPackaging", v => v.RunAsync(mine, w.W.Org1));
        Assert.Equal(["Viewed one"], page.Rows.Select(r => r.Title).ToArray());
        Assert.Equal(["agreementReference", "contractTitle", "plain"], page.Columns.Select(c => c.Key).ToArray());
        Assert.Contains((await As(w, "ownerPackaging", v => v.ListAsync(w.W.Org1))), x => x.Ref == mine && x.Scope == ViewScope.Personal && x.CanEdit);

        // Nobody else can see, run, replace or delete it.
        Assert.DoesNotContain(await As(w, "legalA", v => v.ListAsync(w.W.Org1)), x => x.Ref == mine);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => As(w, "legalA", v => v.RunAsync(mine)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => As(w, "legalA", v => v.SaveMineAsync(mine, Def("Hijack", "agreementReference"))));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => As(w, "legalA", v => Void(v.DeleteMineAsync(mine))));

        // A view cannot be saved with a column, filter, sort or format the person cannot use, or one the grammar does not know.
        async Task<RegisterQueryException> Refused(ViewDefinition d) => await Assert.ThrowsAsync<RegisterQueryException>(() => As(w, "ownerPackaging", v => v.SaveMineAsync(null, d)));
        Assert.Contains("column_unavailable", (await Refused(Def("Bad column", "agreementReference", "secret"))).Issues.Select(i => i.Code));
        Assert.NotEmpty((await Refused(Def("Bad filter", "agreementReference") with { Filters = [new FilterClause("secret", "Eq", ["x"])] })).Issues);
        Assert.NotEmpty((await Refused(Def("Bad operator", "agreementReference") with { Filters = [new FilterClause("estimatedValue", "Contains", ["x"])] })).Issues);
        Assert.NotEmpty((await Refused(Def("Bad sort", "agreementReference") with { Sort = [new SortClause("description")] })).Issues);
        Assert.NotEmpty((await Refused(Def("Bad size", "agreementReference") with { PageSize = 100000 })).Issues);
        Assert.NotEmpty((await Refused(Def("Bad group", "agreementReference") with { GroupBy = "owner" })).Issues);
        Assert.NotEmpty((await Refused(Def("Bad classification", "agreementReference") with { Filters = [new FilterClause("classification", "Eq", ["Legal Privileged"])] })).Issues);
        Assert.NotEmpty((await Refused(Def("", "agreementReference"))).Issues);
        Assert.NotEmpty((await Refused(Def("Injection", "agreementReference") with { Filters = [new FilterClause("estimatedValue", "Eq", ["1; DROP TABLE Contract"])] })).Issues);

        // Replace, then delete; the name is unique per person.
        var again = await As(w, "ownerPackaging", v => v.SaveMineAsync(mine, Def("Renamed", "agreementReference", "contractTitle")));
        Assert.Equal(mine, again);
        Assert.Equal("Renamed", (await As(w, "ownerPackaging", v => v.GetAsync(mine))).Definition.Name);
        var dup = await Assert.ThrowsAsync<DomainException>(() => As(w, "ownerPackaging", v => v.SaveMineAsync(null, Def("Renamed", "agreementReference"))));
        Assert.Equal("view_name_in_use", dup.Code);
        await As(w, "ownerPackaging", v => Void(v.DeleteMineAsync(mine)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => As(w, "ownerPackaging", v => v.RunAsync(mine)));
        var audit = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action.StartsWith("contracts.view.")).Select(e => e.Action).ToListAsync());
        Assert.Contains("contracts.view.saved", audit); Assert.Contains("contracts.view.deleted", audit);
    }

    public static async Task Shared_views_are_published_through_configuration_and_each_viewer_still_sees_only_their_own_access(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(Field("secret", "TEXT", "\"classification\": \"Restricted\", \"filterable\": true, \"reportable\": true") + "," + Field("plain", "TEXT", "\"filterable\": true") + "," + Field("spare", "TEXT")));
        var id = await RegisterScenarios.Make(w, "Shared target", custom: new() { ["plain"] = "yes" });
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractCustomFieldWriter>().ApplyAsync(id, new Dictionary<string, string?> { ["secret"] = "classified" }, "test", "Migration", Guid.NewGuid(), null); return 0; });
        Task<T> Studio<T>(string user, Func<IContractViewStudio, Task<T>> f) => w.As(user, s => f(s.Get<IContractViewStudio>()));
        var org = w.W.Org1;

        // Only someone who may draft configuration can author shared views.
        await Assert.ThrowsAnyAsync<Exception>(() => Studio("ownerPackaging", s => Void(s.StartDraftAsync(org))));
        await Studio("orgAdmin", s => Void(s.StartDraftAsync(org)));
        // The definition catalogue lets an administrator who cannot read contracts name the organisation's fields (it describes fields, not data).
        Assert.NotNull((await Studio("orgAdmin", s => s.CatalogueAsync(org))).Find("secret"));
        await Studio("orgAdmin", s => Void(s.SaveViewAsync(org, new ViewDefinition("with-secret", "With the secret", "Shows a restricted detail", ["agreementReference", "contractTitle", "plain", "secret", "spare"], [], [new FilterClause("plain", "Eq", ["yes"])], 25))));
        await Studio("orgAdmin", s => Void(s.SaveViewAsync(org, new ViewDefinition("all-contracts", "Clash", "", ["agreementReference"], [], [], 25))));
        var bad = await Studio("orgAdmin", s => s.ValidateAsync(org));
        Assert.False(bad.IsValid);
        Assert.Contains(bad.Messages, m => m.Code == "system_view");
        await Assert.ThrowsAsync<DomainException>(() => Studio("orgAdmin", s => s.PublishAsync(org, "should fail")));
        await Studio("orgAdmin", s => Void(s.RemoveViewAsync(org, "all-contracts")));
        Assert.True((await Studio("orgAdmin", s => s.ValidateAsync(org))).IsValid);
        var release = await Studio("orgAdmin", s => s.PublishAsync(org, "shared view for the scenario"));
        Assert.Equal(1, release.Number);

        // Everyone in the organisation can open it; each sees the columns and rows their own access allows.
        var listed = await As(w, "ownerPackaging", v => v.ListAsync());
        var shared = listed.Single(v => v.Scope == ViewScope.Shared);
        Assert.Equal("With the secret", shared.Name);
        var ownerPage = await As(w, "ownerPackaging", v => v.RunAsync(shared.Ref));
        Assert.DoesNotContain("secret", ownerPage.Columns.Select(c => c.Key));                      // the owner is not cleared for it ...
        Assert.Contains(ownerPage.Notes, n => n.Contains("secret", StringComparison.Ordinal));          // ... and is told the column is not shown
        Assert.DoesNotContain("classified", string.Join("|", ownerPage.Rows.SelectMany(r => r.Cells.Values)));
        var legalPage = await As(w, "legalA", v => v.RunAsync(shared.Ref));
        Assert.Contains("secret", legalPage.Columns.Select(c => c.Key));
        Assert.Contains("classified", legalPage.Rows.SelectMany(r => r.Cells.Values));

        // A change is a new version; the earlier one can be restored as a new release; history and audit record both.
        await Studio("orgAdmin", s => Void(s.StartDraftAsync(org)));
        await Studio("orgAdmin", s => Void(s.RemoveViewAsync(org, "with-secret")));
        Assert.Equal(2, (await Studio("orgAdmin", s => s.PublishAsync(org, "retire the view"))).Number);
        Assert.DoesNotContain(await As(w, "ownerPackaging", v => v.ListAsync()), v => v.Scope == ViewScope.Shared);
        await Studio("orgAdmin", s => Void(s.RollbackAsync(org, 1, "needed again")));
        Assert.Contains(await As(w, "ownerPackaging", v => v.ListAsync()), v => v.Scope == ViewScope.Shared);
        var state = await Studio("orgAdmin", s => s.LoadAsync(org, true));
        Assert.Equal(3, state.Releases!.Count);
        var actions = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "contracts.views.published").CountAsync());
        Assert.Equal(3, actions);
        // A retired configured field in a shared view degrades gracefully: the column is dropped with a note rather than the view failing.
        await w.PublishFieldsAsync(Doc(Field("secret", "TEXT", "\"classification\": \"Restricted\", \"filterable\": true, \"reportable\": true") + "," + Field("plain", "TEXT", "\"filterable\": true")));     // "spare" held no values, so it can go
        var after = await As(w, "legalA", v => v.RunAsync(shared.Ref));
        Assert.DoesNotContain("spare", after.Columns.Select(c => c.Key));
        Assert.Contains(after.Notes, n => n.Contains("spare", StringComparison.Ordinal));
        Assert.Contains("secret", after.Columns.Select(c => c.Key));
    }

    public static async Task An_export_carries_the_same_columns_and_filters_and_only_what_the_person_may_export(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(string.Join(",",
            Field("note", "TEXT", "\"filterable\": true, \"reportable\": true"),
            Field("internal_only", "TEXT", "\"filterable\": true, \"reportable\": false"),
            Field("secret", "TEXT", "\"classification\": \"Legal Privileged\", \"reportable\": true"),
            Field("score", "DECIMAL", "\"filterable\": true, \"reportable\": true"))));
        var sup = await w.W.AsScopeless().Run(s => s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(w.W.Org1, "Xyzsup Trading Pty Ltd")));
        var danger = await RegisterScenarios.Make(w, "=HYPERLINK(\"http://evil\",\"x\")", 1234.5m, custom: new() { ["note"] = "+cmd|' /C calc'!A0", ["internal_only"] = "not exported", ["score"] = "-5" }, supplier: sup);
        var plain = await RegisterScenarios.Make(w, "Plain contract", 99m, custom: new() { ["note"] = "fine" }, supplier: sup);
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractCustomFieldWriter>().ApplyAsync(plain, new Dictionary<string, string?> { ["secret"] = "classified" }, "test", "Migration", Guid.NewGuid(), null); return 0; });
        Task<ExportResult> Export(string user, ExportRequest r) => w.As(user, s => s.Get<IContractExports>().ExportAsync(r));
        var query = new ContractQuery(Filters: [new FilterClause("supplier", "Contains", ["xyzsup"])], Columns: ["agreementReference", "contractTitle", "estimatedValue", "note", "internal_only", "secret", "score"], OrganisationId: w.W.Org1, PageSize: 200);

        // The people who may export are named by the permission; an owner may not.
        await Assert.ThrowsAsync<ForbiddenException>(() => Export("ownerPackaging", new ExportRequest(query)));

        // Procurement (export right, no Legal clearance): the reportable fields they may see, spreadsheet-safe, with the not-reportable and restricted columns left out.
        var csv = await Export("procAdmin", new ExportRequest(query, ExportFormat.Csv, ViewName: "test view", FilterSummary: "supplier contains xyzsup"));
        var text = System.Text.Encoding.UTF8.GetString(csv.Content);
        var header = text.Split('\n')[0];
        Assert.Contains("Contract Title", header); Assert.Contains("note label", header); Assert.Contains("score label", header);
        Assert.DoesNotContain("internal_only", header, StringComparison.OrdinalIgnoreCase);        // not reportable
        Assert.DoesNotContain("secret", header, StringComparison.OrdinalIgnoreCase);                  // above the person's level
        Assert.DoesNotContain("classified", text); Assert.DoesNotContain("not exported", text);
        if (!text.Contains("'=HYPERLINK")) Assert.Fail(text);
        Assert.Contains("'=HYPERLINK", text);                                                       // a formula is neutralised ...
        Assert.Contains("'+cmd", text);
        Assert.Contains("-5", text);                                                                  // ... but a negative number stays a number
        Assert.DoesNotContain("\n=HYPERLINK", text); Assert.DoesNotContain(",=HYPERLINK", text);
        Assert.Equal(2, csv.Rows);

        // The same export as a workbook carries the metadata sheet, and the file is a real XLSX.
        var xlsx = await Export("procAdmin", new ExportRequest(query, ExportFormat.Xlsx, KeysAsHeaders: true, ViewName: "test view"));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.ContentType);
        Assert.Equal((byte)'P', xlsx.Content[0]); Assert.Equal((byte)'K', xlsx.Content[1]);

        // Limits: a result over the limit is refused (never silently cut), and a large export needs a reason; both are decided before any row is built.
        var limits = w.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContractsOptions>>().Value;
        (limits.ExportRowLimit, limits.LargeExportRowLimit) = (1, 5);
        var over = await Assert.ThrowsAsync<DomainException>(() => Export("procAdmin", new ExportRequest(query)));
        Assert.Equal("export_too_large", over.Code);
        var noReason = await Assert.ThrowsAsync<DomainException>(() => Export("procAdmin", new ExportRequest(query, Large: true)));
        Assert.Equal("large_export_reason", noReason.Code);
        var large = await Export("procAdmin", new ExportRequest(query, Large: true, Reason: "Quarterly supplier review for the board"));
        Assert.Equal(2, large.Rows);

        // Every export is audited: who, what, how many, and the reason of a large one; the file content is never in the audit trail.
        var events = await w.W.AsScopeless().Run(s => s.Get<IAuditEventSource>().Events.Where(e => e.Action == "contracts.exported").Select(e => new { e.NewValuesJson, e.Reason }).ToListAsync());
        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.Reason == "Quarterly supplier review for the board");
        Assert.DoesNotContain(events, e => e.NewValuesJson!.Contains("classified", StringComparison.Ordinal) || e.NewValuesJson.Contains("evil", StringComparison.Ordinal));
        _ = danger;
    }

    public static async Task The_reporting_schema_is_stable_and_follows_what_the_person_may_export(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(Field("note", "TEXT", "\"reportable\": true") + "," + Field("hidden", "TEXT", "\"reportable\": false") + "," + Field("secret", "TEXT", "\"classification\": \"Restricted\", \"reportable\": true")));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<IContractExports>().SchemaAsync(w.W.Org1)));
        var schema = await w.As("procAdmin", s => s.Get<IContractExports>().SchemaAsync(w.W.Org1));
        Assert.Equal("1", schema.SchemaVersion);
        Assert.Contains(schema.Columns, c => c.Name == "agreementReference" && c.Source == "core");
        Assert.Contains(schema.Columns, c => c.Name == "cf_note" && c.Source == "custom" && c.Type == FieldTypes.Text);
        Assert.DoesNotContain(schema.Columns, c => c.Name == "cf_hidden");                               // not reportable
        Assert.Equal(schema.Columns.Select(c => c.Name).Distinct().Count(), schema.Columns.Count);         // names are unique
        Assert.All(schema.Columns, c => Assert.Matches("^[A-Za-z][A-Za-z0-9_]*$", c.Name));
        // A later definition adds a column without renaming any existing one.
        await w.PublishFieldsAsync(Doc(Field("note", "TEXT", "\"reportable\": true") + "," + Field("added", "INTEGER", "\"reportable\": true")));
        var next = await w.As("procAdmin", s => s.Get<IContractExports>().SchemaAsync(w.W.Org1));
        Assert.Empty(schema.Columns.Where(c => c.Source == "core").Select(c => c.Name).Except(next.Columns.Select(c => c.Name)));
        Assert.Contains(next.Columns, c => c.Name == "cf_added");
        Assert.True(next.CustomFieldRelease > schema.CustomFieldRelease);
    }
}
