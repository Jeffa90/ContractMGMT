using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public class ConnectionInspectionTests
{
    [Theory]
    [InlineData("Server=db.example;Database=Ecp;Integrated Security=True;Encrypt=True", ConnectionAuthentication.IntegratedWindows, true)]
    [InlineData("Data Source=db.example;Initial Catalog=Ecp;Integrated Security=SSPI", ConnectionAuthentication.IntegratedWindows, true)]
    [InlineData("Server=db.example;Database=Ecp;Authentication=Active Directory Managed Identity", ConnectionAuthentication.DirectoryIdentity, true)]
    [InlineData("Server=db.example;Database=Ecp;User Id=app;Password=not-a-real-secret", ConnectionAuthentication.SqlLogin, false)]
    [InlineData("Server=db.example;Database=Ecp;Integrated Security=True;User Id=app;Password=not-a-real-secret", ConnectionAuthentication.SqlLogin, false)]
    [InlineData("Server=db.example;Database=Ecp", ConnectionAuthentication.Unknown, false)]
    [InlineData("this is not a connection string", ConnectionAuthentication.Unknown, false)]
    [InlineData("", ConnectionAuthentication.Unknown, false)]
    public void The_inspector_classifies_how_the_application_signs_in(string connection, ConnectionAuthentication expected, bool credentialFree)
    {
        var facts = SqlConnectionInspector.Inspect(connection);
        Assert.Equal(expected, facts.Authentication);
        Assert.Equal(credentialFree, facts.CredentialFree);
    }

    [Fact]
    public void The_description_names_server_database_and_method_but_never_a_credential()
    {
        var facts = SqlConnectionInspector.Inspect("Server=db.example;Database=Ecp;User Id=app_user;Password=not-a-real-secret");
        var text = facts.Describe();
        Assert.Contains("db.example", text); Assert.Contains("Ecp", text); Assert.Contains("SqlLogin", text);
        Assert.DoesNotContain("not-a-real-secret", text); Assert.DoesNotContain("app_user", text);
    }

    [Fact]
    public void Encryption_and_certificate_trust_are_reported()
    {
        var facts = SqlConnectionInspector.Inspect("Server=x;Database=y;Integrated Security=True;Encrypt=True;TrustServerCertificate=True");
        Assert.True(facts.Encrypt); Assert.True(facts.TrustServerCertificate);
        Assert.False(SqlConnectionInspector.Inspect("Server=x;Database=y;Integrated Security=True;Encrypt=False").Encrypt);
    }

    private static StartupFacts Facts(bool inline, bool credentialFree, bool secret)
        => new(EcpMode.Production, DatabaseProvider.SqlServer, secret, inline, false, false, "/var/lib/ecp", "/app", false, credentialFree);

    [Fact]
    public void Production_accepts_a_credential_free_inline_string_and_still_refuses_one_with_a_password()
    {
        Assert.Empty(ProductionGuard.Evaluate(Facts(inline: true, credentialFree: true, secret: false)));
        Assert.Contains(ProductionGuard.Evaluate(Facts(inline: true, credentialFree: false, secret: false)), v => v.CheckId == "R-SEC-02");
        Assert.Contains(ProductionGuard.Evaluate(Facts(inline: false, credentialFree: false, secret: false)), v => v.CheckId == "R-SEC-02");
        Assert.Empty(ProductionGuard.Evaluate(Facts(inline: false, credentialFree: false, secret: true)));
    }
}
