namespace Ecp.SharedKernel;

/// <summary>The governance rule set an installation runs under. Production always enforces second-person configuration governance.</summary>
public enum GovernanceKind { Production, TestEnforced, TestSimplified, DemoSimplified }

/// <summary>
/// Whether configuration changes need a second person to publish them, and why. Production always requires it, and no setting lowers it. Test requires it only when <c>Ecp:Governance:SecondPerson</c> is
/// <c>Enforced</c>; an unrecognised value fails safe to the stricter rule. Demo keeps its simplified behaviour and ignores the setting, so a trial of the Production rules is always a Test installation
/// that says so (docs/IMPLEMENTATION_REHEARSAL.md, ADR-0038).
/// </summary>
public sealed record GovernanceMode(GovernanceKind Kind, bool SecondPersonRequired, string SettingValue, string Label, string Note)
{
    public const string SettingKey = "Ecp:Governance:SecondPerson";

    /// <summary>The stable name used on audit events, releases, reports and screens.</summary>
    public string Name => Kind.ToString();

    public bool IsProductionEquivalent => SecondPersonRequired;

    public static GovernanceMode Resolve(EcpMode mode, string? setting)
    {
        var raw = string.IsNullOrWhiteSpace(setting) ? "Default" : setting.Trim();
        var enforced = raw.Equals("Enforced", StringComparison.OrdinalIgnoreCase);
        var isDefault = raw.Equals("Default", StringComparison.OrdinalIgnoreCase);
        switch (mode)
        {
            case EcpMode.Production:
                return new(GovernanceKind.Production, true, raw, "Production governance",
                    "A configuration change is published by a different person from the one who drafted, last edited or submitted it. No setting lowers this.");
            case EcpMode.Test:
                if (enforced || !isDefault)
                    return new(GovernanceKind.TestEnforced, true, enforced ? "Enforced" : raw, "Test with Production-equivalent governance",
                        enforced ? "The second-person rule is enforced exactly as in Production." : $"The value '{raw}' is not recognised, so the stricter rule applies. Use Default or Enforced.");
                return new(GovernanceKind.TestSimplified, false, "Default", "Test with simplified governance",
                    "A configuration change can be published by the person who made it. Set Ecp:Governance:SecondPerson to Enforced to rehearse the Production rules.");
            default:
                return new(GovernanceKind.DemoSimplified, false, raw, "Demonstration with simplified governance",
                    "Demonstration installations keep simplified governance and ignore Ecp:Governance:SecondPerson. Use Test mode to rehearse the Production rules.");
        }
    }
}
