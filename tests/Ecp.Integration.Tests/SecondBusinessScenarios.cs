using Ecp.Approvals;
using Ecp.Approvals.Domain;
using Ecp.Approvals.Resolution;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Register;
using Ecp.Identity;
using Ecp.Identity.Domain;
using System.Text.Json;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Notifications;
using Ecp.Notifications.Policy;
using Microsoft.EntityFrameworkCore;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Slice S9: the second-business proof (docs/SECOND_BUSINESS_PROOF.md, SBP-AC-01 to SBP-AC-19). A synthetic US general contractor runs on the shipped external pack construction-subcontracts. The scenarios drive the
/// product through the services the pages call; they never write to a table and never tell the platform a construction word.
/// </summary>
internal static class SecondBusinessScenarios
{
    public static async Task The_journey_from_upload_to_a_published_onboarded_organisation_needs_no_rebuild(TestApp app)
    {
        var w = await HarborlineWorld.CreateTenantAsync(app);
        var up = await w.UploadAndInstallAsync();
        Assert.DoesNotContain(up.Report.Issues, i => i.IsError);
        // A pack that is not on the file system (or in the pack store) is refused with the ordinary unknown-pack error: the pack is the only thing that makes the business exist.
        var absent = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IPackAssignments>().AssignAsync(w.Org, HarborlineWorld.PackKey, "9.9.9")));
        Assert.Equal("pack_not_available", absent.Code);
        var assigned = await w.AssignAsync();
        // every kind the pack carries, other than the capability defaults and the terminology that already apply as a layer, became a draft
        foreach (var kind in HarborlineWorld.PublicationOrder) Assert.Contains(assigned.Kinds, k => k.Kind == kind);
        Assert.All(assigned.Kinds.Where(k => k.Kind != CapabilityKinds.Name), k => Assert.False(k.NeedsAttention, k.Kind + ": " + k.Outcome + " " + k.Detail));
        await w.PublishAllAsync();
        await w.OnboardAsync();
        Assert.Equal(8, w.Categories.Count);
        Assert.Equal(2, w.Sites.Count);
    }

    // ---- SBP-AC-05: the whole lifecycle, every state and every step of both workflows, computed from the pack's own documents ----

    private static (HashSet<string> States, HashSet<(string Code, string From)> Steps) Definition(string kind)
    {
        using var doc = JsonDocument.Parse(HarborlineWorld.PackDocuments()[kind]);
        var states = doc.RootElement.GetProperty("states").EnumerateArray().Select(x => x.GetProperty("code").GetString()!).ToHashSet();
        var steps = doc.RootElement.GetProperty("transitions").EnumerateArray()
            .SelectMany(t => t.GetProperty("from").EnumerateArray().Select(f => (t.GetProperty("code").GetString()!, f.GetString()!))).ToHashSet();
        return (states, steps);
    }

    public static async Task Every_state_and_every_step_of_the_lifecycle_and_the_change_order_workflow_is_visited(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var contracts = new List<Guid>();

        // A: internal review and back, signature declined and re-sent, activation rejected and re-submitted, then approved.
        var a = await w.NewSubcontractAsync(); contracts.Add(a);
        await w.Step("packageManagerA", a, "SubmitForInternalReview");
        await w.Step("commercial", a, "ReturnToDraft", comment: "Scope summary needs the trade split.");
        Assert.Equal("Draft", await w.StatusAsync(a));
        await w.Step("packageManagerA", a, "SubmitForInternalReview");
        Assert.Equal("InternalReview", await w.StatusAsync(a));
        await w.Step("commercial", a, "ReleaseForSignature");
        await w.AddDocumentAsync("packageManagerA", a);
        await w.AsAsync("packageManagerA", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, a, "mock"));
        Assert.Equal("SentForSignature", await w.StatusAsync(a));
        await w.AsAsync("packageManagerA", s => s.Get<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Contract, a, SignatureRequestStatus.Declined));
        await w.Step("packageManagerA", a, "ReturnToReady");
        Assert.Equal("ReadyForSignature", await w.StatusAsync(a));
        await w.SignAsync(a);
        var request = await w.AsAsync("packageManagerA", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, a, "Executed copy attached"));
        Assert.Equal("AwaitingActivation", await w.StatusAsync(a));
        await w.AsAsync("director1", s => s.Get<IRegisterAuthorisationService>().RejectAsync(request, "The retention percentage is missing."));
        Assert.Equal("ReadyForSignature", await w.StatusAsync(a));
        await w.SignAsync(a);
        request = await w.AsAsync("packageManagerA", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, a, "Corrected"));
        await w.ApproveAllAsync(request);
        Assert.Equal("Active", await w.StatusAsync(a));
        Assert.True((await w.AsAsync("packageManagerA", s => s.Get<ILifecycleService>().StateAsync(a))).Locked);

        // B: change orders from an active subcontract take every step of their own workflow: cancelled at each of its three early states, a signature declined and re-sent, rejected, and applied.
        async Task<Guid> Open(string title, string value = "110000") => await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().CreateAsync(a, ChangeOrder(title, value)));
        Task Cancel(Guid co) => w.AsAsync("packageManagerA", s => s.Get<IVariationService>().CancelAsync(co, "Not needed"));
        var co1 = await Open("Cancelled while open");
        Assert.Equal("ChangeOrderOpen", await w.StatusAsync(a));
        await Cancel(co1);
        Assert.Equal("Active", await w.StatusAsync(a));
        Assert.Equal(100_000m, (await w.FactsAsync(a)).EstimatedValue);
        var co2 = await Open("Cancelled when prepared");
        await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().TransitionAsync(co2, "Prepare", null));
        await Cancel(co2);
        var co3 = await Open("Cancelled while out for signature");
        await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().TransitionAsync(co3, "Prepare", null));
        await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().AddDocumentAsync(new AddVariationDocumentRequest(co3, "Final", "Change order", "co.pdf", "application/pdf", HarborlineWorld.Bytes("co"))));
        await w.AsAsync("packageManagerA", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Variation, co3, "mock"));
        await Cancel(co3);
        Assert.Equal("Active", await w.StatusAsync(a));
        var co4 = await Open("Declined, re-sent, then rejected", "120000");
        await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().TransitionAsync(co4, "Prepare", null));
        await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().AddDocumentAsync(new AddVariationDocumentRequest(co4, "Final", "Change order", "co.pdf", "application/pdf", HarborlineWorld.Bytes("co"))));
        await w.AsAsync("packageManagerA", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Variation, co4, "mock"));
        await w.AsAsync("packageManagerA", s => s.Get<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Variation, co4, SignatureRequestStatus.Declined));
        await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().TransitionAsync(co4, "ReturnToPrepared", null));
        await w.AsAsync("packageManagerA", s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Variation, co4, "mock"));
        await w.AsAsync("packageManagerA", s => s.Get<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Variation, co4, SignatureRequestStatus.Completed));
        var rejectedRequest = await w.AsAsync("packageManagerA", s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Variation, co4, "Signed change order"));
        await w.AsAsync("director1", s => s.Get<IRegisterAuthorisationService>().RejectAsync(rejectedRequest, "Not justified."));
        Assert.Equal("Active", await w.StatusAsync(a));
        Assert.Equal(100_000m, (await w.FactsAsync(a)).EstimatedValue);
        var applied = await w.ChangeOrderToApprovalAsync(a, ChangeOrder("Add rebar, agreed", value: "120000", time: "10"));
        await w.ApproveAllAsync(applied.RequestId);
        Assert.Equal("Active", await w.StatusAsync(a));
        Assert.Equal(120_000m, (await w.FactsAsync(a)).EstimatedValue);

        // C: stop work, resume, stop again, terminate from stop work.
        await w.Step("packageManagerA", a, "StopWork", reason: "Site closed by the owner.");
        Assert.Equal("StopWork", await w.StatusAsync(a));
        await w.Step("packageManagerA", a, "ResumeWork", reason: "Site reopened.");
        await w.Step("packageManagerA", a, "StopWork", reason: "Utilities conflict.");
        await w.Step("commercial", a, "Terminate", reason: "Breach of the safety plan.");
        Assert.Equal("Terminated", await w.StatusAsync(a));

        // D: completed. E: terminated from active. F: withdrawn from each of the three states before signature.
        var d = await w.ToActiveAsync(); contracts.Add(d);
        await w.Step("commercial", d, "Complete");
        Assert.Equal("Completed", await w.StatusAsync(d));
        var e = await w.ToActiveAsync(); contracts.Add(e);
        await w.Step("commercial", e, "Terminate", reason: "Insolvency.");
        var f1 = await w.NewSubcontractAsync(); contracts.Add(f1);
        await w.Step("packageManagerA", f1, "WithdrawDraft", reason: "Package re-let.");
        var f2 = await w.NewSubcontractAsync(); contracts.Add(f2);
        await w.Step("packageManagerA", f2, "SubmitForInternalReview");
        await w.Step("packageManagerA", f2, "WithdrawDraft", reason: "Package re-let.");
        var f3 = await w.NewSubcontractAsync(); contracts.Add(f3);
        await w.ToReadyForSignatureAsync(f3);
        await w.Step("packageManagerA", f3, "WithdrawDraft", reason: "Package re-let.");
        foreach (var id in new[] { f1, f2, f3 }) Assert.Equal("Withdrawn", await w.StatusAsync(id));

        // Coverage, computed from the definitions in the pack so a state or step added later without a scenario fails here.
        var history = new List<HistoryItem>();
        foreach (var id in contracts.Append(a)) history.AddRange(await w.AsAsync("commercial", s => s.Get<ILifecycleService>().HistoryAsync(id)));
        void Covered(string kind, string subject)
        {
            var (states, steps) = Definition(kind);
            var rows = history.Where(h => h.SubjectKind == subject).ToList();
            var visited = rows.SelectMany(h => new[] { h.FromStatus, h.ToStatus }).ToHashSet();
            var taken = rows.Select(h => (h.TransitionCode, h.FromStatus)).ToHashSet();
            var missingStates = states.Where(x => !visited.Contains(x)).ToList();
            var missingSteps = steps.Where(x => !taken.Contains(x)).ToList();
            Assert.True(missingStates.Count == 0, $"{subject}: states never visited: {string.Join(", ", missingStates)}");
            Assert.True(missingSteps.Count == 0, $"{subject}: steps never taken: {string.Join(", ", missingSteps)}");
        }
        Covered("WorkflowContractLifecycle", "Contract");
        Covered("WorkflowVariation", "Variation");
    }

    // ---- SBP-AC-06: approvals, table driven, through the resolver the live submission also uses ----

    public static async Task Activation_approval_follows_the_value_tiers_the_exceptions_the_currency_and_the_people(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var probe = await w.NewSubcontractAsync();
        var bond = new Dictionary<string, string?> { ["bond_required"] = "true" };

        // value, risk, custom facts, expected plan shape. The maximum of a tier is exclusive: the boundary value belongs to the next tier.
        var table = new (decimal Value, string? Risk, Dictionary<string, string?>? Custom, string Expected)[]
        {
            (0m, null, null, "project-director@0:FirstResponse"),
            (249_999.99m, null, null, "project-director@0:FirstResponse"),
            (250_000m, null, null, "project-director@0:FirstResponse, commercial-manager@1:FirstResponse"),
            (1_999_999.99m, null, null, "project-director@0:FirstResponse, commercial-manager@1:FirstResponse"),
            (2_000_000m, null, null, "commercial-manager@0:AllRequired, project-director@0:AllRequired, regional-director@0:AllRequired"),
            (100_000m, "High", null, "project-director@0:FirstResponse, safety-risk@1:FirstResponse"),
            (100_000m, "Low", null, "project-director@0:FirstResponse"),
            (100_000m, null, bond, "project-director@0:FirstResponse"),                                                                         // bonded work below the threshold adds nothing
            (300_000m, null, bond, "project-director@0:FirstResponse, commercial-manager@1:FirstResponse, bond-check@2:FirstResponse"),
            (3_000_000m, "Critical", bond, "commercial-manager@0:AllRequired, project-director@0:AllRequired, regional-director@0:AllRequired, safety-risk@1:FirstResponse, bond-check@2:FirstResponse"),
        };
        foreach (var (value, risk, custom, expected) in table)
        {
            var r = await w.ResolveAsync("subcontract-activation", HarborlineWorld.Facts(value, risk: risk, custom: custom), probe);
            Assert.True(r.Resolved, $"{value} {risk}: {r.BlockMessage}");
            Assert.Equal(expected, HarborlineWorld.Shape(r));
            Assert.NotEmpty(r.Trace);
            Assert.NotEmpty(r.Explanations);                                                                                                       // why each person is in or out
        }

        // A foreign-currency subcontract is judged at the dated, synthetic rate; a currency with no rate blocks (there is no implicit conversion).
        var cad = await w.ResolveAsync("subcontract-activation", HarborlineWorld.Facts(400_000m, "CAD"), probe);
        Assert.True(cad.Resolved, cad.BlockMessage);
        Assert.Equal("project-director@0:FirstResponse, commercial-manager@1:FirstResponse", HarborlineWorld.Shape(cad));          // 400,000 CAD at 0.74 is 296,000 USD
        var eur = await w.ResolveAsync("subcontract-activation", HarborlineWorld.Facts(400_000m, "EUR"), probe);
        Assert.False(eur.Resolved);
        Assert.False(string.IsNullOrWhiteSpace(eur.BlockMessage));

        // People: the first tier names the Project Director group and, as a backup, the Deputy group; the submitter is never an approver.
        var first = await w.ResolveAsync("subcontract-activation", HarborlineWorld.Facts(100_000m), probe);
        var step = first.Plan!.Steps.Single();
        Assert.Contains(step.Approvers, a => a.UserId == w.Users["director1"] && a.Role == ParticipantRoles.Primary);
        Assert.Contains(step.Approvers, a => a.UserId == w.Users["director2"] && a.Role == ParticipantRoles.Primary);
        Assert.Contains(step.Approvers, a => a.UserId == w.Users["directorDeputy"] && a.Role == ParticipantRoles.Backup);
        Assert.DoesNotContain(step.Approvers, a => a.UserId == w.Users["commercialManager"]);
        var asSubmitter = await w.ResolveAsync("subcontract-activation", HarborlineWorld.Facts(100_000m), probe, submitter: w.Users["director1"]);
        Assert.DoesNotContain(asSubmitter.Plan!.Steps.Single().Approvers, a => a.UserId == w.Users["director1"]);
        Assert.Contains(asSubmitter.Explanations, e => e.UserId == w.Users["director1"] && !e.Included && e.Reasons.Count > 0);

        // Delegation: a Commercial Manager hands their own authority to a member of the backup group for a period, and the resolver says who acts on whose behalf.
        await w.AsAsync("commercialManager", s => s.Get<IApprovalDelegations>().CreateAsync(new CreateDelegationRequest(w.Org, "CommercialManagers", w.Users["commercialDeputy"], w.Now.AddMinutes(-1), w.Now.AddDays(5), "Annual leave")));
        var delegated = await w.ResolveAsync("subcontract-activation", HarborlineWorld.Facts(500_000m), probe);
        Assert.Contains(delegated.Explanations, e => e.UserId == w.Users["commercialDeputy"] && e.Included && e.OnBehalfOfUserId == w.Users["commercialManager"]);

        // Effective dates: before the matrix takes effect there is nothing to resolve.
        var early = await w.ResolveAsync("subcontract-activation", HarborlineWorld.Facts(100_000m), probe, asOf: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.False(early.Resolved);

        // The simulator and a real submission agree on the same facts (the large tier, in parallel, all required).
        var large = await w.NewSubcontractAsync(value: 2_500_000m, riskLevel: "Low");
        var simulated = (await w.AsAsync("admin", s => s.Get<IApprovalSimulator>().SimulateAsync(new SimulationRequest(w.Org, "subcontract-activation", HarborlineWorld.Facts(2_500_000m, risk: "Low"), 
            w.AsScopeless().Run(x => x.Get<ISecuredContractService>().DescribeAsync(large)).GetAwaiter().GetResult(), w.Users["packageManagerA"], null, MatrixSource.Published)))).Resolution;
        var request = await w.ToAwaitingActivationAsync(large);
        var real = (await w.DetailAsync("director1", request)).Plan!;
        Assert.Equal(simulated.Plan!.Steps.Select(x => (x.Key, x.ApproverGroup)).Order().ToArray(), real.Steps.Select(x => (x.Key, x.ApproverGroup)).Order().ToArray());
        Assert.Equal(simulated.RowCodes, real.Steps.Select(x => x.RowCode).Distinct().ToArray());

        // Parallel and all required: one response is not enough; the subcontract activates only when the third arrives. A person outside the group cannot act.
        await Assert.ThrowsAnyAsync<Exception>(() => w.AsAsync("safetyManager", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "not mine")));
        await w.AsAsync("director1", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "ok"));
        await w.AsAsync("commercialManager", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "ok"));
        Assert.Equal("AwaitingActivation", await w.StatusAsync(large));
        await w.AsAsync("regionalDirector", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "ok"));
        Assert.Equal("AwaitingActivation", await w.StatusAsync(large));                                   // "all required" means every named primary: the second Project Director is still outstanding
        await w.AsAsync("director2", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "ok"));
        Assert.Equal("Active", await w.StatusAsync(large));

        // A medium subcontract goes in sequence: the Commercial Manager cannot act before the Project Director.
        var medium = await w.NewSubcontractAsync(value: 500_000m);
        var mediumRequest = await w.ToAwaitingActivationAsync(medium);
        await Assert.ThrowsAnyAsync<Exception>(() => w.AsAsync("commercialManager", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(mediumRequest, "too early")));
        await w.AsAsync("directorDeputy", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(mediumRequest, "backup acts for the director"));
        await w.AsAsync("commercialDeputy", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(mediumRequest, "backup acts for the manager"));
        Assert.Equal("Active", await w.StatusAsync(medium));
    }

    // ---- change orders: the parent stays locked, nothing changes until applied, before and after are kept, and an administrative one records practical completion ----

    public static async Task Change_orders_record_facts_and_never_alter_the_parent_until_applied(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var id = await w.ToActiveAsync(value: 400_000m);          // medium tier: Project Director then Commercial Manager
        Assert.Equal("", await w.CustomValueAsync(id, "practical_completion_date"));

        // The parent is locked: an ordinary edit is refused while it is active, and again while a change order is open.
        await Assert.ThrowsAnyAsync<Exception>(() => w.AsAsync("packageManagerA", s => s.Get<ISecuredContractService>().UpdateFieldsAsync(id, new Dictionary<string, string?> { ["estimatedValue"] = "999999" })));
        var cancelled = await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().CreateAsync(id, SecondBusinessScenarios.ChangeOrder("Cancelled value change", "1200000")));
        await Assert.ThrowsAnyAsync<Exception>(() => w.AsAsync("packageManagerA", s => s.Get<ISecuredContractService>().UpdateFieldsAsync(id, new Dictionary<string, string?> { ["estimatedValue"] = "999999" })));
        await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().CancelAsync(cancelled, "Not agreed"));
        Assert.Equal(400_000m, (await w.FactsAsync(id)).EstimatedValue);                                                                           // a cancelled change order leaves the parent as it was
        Assert.Equal("", await w.CustomValueAsync(id, "change_order_type"));

        // A value change: judged on the value the subcontract would have once applied (1,200,000 is in the change order matrix's medium tier), preserved before and after.
        var value = await w.ChangeOrderToApprovalAsync(id, SecondBusinessScenarios.ChangeOrder("Extra scope", "1200000", time: "14", type: "VALUE"));
        var plan = (await w.DetailAsync("director1", value.RequestId)).Plan!;
        Assert.Equal(["commercial-manager", "project-director"], plan.Steps.Select(x => x.Key[(x.Key.IndexOf('/') + 1)..]).Order().ToArray());
        Assert.Equal(400_000m, (await w.FactsAsync(id)).EstimatedValue);
        await w.ApproveAllAsync(value.RequestId);
        Assert.Equal(1_200_000m, (await w.FactsAsync(id)).EstimatedValue);
        Assert.Equal("14", await w.CustomValueAsync(id, "time_extension_days"));
        var detail = await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().GetAsync(value.VariationId));
        Assert.Equal(VariationOutcome.Applied, detail.Summary.Outcome);
        var money = detail.Comparison.Single(c => c.Field == "estimatedValue");
        Assert.Equal(400_000m, Decimal.Parse(money.Before!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1_200_000m, Decimal.Parse(money.After!, System.Globalization.CultureInfo.InvariantCulture));

        // The administrative change order records practical completion on the locked parent, with no new state and no value change, approved by one Commercial Manager.
        var administrative = await w.ChangeOrderToApprovalAsync(id, SecondBusinessScenarios.ChangeOrder("Practical completion certified", "1200000", type: "ADMINISTRATIVE", complete: "2026-11-30"));
        var adminPlan = (await w.DetailAsync("commercialManager", administrative.RequestId)).Plan!;
        Assert.EndsWith("/commercial-manager", Assert.Single(adminPlan.Steps).Key);
        await Assert.ThrowsAnyAsync<Exception>(() => w.AsAsync("director1", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(administrative.RequestId, "not the right tier")));
        Assert.Equal("", await w.CustomValueAsync(id, "practical_completion_date"));
        await w.AsAsync("commercialManager", s => s.Get<IRegisterAuthorisationService>().ApproveAsync(administrative.RequestId, "Certificate seen"));
        Assert.Equal("Active", await w.StatusAsync(id));
        Assert.Equal(1_200_000m, (await w.FactsAsync(id)).EstimatedValue);
        Assert.Equal("2026-11-30", await w.CustomValueAsync(id, "practical_completion_date"));
        var certified = await w.AsAsync("packageManagerA", s => s.Get<IVariationService>().GetAsync(administrative.VariationId));
        var fact = certified.Comparison.Single(c => c.Field == "cf.practical_completion_date");
        Assert.True(string.IsNullOrEmpty(fact.Before)); Assert.Equal("2026-11-30", fact.After);
        Assert.True((await w.AsAsync("packageManagerA", s => s.Get<ILifecycleService>().StateAsync(id))).Locked);

        // Every step is in the audit history, the chain verifies, and the parent still completes normally.
        await using (var scope = w.AsScopeless())
            Assert.True((await scope.Get<Ecp.Audit.AuditChainVerifier>().VerifyAsync()).IsValid);
        await w.Step("commercial", id, "Complete");
        Assert.Equal("Completed", await w.StatusAsync(id));
    }

    // ---- SBP-AC-10: notifications ----

    public static async Task Notifications_reach_the_intended_people_in_the_packs_words_and_never_the_formal_review_or_extension_events(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);

        // The policy in force names no formal review event and no extension event.
        var policy = await w.AsScopeless().Run(s => s.Get<INotificationPolicyStore>().CurrentAsync(w.Org));
        Assert.Null(policy.RuleFor("legal.submitted")); Assert.Null(policy.RuleFor("renewal.started")); Assert.Null(policy.RuleFor("disposition.requested"));
        Assert.NotNull(policy.RuleFor("approval.submitted")); Assert.NotNull(policy.RuleFor("variation.raised")); Assert.NotNull(policy.RuleFor("reminder.endDate"));
        Assert.Equal("Restricted", policy.Content.WithholdEverythingFromLevel);
        Assert.Equal("Confidential", policy.Content.WithholdTitleFromLevel);

        // Submitting for activation tells the approvers of the first tier (and not the person who submitted, nor a manager of a later tier), in the pack's words.
        var id = await w.NewSubcontractAsync(value: 100_000m);
        var request = await w.ToAwaitingActivationAsync(id);
        await w.RunJobsAsync();
        var needed = (await w.InboxAsync("director1")).Single(n => n.EventType == "approval.submitted");
        Assert.StartsWith("Activation approval needed:", needed.Title);
        Assert.Contains(HarborlineWorld.Reference(1), needed.Title);
        Assert.NotNull(needed.LinkPath);
        Assert.Contains(await w.InboxAsync("director2"), n => n.EventType == "approval.submitted");
        Assert.DoesNotContain(await w.InboxAsync("packageManagerA"), n => n.EventType == "approval.submitted");
        Assert.DoesNotContain(await w.InboxAsync("commercialManager"), n => n.EventType == "approval.submitted");
        Assert.DoesNotContain(await w.InboxAsync("safetyManager"), n => n.EventType == "approval.submitted");

        // Running everything again changes nothing.
        var before = await w.AsScopeless().Run(s => s.Get<Ecp.Notifications.Persistence.NotificationsDbContext>().Notifications.CountAsync());
        await w.RunJobsAsync(); await w.RunJobsAsync();
        Assert.Equal(before, await w.AsScopeless().Run(s => s.Get<Ecp.Notifications.Persistence.NotificationsDbContext>().Notifications.CountAsync()));

        // Approval tells the package manager and the submitter, mandatory, with no procurement or formal-review wording.
        await w.ApproveAllAsync(request);
        await w.RunJobsAsync();
        var approved = (await w.InboxAsync("packageManagerA")).Single(n => n.EventType == "approval.approved");
        Assert.True(approved.Mandatory);
        Assert.StartsWith("Approved:", approved.Title);

        // A change order raised, applied: in the pack's words ("Change Order"), never "Variation".
        var co = await w.ChangeOrderToApprovalAsync(id, SecondBusinessScenarios.ChangeOrder("Extra scope", "150000"));
        await w.RunJobsAsync();
        Assert.DoesNotContain(await w.InboxAsync("packageManagerA"), n => n.EventType == "variation.raised");                         // not told of their own action
        var raised = (await w.InboxAsync("director1")).Single(n => n.EventType == "variation.raised");
        Assert.StartsWith("Change Order raised:", raised.Title);
        Assert.DoesNotContain("Variation", System.Text.RegularExpressions.Regex.Replace(raised.Title + raised.Body, @"/\S+", ""));          // the route is an identifier, not wording
        await w.ApproveAllAsync(co.RequestId);
        await w.RunJobsAsync();
        var applied = (await w.InboxAsync("packageManagerA")).Single(n => n.EventType == "variation.applied");
        Assert.Contains("administrative", applied.Body);

        // Everything anyone received uses an event the policy names.
        var everyone = new List<string>();
        foreach (var user in w.Users.Keys) everyone.AddRange((await w.InboxAsync(user)).Select(n => n.EventType));
        Assert.All(everyone, e => Assert.NotNull(policy.RuleFor(e)));

        // Reminders: scheduled completion is the End Date, with this pack's windows and its own wording. A lead time the pack does not allow is refused.
        var draft = await w.NewSubcontractAsync();
        var refused = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("packageManagerA", s => s.Get<IReminderSettings>().SetLeadDaysAsync(draft, 45)));
        Assert.Equal("lead_not_allowed", refused.Code);
        Assert.Contains("14, 30, 60, 90", refused.Message);
        var soon = await w.ToActiveAsync(value: 100_000m, end: w.Today.AddDays(20), lead: 30, notice: 14, critical: true);
        var result = await w.ScanRemindersAsync();
        Assert.True(result.RemindersSent >= 1);
        var reminders = (await w.InboxAsync("packageManagerA")).Where(n => n.Kind == "Reminder" && n.ContractId == soon).ToList();
        var end = Assert.Single(reminders, n => n.ReminderKind == "endDate");
        Assert.StartsWith("Scheduled completion in", end.Title);
        // Finding G12 (docs/PORTABILITY_AUDIT.md): notification dates are ISO 8601 for every organisation, an unambiguous platform convention that is neither day-first nor Australian. Formatting them in the organisation's
        // culture is a generic improvement recorded for S10, not part of this proof.
        Assert.Matches(@"\b\d{4}-\d{2}-\d{2}\b", end.Body);
        Assert.DoesNotMatch(@"\b(1[3-9]|2\d|3[01])/\d{1,2}/\d{4}\b", end.Body);
        var countBefore = (await w.InboxAsync("packageManagerA")).Count;
        await w.ScanRemindersAsync();
        Assert.Equal(countBefore, (await w.InboxAsync("packageManagerA")).Count);                                              // a window is used once

        // Nobody owns the category: the organisation's administrators are told, because this pack's policy says so.
        var orphan = await w.ToActiveAsync(value: 80_000m, trade: "FINISHES", end: w.Today.AddDays(20), lead: 30);
        await using (var scope = w.AsScopeless())
        {
            var cats = scope.Get<ICategoryAdministration>();
            var current = (await cats.ListAllAsync()).Single(c => c.Id == w.Categories["FINISHES"]).Owners.Single(o => o.UserId == w.Users["packageManagerA"] && o.EffectiveToUtc == null);
            await cats.EndOwnerTrustedAsync(current.AssignmentId, "moved to another project");
        }
        var held = await w.ScanRemindersAsync();
        Assert.Equal(0, held.SkippedNoRecipient);
        // The stable recipient rule "OrganisationAdministrators" resolves to the holders of the Procurement Administrator role code (finding G13: the rule's name is older than the role's neutral label, Commercial Administrator here).
        Assert.Contains(await w.InboxAsync("commercial"), n => n.Kind == "Reminder" && n.ContractId == orphan && n.ReminderKind == "endDate");
        Assert.DoesNotContain(await w.InboxAsync("packageManagerA"), n => n.Kind == "Reminder" && n.ContractId == orphan);
    }

    // ---- SBP-AC-07: capabilities ----

    public static async Task Legal_review_and_renewals_are_off_by_the_packs_defaults_and_the_other_three_work(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var set = await w.AsAsync("admin", s => s.Get<ICapabilityResolver>().GetAsync(w.Org));
        Assert.False(set.IsEnabled(CapabilityKeys.LegalReview)); Assert.False(set.IsEnabled(CapabilityKeys.Renewals));
        Assert.True(set.IsEnabled(CapabilityKeys.RegisterAuthorisation)); Assert.True(set.IsEnabled(CapabilityKeys.Variations)); Assert.True(set.IsEnabled(CapabilityKeys.SignatureRequests));

        var id = await w.ToActiveAsync();
        // Nothing legal or renewal is offered, and the gate refuses a start with the stable code.
        var offered = (await w.AsAsync("packageManagerA", s => s.Get<ILifecycleService>().AvailableAsync(id))).Select(t => t.Code).ToList();
        Assert.Contains("OpenChangeOrder", offered); Assert.Contains("StopWork", offered); Assert.Contains("Complete", offered);
        Assert.DoesNotContain(offered, c => c.Contains("Legal", StringComparison.Ordinal) || c.Contains("Renewal", StringComparison.Ordinal));
        var renewal = await Assert.ThrowsAnyAsync<DomainException>(() => w.AsAsync("packageManagerA", s => s.Get<IRenewalService>().StartAsync(id, null)));
        Assert.Contains(renewal.Code, new[] { "capability_disabled", "invalid_transition" });                                       // this lifecycle defines no renewal step at all
        var draft = await w.NewSubcontractAsync();
        var legal = await Assert.ThrowsAnyAsync<DomainException>(() => w.AsAsync("packageManagerA", s => s.Get<ILegalReviewService>().SubmitAsync(new SubmitLegalRequest(SubjectKinds.Contract, draft, "Please review"))));
        Assert.Contains(legal.Code, new[] { "capability_disabled", "invalid_transition" });

        // The register, built-in lists and cards that exist only because of those capabilities are not there.
        var views = (await w.AsAsync("packageManagerA", s => s.Get<IContractViews>().ListAsync(w.Org))).Select(v => v.Ref).ToList();
        Assert.DoesNotContain("system:awaiting-legal", views); Assert.DoesNotContain("system:renewals-in-progress", views);
        Assert.Contains("system:variations-in-progress", views);

        // A capability grants and removes nothing: the same person holds the same permissions whichever way a capability is set.
        CapabilityInput On(string key) => new(key, "on", null);
        CapabilityInput Off(string key) => new(key, "off", null);
        async Task<DraftDto> Draft(params CapabilityInput[] inputs)
        {
            var saved = await w.AsAsync("admin", s => s.Get<ICapabilityAdministration>().SaveAsync(w.Org, inputs));
            return await w.AsAsync("admin", s => s.Get<IConfigurationService>().ValidateAsync(saved.Id));
        }
        string[] Codes(DraftDto d) => d.Validation!.Messages.Select(m => m.Code).ToArray();

        // This lifecycle uses Variations and Signature Requests, so neither can be switched off; Variations also need Register Authorisation.
        foreach (var key in new[] { CapabilityKeys.Variations, CapabilityKeys.SignatureRequests, CapabilityKeys.RegisterAuthorisation })
        {
            var refused = await Draft(Off(key));
            Assert.False(refused.IsPublishable, key);
            Assert.Contains(Codes(refused), c => c is "workflow_uses_disabled_capability" or "capability_requires");
            await w.AsAsync("admin", s => s.Get<IConfigurationService>().DiscardDraftAsync(refused.Id));
        }
        // Turning Formal Review on is coherent and changes nothing the lifecycle offers (the lifecycle has no such step).
        var on = await Draft(On(CapabilityKeys.LegalReview));
        Assert.True(on.IsPublishable);
        await w.AsAsync("admin", s => s.Get<IConfigurationService>().PublishAsync(on.Id, "Counsel joins the platform"));
        var after = (await w.AsAsync("packageManagerA", s => s.Get<ILifecycleService>().AvailableAsync(id))).Select(t => t.Code).ToList();
        Assert.Equal(offered.Order().ToArray(), after.Order().ToArray());
        Assert.True((await w.AsAsync("admin", s => s.Get<ICapabilityResolver>().GetAsync(w.Org))).IsEnabled(CapabilityKeys.LegalReview));

        // The hidden Legal role is not offered in a role picker, yet nothing about access changes (SBP-AC-09).
        var words = await w.AsAsync("admin", s => s.Get<ITerminologyResolver>().GetAsync(w.Org));
        Assert.Contains(RoleCodes.Legal, words.HiddenRoles);
        Assert.DoesNotContain(RoleCodes.CategoryOwner, words.HiddenRoles);
    }

    // ---- SBP-AC-16: publication order ----

    public static async Task A_draft_that_names_something_unpublished_is_refused_until_it_exists_and_every_draft_is_published_in_order(TestApp app)
    {
        var w = await HarborlineWorld.CreateTenantAsync(app);
        await w.UploadAndInstallAsync();
        await w.AssignAsync();

        async Task<DraftDto> ValidateAsync(string kind)
        {
            var draft = (await w.AsAsync("admin", s => s.Get<IConfigurationService>().FindOpenDraftAsync(kind, kind is "ClassificationPolicy" or "Branding" ? Guid.Empty : w.Org)))!;
            return await w.AsAsync("admin", s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        }
        string[] Codes(DraftDto d) => d.Validation!.Messages.Where(m => m.Severity == ValidationSeverity.Error).Select(m => m.Code).ToArray();

        // Before the fields exist, the matrix, the views and the workflows (which name the matrix) cannot be published for this organisation.
        Assert.Contains("fact_unavailable", Codes(await ValidateAsync("ApprovalMatrix")));
        Assert.Contains(Codes(await ValidateAsync("ContractListView")), c => c is "column_unavailable" or "filter_unavailable");
        Assert.Contains("matrix_missing", Codes(await ValidateAsync(WorkflowKinds.ContractLifecycle)));
        Assert.Contains("matrix_missing", Codes(await ValidateAsync(WorkflowKinds.Variation)));

        await w.PublishAsync("ClassificationPolicy");
        await w.PublishAsync("CustomField");
        Assert.Empty(Codes(await ValidateAsync("ApprovalMatrix")));
        Assert.Contains("matrix_missing", Codes(await ValidateAsync(WorkflowKinds.ContractLifecycle)));                      // still: the matrix is valid but not yet published
        await w.PublishAsync("Terminology");
        await w.PublishAsync("ApprovalMatrix");
        await w.PublishAsync(WorkflowKinds.Variation);
        await w.PublishAsync(WorkflowKinds.ContractLifecycle);
        await w.PublishAsync(Ecp.Notifications.Policy.NotificationPolicyKinds.Policy);
        await w.PublishAsync("ContractListView");
        await w.PublishAsync("Branding");

        // Every draft the pack created has been published: none is left open.
        foreach (var kind in HarborlineWorld.PublicationOrder)
            Assert.Null(await w.AsAsync("admin", s => s.Get<IConfigurationService>().FindOpenDraftAsync(kind, kind is "ClassificationPolicy" or "Branding" ? Guid.Empty : w.Org)));
    }

    // ---- SBP-AC-12, 14: fields, lookups, exports, views and the four-level scale ----

    public static async Task Pack_fields_lookups_views_exports_and_the_four_level_scale_work_together(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);

        // The scale has four levels and none is privileged; the policy's content levels are inside it.
        var scale = await w.AsAsync("commercial", s => s.Get<IAccessService>().ClassificationScaleAsync());
        Assert.Equal(["Public", "Internal", "Confidential", "Restricted"], scale.Levels);
        Assert.Empty(scale.PrivilegedLevels);

        // Lookups make the core lists controlled; a value outside them is refused, a member is accepted.
        await Assert.ThrowsAnyAsync<Exception>(() => w.NewSubcontractAsync(riskLevel: "Extreme"));
        var id = await w.NewSubcontractAsync(riskLevel: "High", custom: new Dictionary<string, string?>
        {
            ["project_number"] = "P-2026-07", ["bond_required"] = "true", ["insurance_expiry"] = w.Today.AddDays(30).ToString("yyyy-MM-dd"), ["planned_completion_date"] = w.Today.AddDays(45).ToString("yyyy-MM-dd"),
            ["retention_percentage"] = "5", ["original_value"] = "100000", ["safety_critical"] = "true"
        });
        Assert.Equal("P-2026-07", await w.CustomValueAsync(id, "project_number"));

        // A step requirement from the pack is enforced: internal review needs a project number.
        var noProject = await w.AsAsync("packageManagerA", s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(w.Org, HarborlineWorld.Reference(700), "No project", w.Subcontractor, w.Categories["CONCRETE"],
            CurrencyCode: "USD", EstimatedValue: 10_000m, ContractType: "LUMP_SUM", SiteId: w.Sites["PRJ-LAKESIDE"])));
        await Assert.ThrowsAsync<TransitionBlockedException>(() => w.Step("packageManagerA", noProject, "SubmitForInternalReview"));

        // Validation: a pattern from the pack, a range, and decimal places.
        await Assert.ThrowsAnyAsync<Exception>(() => w.NewSubcontractAsync(custom: new Dictionary<string, string?> { ["project_number"] = "lower case!" }));
        await Assert.ThrowsAnyAsync<Exception>(() => w.NewSubcontractAsync(custom: new Dictionary<string, string?> { ["project_number"] = "P-1", ["retention_percentage"] = "55" }));

        // Shared views over core and pack-provided fields find the record.
        var org = w.Org.ToString("N");
        var insurance = await w.AsAsync("packageManagerA", s => s.Get<IContractViews>().RunAsync($"shared:{org}:insurance-expiring", w.Org));
        Assert.True(insurance.Rows.Count == 0 || insurance.Rows.All(r => r.Cells.ContainsKey("insurance_expiry")));
        var all = (await w.AsAsync("packageManagerA", s => s.Get<IContractViews>().ListAsync(w.Org))).ToList();
        foreach (var key in new[] { "awaiting-signature", "awaiting-activation", "active-subcontracts", "insurance-expiring", "bonds-expiring", "completion-approaching", "safety-critical", "stop-work", "completed-subcontracts", "terminated-subcontracts", "change-orders-open" })
            Assert.Contains(all, v => v.Ref == $"shared:{org}:{key}");

        // Exports speak the pack's words in their headings and carry the pack's fields in the reporting projection.
        var export = await w.AsAsync("commercial", s => s.Get<IContractExports>().ExportAsync(new ExportRequest(new ContractQuery(OrganisationId: w.Org, PageSize: 200), ExportFormat.Csv,
            Columns: ["agreementReference", "contractTitle", "supplier", "category", "site", "status", "estimatedValue", "endDate", "insurance_expiry", "practical_completion_date"])));
        var header = System.Text.Encoding.UTF8.GetString(export.Content).Split('\n')[0];
        Assert.Contains("Subcontract Number", header); Assert.Contains("Subcontractor", header); Assert.Contains("Trade Package", header);
        Assert.DoesNotContain("Supplier", header); Assert.DoesNotContain("Category", header); Assert.DoesNotContain("Contract Reference", header);
        var schema = await w.AsAsync("commercial", s => s.Get<IContractExports>().SchemaAsync(w.Org));
        Assert.Contains(schema.Columns, c => c.Name == "cf_insurance_expiry");
        Assert.Contains(schema.Columns, c => c.Name == "cf_practical_completion_date");
    }

    // ---- SBP-AC-04, 15: three businesses in one tenant, and a second tenant that shares nothing ----

    public static async Task Three_businesses_in_one_tenant_share_no_wording_capability_workflow_matrix_field_view_or_notification_and_a_second_tenant_shares_nothing(TestApp app)
    {
        var lw = await LifecycleWorld.CreateAsync(app);
        var jbs = await PackResolutionScenarios.AddOrganisationAsync(lw, "Reference Org", "ISJ", TestPacks.Reference);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(lw, "Blank Org", "ISB", TestPacks.Blank);
        var jbsContract = await lw.W.AsAsync(jbs.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(jbs.Org, "ISJ-000001", "Reference", jbs.Supplier, jbs.Category)));
        var blankContract = await lw.W.AsAsync(blank.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(blank.Org, "ISB-000001", "Blank", blank.Supplier, blank.Category)));
        // The reference organisation's starting configuration is installed on first use; touch it all before the snapshot so the comparison is about the other business, not about lazy installation.
        _ = await lw.W.AsScopeless().Run(s => s.Get<INotificationPolicyStore>().CurrentAsync(jbs.Org));
        _ = await lw.W.AsScopeless().Run(s => s.Get<IWorkflowDefinitions>().CurrentAsync(WorkflowKinds.Variation, jbs.Org));
        var jbsReleasesBefore = await ReleaseHashesAsync(lw.W.AsScopeless(), jbs.Org);

        var h = await HarborlineWorld.CreateInTenantAsync(app, lw.W.TenantId);
        await h.UploadAndInstallAsync();
        var assigned = await h.AssignAsync();
        // The tenant-level drafts (scale and branding) are created and reported; they are not published here because the other two businesses share the tenant's scale.
        Assert.Contains(assigned.Kinds, k => k.Kind == "ClassificationPolicy" && k.Scope == "tenant");
        await h.PublishAllAsync(includeTenantLevel: false);
        Assert.NotNull(await h.AsAsync("admin", s => s.Get<IConfigurationService>().FindOpenDraftAsync("ClassificationPolicy", Guid.Empty)));
        await h.OnboardAsync();
        var harborContract = await h.NewSubcontractAsync();

        // Wording: each organisation reads its own words, interleaved so a cache that mixed them up would show.
        async Task<string> Word(Guid org, string key) => (await lw.W.AsScopeless().Run(s => s.Get<ITerminologyResolver>().GetAsync(org))).Singular(key);
        foreach (var round in new[] { 1, 2 })
        {
            Assert.Equal("Agreement Reference", await Word(jbs.Org, "contract.reference"));
            Assert.Equal("Subcontract Number", await Word(h.Org, "contract.reference"));
            Assert.Equal("Contract Reference", await Word(blank.Org, "contract.reference"));
            Assert.Equal("Supplier", await Word(jbs.Org, "supplier"));
            Assert.Equal("Subcontractor", await Word(h.Org, "supplier"));
            Assert.Equal("Counterparty", await Word(blank.Org, "supplier"));
            Assert.Equal("ABN", await Word(jbs.Org, "supplier.tax.id"));
            Assert.Equal("Tax Identifier", await Word(h.Org, "supplier.tax.id"));
            _ = round;
        }

        // Capabilities.
        async Task<CapabilitySet> Caps(Guid org) => await lw.W.AsScopeless().Run(s => s.Get<ICapabilityResolver>().GetAsync(org));
        Assert.True((await Caps(jbs.Org)).AllEnabled);
        var blankCaps = await Caps(blank.Org);
        Assert.False(blankCaps.IsEnabled(CapabilityKeys.LegalReview)); Assert.False(blankCaps.IsEnabled(CapabilityKeys.SignatureRequests));
        var harborCaps = await Caps(h.Org);
        Assert.False(harborCaps.IsEnabled(CapabilityKeys.LegalReview)); Assert.True(harborCaps.IsEnabled(CapabilityKeys.SignatureRequests));

        // Workflows: each contract is pinned to its own organisation's lifecycle, and the pack's states appear nowhere else.
        async Task<HashSet<string>> States(Guid contract, string user) => (await lw.W.AsAsync(user, s => s.Get<ILifecycleService>().StateAsync(contract))).Path.Select(p => p.Code).ToHashSet();
        var harborStates = await h.AsAsync("packageManagerA", s => s.Get<ILifecycleService>().StateAsync(harborContract));
        var harborCodes = harborStates.Path.Select(p => p.Code).ToHashSet();
        Assert.Contains("InternalReview", harborCodes); Assert.Contains("StopWork", harborCodes);
        var jbsCodes = await States(jbsContract, jbs.User);
        var blankCodes = await States(blankContract, blank.User);
        foreach (var distinctive in new[] { "InternalReview", "ReadyForSignature", "SentForSignature", "AwaitingActivation", "StopWork", "ChangeOrderOpen" })
        { Assert.DoesNotContain(distinctive, jbsCodes); Assert.DoesNotContain(distinctive, blankCodes); }
        foreach (var jbsOnly in new[] { "AwaitingLegal", "InLegalReview", "LegalCleared", "RenewalInProgress", "FlaggedForDisposition" }) Assert.DoesNotContain(jbsOnly, harborCodes);

        // Matrices, fields, views and notifications: published only where the pack put them.
        async Task<bool> Has(string kind, Guid org) => await lw.W.AsScopeless().Run(async s => await s.Get<IConfigurationReleases>().LatestAsync(kind, org) is not null);
        foreach (var kind in new[] { "ApprovalMatrix", "CustomField", "ContractListView" })
        { Assert.True(await Has(kind, h.Org), kind); Assert.False(await Has(kind, jbs.Org), kind); Assert.False(await Has(kind, blank.Org), kind); }
        var jbsPolicy = await lw.W.AsScopeless().Run(s => s.Get<INotificationPolicyStore>().CurrentAsync(jbs.Org));
        var harborPolicy = await lw.W.AsScopeless().Run(s => s.Get<INotificationPolicyStore>().CurrentAsync(h.Org));
        Assert.NotNull(jbsPolicy.RuleFor("legal.submitted")); Assert.Null(harborPolicy.RuleFor("legal.submitted"));
        Assert.NotNull(jbsPolicy.RuleFor("renewal.started")); Assert.Null(harborPolicy.RuleFor("renewal.started"));

        // The reference organisation's configuration is byte-for-byte what it was before the other business arrived, and its scale still has its privileged level.
        Assert.Equal(jbsReleasesBefore, await ReleaseHashesAsync(lw.W.AsScopeless(), jbs.Org));
        var scale = await lw.W.AsAsync("tenantAdmin", s => s.Get<IAccessService>().ClassificationScaleAsync());
        Assert.Contains("Legal Privileged", scale.Levels);

        // A person of one business sees nothing of another's records.
        var seenByManager = await h.AsAsync("packageManagerA", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200)));
        Assert.DoesNotContain(seenByManager.Items, i => i.Id == jbsContract || i.Id == blankContract);
        Assert.Contains(seenByManager.Items, i => i.Id == harborContract);
        var seenByJbs = await lw.W.AsAsync(jbs.User, s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200)));
        Assert.DoesNotContain(seenByJbs.Items, i => i.Id == harborContract);

        // A second tenant installs the same pack and shares nothing with the first.
        var second = await HarborlineWorld.ReadyAsync(app, upload: true);
        Assert.NotEqual(h.TenantId, second.TenantId);
        var secondContract = await second.NewSubcontractAsync();
        var firstSees = await h.AsAsync("packageManagerA", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200)));
        Assert.DoesNotContain(firstSees.Items, i => i.Id == secondContract);
        var secondSees = await second.AsAsync("packageManagerA", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200)));
        Assert.Equal([secondContract], secondSees.Items.Select(i => i.Id).ToArray());
        var orgsOfSecond = await second.AsAsync("admin", s => s.Get<IOrganisationStructureService>().ListOrganisationsAsync());
        Assert.DoesNotContain(orgsOfSecond, o => o.Id == jbs.Org || o.Id == blank.Org || o.Id == h.Org);
    }

    private static async Task<string> ReleaseHashesAsync(TenantScope scope, Guid org)
    {
        await using (scope)
        {
            var parts = new List<string>();
            foreach (var kind in new[] { "WorkflowContractLifecycle", "WorkflowVariation", "NotificationPolicy", "Terminology" })
                if (await scope.Get<IConfigurationReleases>().LatestAsync(kind, org) is { } r)
                    parts.Add(kind + ":" + r.Number + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(r.ContentJson))));
            return string.Join("|", parts);
        }
    }

    // ---- SBP-AC-13: locale. US formats, the organisation's own zones, and date-only values that do not move ----

    public static async Task The_organisation_works_in_US_formats_and_its_own_time_zones_and_date_only_values_do_not_move(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var org = (await w.AsAsync("admin", s => s.Get<IOrganisationStructureService>().GetOrganisationAsync(w.Org)))!;
        Assert.Equal(("en-US", "USD", "America/Chicago"), (org.CultureCode, org.DefaultCurrencyCode, org.TimeZoneId));
        var structure = await w.AsAsync("admin", s => s.Get<IOrganisationStructureService>().GetAsync(w.Org));
        Assert.Equal("America/Chicago", structure.Sites.Single(x => x.Key == "PRJ-LAKESIDE").TimeZoneId);
        Assert.Equal("America/Denver", structure.Sites.Single(x => x.Key == "PRJ-FOOTHILLS").TimeZoneId);

        var f = new Ecp.SharedKernel.Localisation.LocaleFormatter(org.CultureCode, org.TimeZoneId, org.DefaultCurrencyCode);
        Assert.Equal("$1,200,000.50", f.Money(1_200_000.5m));
        Assert.Equal("11/30/2026", f.Date(new DateOnly(2026, 11, 30)));                                                   // month first
        Assert.Equal("1,234.50", f.Number(1234.5m));

        // A date-only value is stored and read back as the same calendar date for a project in either zone; it is never shifted by a zone.
        var lakeside = await w.NewSubcontractAsync(site: "PRJ-LAKESIDE", end: new DateOnly(2026, 12, 31));
        var foothills = await w.NewSubcontractAsync(site: "PRJ-FOOTHILLS", end: new DateOnly(2026, 12, 31));
        foreach (var id in new[] { lakeside, foothills })
            Assert.Equal(new DateOnly(2026, 12, 31), (await w.AsAsync("packageManagerA", s => s.Get<ISecuredContractService>().GetAsync(id))).EndDate);

        // "Today" is the organisation's day: the default test clock is early on the 16th in UTC and the evening of the 15th in Chicago, so a reminder 14 days ahead says 14, not 13.
        Assert.Equal(new DateOnly(2026, 10, 15), w.Today);
        var due = await w.ToActiveAsync(end: w.Today.AddDays(14), lead: 14);
        await w.ScanRemindersAsync();
        var reminder = (await w.InboxAsync("packageManagerA")).Single(n => n.Kind == "Reminder" && n.ContractId == due && n.ReminderKind == "endDate");
        Assert.Equal("Scheduled completion in 14 days: " + HarborlineWorld.Reference(3), reminder.Title);
    }

    internal static VariationInput ChangeOrder(string title, string value = "110000", string? time = null, string type = "VALUE", string? complete = null)
    {
        var changes = new Dictionary<string, string?> { ["estimatedValue"] = value, ["cf.change_order_type"] = type, ["cf.change_order_reason"] = "Owner-directed change." };
        if (time is not null) changes["cf.time_extension_days"] = time;
        if (complete is not null) changes["cf.practical_completion_date"] = complete;
        return new VariationInput(title, "Synthetic change order", "Owner-directed change", null, false, null, null, null, changes);
    }
}

