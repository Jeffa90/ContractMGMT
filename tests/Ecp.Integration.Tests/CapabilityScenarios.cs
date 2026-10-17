using System.Text.Json.Nodes;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S6 (docs/CAPABILITIES.md): optional platform capabilities are configuration. An organisation's published capability document wins, then the defaults of its solution pack, then the platform default (all on). Turning a
/// capability off is refused when the organisation's workflows still use it or work is in flight; once it is off, nothing new can be started (one engine chokepoint), work under way can still finish, and the five
/// capabilities (Legal Review, Register Authorisation, Variations, Renewals, Signature Requests) can all be off in one organisation with no code change.
/// </summary>
internal static class CapabilityScenarios
{
    private const string Kind = CapabilityKinds.Name;

    /// <summary>Forces a set of capabilities off for a test host (everything else resolves normally), so the engine gate can be exercised on the reference lifecycle, which publishing would not allow to be switched off.</summary>
    internal sealed class Switch { public volatile string[] Off = []; public volatile bool FailCapabilityReads; }

    internal static void Install(IServiceCollection services, Switch sw)
    {
        var d = services.Last(x => x.ServiceType == typeof(ICapabilityResolver));
        services.Remove(d);
        services.Add(new ServiceDescriptor(typeof(ICapabilityResolver), sp =>
        {
            var inner = (ICapabilityResolver)(d.ImplementationFactory is not null ? d.ImplementationFactory(sp) : ActivatorUtilities.CreateInstance(sp, d.ImplementationType!));
            return new SwitchableResolver(inner, sw);
        }, d.Lifetime));
    }

    internal static void InstallFlakyReleases(IServiceCollection services, Switch sw)
    {
        var d = services.Last(x => x.ServiceType == typeof(IConfigurationReleases));
        services.Remove(d);
        services.Add(new ServiceDescriptor(typeof(IConfigurationReleases), sp =>
            new FlakyReleases((IConfigurationReleases)(d.ImplementationFactory is not null ? d.ImplementationFactory(sp) : ActivatorUtilities.CreateInstance(sp, d.ImplementationType!)), sw), d.Lifetime));
    }

    private sealed class FlakyReleases(IConfigurationReleases inner, Switch sw) : IConfigurationReleases
    {
        public Task<ReleaseDto?> FindAsync(Guid releaseId, CancellationToken ct = default) => inner.FindAsync(releaseId, ct);
        public Task<ReleaseDto?> LatestAsync(string kind, Guid organisationId, CancellationToken ct = default)
            => sw.FailCapabilityReads && kind == Kind ? throw new InvalidOperationException("The configuration store is unavailable.") : inner.LatestAsync(kind, organisationId, ct);
    }

    private sealed class SwitchableResolver(ICapabilityResolver inner, Switch sw) : ICapabilityResolver
    {
        public async Task<CapabilitySet> GetAsync(Guid organisationId, CancellationToken ct = default)
        {
            var real = await inner.GetAsync(organisationId, ct);
            var off = sw.Off;
            return off.Length == 0 ? real : CapabilitySet.Build(off.Select(k => new CapabilityEntry(k, false)), real.Items.Values.Select(i => new CapabilityEntry(i.Key, i.Enabled)), "switch");
        }
        public Task<CapabilitySet> PreviewAsync(Guid organisationId, CapabilityDocument draft, CancellationToken ct = default) => inner.PreviewAsync(organisationId, draft, ct);
        public Task<CapabilitySet> AfterPackAsync(Guid organisationId, string packKey, string packVersion, CancellationToken ct = default) => inner.AfterPackAsync(organisationId, packKey, packVersion, ct);
        public Task<PackCapabilities?> PackLayerAsync(Guid organisationId, CancellationToken ct = default) => inner.PackLayerAsync(organisationId, ct);
        public async Task<bool> IsEnabledAsync(Guid organisationId, string key, CancellationToken ct = default) => (await GetAsync(organisationId, ct)).IsEnabled(key);
        public Task<IReadOnlySet<string>> EnabledForCurrentPersonAsync(CancellationToken ct = default) => inner.EnabledForCurrentPersonAsync(ct);
    }

    private static CapabilityInput On(string key, string? note = null) => new(key, "on", note);
    private static CapabilityInput Off(string key, string? note = null) => new(key, "off", note);

    private static Task<CapabilitySet> SetOfAsync(LifecycleWorld w, Guid org, string user = "tenantAdmin") => w.W.AsAsync(user, s => s.Get<ICapabilityResolver>().GetAsync(org));

