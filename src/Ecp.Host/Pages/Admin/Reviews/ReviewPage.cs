using Ecp.Configuration;
using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Reviews;

/// <summary>
/// What a business person calls each kind of configuration, and how a stored actor (a user id) is shown as a name. Presentation only: nothing here decides who may do what.
/// </summary>
public static class ConfigurationKindLabels
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WorkflowContractLifecycle"] = "Contract workflow",
        ["WorkflowVariation"] = "Variation workflow",
        ["ApprovalMatrix"] = "Approval matrices",
        ["NotificationPolicy"] = "Notification policy",
        ["Terminology"] = "Terminology",
        ["Capabilities"] = "Capabilities",
        ["RegionalProfile"] = "Regional settings",
        ["OrganisationOnboarding"] = "Organisation onboarding",
        ["CustomField"] = "Additional fields",
        ["ContractListView"] = "Saved views",
        ["SolutionPack"] = "Solution pack choice",
        ["Branding"] = "Branding",
        ["ClassificationPolicy"] = "Classification levels",
    };

    public static string Of(string kind) => Labels.TryGetValue(kind, out var l) ? l : kind;

    /// <summary>Where the person goes to edit this kind (the form editor), or null when the kind is edited elsewhere.</summary>
    public static string? EditPath(string kind, Guid organisationId) => kind switch
    {
        "Terminology" => $"/Admin/Terminology/Edit?organisationId={organisationId}",
        "Capabilities" => $"/Admin/Capabilities/Edit?organisationId={organisationId}",
        "RegionalProfile" => $"/Admin/Regional?organisationId={organisationId}",
        "OrganisationOnboarding" => $"/Onboarding?org={organisationId}",
        "CustomField" => "/Admin/Studio/Fields",
        "ContractListView" => "/Admin/Studio/Views",
        "WorkflowContractLifecycle" or "WorkflowVariation" or "ApprovalMatrix" or "NotificationPolicy" => $"/Admin/Configuration?kind={kind}&organisationId={organisationId}",
        _ => null,
    };
}

/// <summary>Shared by the review pages: names for people and organisations, and the step-up that publishing needs.</summary>
public abstract class ReviewPage(IConfigurationService configuration, IOrganisationStructureService organisations, IPeopleDirectory people, IAuthorizationService authorisation) : EcpPageModel
{
    protected IConfigurationService Configuration => configuration;
    protected IReadOnlyDictionary<string, string> Names { get; private set; } = new Dictionary<string, string>();
    protected IReadOnlyDictionary<Guid, string> OrganisationNames { get; private set; } = new Dictionary<Guid, string>();

    public string NameOf(string? actor) => string.IsNullOrEmpty(actor) ? "-" : Names.TryGetValue(actor, out var n) ? n : actor;
    public string OrganisationOf(Guid id) => id == Guid.Empty ? "Whole tenant" : OrganisationNames.TryGetValue(id, out var n) ? n : id.ToString();

    protected async Task ResolveNamesAsync(IEnumerable<string?> actors, IEnumerable<Guid> organisationIds, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var guids = actors.Where(a => !string.IsNullOrEmpty(a)).Select(a => Guid.TryParse(a, out var g) ? (Guid?)g : null).Where(g => g is not null).Select(g => g!.Value).Distinct().ToList();
        if (guids.Count > 0)
        {
            try { foreach (var (id, name) in await people.DisplayNamesAsync(guids, ct)) map[id.ToString()] = name; }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* names are a courtesy; the id is still shown */ }
        }
        Names = map;
        var orgs = await organisations.ListOrganisationsAsync(ct);
        OrganisationNames = orgs.ToDictionary(o => o.Id, o => o.Name);
    }

    protected async Task<bool> RecentAsync() => (await authorisation.AuthorizeAsync(User, Policies.RecentAuthentication)).Succeeded;
    protected IActionResult ReauthRedirect() => Redirect("/Reauth?returnUrl=" + Uri.EscapeDataString(Request.Path.Value + Request.QueryString.Value));
    protected async Task<IActionResult> StepUpAsync(Func<Task<IActionResult>> body, Func<Task>? reload = null) => await RecentAsync() ? await RunAsync(body, reload) : ReauthRedirect();
}

public static class GovernanceExtensions
{
    /// <summary>The governance picture for a draft, or null when the person has no right to see it (the page then simply does not offer review actions).</summary>
    public static async Task<GovernanceInfo?> TryGovernanceAsync(this IConfigurationService configuration, Guid? draftId, CancellationToken ct)
    {
        if (draftId is not { } id) return null;
        try { return await configuration.GovernanceAsync(id, ct); }
        catch (Ecp.SharedKernel.ResourceNotFoundException) { return null; }
        catch (Ecp.SharedKernel.ForbiddenException) { return null; }
    }

    /// <summary>What to tell the person after they pressed Publish.</summary>
    public static string Message(this PublishOutcome outcome, string published)
        => outcome.Release is not null ? published
            : "Submitted for review. A second person with permission to publish has to review it and publish it. They find it under Configuration reviews.";
}