public sealed class SecondBusinessScenarioTests_Sqlite
{
    [Fact] public Task Journey() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.The_journey_from_upload_to_a_published_onboarded_organisation_needs_no_rebuild, LifecycleScenarios.Scanner);
    [Fact] public Task Approvals() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.Activation_approval_follows_the_value_tiers_the_exceptions_the_currency_and_the_people, LifecycleScenarios.Scanner);
    [Fact] public Task ChangeOrders() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.Change_orders_record_facts_and_never_alter_the_parent_until_applied, LifecycleScenarios.Scanner);
    [Fact] public Task Notifications() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.Notifications_reach_the_intended_people_in_the_packs_words_and_never_the_formal_review_or_extension_events, LifecycleScenarios.Scanner);
    [Fact] public Task Capabilities() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.Legal_review_and_renewals_are_off_by_the_packs_defaults_and_the_other_three_work, LifecycleScenarios.Scanner);
    [Fact] public Task Order() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.A_draft_that_names_something_unpublished_is_refused_until_it_exists_and_every_draft_is_published_in_order, LifecycleScenarios.Scanner);
    [Fact] public Task Fields() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.Pack_fields_lookups_views_exports_and_the_four_level_scale_work_together, LifecycleScenarios.Scanner);
    [Fact] public Task Isolation() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.Three_businesses_in_one_tenant_share_no_wording_capability_workflow_matrix_field_view_or_notification_and_a_second_tenant_shares_nothing, LifecycleScenarios.Scanner);
    [Fact] public Task Locale() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.The_organisation_works_in_US_formats_and_its_own_time_zones_and_date_only_values_do_not_move, LifecycleScenarios.Scanner);
    [Fact] public Task Walk() => ProviderRunner.SqliteAsync(SecondBusinessScenarios.Every_state_and_every_step_of_the_lifecycle_and_the_change_order_workflow_is_visited, LifecycleScenarios.Scanner);
}

