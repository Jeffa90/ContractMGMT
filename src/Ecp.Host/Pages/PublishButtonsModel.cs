using Ecp.Configuration;

namespace Ecp.Host.Pages;

/// <summary>What the shared publish buttons need: the draft, what this person may do with it, which handler the button posts to and what it says when it simply publishes.</summary>
public sealed record PublishButtonsModel(Guid? DraftId, GovernanceInfo? Governance, string Handler = "Publish", string PublishLabel = "Publish");
