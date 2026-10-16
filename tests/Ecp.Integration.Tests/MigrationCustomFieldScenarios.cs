using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Persistence;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Migration into configured fields: mapping targets, dry-run checks, explicit choice mapping, retired fields, provenance, idempotent reruns and rollback.</summary>
internal static class MigrationCustomFieldScenarios
{
    private const string Fields = """
        { "schemaVersion": 1, "fields": [
          { "key": "service_tier", "label": "Service tier", "type": "SINGLE_CHOICE", "choices": [ { "code": "GOLD", "label": "Gold" }, { "code": "SILVER", "label": "Silver" } ] },
          { "key": "score", "label": "Score", "type": "INTEGER", "validation": { "min": 0, "max": 100 } },
          { "key": "tags", "label": "Tags", "type": "MULTI_CHOICE", "choices": [ { "code": "ALPHA", "label": "Alpha" }, { "code": "BETA", "label": "Beta" } ] },
          { "key": "other", "label": "Other", "type": "INTEGER" },
          { "key": "calc", "label": "Calc", "type": "CALCULATED_DISPLAY", "calculated": { "kind": "Sum", "operands": ["other", "other"] } },
          { "key": "lead", "label": "Lead", "type": "PERSON" },
          { "key": "old_note", "label": "Old note", "type": "TEXT", "status": "Retired" } ] }
        """;

    private const string Header = MigrationWorld.ContractsHeader + ",ServiceTier,Score,Tags,Type,Risk,Critical,Notice,NoticeUnit";
    private static string Row(string id, string reference, string tier = "Gold", string score = "42", string tags = "ALPHA;BETA", string type = "Services", string risk = "high", string value = "1000", string critical = "No", string notice = "3", string unit = "Months", string supplier = "Southern Cross Demo Supplies Pty Ltd")
        => MigrationWorld.Row(id, reference, supplier: supplier, value: value) + $",{tier},{score},\"{tags}\",{type},{risk},{critical},{notice},{unit}";
    private static byte[] Contracts(params string[] rows) => MigrationWorld.Csv([Header, .. rows]);

    private const string MappingRows =
        "ServiceTier,custom.service_tier,LookupMap,,No,tier,OnMissing=Error,\r\n" +
        "Score,custom.score,ParseInteger,,No,,,\r\n" +
        "Tags,custom.tags,Trim,,No,,,\r\n" +
        "Type,contractType,Trim,,No,,,\r\n" +
        "Risk,riskLevel,Upper,,No,,,\r\n" +
        "Critical,criticalNotice,MapBoolean,,No,,,\r\n" +
        "Notice,noticePeriodValue,ParseInteger,,No,,,\r\n" +
        "NoticeUnit,noticePeriodUnit,Trim,,No,,,";
    private const string Lookups = "tier,Gold,GOLD,\r\ntier,Silver Plus,SILVER,";

    private static async Task<MigrationWorld> WorldAsync(TestApp app, string? mapping = null)
    {
        var mw = await MigrationWorld.CreateAsync(app, extraMappingRows: mapping ?? MappingRows, extraLookupRows: Lookups);
        await mw.TargetAsync(async s => { await s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(MetadataModule.CustomFieldKind, mw.Org1, Fields, "test", "fields for migration"); return 0; });
        return mw;
    }

    private static Task<List<ContractCustomValue>> ValuesAsync(MigrationWorld mw, Guid contractId)
        => mw.TargetAsync(s => s.Get<ContractsDbContext>().CustomValues.AsNoTracking().Where(v => v.ContractId == contractId).OrderBy(v => v.FieldKey).ThenBy(v => v.Ordinal).ToListAsync());

    private static Task<Guid> ContractOf(MigrationWorld mw, string sourceId)
        => mw.TargetAsync(async s => (await s.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(c => c.SourceRecordId == sourceId)).Id);

