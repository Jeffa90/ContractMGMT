using System.Text.Json.Nodes;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Rollback governance for every kind of configuration the second business (Harborline) publishes: a rollback is requested with a reason, needs a recent sign-in, goes to a second person, and when that person publishes it
/// a new release carries the earlier content while every earlier release is untouched. The documents are the pack's own; the second release is the same document written compactly, so nothing about the kinds is invented.
/// </summary>
internal static class ConfigurationRollbackKindsScenarios
{
    /// <summary>The same document written differently (compact, or indented when it already was compact), so the second release has the same meaning and a different checksum.</summary>
    private static string Rewritten(string json)
    {
        var compact = JsonNode.Parse(json)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        return compact != json ? compact : JsonNode.Parse(json)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public static async Task Every_kind_of_configuration_is_rolled_back_through_a_second_person_and_history_is_never_rewritten(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        await w.AddAdministratorAsync("admin2");
        var checkedKinds = new List<string>();
        // The kinds the pack carries in the documented order, then the organisation-level kinds an administrator publishes from the forms and the wizard.
        foreach (var extra in new[] { CapabilityKinds.Name, RegionalKinds.Name })
            if (await w.AsAsync("admin", s => s.Get<IConfigurationService>().LatestReleaseAsync(extra, w.Org)) is null)
            {
                // An organisation's own document: an override of the pack default (the pack's own document is the default it inherits from).
                var document = extra == CapabilityKinds.Name ? CapabilityDocument.Empty.ToJson() : RegionalProfileDocument.Empty.ToJson();
                await w.AsAsync("admin", s => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(extra, w.Org, document, "history-setup", "From the pack"));
            }
        foreach (var kind in HarborlineWorld.PublicationOrder.Concat([CapabilityKinds.Name, RegionalKinds.Name, "OrganisationOnboarding"]))
        {
            var scopeOrg = HarborlineWorld.TenantLevelKinds.Contains(kind) ? Guid.Empty : w.Org;
            var v1 = await w.AsAsync("admin", s => s.Get<IConfigurationService>().LatestReleaseAsync(kind, scopeOrg));
            Assert.NotNull(v1);
            // A second release with the same meaning (written compactly), published through the trusted path so the setup of the history is not itself under test.
            var v2 = await w.AsAsync("admin", s => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(kind, scopeOrg, Rewritten(v1!.ContentJson), "history-setup", "Same content, written compactly"));
            Assert.Equal(v1.Number + 1, v2.Number);
            Assert.NotEqual(v1.ContentHash, v2.ContentHash);
            checkedKinds.Add(kind);
        }

        await using (var s = w.AsScopeless()) { var o = s.Get<ConfigurationOptions>(); o.RequireSecondPersonPublish = true; o.RequireRecentSignInForRollback = true; }
        foreach (var kind in checkedKinds)
        {
            var scopeOrg = HarborlineWorld.TenantLevelKinds.Contains(kind) ? Guid.Empty : w.Org;
            var before = (await w.AsAsync("admin", s => s.Get<IConfigurationService>().ReleasesAsync(kind, scopeOrg))).OrderBy(r => r.Number).ToList();
            var first = before.First(); var count = before.Count;

            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("admin", s => s.Get<IConfigurationService>().RequestRollbackAsync(kind, scopeOrg, first.Number, "Back to the start")));
            await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => { s.Get<ReauthenticationContext>().Set(w.Now); return s.Get<IConfigurationService>().RequestRollbackAsync(kind, scopeOrg, first.Number, ""); }));
            var requested = await w.AsAsync("admin", s => { s.Get<ReauthenticationContext>().Set(w.Now); return s.Get<IConfigurationService>().RequestRollbackAsync(kind, scopeOrg, first.Number, "Back to the start"); });
            Assert.True(requested.AwaitingReview, kind);
            Assert.Equal(count, (await w.AsAsync("admin", s => s.Get<IConfigurationService>().ReleasesAsync(kind, scopeOrg))).Count);       // nothing took effect

            var self = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IConfigurationService>().PublishAsync(requested.Draft!.Id, "mine")));
            Assert.Equal("second_person_required", self.Code);

            var release = await w.AsAsync("admin2", s => s.Get<IConfigurationService>().PublishAsync(requested.Draft!.Id, "Agreed by a second person"));
            Assert.Equal(count + 1, release.Number);
            Assert.Equal(first.Number, release.RollbackOfNumber);
            Assert.Equal(first.ContentHash, release.ContentHash);
            Assert.Equal(first.ContentJson, release.ContentJson);
            Assert.Equal(w.Users["admin"].ToString(), release.SubmittedBy);
            Assert.Equal(w.Users["admin2"].ToString(), release.PublishedBy);

            var after = (await w.AsAsync("admin", s => s.Get<IConfigurationService>().ReleasesAsync(kind, scopeOrg))).OrderBy(r => r.Number).ToList();
            for (var i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].Number, after[i].Number); Assert.Equal(before[i].ContentHash, after[i].ContentHash);
                Assert.Equal(before[i].PublishedBy, after[i].PublishedBy); Assert.Equal(before[i].Note, after[i].Note);
            }

            await using var scope = w.AsScopeless();
            var events = await scope.Get<IAuditEventSource>().Events.Where(e => e.EntityType == kind && (e.Action == "config.rollback.requested" || e.Action == "config.rolled_back")).ToListAsync();
            var req = Assert.Single(events, e => e.Action == "config.rollback.requested");
            Assert.Equal("Back to the start", req.Reason);
            Assert.Equal(w.Users["admin"].ToString(), req.ActorId);
            var done = Assert.Single(events, e => e.Action == "config.rolled_back");
            Assert.Equal(w.Users["admin2"].ToString(), done.ActorId);
            Assert.Contains($"\"sourceVersion\":{first.Number}", done.NewValuesJson!);
            Assert.Contains($"\"resultingVersion\":{release.Number}", done.NewValuesJson!);
        }
        Assert.True(checkedKinds.Count >= 12, "every kind the second business publishes was exercised");
    }
}

[Trait("Category", "Governance")]
public sealed class ConfigurationRollbackKindsScenarioTests_Sqlite
{
    [Fact] public Task Every_kind() => ProviderRunner.SqliteAsync(ConfigurationRollbackKindsScenarios.Every_kind_of_configuration_is_rolled_back_through_a_second_person_and_history_is_never_rewritten);
}

[Trait("Category", "SqlServer")]
public sealed class ConfigurationRollbackKindsScenarioTests_SqlServer
{
    [SqlServerFact] public Task Every_kind() => ProviderRunner.SqlServerAsync(ConfigurationRollbackKindsScenarios.Every_kind_of_configuration_is_rolled_back_through_a_second_person_and_history_is_never_rewritten);
}