    /// <summary>Saves the inputs as the organisation's capability draft and validates it. The draft is left open for the caller to publish or discard.</summary>
    private static async Task<DraftDto> DraftAsync(LifecycleWorld w, string admin, Guid org, params CapabilityInput[] inputs)
    {
        var saved = await w.W.AsAsync(admin, s => s.Get<ICapabilityAdministration>().SaveAsync(org, inputs));
        return await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ValidateAsync(saved.Id));
    }

    private static async Task DiscardAsync(LifecycleWorld w, string admin, DraftDto draft) => await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().DiscardDraftAsync(draft.Id));

    private static async Task<ReleaseDto> PublishAsync(LifecycleWorld w, string admin, Guid org, params CapabilityInput[] inputs)
    {
        var draft = await DraftAsync(w, admin, org, inputs);
        Assert.True(draft.IsPublishable, string.Join("; ", draft.Validation!.Messages.Select(m => m.Code + " " + m.Message)));
        return await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "Reviewed"));
    }

    private static string[] Codes(DraftDto draft) => draft.Validation!.Messages.Select(m => m.Code).ToArray();

    // ---- 1. layers ----

    public static async Task Capabilities_resolve_from_the_organisation_then_the_pack_then_the_platform_default(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var jbs = await PackResolutionScenarios.AddOrganisationAsync(w, "Reference Org", "CPJ", TestPacks.Reference);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Org", "CPB", TestPacks.Blank);
        var implied = await PackResolutionScenarios.AddOrganisationAsync(w, "Default Org", "CPD", null);        // no pack of its own: the installation default (the reference pack in these hosts)

        var forJbs = await SetOfAsync(w, jbs.Org);
        Assert.True(forJbs.AllEnabled);
        Assert.All(CapabilityKeys.All, k => Assert.Equal(CapabilityLayer.Pack, forJbs.LayerOf(k)));

        var forBlank = await SetOfAsync(w, blank.Org);
        Assert.Equal([CapabilityKeys.LegalReview, CapabilityKeys.Renewals, CapabilityKeys.SignatureRequests], forBlank.Disabled);
        Assert.Equal([CapabilityKeys.RegisterAuthorisation, CapabilityKeys.Variations], forBlank.Enabled);
        Assert.Equal(CapabilityLayer.Pack, forBlank.LayerOf(CapabilityKeys.LegalReview));
        Assert.Equal("The neutral lifecycle has no legal review step.", forBlank.Find(CapabilityKeys.LegalReview)!.Note);

        Assert.True((await SetOfAsync(w, implied.Org)).AllEnabled);

        // An organisation or tenant the resolver cannot place is never switched off: the platform default is everything on.
        Assert.True((await SetOfAsync(w, Guid.NewGuid())).AllEnabled);
        Assert.True((await SetOfAsync(w, Guid.Empty)).AllEnabled);
    }

    // ---- 2. organisation overrides: publish, win over the pack, roll back, audit, permissions ----

    public static async Task An_organisation_override_wins_over_the_pack_and_publishing_and_rolling_back_take_effect_at_once(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Org", "CPB", TestPacks.Blank);
        var other = await PackResolutionScenarios.AddOrganisationAsync(w, "Other Blank Org", "CPO", TestPacks.Blank);
        var admin = await TerminologyScenarios.AdminForAsync(w, blank.Org, "capAdmin", RoleCodes.OrganisationAdministrator);

        Assert.False((await SetOfAsync(w, blank.Org, admin)).IsEnabled(CapabilityKeys.LegalReview));

        // Release 1: the organisation turns Legal Review on although its pack leaves it off.
        var r1 = await PublishAsync(w, admin, blank.Org, On(CapabilityKeys.LegalReview, "We review high-value agreements"));
        Assert.Equal(1, r1.Number);
        var after1 = await SetOfAsync(w, blank.Org, admin);
        Assert.True(after1.IsEnabled(CapabilityKeys.LegalReview));
        Assert.Equal(CapabilityLayer.Organisation, after1.LayerOf(CapabilityKeys.LegalReview));
        Assert.Equal(CapabilityLayer.Pack, after1.LayerOf(CapabilityKeys.Renewals));      // what the organisation did not decide still comes from the pack
        Assert.False(after1.IsEnabled(CapabilityKeys.Renewals));

        // Another organisation of the same tenant, on the same pack, is not affected.
        Assert.False((await SetOfAsync(w, other.Org)).IsEnabled(CapabilityKeys.LegalReview));

        // Release 2: the organisation changes its mind and says so explicitly (its choice, not the pack's, now keeps it off).
        var r2 = await PublishAsync(w, admin, blank.Org, Off(CapabilityKeys.LegalReview, "Not needed"));
        Assert.Equal(2, r2.Number);
        var after2 = await SetOfAsync(w, blank.Org, admin);
        Assert.False(after2.IsEnabled(CapabilityKeys.LegalReview));
        Assert.Equal(CapabilityLayer.Organisation, after2.LayerOf(CapabilityKeys.LegalReview));

        // Rolling back republishes release 1 as release 3; history is not rewritten.
        var r3 = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().RollbackAsync(Kind, blank.Org, 1, "We do review after all"));
        Assert.Equal(3, r3.Number);
        Assert.Equal(1, r3.RollbackOfNumber);
        Assert.True((await SetOfAsync(w, blank.Org, admin)).IsEnabled(CapabilityKeys.LegalReview));

        var history = await w.W.AsAsync(admin, s => s.Get<ICapabilityAdministration>().HistoryAsync(blank.Org));
        Assert.Equal([3, 2, 1], history.Select(h => h.Release.Number));
        Assert.Contains(history.Single(h => h.Release.Number == 2).Changes, c => c.StartsWith(CapabilityKeys.LegalReview + ": off"));

        var audit = await w.W.AsAsync(admin, s => s.Get<ICapabilityAdministration>().AuditAsync(blank.Org));
        Assert.True(audit.Items.Count >= 3);

        // The overview says where each answer comes from.
        var overview = await w.W.AsAsync(admin, s => s.Get<ICapabilityAdministration>().OverviewAsync(blank.Org));
        Assert.Equal(TestPacks.Blank, overview.PackKey);
        Assert.Equal(3, overview.ReleaseNumber);
        Assert.Equal(CapabilityLayer.Organisation, overview.Rows.Single(r => r.Key == CapabilityKeys.LegalReview).Layer);
        Assert.False(overview.Rows.Single(r => r.Key == CapabilityKeys.LegalReview).PackDefault);
    }

    public static async Task Only_people_with_the_capability_permissions_can_draft_publish_or_roll_back(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Org", "CPB", TestPacks.Blank);
        var admin = await TerminologyScenarios.AdminForAsync(w, blank.Org, "capAdmin", RoleCodes.OrganisationAdministrator);
        await PublishAsync(w, admin, blank.Org, On(CapabilityKeys.LegalReview));

        // A business configuration administrator can prepare a change but not publish it; a contract owner can do neither (a capability is operating-model configuration, never something an ordinary user decides).
        var preparer = await TerminologyScenarios.AdminForAsync(w, blank.Org, "capPreparer", RoleCodes.BusinessConfigurationAdministrator);
        var prepared = await w.W.AsAsync(preparer, s => s.Get<ICapabilityAdministration>().SaveAsync(blank.Org, [Off(CapabilityKeys.LegalReview)]));
        await Assert.ThrowsAnyAsync<Exception>(() => w.W.AsAsync(preparer, s => s.Get<IConfigurationService>().PublishAsync(prepared.Id, "no")));
        await Assert.ThrowsAnyAsync<Exception>(() => w.W.AsAsync(preparer, s => s.Get<IConfigurationService>().RollbackAsync(Kind, blank.Org, 1, "no")));
        await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().DiscardDraftAsync(prepared.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => w.W.AsAsync(blank.User, s => s.Get<ICapabilityAdministration>().SaveAsync(blank.Org, [Off(CapabilityKeys.LegalReview)])));
        await Assert.ThrowsAnyAsync<Exception>(() => w.W.AsAsync(blank.User, s => s.Get<IConfigurationService>().RollbackAsync(Kind, blank.Org, 1, "no")));
        Assert.True((await SetOfAsync(w, blank.Org)).IsEnabled(CapabilityKeys.LegalReview));

        // Switching a capability off or on never changes anyone's permissions: the administrator's own access is the same either way.
        var before = await w.W.AsAsync(admin, s => s.Get<ICapabilityAdministration>().OverviewAsync(blank.Org));
        Assert.True(before.CanDraft && before.CanPublish && before.CanRollback);
    }

    // ---- 3. compatibility: incoherent combinations are errors, not silently accepted ----

    public static async Task Turning_off_a_capability_the_workflows_use_or_another_capability_needs_is_refused(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var jbs = await PackResolutionScenarios.AddOrganisationAsync(w, "Reference Org", "CPJ", TestPacks.Reference);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Org", "CPB", TestPacks.Blank);
        // The first contract installs each organisation's lifecycle from its pack (the published definition the check reads).
        _ = await w.W.AsAsync(jbs.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(jbs.Org, "CPJ-000001", "Reference", jbs.Supplier, jbs.Category)));
        _ = await w.W.AsAsync(blank.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(blank.Org, "CPB-000001", "Blank", blank.Supplier, blank.Category)));
        var jbsAdmin = await TerminologyScenarios.AdminForAsync(w, jbs.Org, "capAdminJ", RoleCodes.OrganisationAdministrator);
        var blankAdmin = await TerminologyScenarios.AdminForAsync(w, blank.Org, "capAdminB", RoleCodes.OrganisationAdministrator);

        // The reference lifecycle uses all five capabilities, so none of them can be switched off while it is published.
        foreach (var key in CapabilityKeys.All)
        {
            var draft = await DraftAsync(w, jbsAdmin, jbs.Org, Off(key));
            Assert.False(draft.IsPublishable, key);
            Assert.Contains(Codes(draft), c => c is "workflow_uses_disabled_capability" or "capability_requires");
            await DiscardAsync(w, jbsAdmin, draft);
        }
        Assert.True((await SetOfAsync(w, jbs.Org)).AllEnabled);

        // Variations need Register Authorisation: turning only the latter off leaves an incoherent set.
        var needs = await DraftAsync(w, blankAdmin, blank.Org, Off(CapabilityKeys.RegisterAuthorisation));
        Assert.False(needs.IsPublishable);
        Assert.Contains("capability_requires", Codes(needs));
        await DiscardAsync(w, blankAdmin, needs);

        // The Blank lifecycle has amendments, so Variations cannot be switched off until a lifecycle without them is published.
        var uses = await DraftAsync(w, blankAdmin, blank.Org, Off(CapabilityKeys.Variations), Off(CapabilityKeys.RegisterAuthorisation));
        Assert.False(uses.IsPublishable);
        Assert.Contains("workflow_uses_disabled_capability", Codes(uses));
        await DiscardAsync(w, blankAdmin, uses);

        // Turning something on is always coherent (the Blank lifecycle simply does not use it).
        var on = await DraftAsync(w, blankAdmin, blank.Org, On(CapabilityKeys.Renewals));
        Assert.True(on.IsPublishable);
        await DiscardAsync(w, blankAdmin, on);
    }

    public static async Task A_capability_cannot_be_turned_off_from_under_work_in_flight(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Org", "CPB", TestPacks.Blank);
        var admin = await TerminologyScenarios.AdminForAsync(w, blank.Org, "capAdmin", RoleCodes.OrganisationAdministrator);
        var contract = await w.W.AsAsync(blank.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(blank.Org, "CPB-000001", "In flight", blank.Supplier, blank.Category,
            CurrencyCode: "AUD", EstimatedValue: 1000m, EndDate: DateOnly.FromDateTime(w.W.Now.AddDays(300)))));
        await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contract, "Activate")));
        _ = await w.W.AsAsync(blank.User, s => s.Get<IVariationService>().CreateAsync(contract,
            new VariationInput("Extend term", "Summary", "Reason", null, false, null, null, null, new Dictionary<string, string?> { ["endDate"] = DateOnly.FromDateTime(w.W.Now.AddDays(500)).ToString("yyyy-MM-dd") })));

        var draft = await DraftAsync(w, admin, blank.Org, Off(CapabilityKeys.Variations), Off(CapabilityKeys.RegisterAuthorisation));
        Assert.False(draft.IsPublishable);
        Assert.Contains("capability_in_use", Codes(draft));
        var finding = draft.Validation!.Messages.First(m => m.Code == "capability_in_use");
        Assert.Contains("still under way", finding.Message);
        await DiscardAsync(w, admin, draft);

        // The overview and preview report the same finding without publishing anything.
        await DraftAsync(w, admin, blank.Org, Off(CapabilityKeys.Variations), Off(CapabilityKeys.RegisterAuthorisation));
        var preview = await w.W.AsAsync(admin, s => s.Get<ICapabilityAdministration>().PreviewAsync(blank.Org));
        Assert.True(preview.HasDraft);
        Assert.Contains(preview.Findings, f => f.Code == "capability_in_use" && f.Capability == CapabilityKeys.Variations);
        Assert.Contains(preview.Changes, c => c.Key == CapabilityKeys.Variations && c.Now && !c.After);
        Assert.True((await SetOfAsync(w, blank.Org)).IsEnabled(CapabilityKeys.Variations));
    }

    // ---- 4. packs: assignment preview and refusal ----

    public static async Task Assigning_a_pack_whose_defaults_do_not_fit_the_organisation_is_refused_with_the_reason(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Reference Org", "CPJ", TestPacks.Reference);
        _ = await w.W.AsAsync(org.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(org.Org, "CPJ-000001", "Reference", org.Supplier, org.Category)));
        var admin = await TerminologyScenarios.AdminForAsync(w, org.Org, "capAdminPack", RoleCodes.OrganisationAdministrator);
        var blankVersion = (await w.W.AsAsync(admin, s => s.Get<IPackLoader>().FindAsync(TestPacks.Blank, null, true))) !.Version;

        var preview = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().PreviewAsync(org.Org, TestPacks.Blank, blankVersion));
        Assert.Contains(preview.Capabilities, c => c.Key == CapabilityKeys.LegalReview && c.Now && !c.After);
        Assert.Contains(preview.Capabilities, c => c.Key == CapabilityKeys.Variations && c.Now && c.After);
        Assert.Contains(preview.CapabilityFindings, f => f.Severity == ValidationSeverity.Error && f.Code == "workflow_uses_disabled_capability");

        var refused = await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IPackAssignments>().AssignAsync(org.Org, TestPacks.Blank, blankVersion)));
        Assert.Equal("capability_conflict", refused.Code);

        // Nothing changed: the organisation still works from its pack with every capability on.
        Assert.True((await SetOfAsync(w, org.Org)).AllEnabled);
    }

    // ---- 5. the engine gate: no new starts when a capability is off, work in flight can finish ----

    public static async Task A_disabled_capability_starts_nothing_new_but_work_under_way_can_finish(TestApp app, Switch sw)
    {
        var w = await LifecycleWorld.CreateAsync(app);

        // Legal Review: off means a draft cannot be submitted, and the step is not offered.
        var draft = await w.NewDraftAsync(legal: true);
        sw.Off = [CapabilityKeys.LegalReview];
        var offered = await w.As("ownerPackaging", s => s.Get<ILifecycleService>().AvailableAsync(draft));
        Assert.DoesNotContain(offered, t => t.Role == "submitLegal");
        Assert.Contains(offered, t => t.Code == "ProceedWithoutLegal");
        var refused = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, draft, "Please review"))));
        Assert.Equal("capability_disabled", refused.Code);
        Assert.Equal("Draft", await w.StatusAsync(draft));

        // The same review, begun while the capability was on, can be finished after it is turned off.
        sw.Off = [];
        var inflight = await w.NewDraftAsync(legal: true);
        await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, inflight, "Please review")));
        var review = await w.As("ownerPackaging", s => s.Get<ILegalReviewService>().FindForSubjectAsync(SubjectKinds.Contract, inflight));
        sw.Off = [CapabilityKeys.LegalReview];
        await w.As("legalA", s => s.Get<ILegalReviewService>().BeginAsync(review!.ReviewId));
        await w.As("legalA", s => s.Get<ILegalReviewService>().ClearAsync(review!.ReviewId, "Acceptable"));
        Assert.Equal("LegalCleared", await w.StatusAsync(inflight));

        // Register Authorisation and Signature Requests: a contract that reached the end of the process before they were turned off.
        sw.Off = [];
        var toSign = await w.NewDraftAsync(legal: false);
        await w.As("ownerPackaging", s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(toSign, "ProceedWithoutLegal")));
        await w.AddDocumentAsync(toSign, "FinalAgreement", "Final agreement");
        sw.Off = [CapabilityKeys.SignatureRequests];
        var noSignature = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, toSign, "manual")));
        Assert.Equal("capability_disabled", noSignature.Code);

        sw.Off = [];
        await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, toSign, "manual"));
        await w.As("ownerPackaging", s => s.Get<ISignatureRequestService>().RecordExecutedCopyAsync(new RecordExecutedCopyRequest(SubjectKinds.Contract, toSign, "executed.pdf", "application/pdf", LifecycleWorld.Bytes("executed"))));
        sw.Off = [CapabilityKeys.RegisterAuthorisation];
        var noRegister = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, toSign, "Executed copy attached")));
        Assert.Equal("capability_disabled", noRegister.Code);

        sw.Off = [];
        var request = await w.As("ownerPackaging", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, toSign, "Executed copy attached"));
        sw.Off = [CapabilityKeys.RegisterAuthorisation];
        await w.As("groupHead", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "Approved"));         // already submitted: it can be decided
        Assert.Equal("Effective", await w.StatusAsync(toSign));

        // Variations and Renewals on the now-Effective contract.
        sw.Off = [CapabilityKeys.Variations];
        var input = new VariationInput("Extend term", "Summary", "Reason", null, false, null, null, null, new Dictionary<string, string?> { ["endDate"] = DateOnly.FromDateTime(w.W.Now.AddDays(500)).ToString("yyyy-MM-dd") });
        var noVariation = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(toSign, input)));
        Assert.Equal("capability_disabled", noVariation.Code);
        Assert.Equal("Effective", await w.StatusAsync(toSign));

        sw.Off = [];
        var variation = await w.As("ownerPackaging", s => s.Get<IVariationService>().CreateAsync(toSign, input));
        sw.Off = [CapabilityKeys.Variations];
        await w.As("ownerPackaging", s => s.Get<IVariationService>().CancelAsync(variation, "Not needed"));              // work under way can be cancelled
        Assert.Equal("Effective", await w.StatusAsync(toSign));

        sw.Off = [CapabilityKeys.Renewals];
        var noRenewal = await Assert.ThrowsAsync<DomainException>(() => w.As("ownerPackaging", s => s.Get<IRenewalService>().StartAsync(toSign, "Renew")));
        Assert.Equal("capability_disabled", noRenewal.Code);
        sw.Off = [];
        await w.As("ownerPackaging", s => s.Get<IRenewalService>().StartAsync(toSign, "Renew"));
        sw.Off = [CapabilityKeys.Renewals];
        await w.As("ownerPackaging", s => s.Get<IRenewalService>().CancelAsync(toSign, "Not renewing"));
        Assert.Equal("Effective", await w.StatusAsync(toSign));
    }

    // ---- 6. the proof: a business operates with all five switched off ----

    private static string MinimalLifecycle()
    {
        var path = Path.Combine(RepoRoot.Find(), "templates", "packs", "blank", "workflows", "contract-lifecycle.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        // Remove everything the Variations capability owns, found only through the capability catalogue: no state or step is named here.
        var states = root["states"]!.AsArray();
        foreach (var s in states.Where(s => CapabilityCatalogue.OfStateRole(s!["role"]!.GetValue<string>()) is not null).ToList()) states.Remove(s);
        var transitions = root["transitions"]!.AsArray();
        foreach (var t in transitions.Where(t => CapabilityCatalogue.OfTransitionRole(t!["role"]!.GetValue<string>()) is not null).ToList()) transitions.Remove(t);
        // And the guards it owns on the steps that remain (a business without Variations does not check for an open Variation when it closes an agreement).
        foreach (var t in transitions)
            if (t!["guards"] is JsonArray guards)
                foreach (var g in guards.Where(g => CapabilityCatalogue.OfGuard(g!.GetValue<string>()) is not null).ToList()) guards.Remove(g);
        return root.ToJsonString();
    }

    public static async Task A_business_can_operate_with_all_five_capabilities_switched_off_with_no_code_change(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Neutral Org", "CPN", TestPacks.Blank);
        var admin = await TerminologyScenarios.AdminForAsync(w, blank.Org, "capAdmin", RoleCodes.OrganisationAdministrator);
        // The first contract installs the Blank lifecycle (which has amendments, so Variations are in use).
        var first = await w.W.AsAsync(blank.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(blank.Org, "CPN-000001", "Before", blank.Supplier, blank.Category,
            CurrencyCode: "AUD", EstimatedValue: 1000m, EndDate: DateOnly.FromDateTime(w.W.Now.AddDays(300)))));
        Assert.Equal("Open", (await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().StateAsync(first))).Status);

        // 1. The organisation publishes a lifecycle without Variations, through the ordinary configuration lifecycle.
        var lifecycle = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(WorkflowKinds.ContractLifecycle, blank.Org, MinimalLifecycle()));
        if (lifecycle.ContentJson != MinimalLifecycle()) lifecycle = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().SaveDraftAsync(lifecycle.Id, MinimalLifecycle()));
        var validated = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ValidateAsync(lifecycle.Id));
        Assert.True(validated.IsPublishable, string.Join("; ", validated.Validation!.Messages.Select(m => m.Code + " " + m.Message)));
        await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().PublishAsync(lifecycle.Id, "No amendments"));

        // 2. The organisation says all five are off. Variations need Register Authorisation, so both go together; the rest are already off in its pack.
        var release = await PublishAsync(w, admin, blank.Org, Off(CapabilityKeys.LegalReview), Off(CapabilityKeys.RegisterAuthorisation), Off(CapabilityKeys.Variations), Off(CapabilityKeys.Renewals), Off(CapabilityKeys.SignatureRequests));
        Assert.Equal(1, release.Number);
        var set = await SetOfAsync(w, blank.Org, admin);
        Assert.Equal(CapabilityKeys.All, set.Disabled);
        Assert.Empty(set.Enabled);
        Assert.All(CapabilityKeys.All, k => Assert.Equal(CapabilityLayer.Organisation, set.LayerOf(k)));

        // 3. A contract is created, activated and closed. Nothing optional is in the way, and no optional step is offered.
        var contract = await w.W.AsAsync(blank.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(blank.Org, "CPN-000002", "Operates without optional steps", blank.Supplier, blank.Category,
            CurrencyCode: "AUD", EstimatedValue: 5000m, EndDate: DateOnly.FromDateTime(w.W.Now.AddDays(300)))));
        var offered = await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().AvailableAsync(contract));
        Assert.All(offered, t => Assert.Null(CapabilityCatalogue.OfTransitionRole(t.Role)));
        await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contract, "Activate")));
        var active = await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().StateAsync(contract));
        Assert.Equal("Active", active.Status);
        Assert.True(active.Locked);
        var activeSteps = await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().AvailableAsync(contract));
        Assert.All(activeSteps, t => Assert.Null(CapabilityCatalogue.OfTransitionRole(t.Role)));
        await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contract, "Close")));
        Assert.Equal("Closed", (await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().StateAsync(contract))).Status);

        // 4. None of the five can be started. (The permission to try is not the question: the organisation does not use them.)
        var open = await w.W.AsAsync(blank.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(blank.Org, "CPN-000003", "Still active", blank.Supplier, blank.Category,
            CurrencyCode: "AUD", EstimatedValue: 5000m, EndDate: DateOnly.FromDateTime(w.W.Now.AddDays(300)))));
        await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(open, "Activate")));
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(blank.User, s => s.Get<IVariationService>().CreateAsync(open,
            new VariationInput("Extend", "Summary", "Reason", null, false, null, null, null, new Dictionary<string, string?> { ["endDate"] = DateOnly.FromDateTime(w.W.Now.AddDays(500)).ToString("yyyy-MM-dd") }))));
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(blank.User, s => s.Get<IRenewalService>().StartAsync(open, "Renew")));
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(blank.User, s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, open, "Review"))));
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(blank.User, s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, open, "manual")));
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(blank.User, s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, open, "Submit")));
        Assert.Equal("Active", (await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().StateAsync(open))).Status);

        // 5. What a person can reach (navigation, dashboards, views) contains none of the five.
        var reachable = await w.W.AsAsync(blank.User, s => s.Get<ICapabilityResolver>().EnabledForCurrentPersonAsync());
        Assert.Empty(reachable);

        // 6. Nothing is incoherent: the compatibility check reports no error for this organisation.
        var overview = await w.W.AsAsync(admin, s => s.Get<ICapabilityAdministration>().OverviewAsync(blank.Org));
        Assert.DoesNotContain(overview.Findings, f => f.Severity == ValidationSeverity.Error);

        // 7. And it is reversible: turning Renewals on again is one published change, and rolling back restores the all-off position.
        await PublishAsync(w, admin, blank.Org, Off(CapabilityKeys.LegalReview), Off(CapabilityKeys.RegisterAuthorisation), Off(CapabilityKeys.Variations), On(CapabilityKeys.Renewals), Off(CapabilityKeys.SignatureRequests));
        Assert.True((await SetOfAsync(w, blank.Org, admin)).IsEnabled(CapabilityKeys.Renewals));
        await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().RollbackAsync(Kind, blank.Org, 1, "Back to the simple process"));
        Assert.False((await SetOfAsync(w, blank.Org, admin)).IsEnabled(CapabilityKeys.Renewals));
    }

    // ---- 7. the cache: keyed by tenant, organisation and release; invalidated by publishing; a failing source never switches anything off ----

    public static async Task Resolved_sets_are_cached_per_organisation_and_a_publish_is_seen_at_once(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var a = await PackResolutionScenarios.AddOrganisationAsync(w, "Org A", "CPA", TestPacks.Blank);
        var b = await PackResolutionScenarios.AddOrganisationAsync(w, "Org B", "CPC", TestPacks.Blank);
        var admin = await TerminologyScenarios.AdminForAsync(w, a.Org, "capAdmin", RoleCodes.OrganisationAdministrator);
        var cache = app.Services.GetRequiredService<ICapabilityCache>();
        cache.Clear();

        _ = await SetOfAsync(w, a.Org);
        var afterFirst = cache.Statistics;
        _ = await SetOfAsync(w, a.Org);
        Assert.Equal(afterFirst.Hits + 1, cache.Statistics.Hits);

        _ = await SetOfAsync(w, b.Org);
        Assert.True(cache.Statistics.Entries >= 2);                                   // separate entries per organisation

        await PublishAsync(w, admin, a.Org, On(CapabilityKeys.Renewals));
        Assert.True((await SetOfAsync(w, a.Org)).IsEnabled(CapabilityKeys.Renewals));   // seen at once
        Assert.False((await SetOfAsync(w, b.Org)).IsEnabled(CapabilityKeys.Renewals)); // the other organisation is untouched
    }
    // ---- 8. what people see: dashboards, built-in views and register fields follow the organisation's capabilities ----

    public static async Task Dashboards_views_and_register_fields_show_only_what_the_organisation_uses(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var jbs = await PackResolutionScenarios.AddOrganisationAsync(w, "Reference Org", "CPJ", TestPacks.Reference);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Org", "CPB", TestPacks.Blank);

        var jbsCards = (await w.W.AsAsync(jbs.User, s => s.Get<IDashboardService>().OverviewAsync())).Select(c => c.Key).ToList();
        var blankCards = (await w.W.AsAsync(blank.User, s => s.Get<IDashboardService>().OverviewAsync())).Select(c => c.Key).ToList();
        foreach (var key in CapabilityCatalogue.All.SelectMany(c => c.ViewKeys)) Assert.Contains(key, jbsCards);                 // every card for the organisation that uses everything
        Assert.DoesNotContain(DashboardKeys.AwaitingLegal, blankCards);                                                         // Legal Review is off
        Assert.DoesNotContain(DashboardKeys.RenewalsInProgress, blankCards);                                                    // Renewals are off
        Assert.Contains(DashboardKeys.VariationsInProgress, blankCards);                                                        // Variations are on
        Assert.Contains(DashboardKeys.PendingApproval, blankCards);                                                             // Register Authorisation is on
        Assert.Contains(DashboardKeys.MyContracts, blankCards);                                                                 // generic cards are never affected
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.W.AsAsync(blank.User, s => s.Get<IDashboardService>().CardAsync(DashboardKeys.AwaitingLegal)));

        var jbsViews = (await w.W.AsAsync(jbs.User, s => s.Get<Ecp.Contracts.Register.IContractViews>().ListAsync(jbs.Org))).Select(v => v.Ref).ToList();
        var blankViews = (await w.W.AsAsync(blank.User, s => s.Get<Ecp.Contracts.Register.IContractViews>().ListAsync(blank.Org))).Select(v => v.Ref).ToList();
        Assert.Contains("system:awaiting-legal", jbsViews);
        Assert.DoesNotContain("system:awaiting-legal", blankViews);
        Assert.DoesNotContain("system:renewals-in-progress", blankViews);
        Assert.Contains("system:variations-in-progress", blankViews);
        Assert.Contains("system:all-contracts", blankViews);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.W.AsAsync(blank.User, s => s.Get<Ecp.Contracts.Register.IContractViews>().GetAsync("system:awaiting-legal", blank.Org)));

        var jbsFields = (await w.W.AsAsync(jbs.User, s => s.Get<Ecp.Contracts.Register.IContractRegister>().CatalogueAsync(jbs.Org))).Fields.Select(f => f.Key).ToList();
        var blankFields = (await w.W.AsAsync(blank.User, s => s.Get<Ecp.Contracts.Register.IContractRegister>().CatalogueAsync(blank.Org))).Fields.Select(f => f.Key).ToList();
        Assert.Contains("legalReviewRequired", jbsFields);
        Assert.Contains("registeredDate", jbsFields);
        Assert.DoesNotContain("legalReviewRequired", blankFields);
        Assert.DoesNotContain("legalReviewStatus", blankFields);
        Assert.Contains("registeredDate", blankFields);                                                                         // Register Authorisation is on for the Blank pack
        Assert.Contains("agreementReference", blankFields);

        // What a person can reach: the Blank organisation's owner reaches only the capabilities that are on there.
        var reach = await w.W.AsAsync(blank.User, s => s.Get<ICapabilityResolver>().EnabledForCurrentPersonAsync());
        Assert.Equal([CapabilityKeys.RegisterAuthorisation, CapabilityKeys.Variations], reach.Order().ToArray());
    }
    // ---- 9. a failing configuration source never switches anything off ----

    public static async Task When_the_capability_configuration_cannot_be_read_every_capability_is_treated_as_on(TestApp app, Switch sw)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Org", "CPB", TestPacks.Blank);
        app.Services.GetRequiredService<ICapabilityCache>().Clear();
        Assert.False((await SetOfAsync(w, blank.Org)).IsEnabled(CapabilityKeys.LegalReview));

        // The store fails: the platform behaves as it did before capabilities were configurable (everything on), the failure is not cached, and the next healthy read is right again.
        app.Services.GetRequiredService<ICapabilityCache>().Clear();
        sw.FailCapabilityReads = true;
        Assert.True((await SetOfAsync(w, blank.Org)).AllEnabled);
        sw.FailCapabilityReads = false;
        Assert.False((await SetOfAsync(w, blank.Org)).IsEnabled(CapabilityKeys.LegalReview));
    }
}

