namespace Ecp.SharedKernel;

public enum UpgradeFindingSeverity { Info, Warning, Blocker }

/// <summary>
/// One thing a module noticed about its own data that matters to an upgrade (docs/UPGRADES.md, "Pre-flight"). The code is stable and the text is plain language; <see cref="Detail"/> is for authorised operators and carries
/// counts and identifiers, never record content. <see cref="Count"/> is the number of affected records (summed over tenants).
/// </summary>
public sealed record UpgradeFinding(string Area, string Code, UpgradeFindingSeverity Severity, string Title, string Description, string Detail, string Remediation, int Count = 1);

/// <summary>
/// Implemented by a module that can say whether its own published data and in-flight work are compatible with the version being upgraded to. A probe is called once per tenant, inside that tenant's scope (so row-level
/// security applies exactly as in normal use), read-only, and must not change anything. A probe that cannot judge something returns no finding for it rather than guessing.
/// </summary>
public interface IUpgradeProbe
{
    string Area { get; }
    Task<IReadOnlyList<UpgradeFinding>> ProbeAsync(CancellationToken ct = default);
}

/// <summary>
/// Tells services whether the installation is in maintenance mode, so a service that starts long-running or configuration-changing work (publishing configuration, committing a migration batch) can refuse it even if it is
/// called from somewhere other than a web request. The default implementation (nothing registered) is "not in maintenance".
/// </summary>
public interface IMaintenanceGate
{
    /// <summary>Throws a <see cref="DomainException"/> with code <c>maintenance_mode</c> when the installation is in maintenance mode.</summary>
    Task EnsureWritableAsync(string what, CancellationToken ct = default);
}
