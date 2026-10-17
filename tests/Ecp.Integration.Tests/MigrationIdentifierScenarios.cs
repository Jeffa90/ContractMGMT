using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Persistence;
using Ecp.DataMigration;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S10 (docs/TAX_IDENTIFIERS.md, docs/MIGRATION.md): a migrated counterparty identifier is mapped to the organisation's configured primary type, checked with the same structural rule as a manual entry, loaded with
/// provenance, and removed again when the batch is rolled back. An organisation that sets up no identifier type is told so rather than having a value stored under a guessed type.
/// </summary>
internal static class MigrationIdentifierScenarios
{
    private const string Mapping = "SupplierTaxId,supplier.taxIdentifier,Trim,,No,,,Source tax identifier";

    public static async Task A_migrated_identifier_is_checked_against_the_configured_type_loaded_with_provenance_and_removed_on_rollback(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app, extraMappingRows: Mapping);
        string Line(string id, string reference, string supplier, string vendor, string tax) => MigrationWorld.Row(id, reference, supplier: supplier, vendor: vendor) + "," + tax;
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: MigrationWorld.Csv(MigrationWorld.ContractsHeader + ",SupplierTaxId",
            Line("I-1", "AGR-I1", "Identifier Good Pty Ltd", "730001", "51 824 753 556"),
            Line("I-2", "AGR-I2", "Identifier Short Pty Ltd", "730002", "123"),
            Line("I-3", "AGR-I3", "Identifier Blank Pty Ltd", "730003", "")));
        var summary = await mw.ValidateAsync(batch);
        Assert.Equal(2, summary.Accepted);
        Assert.Equal(1, summary.Rejected);
        Assert.Equal(1, summary.MessagesByCode["MIG-E027"]);                       // the value does not fit the organisation's configured structure
        await mw.ValidateAndCommitAsync(batch);

        var rows = await mw.TargetAsync(async s =>
        {
            var db = s.Get<ContractsDbContext>();
            var good = await db.Suppliers.AsNoTracking().SingleAsync(x => x.Name == "Identifier Good Pty Ltd");
            var identifiers = await db.SupplierIdentifiers.AsNoTracking().Where(i => i.SupplierId == good.Id).ToListAsync();
            var shortOne = await db.Suppliers.AsNoTracking().AnyAsync(x => x.Name == "Identifier Short Pty Ltd");
            return (good, identifiers, shortOne);
        });
        Assert.Equal("51824753556", rows.good.TaxIdentifier);                       // the projection used by lists, search and exports
        var identifier = Assert.Single(rows.identifiers);
        Assert.Equal("au.abn", identifier.TypeKey);
        Assert.Equal("Migration", identifier.SourceKind);
        Assert.Equal(MigrationWorld.SourceSystem, identifier.SourceSystem);
        Assert.Equal(batch, identifier.MigrationBatchId);
        Assert.Equal("FormatValid", identifier.VerificationStatus);                 // structure only: nothing is verified against a registry
        Assert.False(rows.shortOne);

        // Rolling the batch back removes what the batch created, identifiers included.
        var result = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(batch, "Wrong extract"));
        Assert.NotNull(result);
        var left = await mw.TargetAsync(s => s.Get<ContractsDbContext>().SupplierIdentifiers.AsNoTracking().CountAsync(i => i.MigrationBatchId == batch));
        Assert.Equal(0, left);
    }

    public static async Task An_organisation_with_no_identifier_type_is_told_so_and_nothing_is_stored_under_a_guessed_type(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app, extraMappingRows: Mapping);
        await mw.World.AsScopeless().Run(async s =>
        {
            await s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(RegionalKinds.Name, mw.Org1, "{ \"schemaVersion\": 1, \"mode\": \"overrides\", \"identifiers\": [] }", "test", "no identifier types");
            return true;
        });
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: MigrationWorld.Csv(MigrationWorld.ContractsHeader + ",SupplierTaxId",
            MigrationWorld.Row("N-1", "AGR-N1", supplier: "No Type Pty Ltd", vendor: "740001") + ",51824753556",
            MigrationWorld.Row("N-2", "AGR-N2", supplier: "No Value Pty Ltd", vendor: "740002") + ","));
        var summary = await mw.ValidateAsync(batch);
        Assert.Equal(1, summary.Accepted);                                            // the row without a value loads
        Assert.Equal(1, summary.Rejected);
        Assert.Equal(1, summary.MessagesByCode["MIG-E028"]);                         // no identifier type is set up for the organisation
    }
}

public sealed class MigrationIdentifierScenarioTests_Sqlite
{
    [Fact] public Task Checked_loaded_and_rolled_back() => ProviderRunner.SqliteAsync(MigrationIdentifierScenarios.A_migrated_identifier_is_checked_against_the_configured_type_loaded_with_provenance_and_removed_on_rollback);
    [Fact] public Task No_type() => ProviderRunner.SqliteAsync(MigrationIdentifierScenarios.An_organisation_with_no_identifier_type_is_told_so_and_nothing_is_stored_under_a_guessed_type);
}

[Trait("Category", "SqlServer")]
public sealed class MigrationIdentifierScenarioTests_SqlServer
{
    [SqlServerFact] public Task Checked_loaded_and_rolled_back() => ProviderRunner.SqlServerAsync(MigrationIdentifierScenarios.A_migrated_identifier_is_checked_against_the_configured_type_loaded_with_provenance_and_removed_on_rollback);
    [SqlServerFact] public Task No_type() => ProviderRunner.SqlServerAsync(MigrationIdentifierScenarios.An_organisation_with_no_identifier_type_is_told_so_and_nothing_is_stored_under_a_guessed_type);
}
