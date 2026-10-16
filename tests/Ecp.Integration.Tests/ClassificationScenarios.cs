using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Contracts.Persistence;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Classification (five ordered levels, Legal Privileged needing an explicit grant) enforced across reads, lists, counts, documents, exports and audit evidence.</summary>
internal static class ClassificationScenarios
{
    private static async Task<AccessWorld> WorldAsync(TestApp app) => await AccessWorld.CreateAsync(app, "TenantC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());

    private static async Task<Guid> AddAsync(AccessWorld w, string key, string classification, string? title = null, Guid? category = null)
    {
        await using var s = w.AsScopeless();
        var id = await s.Get<IContractService>().CreateDraftAsync(new CreateContractRequest(w.Org1, w.TenantRef(key), title ?? "Synthetic " + key, w.Sup1, category ?? w.CatPackaging, Classification: classification));
        w.Contracts[key] = id;
        return id;
    }

    private static string TenantRef(this AccessWorld w, string key) => "CLS-" + w.TenantId.ToString("N")[..6].ToUpperInvariant() + "-" + key;

    private static async Task<Guid> GrantUserAsync(AccessWorld w, string key, string role, ViewerKind? kind, string ceiling, bool privileged = false, string? group = null)
    {
        await using var s = w.AsScopeless();
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync(key, key + "@" + w.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", key + "|" + w.TenantId, "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, kind, ScopeSet.ForOrganisations([w.Org1], ceiling), group, EffectiveFromUtc: w.Now.AddDays(-5), ExplicitHighCeiling: true, PrivilegedAccess: privileged),
            AssignmentSource.Demo, "test");
        w.Users[key] = id;
        return id;
    }

    // ---- AUT-006 / AUT-017 -----------------------------------------------------------------------------------------------------------------------