    public static async Task Configured_fields_and_the_new_core_fields_load_with_every_bad_value_reported_and_nothing_guessed(TestApp app)
    {
        var mw = await WorldAsync(app);
        var batch = await mw.CreateBatchAsync(legacy: true);
        await mw.UploadAsync(batch, contracts: Contracts(
            Row("C-1", "CF-001"),                                            // good: label mapped through the lookup, multi value, core fields
            Row("C-2", "CF-002", tier: "Platinum"),                           // not in the lookup map: refused, never guessed
            Row("C-3", "CF-003", score: "abc"),                               // not a whole number
            Row("C-4", "CF-004", score: "150"),                               // over the field's maximum
            Row("C-5", "CF-005", tags: "ALPHA;GAMMA"),                        // GAMMA is not a defined choice
            Row("C-6", "CF-006", tier: "Silver Plus", score: "", tags: ""),   // blanks are simply empty
            Row("C-7", "CF-007", notice: "", unit: "Months")));               // unit without length
        var summary = await mw.ValidateAsync(batch);
        Assert.Empty(summary.Blockers);
        Assert.Equal((2, 5), (summary.Accepted, summary.Rejected));
        Assert.Equal(1, summary.MessagesByCode["MIG-E004"]);   // tier map miss
        Assert.Equal(1, summary.MessagesByCode["MIG-E003"]);   // not a number
        Assert.Equal(1, summary.MessagesByCode["MIG-E023"]);   // over the maximum
        Assert.Equal(1, summary.MessagesByCode["MIG-E022"]);   // undefined choice
        Assert.Equal(1, summary.MessagesByCode["MIG-E001"]);   // notice pair
        await mw.ValidateAndCommitAsync(batch);

        var c1 = await ContractOf(mw, "C-1");
        var values = await ValuesAsync(mw, c1);
        Assert.Equal(["score", "service_tier", "tags", "tags"], values.Select(v => v.FieldKey).ToArray());
        Assert.Equal(42m, values.Single(v => v.FieldKey == "score").NumberValue);
        Assert.Equal("GOLD", values.Single(v => v.FieldKey == "service_tier").TextValue);                              // stored as the code
        Assert.Equal("ALPHA;BETA", string.Join(";", values.Where(v => v.FieldKey == "tags").Select(v => v.TextValue)));
        // Provenance: the batch, the source system and the value as the source gave it.
        Assert.All(values, v => { Assert.Equal(batch, v.MigrationBatchId); Assert.Equal(MigrationWorld.SourceSystem, v.SourceSystem); });
        Assert.Equal("Gold", values.Single(v => v.FieldKey == "service_tier").SourceValue);
        Assert.Equal("42", values.Single(v => v.FieldKey == "score").SourceValue);
        // The core fields arrived typed.
        var contract = await mw.TargetAsync(s => s.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(c => c.Id == c1));
        Assert.Equal(("Services", "HIGH", 3, "Months"), (contract.ContractType, contract.RiskLevel, contract.NoticePeriodValue, contract.NoticePeriodUnit));
        Assert.Equal(90, contract.NoticePeriodDays is >= 89 and <= 92 ? 90 : contract.NoticePeriodDays);
        // C-6 holds only its tier; the loaded record is audited with the batch.
        Assert.Single(await ValuesAsync(mw, await ContractOf(mw, "C-6")));
        var audit = await mw.TargetAsync(s => s.Get<IAuditEventSource>().Events.Where(e => e.MigrationBatchId == batch && e.Action == "migration.contract.custom_fields").CountAsync());
        Assert.Equal(2, audit);
        // Nothing was loaded for the rejected rows.
        Assert.Equal(2, await mw.TargetAsync(s => s.Get<ContractsDbContext>().Contracts.CountAsync(c => c.MigrationBatchId == batch)));
    }

    public static async Task A_profile_that_names_an_unknown_retired_calculated_or_person_field_stops_the_dry_run(TestApp app)
    {
        foreach (var (target, code) in new[] { ("custom.no_such_field", "MIG-E020"), ("custom.old_note", "MIG-E021"), ("custom.calc", "MIG-E025"), ("custom.lead", "MIG-E025") })
        {
            var mw = await WorldAsync(app, $"Notes,{target},Trim,,No,,,");
            var batch = await mw.CreateBatchAsync();
            await mw.UploadAsync(batch, contracts: MigrationWorld.Csv([MigrationWorld.ContractsHeader + ",Notes", MigrationWorld.Row("N-1", "NF-001") + ",x"]));
            var summary = await mw.ValidateAsync(batch);
            Assert.Contains(summary.Blockers, b => b.StartsWith(code, StringComparison.Ordinal));
            Assert.Equal(0, await mw.TargetAsync(s => s.Get<ContractsDbContext>().Contracts.CountAsync(c => c.MigrationBatchId == batch)));
        }
        // The mapping itself is refused when a target is not written as custom.<key>.
        await Assert.ThrowsAnyAsync<Exception>(() => WorldAsync(app, "Notes,custom.Bad Key!,Trim,,No,,,"));
    }

