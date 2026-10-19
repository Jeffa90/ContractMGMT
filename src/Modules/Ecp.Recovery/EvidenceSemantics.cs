namespace Ecp.Recovery;

/// <summary>What an operational result counts as when it is used as rehearsal or implementation evidence.</summary>
public enum EvidenceVerdict
{
    /// <summary>The operation succeeded (Ready, Verified, Succeeded) or succeeded with warnings that were each explicitly accepted.</summary>
    Passed,
    /// <summary>The operation did not succeed (Blocked, Failed). It is never counted as a success.</summary>
    Failed,
    /// <summary>Nobody could check it (Not Verified). It is never counted as a success.</summary>
    NotVerified,
    /// <summary>It succeeded with warnings that have not all been accepted. It is not yet a pass.</summary>
    Warning,
}

/// <summary>
/// The judgement of one operational result: the original outcome as the operation reported it, what it counts as, a stable code and the reason. The original outcome is always kept; nothing converts it (ADR-0038).
/// </summary>
public sealed record EvidenceJudgement(string Operational, EvidenceVerdict Verdict, string Code, string Reason)
{
    public bool CountsAsSuccess => Verdict == EvidenceVerdict.Passed;
}

/// <summary>
/// The one place that decides what an operational outcome counts as in evidence (D14.13, D14.14). Ready is Passed. Ready with warnings is Passed only when every warning has explicit acceptance evidence. Blocked and Failed are
/// Failed. Not Verified stays Not Verified. Anything this class does not recognise is Not Verified, never Passed.
/// </summary>
public static class EvidenceSemantics
{
    public static string Label(EvidenceVerdict verdict) => verdict switch
    {
        EvidenceVerdict.Passed => "Passed",
        EvidenceVerdict.Failed => "Failed",
        EvidenceVerdict.NotVerified => "Not Verified",
        _ => "Warning (not accepted)",
    };

    /// <summary>An upgrade pre-flight. <paramref name="warningCodes"/> are the codes of its warnings; <paramref name="acceptedCodes"/> are those someone explicitly accepted, with evidence.</summary>
    public static EvidenceJudgement Judge(PreflightOutcome outcome, IEnumerable<string>? warningCodes = null, IEnumerable<string>? acceptedCodes = null)
    {
        var name = outcome switch { PreflightOutcome.ReadyWithWarnings => "Ready with warnings", PreflightOutcome.NotVerified => "Not Verified", _ => outcome.ToString() };
        switch (outcome)
        {
            case PreflightOutcome.Ready:
                return new(name, EvidenceVerdict.Passed, "EVD-001", "Ready: nothing was found.");
            case PreflightOutcome.ReadyWithWarnings:
            {
                var warnings = (warningCodes ?? []).Distinct(StringComparer.Ordinal).ToList();
                var accepted = (acceptedCodes ?? []).ToHashSet(StringComparer.Ordinal);
                var open = warnings.Where(w => !accepted.Contains(w)).ToList();
                if (warnings.Count == 0 || open.Count > 0)
                    return new(name, EvidenceVerdict.Warning, "EVD-011", warnings.Count == 0 ? "Ready with warnings, but the warnings are not listed, so none can be shown as accepted." : $"{open.Count} warning(s) have no recorded acceptance: {string.Join(", ", open)}.");
                return new(name, EvidenceVerdict.Passed, "EVD-010", $"Ready with warnings; every warning ({string.Join(", ", warnings)}) was explicitly accepted.");
            }
            case PreflightOutcome.Blocked:
                return new(name, EvidenceVerdict.Failed, "EVD-020", "Blocked: the operation cannot proceed. This is a failure, not a pass.");
            default:
                return new(name, EvidenceVerdict.NotVerified, "EVD-030", "Not Verified: a check could not be made. This is not a pass.");
        }
    }

    /// <summary>An operations dashboard status.</summary>
    public static EvidenceJudgement Judge(OpsStatus status) => status switch
    {
        OpsStatus.Verified => new("Verified", EvidenceVerdict.Passed, "EVD-001", "Verified."),
        OpsStatus.Warning => new("Warning", EvidenceVerdict.Warning, "EVD-011", "Warning: not a pass until it is accepted."),
        OpsStatus.Failed => new("Failed", EvidenceVerdict.Failed, "EVD-021", "Failed."),
        _ => new("Not Verified", EvidenceVerdict.NotVerified, "EVD-030", "Not Verified: this is not a pass."),
    };

    /// <summary>A post-restore or other validation check.</summary>
    public static EvidenceJudgement Judge(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Passed => new("Passed", EvidenceVerdict.Passed, "EVD-001", "Passed."),
        CheckOutcome.Warning => new("Warning", EvidenceVerdict.Warning, "EVD-011", "Warning: not a pass until it is accepted."),
        CheckOutcome.Failed => new("Failed", EvidenceVerdict.Failed, "EVD-021", "Failed."),
        _ => new("Not Verified", EvidenceVerdict.NotVerified, "EVD-030", "Not Verified: this is not a pass."),
    };

    /// <summary>
    /// A result known only by the word a page or report used. Recognised words map as above; "Succeeded with warnings" needs acceptance like "Ready with warnings"; any other word is Not Verified, so a new or misspelt
    /// outcome can never be read as a pass.
    /// </summary>
    public static EvidenceJudgement Judge(string? operationalOutcome, bool warningsAccepted = false)
    {
        var text = (operationalOutcome ?? "").Trim();
        var key = text.ToLowerInvariant().Replace("-", " ");
        switch (key)
        {
            case "ready": case "verified": case "succeeded": case "passed": case "healthy": case "completed":
                return new(text, EvidenceVerdict.Passed, "EVD-001", text + ".");
            case "ready with warnings": case "succeeded with warnings": case "verified with warnings": case "completed with warnings": case "warning": case "degraded":
                return warningsAccepted ? new(text, EvidenceVerdict.Passed, "EVD-010", text + "; the warnings were explicitly accepted.") : new(text, EvidenceVerdict.Warning, "EVD-011", text + ": not a pass until each warning is accepted.");
            case "blocked": case "failed": case "unhealthy": case "refused": case "rejected":
                return new(text, EvidenceVerdict.Failed, key == "blocked" ? "EVD-020" : "EVD-021", text + ": a failure, not a pass.");
            default:
                return new(text.Length == 0 ? "(no outcome)" : text, EvidenceVerdict.NotVerified, "EVD-030", "Not Verified: the outcome is not one this platform counts as a success.");
        }
    }
}
