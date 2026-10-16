using System.Text.Json.Nodes;
using Ecp.Approvals;
using Ecp.Approvals.Resolution;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Integration.Tests;

/// <summary>Helpers for the approval matrix scenarios: people in approver groups, publishing a matrix and pointing the workflow at it, and resolving against a real record.</summary>
internal static class ApprovalMatrixWorld
{
    public static ApprovalFacts Facts(decimal? value = 1000m, string? currency = "AUD", string[]? category = null, string? classification = "Internal", string? bu = null, string? site = null, string? contractType = null,
        string? risk = null, string? supplierType = null, string? legalEntity = null, Dictionary<string, string?>? custom = null)
        => new(value, currency, category ?? ["PACKAGING"], contractType, legalEntity, bu, site, classification, risk, supplierType, custom ?? []);

    /// <summary>A person holding an Approver (or Backup Approver) assignment for a group, optionally in force only for a period.</summary>
    public static async Task<Guid> ApproverAsync(this LifecycleWorld w, string key, string group, bool backup = false, string ceiling = "Confidential", bool privileged = false, DateTime? from = null, DateTime? to = null,
        Guid? organisation = null)
    {
        await using var s = w.W.AsScopeless();
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync(key, key + "@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", key + "|" + w.W.TenantId, "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, backup ? RoleCodes.BackupApprover : RoleCodes.Approver, null, ScopeSet.ForOrganisations([organisation ?? w.W.Org1], ceiling), group,
            EffectiveFromUtc: from ?? w.W.Now.AddDays(-5), EffectiveToUtc: to, ExplicitHighCeiling: true, PrivilegedAccess: privileged), AssignmentSource.Demo, "test");
        w.W.Users[key] = id;
        return id;
    }

    public static Task<ReleaseDto> PublishMatrixAsync(this LifecycleWorld w, string json, Guid? org = null)
        => w.W.AsScopeless().Run(s => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(ApprovalsModule.MatrixKind, org ?? w.W.Org1, json, "test", "matrix for a scenario"));

    /// <summary>Publishes a copy of the contract workflow (or the Variation workflow) that names the approval matrix, so contracts created afterwards approve through it.</summary>
    public static async Task UseMatrixAsync(this LifecycleWorld w, string? matrixCode, string kind = WorkflowKinds.ContractLifecycle)
    {
        var json = await w.W.AsScopeless().Run(async s =>
        {
            var current = await s.Get<IWorkflowDefinitions>().CurrentAsync(kind, w.W.Org1);
            var release = await s.Get<IConfigurationReleases>().FindAsync(current.ReleaseId);
            return release!.ContentJson;
        });
        var node = JsonNode.Parse(json)!;
        var approval = node["settings"]!["approval"]!.AsObject();
        if (matrixCode is null) approval.Remove("matrix"); else approval["matrix"] = matrixCode;
        await w.W.AsScopeless().Run(s => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(kind, w.W.Org1, node.ToJsonString(), "test", "workflow names an approval matrix"));
    }

    public static Task<ResourceDescriptor> DescribeAsync(this LifecycleWorld w, Guid contractId) => w.W.AsScopeless().Run(s => s.Get<ISecuredContractService>().DescribeAsync(contractId));

    public static Task<ApprovalResolution> ResolveAsync(this LifecycleWorld w, string matrix, ApprovalFacts facts, ResourceDescriptor resource, Guid? submitter, DateTime? asOfUtc = null, MatrixSource? source = null)
        => w.W.AsScopeless().Run(s => s.Get<IApprovalResolver>().ResolveAsync(new ResolutionRequest(w.W.Org1, matrix, facts, resource, submitter, asOfUtc ?? w.W.Now, source ?? MatrixSource.Published)));

    public static IEnumerable<string> Names(this ApprovalResolution r, string stepKey, bool included = true)
        => r.Explanations.Where(e => e.StepKey.EndsWith("/" + stepKey, StringComparison.Ordinal) && e.Included == included).Select(e => e.Name);

    public static string Step(string key, string group, string mode = "FirstResponse", string? backup = null, string extra = "")
        => $"{{ \"key\": \"{key}\", \"display\": \"{key}\", \"approverGroup\": \"{group}\", \"mode\": \"{mode}\"{(backup is null ? "" : $", \"backupGroup\": \"{backup}\"")}{extra} }}";

    public static string Row(string code, string when, string steps, int priority = 100, string kind = "Base", string? effect = null, string flow = "Sequential", string? window = null)
        => $"{{ \"code\": \"{code}\", \"kind\": \"{kind}\"{(effect is null ? "" : $", \"effect\": \"{effect}\"")}, \"priority\": {priority}, \"flow\": \"{flow}\"{(window is null ? "" : ", " + window)}, \"when\": {when}, \"steps\": [ {steps} ] }}";

    public static string Doc(string rows, string code = "flow", string currency = "AUD", string extra = "")
        => $"{{ \"schemaVersion\": 1, \"matrices\": [ {{ \"code\": \"{code}\", \"name\": \"{code}\", \"currency\": \"{currency}\"{extra}, \"rows\": [ {rows} ] }} ] }}";

    public static string Single(string group, string mode = "FirstResponse", string? backup = null) => Doc(Row("only", "{}", Step("approve", group, mode, backup)));
}
