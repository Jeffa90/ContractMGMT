using Ecp.Access;
using Ecp.Identity.Domain;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>The wizard's "Where can they work?" answers become ordinary scope sets on ordinary grants; nothing here is a second permission model.</summary>
public sealed class AccessProfileScopeTests
{
    private static readonly Guid User = Guid.NewGuid(), Org = Guid.NewGuid(), Place = Guid.NewGuid();

    private static ScopeSet ScopeOf(string profileKey, ScopeChoice scope, string role) =>
        AccessProfileService.BuildGrant(new AccessChange(User, Org, [profileKey], scope, [Place]), AccessProfiles.Find(profileKey)!, role, "Internal", explicitHigh: false).Scope;

    [Theory]
    [InlineData(ScopeChoice.Divisions)]
    [InlineData(ScopeChoice.BusinessUnits)]
    [InlineData(ScopeChoice.Sites)]
    [InlineData(ScopeChoice.Categories)]
    public void Each_place_choice_narrows_only_that_dimension_inside_the_chosen_organisation(ScopeChoice choice)
    {
        var scope = ScopeOf(AccessProfiles.ReadOnly, choice, RoleCodes.Viewer);
        Assert.Equal([Org], scope.OrganisationIds);
        Assert.Equal("Internal", scope.ClassificationCeiling);
        Assert.Equal(choice == ScopeChoice.Divisions ? [Place] : [], scope.DivisionIds);
        Assert.Equal(choice == ScopeChoice.BusinessUnits ? [Place] : [], scope.BusinessUnitIds);
        Assert.Equal(choice == ScopeChoice.Sites ? [Place] : [], scope.SiteIds);
        Assert.Equal(choice == ScopeChoice.Categories ? [Place] : null, scope.CategoryIds);
    }

    [Fact]
    public void The_whole_organisation_is_not_narrowed()
    {
        var scope = ScopeOf(AccessProfiles.ReadOnly, ScopeChoice.WholeOrganisation, RoleCodes.Viewer);
        Assert.Empty(scope.DivisionIds); Assert.Empty(scope.BusinessUnitIds); Assert.Empty(scope.SiteIds); Assert.Null(scope.CategoryIds);
    }

    [Theory]
    [InlineData(AccessProfiles.OrganisationAdministrator, RoleCodes.OrganisationAdministrator)]
    [InlineData(AccessProfiles.BusinessConfigurationAdministrator, RoleCodes.BusinessConfigurationAdministrator)]
    [InlineData(AccessProfiles.Auditor, RoleCodes.Auditor)]
    public void An_administrator_grade_profile_is_never_narrowed_whatever_is_posted(string profile, string role)
    {
        var scope = ScopeOf(profile, ScopeChoice.Sites, role);
        Assert.Empty(scope.SiteIds); Assert.Null(scope.CategoryIds);
    }

    [Theory]
    [InlineData(ScopeChoice.WholeOrganisation, ViewerKind.AllContracts)]
    [InlineData(ScopeChoice.Categories, ViewerKind.Category)]
    [InlineData(ScopeChoice.Sites, ViewerKind.Site)]
    [InlineData(ScopeChoice.BusinessUnits, ViewerKind.BusinessUnit)]
    public void A_read_only_grant_takes_its_viewer_kind_from_the_place_choice_without_the_administrator_seeing_it(ScopeChoice choice, ViewerKind expected)
    {
        var grant = AccessProfileService.BuildGrant(new AccessChange(User, Org, [AccessProfiles.ReadOnly], choice, [Place]), AccessProfiles.Find(AccessProfiles.ReadOnly)!, RoleCodes.Viewer, "Internal", false);
        Assert.Equal(expected, grant.ViewerKind);
    }
}
