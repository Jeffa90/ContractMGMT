using System.Text.Json.Serialization;
using Ecp.SharedKernel;

namespace Ecp.Setup;

public enum StageStatus { NotStarted, InProgress, Complete, Skipped }

public sealed class StageRecord
{
    public StageStatus Status { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public string? Note { get; set; }
    public string? CompletedBy { get; set; }
    /// <summary>Non-secret values the stage recorded (choices, identifiers, statuses). Secrets and secret reference targets are never stored here.</summary>
    public Dictionary<string, string> Data { get; set; } = [];
}

/// <summary>
/// Resumable installation state (SETUP_AND_READINESS_SPEC section 1). Held in the protected bootstrap file from the first start and mirrored
/// to the database once it exists. Contains the hash of the setup token, never the token; never a secret.
/// </summary>
public sealed class SetupDocument
{
    public int Revision { get; set; }
    public string? TokenHash { get; set; }
    public DateTime? TokenExpiresUtc { get; set; }
    public string? SessionHash { get; set; }
    public DateTime? SessionExpiresUtc { get; set; }
    public EcpMode? Mode { get; set; }
    public string? Deployment { get; set; }
    public string? TenancyMode { get; set; }
    public string? TenantName { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganisationId { get; set; }
    public string? PackId { get; set; }
    public bool DemoDataApplied { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? CompletedBy { get; set; }
    public Dictionary<string, StageRecord> Stages { get; set; } = [];
    /// <summary>Warnings acknowledged at readiness, each with the acknowledging person and reason.</summary>
    public List<Acknowledgement> Acknowledgements { get; set; } = [];
    public List<ReadinessSnapshot> LastReadiness { get; set; } = [];

    [JsonIgnore] public bool IsComplete => CompletedUtc.HasValue;

    public StageRecord Stage(string id) => Stages.TryGetValue(id, out var s) ? s : Stages[id] = new StageRecord();
    public StageStatus StatusOf(string id) => Stages.TryGetValue(id, out var s) ? s.Status : StageStatus.NotStarted;
    public bool Done(string id) => StatusOf(id) is StageStatus.Complete or StageStatus.Skipped;
    public string? Get(string stage, string key) => Stages.TryGetValue(stage, out var s) && s.Data.TryGetValue(key, out var v) ? v : null;
}

public sealed record Acknowledgement(string CheckId, string Reason, string By, DateTime AtUtc);

public sealed record ReadinessSnapshot(string Id, string Description, string Severity, string Status, string Message);

public enum FieldKind { Text, Select, Checkbox, SecretReference, Number, TextArea }

public sealed record FieldOption(string Value, string Label);

public sealed record StageField(string Name, string Label, FieldKind Kind, bool Required = false, string? Help = null,
    IReadOnlyList<FieldOption>? Options = null, string? Value = null);

/// <summary>What the wizard shows for a stage: fields with their current values, notes and the stage status.</summary>
public sealed record StageDescriptor(string Id, string Title, string Summary, StageStatus Status, IReadOnlyList<StageField> Fields, IReadOnlyList<string> Notes,
    string ActionLabel, string? LastMessage, bool Available, string? UnavailableReason, Ecp.Access.AccessReview? Review = null);

public sealed record StageResult(bool Succeeded, string Message, IReadOnlyList<string> Details)
{
    public static StageResult Ok(string message, params string[] details) => new(true, message, details);
    public static StageResult Fail(string message, params string[] details) => new(false, message, details);
}

/// <summary>Who is running a stage: the holder of the setup session during installation, or a signed-in administrator when amending.</summary>
public sealed record SetupActor(string Label, Guid? UserId = null);

public sealed record StageSummary(string Id, string Title, StageStatus Status, bool Available, string? UnavailableReason);

public sealed record SetupSnapshot(bool IsComplete, int CompletedStages, int TotalStages, string? NextStageId, IReadOnlyList<StageSummary> Stages, EcpMode? Mode, DateTime? CompletedUtc, Guid? TenantId = null);

public sealed record SetupSessionResult(bool Granted, string? SessionId, string? Refusal);

public sealed record SetupReportCheck(string Id, string Description, string Severity, string Status, string Message, string? AcknowledgedBy, string? AcknowledgedReason);

public sealed record SetupReport(
    DateTime GeneratedUtc, string Mode, string? Deployment, string Version, bool SetupComplete, DateTime? CompletedUtc, string? TenancyMode,
    IReadOnlyList<(string Stage, string Title, string Status, DateTime? UpdatedUtc)> Stages, IReadOnlyList<SetupReportCheck> Checks,
    IReadOnlyDictionary<string, string> Integrations, string? SolutionPack, IReadOnlyList<string> Administrators, IReadOnlyList<string> Notes);

/// <summary>Facts about how the host was started that setup needs (no secrets, no connection strings).</summary>
public sealed record SetupEnvironment(
    EcpMode HostMode, string DataDirectory, bool DemoIdentityEnabled, bool DemoSeedEnabled, bool ConnectionFromSecretReference, string DatabaseProvider,
    string Version, bool AllowRemoteSetup, string? GovernanceName = null, string? GovernanceLabel = null, string? GovernanceNote = null);

/// <summary>Protects the bootstrap file. Implemented over ASP.NET Data Protection by the host.</summary>
public interface ISetupProtector
{
    string Protect(string plain);
    string Unprotect(string protectedText);
}