[Trait("Category", "SqlServer")]
public sealed class SecondBusinessScenarioTests_SqlServer
{
    [SqlServerFact] public Task Journey() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.The_journey_from_upload_to_a_published_onboarded_organisation_needs_no_rebuild, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Approvals() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.Activation_approval_follows_the_value_tiers_the_exceptions_the_currency_and_the_people, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task ChangeOrders() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.Change_orders_record_facts_and_never_alter_the_parent_until_applied, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Notifications() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.Notifications_reach_the_intended_people_in_the_packs_words_and_never_the_formal_review_or_extension_events, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Capabilities() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.Legal_review_and_renewals_are_off_by_the_packs_defaults_and_the_other_three_work, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Order() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.A_draft_that_names_something_unpublished_is_refused_until_it_exists_and_every_draft_is_published_in_order, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Fields() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.Pack_fields_lookups_views_exports_and_the_four_level_scale_work_together, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Isolation() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.Three_businesses_in_one_tenant_share_no_wording_capability_workflow_matrix_field_view_or_notification_and_a_second_tenant_shares_nothing, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Locale() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.The_organisation_works_in_US_formats_and_its_own_time_zones_and_date_only_values_do_not_move, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Walk() => ProviderRunner.SqlServerAsync(SecondBusinessScenarios.Every_state_and_every_step_of_the_lifecycle_and_the_change_order_workflow_is_visited, LifecycleScenarios.Scanner);
}
