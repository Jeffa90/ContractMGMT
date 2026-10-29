using Ecp.Configuration;
using Ecp.Setup.Onboarding;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Onboarding;

public sealed record ChecklistItem(string Id, string Title, string Status, string Detail);

public sealed class IndexModel(IOnboardingService onboarding, IOrganisationStructureService organisations, Ecp.Identity.IAccessService access) : OnboardingPageModel(onboarding, organisations)
{
    public OnboardingState? State { get; private set; }
    public IReadOnlyList<ChecklistItem> Checklist { get; private set; } = [];
    public ReadinessReport? Readiness { get; private set; }
    public ReadyToUse? Ready { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        => await GuardedAsync(async () =>
        {
            if (!await SelectOrganisationAsync(ct)) return NotFound();
            State = await Onboarding.GetAsync(OrganisationId, ct);
            Checklist = Build(State);
            Readiness = await Onboarding.ReadinessAsync(OrganisationId, ct);
            if (Readiness.CanPublish && State.CurrentRelease is not null) Ready = new ReadyToUse(OrganisationId, OrganisationName, await access.HoldsAnywhereAsync("contracts.create", ct));
            return Page();
        });

    private static IReadOnlyList<ChecklistItem> Build(OnboardingState s)
    {
        var d = s.Document;
        string Count(int n, string noun) => n == 0 ? "Nothing entered yet" : $"{n} {noun}{(n == 1 ? "" : "s")}";
        string Status(int n) => n == 0 ? "Not started" : "Entered";
        var review = s.Validation is null ? ("Not validated", "Run validation on the review step") : s.Validation.IsValid ? ("Valid", s.IsPublishable ? "Ready to publish" : "Validate again after editing") : ("Needs attention", $"{s.Validation.Messages.Count(m => m.Severity == ValidationSeverity.Error)} error(s)");
        return
        [
            new("pack", "[[solution.pack|sentence]] and classification", d.PackId is null ? "Optional" : "Chosen", d.PackId is null ? "No pack chosen" : d.PackId),
            new("structure", "[[organisation]] structure", Status(d.Structure.Count), Count(d.Structure.Count, "item")),
            new("categories", "[[category|plural]] (taxonomy)", Status(d.Categories.Count), Count(d.Categories.Count, "category")),
            new("types", "[[contract.type|plural|sentence]]", Status(d.ContractTypes.Count), Count(d.ContractTypes.Count, "type")),
            new("owners", "[[contract.owner|plural|sentence]]", Status(d.CategoryOwners.Count), Count(d.CategoryOwners.Count, "owner")),
            new("viewers", "Viewer scopes", Status(d.ViewerScopes.Count), Count(d.ViewerScopes.Count, "scope")),
            new("mappings", "Identity group mappings", Status(d.GroupMappings.Count), Count(d.GroupMappings.Count, "mapping")),
            new("review", "Review, simulate and publish", review.Item1, review.Item2),
        ];
    }
}
