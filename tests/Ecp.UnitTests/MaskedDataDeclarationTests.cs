using Ecp.DataMigration.Domain;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>G-35: the declaration records what the customer says and refuses the incomplete, the future-dated approval and the declaration that never expires. It never says the data is safe.</summary>
[Trait("Category", "Unit")]
public class MaskedDataDeclarationTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);

    private static MaskedDataDeclaration Make(Action<Dictionary<string, object?>>? change = null)
    {
        var a = new Dictionary<string, object?>
        {
            ["owner"] = "Finance Systems", ["source"] = "Legacy CLM", ["method"] = "Names and amounts replaced with generated values", ["fields"] = "Supplier name, Contact email, Amount",
            ["reference"] = "CHG-1042", ["approvedBy"] = "Head of Data", ["approved"] = Now.AddDays(-2), ["review"] = Now.AddDays(30), ["users"] = "a@x.test, b@x.test",
        };
        change?.Invoke(a);
        return MaskedDataDeclaration.Create(Guid.NewGuid(), Guid.NewGuid(), (string)a["owner"]!, (string)a["source"]!, (string)a["method"]!, (string)a["fields"]!, false, false, false,
            (string)a["reference"]!, (string)a["approvedBy"]!, (DateTime)a["approved"]!, (DateTime)a["review"]!, (string)a["users"]!, "declarer", Guid.NewGuid(), Now, "corr");
    }

    [Fact]
    public void A_complete_declaration_is_recorded_for_the_test_environment_only()
    {
        var d = Make();
        Assert.Equal("Test", d.PermittedEnvironment);
        Assert.True(d.IsCurrentAt(Now.AddDays(1)));
        Assert.False(d.IsCurrentAt(Now.AddDays(31)));                 // expired
        Assert.Equal(["a@x.test", "b@x.test"], d.PermittedUserList);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("source")]
    [InlineData("method")]
    [InlineData("fields")]
    [InlineData("reference")]
    [InlineData("approvedBy")]
    public void Every_mandatory_fact_is_required(string field) => Assert.ThrowsAny<Exception>(() => Make(a => a[field] = "  "));

    [Fact]
    public void An_approval_in_the_future_or_an_expiry_in_the_past_or_beyond_a_year_is_refused()
    {
        Assert.Equal("approved_in_future", Assert.Throws<DomainException>(() => Make(a => a["approved"] = Now.AddDays(3))).Code);
        Assert.Equal("review_in_past", Assert.Throws<DomainException>(() => Make(a => a["review"] = Now.AddDays(-1))).Code);
        Assert.Equal("review_too_far", Assert.Throws<DomainException>(() => Make(a => a["review"] = Now.AddDays(400))).Code);
    }

    [Fact]
    public void Revoking_ends_the_declaration_at_once_and_needs_a_reason()
    {
        var d = Make();
        Assert.Throws<DomainException>(() => d.Revoke(" ", Now));
        d.Revoke("Dataset withdrawn", Now.AddHours(1));
        Assert.False(d.IsCurrentAt(Now.AddHours(2)));
    }

    [Fact]
    public void A_classification_of_masked_data_needs_a_declaration_and_synthetic_data_must_not_have_one()
    {
        var t = Guid.NewGuid(); var o = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Equal("declaration_required", Assert.Throws<DomainException>(() => BatchDataClassification.Create(t, o, b, BatchDataClasses.MaskedCompanyData, null, "x", Now)).Code);
        Assert.Equal("declaration_not_needed", Assert.Throws<DomainException>(() => BatchDataClassification.Create(t, o, b, BatchDataClasses.Synthetic, Guid.NewGuid(), "x", Now)).Code);
        Assert.Equal("data_class", Assert.Throws<DomainException>(() => BatchDataClassification.Create(t, o, b, "Production", null, "x", Now)).Code);
        Assert.Equal("Masked Company Data", BatchDataClasses.Label(BatchDataClasses.MaskedCompanyData));
    }
}
