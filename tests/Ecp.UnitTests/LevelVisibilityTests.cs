using Ecp.Contracts.Register;
using Ecp.Identity.Access;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// PER-002: the access test for a configured field (level above, or an id the person's level-allowing grants cover) is left out only when it is true for every contract already in the query, that is, when every
/// grant allows the field's level. Anything else keeps the test. These cases pin that decision, because getting it wrong in the open direction would show a value to someone who may not read it.
/// </summary>
public sealed class LevelVisibilityTests
{
    private static AccessGrant Grant(params string[] levels)
        => new(Guid.NewGuid(), "Role", null, [], [], [], null, levels, GrantRelationship.None);

    [Fact]
    public void Open_when_every_grant_allows_the_level()
        => Assert.True(LevelVisibility.IsOpen([Grant("Public", "Internal"), Grant("Public", "Internal", "Confidential")], "Internal"));

    [Fact]
    public void Closed_when_any_grant_does_not_allow_the_level()
        => Assert.False(LevelVisibility.IsOpen([Grant("Public", "Internal"), Grant("Public", "Internal", "Confidential")], "Confidential"));

    [Fact]
    public void Closed_when_the_person_has_no_grants_at_all()
        => Assert.False(LevelVisibility.IsOpen([], "Internal"));

    [Fact]
    public void A_level_is_matched_exactly_and_case_sensitively()
    {
        Assert.False(LevelVisibility.IsOpen([Grant("internal")], "Internal"));
        Assert.False(LevelVisibility.IsOpen([Grant("Internal ")], "Internal"));
    }
}
