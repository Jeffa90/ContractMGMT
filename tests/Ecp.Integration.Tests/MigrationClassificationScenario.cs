using Ecp.Contracts;
using Ecp.Contracts.Persistence;
using Microsoft.EntityFrameworkCore;
using Ecp.DataMigration;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

internal static class MigrationClassificationScenario
{
    /// <summary>Source classifications are kept, a blank one takes the tenant default, and one that is not on the scale is rejected rather than quietly relaxed. Who can see the result follows the level.</summary>
    public static async Task Migrated_records_keep_their_classification_and_are_protected_by_it(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app, extraMappingRows: "Classification,classification,Trim,,No,,,Source sensitivity label");
        string Line(string id, string reference, string classification) => MigrationWorld.Row(id, reference, supplier: "Supplier Class Pty Ltd", vendor: "710001") + "," + classification;
        var contracts = MigrationWorld.Csv(MigrationWorld.ContractsHeader + ",Classification",
            Line("K-1", "AGR-K1", "Confidential"), Line("K-2", "AGR-K2", "Restricted"), Line("K-3", "AGR-K3", ""), Line("K-4", "AGR-K4", "Top Secret"));
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: contracts);
        var summary = await mw.ValidateAsync(batch);
        Assert.Equal(3, summary.Accepted);
        Assert.Equal(1, summary.Rejected);
        Assert.Equal(1, summary.MessagesByCode["MIG-E004"]);                 // the unknown level is an error on its own row; nothing is guessed
        await mw.ValidateAndCommitAsync(batch);

        var byReference = await mw.TargetAsync(async s =>
            (await s.Get<ContractsDbContext>().Contracts.AsNoTracking().Where(c => c.AgreementReference.StartsWith("AGR-K")).ToListAsync()).ToDictionary(c => c.AgreementReference, c => (c.Id, c.Classification)));
        Assert.Equal("Confidential", byReference["AGR-K1"].Classification);
        Assert.Equal("Restricted", byReference["AGR-K2"].Classification);
        Assert.Equal("Internal", byReference["AGR-K3"].Classification);       // the tenant's default level
        Assert.False(byReference.ContainsKey("AGR-K4"));

        // The Viewer's ceiling is Confidential: the migrated Restricted record does not exist for them, in a list or by id.
        var list = await mw.World.AsAsync("viewerAll", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200)));
        Assert.Contains(list.Items, i => i.Id == byReference["AGR-K1"].Id);
        Assert.Contains(list.Items, i => i.Id == byReference["AGR-K3"].Id);
        Assert.DoesNotContain(list.Items, i => i.Id == byReference["AGR-K2"].Id);
        await Assert.ThrowsAsync<Ecp.SharedKernel.ResourceNotFoundException>(() => mw.World.AsAsync("viewerAll", s => s.Get<ISecuredContractService>().GetAsync(byReference["AGR-K2"].Id)));
    }
}