    public static async Task Legal_privileged_records_need_an_explicit_grant_and_a_role_or_ceiling_alone_never_opens_them(TestApp app)
    {
        var w = await WorldAsync(app);
        var privileged = await AddAsync(w, "P1", "Legal Privileged", "Privileged advice ZEBRA");
        await GrantUserAsync(w, "legalPriv", RoleCodes.Legal, null, "Legal Privileged");                                   // Legal holds records.read_privileged
        await GrantUserAsync(w, "ownerNoGrant", RoleCodes.CategoryOwner, null, "Legal Privileged");                          // ceiling reaches it, no grant
        await GrantUserAsync(w, "ownerGranted", RoleCodes.CategoryOwner, null, "Legal Privileged", privileged: true);        // explicit grant
        await GrantUserAsync(w, "adminNoGrant", RoleCodes.ProcurementAdministrator, null, "Legal Privileged");
        await GrantUserAsync(w, "viewerTop", RoleCodes.Viewer, ViewerKind.AllContracts, "Legal Privileged");
        // Legal below the privileged ceiling sees the ordinary records but not this one.
        await GrantUserAsync(w, "legalLow", RoleCodes.Legal, null, "Restricted");

        async Task<bool> Sees(string user)
        {
            var listed = await w.AsAsync(user, async s => (await s.Get<ISecuredContractService>().ListAsync(new ContractFilter(Search: "ZEBRA", PageSize: 50))).Items.Any(i => i.Id == privileged));
            var counted = await w.AsAsync(user, s => s.Get<ISecuredContractService>().CountAsync(new ContractFilter(Search: "ZEBRA")));
            var opened = await w.AsAsync(user, async s =>
            {
                try { await s.Get<ISecuredContractService>().GetAsync(privileged); return true; }
                catch (ResourceNotFoundException) { return false; }
            });
            Assert.Equal(listed, opened);                    // a list never shows what a direct read would hide, and the count agrees
            Assert.Equal(listed ? 1 : 0, counted);
            return opened;
        }

        Assert.True(await Sees("legalPriv"));
        Assert.True(await Sees("ownerGranted"));
        Assert.False(await Sees("ownerNoGrant"));
        Assert.False(await Sees("adminNoGrant"));
        Assert.False(await Sees("viewerTop"));
        Assert.False(await Sees("legalLow"));
        Assert.False(await Sees("ownerPackaging"));         // the standard world's Category Owner
        // The refusal is a 404 (the record does not exist for them), not a 403 that would confirm it does.
        await w.AsAsync("ownerNoGrant", async s => await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<ISecuredContractService>().GetAsync(privileged)));
    }

    public static async Task Only_a_tenant_administrator_can_grant_privileged_access(TestApp app)
    {
        var w = await WorldAsync(app);
        var target = await GrantUserAsync(w, "target", RoleCodes.CategoryOwner, null, "Confidential");
        var request = new GrantRequest(target, RoleCodes.CategoryOwner, null, ScopeSet.ForOrganisations([w.Org1], "Legal Privileged"), ExplicitHighCeiling: true, PrivilegedAccess: true);
        // An organisation administrator cannot grant the ceiling at all, nor the privileged flag.
        await w.AsAsync("orgAdmin", async s => await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IRoleAssignmentService>().GrantAsync(request)));
        // A tenant administrator can; an administrative role itself can never carry the flag.
        await w.AsAsync("tenantAdmin", async s => await s.Get<IRoleAssignmentService>().GrantAsync(request));
        var admin = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("tenantAdmin", s => s.Get<IRoleAssignmentService>().GrantAsync(
            new GrantRequest(target, RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([w.Org1], "Internal"), PrivilegedAccess: true))));
        Assert.Equal("privileged_not_for_administrators", admin.Code);
    }

    // ---- AUT-019 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task A_category_owner_reads_everything_in_the_organisation_up_to_the_ceiling_and_nothing_above_it(TestApp app)
    {
        var w = await WorldAsync(app);
        await AddAsync(w, "Q1", "Restricted", "Restricted energy ZEBRA", w.CatEnergy);
        var keys = await w.VisibleContractKeysAsync("ownerPackaging");              // read-all, Confidential ceiling
        Assert.Contains("C3", keys);                                               // another category's contract is readable
        Assert.DoesNotContain("C4", keys);                                         // Restricted: above the ceiling
        Assert.DoesNotContain("Q1", keys);
        await w.AsAsync("ownerPackaging", async s =>
        {
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<ISecuredContractService>().GetAsync(w.Contracts["Q1"]));
            Assert.Equal(0, await s.Get<ISecuredContractService>().CountAsync(new ContractFilter(Search: "ZEBRA")));
            var counts = await s.Get<ISecuredContractService>().CountByStatusAsync();
            Assert.Equal(keys.Count, counts.Values.Sum());                           // counts agree with what the person can list
        });
    }

    // ---- INV-32 ----------------------------------------------------------------------------------------------------------------------------------

    public static async Task A_document_classified_above_the_viewer_stays_hidden_even_though_its_contract_is_visible(TestApp app)
    {
        var w = await WorldAsync(app);
        Guid ordinary, secret;
        await using (var s = w.AsScopeless())
        {
            var documents = s.Get<IDocumentService>();
            ordinary = (await documents.AddAsync(new AddDocumentRequest(w.Org1, w.Contracts["C2"], "SupportingDocument", "Cover note", "note.txt", "text/plain", new MemoryStream("note"u8.ToArray()), "Confidential"))).DocumentId;
            secret = (await documents.AddAsync(new AddDocumentRequest(w.Org1, w.Contracts["C2"], "SupportingDocument", "Board paper", "board.txt", "text/plain", new MemoryStream("board"u8.ToArray()), "Restricted"))).DocumentId;
            var db = s.Get<Ecp.Documents.Persistence.DocumentsDbContext>();
            foreach (var v in await db.Versions.ToListAsync()) v.RecordScan(Ecp.Documents.Domain.ScanStatus.Clean);
            await s.Get<IUnitOfWork>().ExecuteAsync(ct => db.SaveChangesAsync(ct));
        }
        // The Category Owner (Confidential ceiling) sees the contract and the ordinary document, but the Restricted document does not exist for them.
        await w.AsAsync("ownerPackaging", async s =>
        {
            var docs = await s.Get<ISecuredContractDocuments>().ListAsync(w.Contracts["C2"]);
            Assert.Contains(docs, d => d.DocumentId == ordinary);
            Assert.DoesNotContain(docs, d => d.DocumentId == secret);
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<ISecuredContractDocuments>().OpenAsync(secret));
            Assert.NotNull(await s.Get<ISecuredContractDocuments>().OpenAsync(ordinary));
        });
        // Legal (Restricted ceiling) sees both.
        await w.AsAsync("legal", async s =>
        {
            var docs = await s.Get<ISecuredContractDocuments>().ListAsync(w.Contracts["C2"]);
            Assert.Contains(docs, d => d.DocumentId == secret);
            Assert.NotNull(await s.Get<ISecuredContractDocuments>().OpenAsync(secret));
        });
        // A document can never be classified below its contract.
        await w.AsAsync("ownerPackaging", async s =>
        {
            var added = await s.Get<ISecuredContractDocuments>().AddAsync(new AddContractDocumentRequest(w.Contracts["C1"], "SupportingDocument", "Low", "low.txt", "text/plain", new MemoryStream("x"u8.ToArray()), "Public"));
            var summary = await s.Get<IDocumentQuery>().GetAsync(added.DocumentId);
            Assert.Equal("Internal", summary!.Classification);                    // raised to the contract's level
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractDocuments>().AddAsync(new AddContractDocumentRequest(w.Contracts["C1"], "SupportingDocument", "High", "high.txt", "text/plain", new MemoryStream("x"u8.ToArray()), "Restricted")));
        });
    }

    // ---- AUT-006 / IMP-002 -----------------------------------------------------------------------------------------------------------------------

    public static async Task Search_counts_and_exports_never_leak_a_record_the_reader_could_not_open(TestApp app)
    {
        var w = await WorldAsync(app);
        await AddAsync(w, "X1", "Restricted", "=HYPERLINK(\"http://example.invalid\") ZEBRA", w.CatEnergy);
        await GrantUserAsync(w, "auditorLow", RoleCodes.Auditor, null, "Confidential");
        // The Procurement Administrator (Confidential ceiling, holds records.export): the Restricted record is absent from the export even by name.
        var exported = await w.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().ExportCsvAsync(new ContractFilter(Search: "ZEBRA")));
        Assert.DoesNotContain("ZEBRA", exported);
        Assert.StartsWith("AgreementReference,", exported);
        // The Auditor (Restricted ceiling) sees it, with the formula text neutralised so a spreadsheet cannot execute it.
        var audit = await w.AsAsync("auditor", s => s.Get<ISecuredContractService>().ExportCsvAsync(new ContractFilter(Search: "ZEBRA")));
        Assert.Contains("'=HYPERLINK", audit);
        Assert.DoesNotContain(",=HYPERLINK", audit);
        // Someone without the export permission cannot export, whatever they can read.
        await w.AsAsync("ownerPackaging", async s => await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().ExportCsvAsync(new ContractFilter())));
        // The export itself is audited.
        await using var t = w.AsScopeless();
        Assert.True(await t.Get<Ecp.Audit.Persistence.AuditDbContext>().Events.AnyAsync(e => e.Action == "contracts.exported"));
    }

    // ---- classification change history -----------------------------------------------------------------------------------------------------------

    public static async Task Classification_changes_are_audited_lowering_needs_authority_and_a_reason_and_documents_follow_a_raise(TestApp app)
    {
        var w = await WorldAsync(app);
        var c1 = w.Contracts["C1"];                                                 // Internal, editable, in Packaging
        Guid doc;
        await using (var s = w.AsScopeless())
            doc = (await s.Get<IDocumentService>().AddAsync(new AddDocumentRequest(w.Org1, c1, "SupportingDocument", "Pricing", "p.txt", "text/plain", new MemoryStream("p"u8.ToArray()), "Internal"))).DocumentId;

        // Raising is open to the record's owner, up to their own access.
        await w.AsAsync("ownerPackaging", s => s.Get<ISecuredContractService>().ChangeClassificationAsync(c1, "Confidential", null));
        await w.AsAsync("ownerPackaging", async s => await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().ChangeClassificationAsync(c1, "Restricted", null)));
        await using (var s = w.AsScopeless()) Assert.Equal("Confidential", (await s.Get<IDocumentQuery>().GetAsync(doc))!.Classification);   // the document was raised with its contract

        // Lowering: the owner lacks the permission; the Procurement Administrator needs a reason.
        await w.AsAsync("ownerPackaging", async s => await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().ChangeClassificationAsync(c1, "Internal", "Not so sensitive after all")));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().ChangeClassificationAsync(c1, "Internal", "short")));
        Assert.Equal("reason_required", noReason.Code);
        await w.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().ChangeClassificationAsync(c1, "Internal", "Pricing removed from this agreement; no longer commercially sensitive"));
        Assert.Equal("Internal", (await w.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().GetAsync(c1))).Classification);

        // A locked record is reclassified only under an audited unlock.
        var locked = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().ChangeClassificationAsync(w.Contracts["C7"], "Confidential", null)));
        Assert.Equal("record_locked", locked.Code);

        // History: each change is an audit event with old and new level and, for the lowering, the reason.
        await using var t = w.AsScopeless();
        var events = await t.Get<Ecp.Audit.Persistence.AuditDbContext>().Events.AsNoTracking().Where(e => e.EntityId == c1.ToString() && e.Action == "contract.classification_changed").OrderBy(e => e.Sequence).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Contains("Internal", events[0].OldValuesJson); Assert.Contains("Confidential", events[0].NewValuesJson);
        Assert.Contains("Confidential", events[1].OldValuesJson); Assert.Contains("Internal", events[1].NewValuesJson); Assert.Contains("Pricing removed", events[1].Reason);
        Assert.All(events, e => Assert.Equal("Confidential", e.Classification));   // classified at the higher of the two levels
    }

    public static async Task Audit_evidence_about_a_classified_record_is_visible_only_where_the_record_is(TestApp app)
    {
        var w = await WorldAsync(app);
        var priv = await AddAsync(w, "P2", "Legal Privileged", "Privileged advice");
        await GrantUserAsync(w, "auditorRestricted", RoleCodes.Auditor, null, "Restricted");
        await using (var s = w.AsScopeless()) { }
        async Task<int> VisibleEventsAbout(string user, Guid id) => await w.AsAsync(user, async s =>
            (await s.Get<IAuditBrowser>().QueryAsync(new AuditFilter(PageSize: 200))).Items.Count(e => e.EntityId == id.ToString()));
        // The Auditor with a Restricted ceiling sees the audit trail of an ordinary contract, and none of the privileged contract's.
        Assert.True(await VisibleEventsAbout("auditor", w.Contracts["C2"]) > 0);
        Assert.Equal(0, await VisibleEventsAbout("auditorRestricted", priv));
        Assert.Equal(0, await VisibleEventsAbout("auditor", priv));
    }
}
