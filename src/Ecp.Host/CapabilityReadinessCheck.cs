using Ecp.Configuration;
using Ecp.Lifecycle.Capabilities;
using Ecp.Lifecycle.Definitions;
using Ecp.Operations;
using Ecp.SharedKernel;

namespace Ecp.Host;

/// <summary>
/// Whether the shipped solution packs are coherent about their own capabilities: a pack's capability defaults must not leave a capability on without one it needs, and the pack's own starting workflows must not use a
/// capability the pack switches off. An incoherent pack would start every organisation that uses it in a process that offers steps nobody can take. Read-only; it changes nothing. Per-organisation findings (an
/// organisation's own choices, work in flight) are on the Capabilities pages, because they need a tenant.
/// </summary>
internal sealed class CapabilityReadinessCheck(IPackLoader loader, IConfiguration configuration) : IReadinessCheck
{
    public string Id => "capabilities";
    private const string Description = "Solution pack capability defaults are coherent";

    public async Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        var problems = new List<(string Pack, string Text)>();
        foreach (var entry in await loader.ListAsync(ct))
        {
            if (entry.Unavailable || !entry.IsCompatible || !entry.Trust.IsAcceptable) continue;
            var json = await loader.GetDocumentAsync(entry.PackKey, entry.Version, CapabilityKinds.Name, ct);
            if (json is null) continue;
            CapabilitySet set;
            try { set = CapabilityDocument.Parse(json).AsSet(); }
            catch (DomainException) { problems.Add((entry.PackKey, "its capabilities document cannot be read")); continue; }
            foreach (var u in set.Unmet()) problems.Add((entry.PackKey, $"'{u.Capability}' is on but '{u.Requires}' is off"));
            foreach (var (kind, label) in new[] { (WorkflowKinds.ContractLifecycle, "lifecycle"), (WorkflowKinds.Variation, "Variation workflow") })
            {
                if (kind == WorkflowKinds.Variation && !set.IsEnabled(CapabilityKeys.Variations)) continue;
                var workflow = await loader.GetDocumentAsync(entry.PackKey, entry.Version, kind, ct);
                if (workflow is null) continue;
                var definition = WorkflowDefinitionParser.Parse(workflow).Definition;
                if (definition is null) continue;
                foreach (var group in WorkflowCapabilityUse.Of(definition).Where(u => !set.IsEnabled(u.Capability)).GroupBy(u => u.Capability))
                    problems.Add((entry.PackKey, $"its {label} uses '{group.Key}', which the pack switches off"));
            }
        }
        if (problems.Count == 0) return new ReadinessResult(Id, Description, ReadinessStatus.Healthy, "Every shipped pack's capability defaults fit its own workflows.");
        var defaultPack = configuration["Ecp:DefaultPackId"];
        var status = !string.IsNullOrWhiteSpace(defaultPack) && problems.Any(p => p.Pack == defaultPack) ? ReadinessStatus.Unhealthy : ReadinessStatus.Degraded;
        return new ReadinessResult(Id, Description, status, "Incoherent capability defaults: " + string.Join("; ", problems.Take(5).Select(p => $"{p.Pack}: {p.Text}")) + ". Correct the pack's capabilities document or workflows.");
    }
}