    public static async Task A_rerun_is_idempotent_an_update_keeps_a_before_image_and_a_rollback_restores_or_removes(TestApp app)
    {
        var mw = await WorldAsync(app);
        var first = await mw.CreateBatchAsync(legacy: true);
        await mw.UploadAsync(first, contracts: Contracts(Row("U-1", "UP-001"), Row("U-2", "UP-002", score: "10")));
        await mw.ValidateAndCommitAsync(first);
        var (u1, u2) = (await ContractOf(mw, "U-1"), await ContractOf(mw, "U-2"));

        // The same file again changes nothing, whatever the strategy.
        foreach (var strategy in new[] { DuplicateStrategy.SkipExisting, DuplicateStrategy.UpdateMapped })
        {
            var again = await mw.CreateBatchAsync(strategy: strategy, legacy: true);
            await mw.UploadAsync(again, contracts: Contracts(Row("U-1", "UP-001"), Row("U-2", "UP-002", score: "10")));
            var summary = await mw.ValidateAsync(again);
            Assert.Equal((0, 0, 2), (summary.Accepted, summary.Rejected, summary.Skipped));
        }

        // A changed value updates only what differs. The records are locked legacy loads, so the policy must allow overwriting.
        await mw.AsAdminAsync(s => s.Get<IMigrationProjects>().SetLegacyPolicyAsync(mw.ProjectId, new LegacyPolicyInput(true, app.Clock.UtcNow.AddDays(30), ["Effective", "Ended"], true, "Correct scores after a data fix")));
        var update = await mw.CreateBatchAsync(strategy: DuplicateStrategy.UpdateMapped, legacy: true);
        await mw.UploadAsync(update, contracts: Contracts(Row("U-1", "UP-001", score: "77", tier: "Silver Plus"), Row("U-2", "UP-002", score: "10")));
        var uSummary = await mw.ValidateAsync(update);
        Assert.Equal((1, 0, 1), (uSummary.Accepted, uSummary.Rejected, uSummary.Skipped));
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await mw.ValidateAndCommitAsync(update);
        var after = await ValuesAsync(mw, u1);
        Assert.Equal(77m, after.Single(v => v.FieldKey == "score").NumberValue);
        Assert.Equal("SILVER", after.Single(v => v.FieldKey == "service_tier").TextValue);
        Assert.Equal(update, after.Single(v => v.FieldKey == "score").MigrationBatchId);
        var prov = await mw.TargetAsync(s => s.Get<MigrationDbContext>().Provenance.AsNoTracking().SingleAsync(p => p.BatchId == update && p.Action == ProvenanceAction.Updated));
        var before = JsonSerializer.Deserialize<Dictionary<string, string?>>(prov.BeforeImageJson!)!;
        Assert.Equal(("42", "GOLD"), (before["cf.score"], before["cf.service_tier"]));            // the earlier values are kept, so the change can be undone
        Assert.Equal(10m, (await ValuesAsync(mw, u2)).Single(v => v.FieldKey == "score").NumberValue);   // the unchanged row was not touched

        // Rolling the update back restores the earlier values.
        var rolled = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(update, "Wrong correction"));
        Assert.Equal(1, rolled.Reversed);
        var restored = await ValuesAsync(mw, u1);
        Assert.Equal(42m, restored.Single(v => v.FieldKey == "score").NumberValue);
        Assert.Equal("GOLD", restored.Single(v => v.FieldKey == "service_tier").TextValue);

        // Rolling back the creating batch removes the untouched record together with its configured values; the one that has had activity since is archived, and keeps its values.
        var preview = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().PreviewAsync(first));
        Assert.True(preview.Allowed);
        var removed = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(first, "Wrong source extract"));
        Assert.Equal(0, removed.NotReversible);
        Assert.Empty(await ValuesAsync(mw, u2));
        Assert.False(await mw.TargetAsync(s => s.Get<ContractsDbContext>().Contracts.AnyAsync(c => c.Id == u2)));
        var kept = await mw.TargetAsync(s => s.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(c => c.Id == u1));
        Assert.True(kept.IsArchived);
        Assert.NotEmpty(await ValuesAsync(mw, u1));
    }

    public static async Task A_field_retired_after_loading_keeps_its_values_and_refuses_a_changed_load(TestApp app)
    {
        var mw = await WorldAsync(app);
        var first = await mw.CreateBatchAsync(legacy: true);
        await mw.UploadAsync(first, contracts: Contracts(Row("R-1", "RT-001")));
        await mw.ValidateAndCommitAsync(first);
        var id = await ContractOf(mw, "R-1");
        // The administrator retires the score field. Its stored value stays; a later load that would change it is stopped.
        var retired = Fields.Replace("\"key\": \"score\", \"label\": \"Score\", \"type\": \"INTEGER\"", "\"key\": \"score\", \"label\": \"Score\", \"type\": \"INTEGER\", \"status\": \"Retired\"");
        await mw.TargetAsync(async s => { await s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(MetadataModule.CustomFieldKind, mw.Org1, retired, "test", "retire score"); return 0; });
        Assert.Equal(42m, (await ValuesAsync(mw, id)).Single(v => v.FieldKey == "score").NumberValue);
        var next = await mw.CreateBatchAsync(strategy: DuplicateStrategy.UpdateMapped, legacy: true);
        await mw.UploadAsync(next, contracts: Contracts(Row("R-1", "RT-001", score: "50")));
        var summary = await mw.ValidateAsync(next);
        Assert.Contains(summary.Blockers, b => b.StartsWith("MIG-E021", StringComparison.Ordinal));
        Assert.Equal(42m, (await ValuesAsync(mw, id)).Single(v => v.FieldKey == "score").NumberValue);
    }
}
