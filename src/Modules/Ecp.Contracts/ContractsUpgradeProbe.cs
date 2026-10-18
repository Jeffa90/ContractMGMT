using Ecp.Contracts.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

/// <summary>Upgrade pre-flight for stored list views: every saved view must still be readable by the register's parser. (Field references are checked by the register when a view is opened.)</summary>
internal sealed class ContractsUpgradeProbe(ContractsDbContext db) : IUpgradeProbe
{
    public string Area => "Contracts";

    public async Task<IReadOnlyList<UpgradeFinding>> ProbeAsync(CancellationToken ct = default)
    {
        var bad = 0;
        foreach (var json in await db.SavedViews.AsNoTracking().Select(v => v.DefinitionJson).ToListAsync(ct))
            if (Ecp.Contracts.Register.ViewParser.Parse(json).Set?.Views.FirstOrDefault() is null) bad++;
        return bad == 0 ? [] : [new("Contracts", "UPG-VW-001", UpgradeFindingSeverity.Warning, "Saved views that the new version would not read",
            "Some personal or shared saved views cannot be read by the register's parser. They are kept untouched and are skipped when lists are shown.", $"{bad} saved view(s).", "Ask the owners to recreate them after the upgrade.", bad)];
    }
}
