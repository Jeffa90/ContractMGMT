using Ecp.Approvals;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Lifecycle;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;
using static Ecp.Integration.Tests.ApprovalMatrixWorld;
using FieldWorld = Ecp.Integration.Tests.CustomFieldWorld;

namespace Ecp.Integration.Tests;

/// <summary>The facts an approval matrix is judged on: one service builds them for a live submission and for the simulator, and the metadata an administrator configures reaches both.</summary>
internal static class ApprovalFactScenarios
{
    public static async Task A_live_submission_and_a_simulation_from_the_same_contract_choose_the_same_steps(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        foreach (var g in new[] { "Standard", "HighRisk", "ServicesTeam", "Pacific", "Strategic" }) await w.ApproverAsync("u" + g, g);
        await w.PublishFieldsAsync(FieldWorld.Doc(FieldWorld.Field("region", "SINGLE_CHOICE", FieldWorld.Choices.Replace("GOLD", "NSW").Replace("SILVER", "QLD").Replace("BRONZE", "VIC") + ", \"usableInApprovals\": true") + "," + FieldWorld.Field("memo", "TEXT")));
        await w.PublishMatrixAsync(Doc(string.Join(",",
            Row("base", "{}", Step("std", "Standard")),
            Row("risk", "{ \"riskLevels\": [\"HIGH\"] }", Step("risk", "HighRisk"), priority: 200, kind: "Exception", effect: "AddSteps"),
            Row("type", "{ \"contractTypes\": [\"SERVICES\"] }", Step("svc", "ServicesTeam"), priority: 210, kind: "Exception", effect: "AddSteps"),
            Row("region", "{ \"custom\": [ { \"field\": \"region\", \"operator\": \"In\", \"values\": [\"NSW\"] } ] }", Step("pac", "Pacific"), priority: 220, kind: "Exception", effect: "AddSteps"),
            Row("supplier", "{ \"supplierTypes\": [\"STRATEGIC\"] }", Step("strat", "Strategic"), priority: 230, kind: "Exception", effect: "AddSteps"))));
        await w.UseMatrixAsync("flow");

        // A supplier of a type the matrix names, and a contract carrying each of the other facts.
        var supplier = await w.W.AsScopeless().Run(s => s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(w.W.Org1, "Strategic Supplies Pty Ltd", VendorNumber: "STRAT01", SupplierType: "STRATEGIC")));
        var id = await w.As("ownerPackaging", s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(w.W.Org1, "", "Facts parity", supplier, w.W.CatPackaging, Classification: "Internal",
            CurrencyCode: "AUD", EstimatedValue: 5000m, ContractType: "SERVICES", RiskLevel: "HIGH", CustomValues: new Dictionary<string, string?> { ["region"] = "NSW", ["memo"] = "internal note" })));
        var request = await w.ToAwaitingApprovalAsync(id, legal: false);

        // What the live submission planned ...
        var live = (await w.As("uStandard", s => s.Get<IRegisterAuthorisationService>().GetAsync(request))).Plan!;
        Assert.Equal(5, live.Steps.Count);
        var liveKeys = live.Steps.Select(s => s.Key).ToArray();

        // ... is what the simulator names for the same contract, built by the same service.
        var simulated = await w.As("procAdmin", async s =>
        {
            var inputs = await s.Get<IApprovalSimulationInputs>().FromContractAsync(id);
            var subject = await s.Get<IApprovalSimulationInputs>().BuildAsync(inputs);
            return (inputs, result: await s.Get<IApprovalSimulator>().SimulateAsync(new SimulationRequest(w.W.Org1, "flow", subject.Facts, subject.Resource, w.W.Users["ownerPackaging"], null, MatrixSource.Published)));
        });
        Assert.Equal("SERVICES", simulated.inputs.ContractType);
        Assert.Equal("HIGH", simulated.inputs.RiskLevel);
        Assert.Equal("STRATEGIC", simulated.inputs.SupplierType);
        Assert.Equal("NSW", simulated.inputs.Custom["region"]);
        Assert.False(simulated.inputs.Custom.ContainsKey("memo"));              // not marked usable in approvals: never offered as a fact
        Assert.True(simulated.result.Resolution.Resolved, simulated.result.Resolution.BlockMessage);
        Assert.Equal(liveKeys, simulated.result.Resolution.Plan!.Steps.Select(s => s.Key).ToArray());

        // Change one fact on the contract and the next simulation follows it, exactly as a live submission would.
        await w.W.AsScopeless().Run(async s => { await s.Get<IContractLifecyclePort>().ApplyControlledChangesAsync(id, new Dictionary<string, string?> { ["riskLevel"] = "LOW" }, ["riskLevel"], "test", "unlock", Guid.NewGuid(), "test"); return 0; });
        var changed = await w.As("procAdmin", async s =>
        {
            var inputs = await s.Get<IApprovalSimulationInputs>().FromContractAsync(id);
            var subject = await s.Get<IApprovalSimulationInputs>().BuildAsync(inputs);
            return await s.Get<IApprovalSimulator>().SimulateAsync(new SimulationRequest(w.W.Org1, "flow", subject.Facts, subject.Resource, null, null, MatrixSource.Published));
        });
        Assert.DoesNotContain("risk/risk", changed.Resolution.Plan!.Steps.Select(s => s.Key));
        // Someone without the right to simulate cannot read a contract's facts this way.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As("ownerPackaging", s => s.Get<IApprovalSimulationInputs>().FromContractAsync(id)));
    }

    public static async Task A_matrix_cannot_use_a_field_that_is_not_marked_usable_in_approvals(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(FieldWorld.Doc(FieldWorld.Field("shared", "TEXT", "\"usableInApprovals\": true") + "," + FieldWorld.Field("private_note", "TEXT")));
        // A multiple-choice value is not one comparable fact, so a field of that type cannot be offered to approvals at all.
        var multi = await Assert.ThrowsAsync<DomainException>(() => w.PublishFieldsAsync(FieldWorld.Doc(FieldWorld.Field("shared", "TEXT", "\"usableInApprovals\": true") + "," + FieldWorld.Field("many", "MULTI_CHOICE", FieldWorld.Choices + ", \"usableInApprovals\": true"))));
        Assert.Contains("multiple choice", multi.Message, StringComparison.OrdinalIgnoreCase);
        string Matrix(string field) => Doc(Row("base", $"{{ \"custom\": [ {{ \"field\": \"{field}\", \"operator\": \"Equals\", \"values\": [\"x\"] }} ] }}", Step("s", "Standard")));
        await w.PublishMatrixAsync(Matrix("shared"));                                                   // allowed
        var e1 = await Assert.ThrowsAsync<DomainException>(() => w.PublishMatrixAsync(Matrix("private_note")));
        Assert.Contains("private_note", e1.Message);
        var e3 = await Assert.ThrowsAsync<DomainException>(() => w.PublishMatrixAsync(Matrix("nosuchfield")));
        Assert.Contains("nosuchfield", e3.Message);
    }
}
