using Ecp.Operations;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>G-12 rules that hold wherever the settings are entered: Production needs a delivering provider, TLS and secret references; simulated providers never reach Production.</summary>
[Trait("Category", "Unit")]
public class EmailRulesTests
{
    private static IEmailSenderFactory Factory()
    {
        var services = new ServiceCollection();
        services.AddOperationsModule(Path.Combine(Path.GetTempPath(), "ecp-email-" + Guid.NewGuid().ToString("N")));
        services.AddSingleton<ISecretProvider>(new EnvironmentAndFileSecretProvider());
        return services.BuildServiceProvider().GetRequiredService<IEmailSenderFactory>();
    }

    private static EmailSettings Smtp(string? userRef = "env:ECP_UNIT_USER", string? passwordRef = "env:ECP_UNIT_PASSWORD", bool tls = true)
        => new(EmailProviderKind.Smtp, "platform@example.invalid", "smtp.example.invalid", 587, tls, userRef, passwordRef);

    [Theory]
    [InlineData("dev-capture")]
    [InlineData("demo-log")]
    [InlineData("disabled")]
    public void Production_refuses_providers_that_deliver_nothing(string key)
        => Assert.NotEmpty(EmailRules.Validate(Factory(), new EmailSettings(EmailProviderKind.Mock, "platform@example.invalid"), key, EcpMode.Production));

    [Theory]
    [InlineData(EcpMode.Demo)]
    [InlineData(EcpMode.Test)]
    public void Demo_and_Test_allow_simulated_providers(EcpMode mode)
        => Assert.Empty(EmailRules.Validate(Factory(), new EmailSettings(EmailProviderKind.Mock, "platform@example.invalid"), "dev-capture", mode));

    [Fact]
    public void Production_smtp_needs_tls_and_both_secret_references()
    {
        var factory = Factory();
        Environment.SetEnvironmentVariable("ECP_UNIT_USER", "u"); Environment.SetEnvironmentVariable("ECP_UNIT_PASSWORD", "p");
        try
        {
            Assert.Empty(EmailRules.Validate(factory, Smtp(), "smtp", EcpMode.Production));
            Assert.Contains(EmailRules.Validate(factory, Smtp(tls: false), "smtp", EcpMode.Production), i => i.Code == "tls");
            Assert.Contains(EmailRules.Validate(factory, Smtp(userRef: null, passwordRef: null), "smtp", EcpMode.Production), i => i.Code == "credentials_required");
            Assert.Contains(EmailRules.Validate(factory, Smtp(passwordRef: "literal-password"), "smtp", EcpMode.Production), i => i.Code == "password_ref");
        }
        finally { Environment.SetEnvironmentVariable("ECP_UNIT_USER", null); Environment.SetEnvironmentVariable("ECP_UNIT_PASSWORD", null); }
    }

    [Fact]
    public void Production_refuses_a_secret_reference_that_does_not_resolve()
        => Assert.Contains(EmailRules.Validate(Factory(), Smtp("env:ECP_UNIT_NOT_SET_USER", "env:ECP_UNIT_NOT_SET_PASSWORD"), "smtp", EcpMode.Production), i => i.Code == "user_unresolved");

    [Fact]
    public void The_fingerprint_changes_with_any_setting_and_ignores_case_and_spacing_of_the_host()
    {
        var a = EmailConfigurationFingerprint.Of("smtp", "a@x.test", "Smtp.X.test", 587, true, "env:U", "env:P");
        Assert.Equal(a, EmailConfigurationFingerprint.Of("smtp", "A@X.test", " smtp.x.test", 587, true, "env:U", "env:P"));
        Assert.NotEqual(a, EmailConfigurationFingerprint.Of("smtp", "a@x.test", "smtp.x.test", 25, true, "env:U", "env:P"));
        Assert.NotEqual(a, EmailConfigurationFingerprint.Of("smtp", "a@x.test", "smtp.x.test", 587, false, "env:U", "env:P"));
        Assert.NotEqual(a, EmailConfigurationFingerprint.Of("smtp", "a@x.test", "smtp.x.test", 587, true, "env:U", "env:Q"));
    }
}
