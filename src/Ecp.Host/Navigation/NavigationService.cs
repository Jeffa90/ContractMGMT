using Ecp.Host.Terminology;
using Ecp.Identity;
using Ecp.Notifications;

namespace Ecp.Host.Navigation;

/// <summary>The navigation for the current request, built once per request from what the signed-in person may do.</summary>
public sealed class NavigationService(IAccessService access, TermContext terms, INotificationInbox inbox)
{
    private Task<NavigationModel>? _model;

    public Task<NavigationModel> GetAsync(CancellationToken ct = default) => _model ??= BuildAsync(ct);

    private async Task<NavigationModel> BuildAsync(CancellationToken ct)
    {
        var unread = 0;
        try { unread = await inbox.UnreadCountAsync(); } catch (Exception) { /* a session without a person (an installation administrator) has no inbox */ }
        return await NavigationBuilder.BuildAsync(async permissions =>
        {
            foreach (var p in permissions) if (await access.HoldsAnywhereAsync(p, ct)) return true;
            return false;
        }, terms.UsesAnywhere, unread);
    }
}