public sealed class CapabilityScenarioTests_Sqlite
{
    [Fact] public Task Layers() => ProviderRunner.SqliteAsync(CapabilityScenarios.Capabilities_resolve_from_the_organisation_then_the_pack_then_the_platform_default);
    [Fact] public Task Overrides() => ProviderRunner.SqliteAsync(CapabilityScenarios.An_organisation_override_wins_over_the_pack_and_publishing_and_rolling_back_take_effect_at_once);
    [Fact] public Task Permissions() => ProviderRunner.SqliteAsync(CapabilityScenarios.Only_people_with_the_capability_permissions_can_draft_publish_or_roll_back);
    [Fact] public Task Compatibility() => ProviderRunner.SqliteAsync(CapabilityScenarios.Turning_off_a_capability_the_workflows_use_or_another_capability_needs_is_refused);
    [Fact] public Task In_flight() => ProviderRunner.SqliteAsync(CapabilityScenarios.A_capability_cannot_be_turned_off_from_under_work_in_flight);
    [Fact] public Task Pack_assignment() => ProviderRunner.SqliteAsync(CapabilityScenarios.Assigning_a_pack_whose_defaults_do_not_fit_the_organisation_is_refused_with_the_reason);
    [Fact] public Task Gate() { var sw = new CapabilityScenarios.Switch(); return ProviderRunner.SqliteAsync(app => CapabilityScenarios.A_disabled_capability_starts_nothing_new_but_work_under_way_can_finish(app, sw), svc => { LifecycleScenarios.Scanner(svc); CapabilityScenarios.Install(svc, sw); }); }
    [Fact] public Task All_five_off() => ProviderRunner.SqliteAsync(CapabilityScenarios.A_business_can_operate_with_all_five_capabilities_switched_off_with_no_code_change);
    [Fact] public Task Cache() => ProviderRunner.SqliteAsync(CapabilityScenarios.Resolved_sets_are_cached_per_organisation_and_a_publish_is_seen_at_once);
    [Fact] public Task Fail_open() { var sw = new CapabilityScenarios.Switch(); return ProviderRunner.SqliteAsync(app => CapabilityScenarios.When_the_capability_configuration_cannot_be_read_every_capability_is_treated_as_on(app, sw), svc => CapabilityScenarios.InstallFlakyReleases(svc, sw)); }
    [Fact] public Task Pages_data() => ProviderRunner.SqliteAsync(CapabilityScenarios.Dashboards_views_and_register_fields_show_only_what_the_organisation_uses);
}

