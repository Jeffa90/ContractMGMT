using Ecp.Host.Security;
using Ecp.Host.Pages;
using Ecp.Setup;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Setup;

/// <summary>Shared behaviour of the setup pages: the caller must hold the single open setup session (cookie) obtained with the one-time token.</summary>
public abstract class SetupPageModel(ISetupService setup) : EcpPageModel
{
    protected ISetupService Setup { get; } = setup;

    protected async Task<bool> HasSessionAsync(CancellationToken ct)
        => await Setup.ValidateSessionAsync(Request.Cookies[SetupGateMiddleware.SessionCookie], ct);

    protected static SetupActor SessionActor => new("setup session");

    /// <summary>The submitted form as plain values (antiforgery token and framework fields removed).</summary>
    protected IReadOnlyDictionary<string, string> FormValues()
        => Request.HasFormContentType
            ? Request.Form.Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal) && kv.Key != "handler").ToDictionary(kv => kv.Key, kv => kv.Value.ToString())
            : new Dictionary<string, string>();
}

/// <summary>A signed-in installation administrator amending configuration after setup. Needs permission setup.run and a recent authentication.</summary>
public static class AmendPolicy
{
    public const string Permission = "perm:setup.run";
}
