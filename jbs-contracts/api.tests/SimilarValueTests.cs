using JbsContracts.Api.Features;

namespace JbsContracts.Api.Tests;

/// <summary>D-118: the translate step pre-chooses a list value that's clearly the same as an old one (fictional plants).</summary>
public sealed class SimilarValueTests
{
    private static readonly string[] Sites = ["1220 Northgate Processing", "1225 Eastbrook Process", "HUON1 Corporate", "Pork Farms", "1832 Hillside Farming", "1833 Hillside Farming"];

    [Theory]
    [InlineData("1220 - NORTHGATE PROCESSING", "1220 Northgate Processing")] // case, spaces and punctuation ignored
    [InlineData("1220 NORTHGATE PROCESSIN", "1220 Northgate Processing")]   // a cut-off name, found by its code
    [InlineData("1225", "1225 Eastbrook Process")]                           // the code alone
    [InlineData("pork farms", "Pork Farms")]
    [InlineData("HUON1 - CORPORATE", "HUON1 Corporate")]
    [InlineData("Hillside Farming", null)]                                   // two match: a person chooses
    [InlineData("9999 Somewhere", null)]                                     // no such code
    [InlineData("Westside", null)]
    public void Picks_a_value_only_when_it_is_clearly_the_same(string old, string? expected) =>
        Assert.Equal(expected, LegacyImportEndpoints.Similar(old, Sites));
}