[Trait("Category", "SqlServer")]
public sealed class CapabilityScenarioTests_SqlServer
{
    [SqlServerFact] public Task Layers() => ProviderRunner.SqlServerAsync(CapabilityScenarios.Capabilities_resolve_from_the_organisation_then_the_pack_then_the_platform_default);
    [SqlServerFact] public Task Overrides() => ProviderRunner.SqlServerAsync(CapabilityScenarios.An_organisation_override_wins_over_the_pack_and_publishing_and_rolling_back_take_effect_at_once);
    [SqlServerFact] public Task Permissions() => ProviderRunner.SqlServerAsync(CapabilityScenarios.Only_people_with_the_capability_permissions_can_draft_publish_or_roll_back);
    [SqlServerFact] public Task Compatibility() => ProviderRunner.SqlServerAsync(CapabilityScenarios.Turning_off_a_capability_the_workflows_use_or_another_capability_needs_is_refused);
    [SqlServerFact] public Task In_flight() => ProviderRunner.SqlServerAsync(CapabilityScenarios.A_capability_cannot_be_turned_off_from_under_work_in_flight);
    [SqlServerFact] public Task Pack_assignment() => ProviderRunner.SqlServerAsync(CapabilityScenarios.Assigning_a_pack_whose_defaults_do_not_fit_the_organisation_is_refused_with_the_reason);
    [SqlServerFact] public Task Gate() { var sw = new CapabilityScenarios.Switch(); return ProviderRunner.SqlServerAsync(app => CapabilityScenarios.A_disabled_capability_starts_nothing_new_but_work_under_way_can_finish(app, sw), svc => { LifecycleScenarios.Scanner(svc); CapabilityScenarios.Install(svc, sw); }); }
    [SqlServerFact] public Task All_five_off() => ProviderRunner.SqlServerAsync(CapabilityScenarios.A_business_can_operate_with_all_five_capabilities_switched_off_with_no_code_change);
    [SqlServerFact] public Task Cache() => ProviderRunner.SqlServerAsync(CapabilityScenarios.Resolved_sets_are_cached_per_organisation_and_a_publish_is_seen_at_once);
    [SqlServerFact] public Task Fail_open() { var sw = new CapabilityScenarios.Switch(); return ProviderRunner.SqlServerAsync(app => CapabilityScenarios.When_the_capability_configuration_cannot_be_read_every_capability_is_treated_as_on(app, sw), svc => CapabilityScenarios.InstallFlakyReleases(svc, sw)); }
    [SqlServerFact] public Task Pages_data() => ProviderRunner.SqlServerAsync(CapabilityScenarios.Dashboards_views_and_register_fields_show_only_what_the_organisation_uses);
}
