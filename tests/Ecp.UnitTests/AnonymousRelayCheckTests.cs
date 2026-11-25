using Ecp.Operations;
using Ecp.Setup;
using Ecp.Setup.Stages;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>ADR-0048: in Production an SMTP relay used without signing in is never silent. Readiness R-NOT-02 reports it as a warning that needs a recorded reason.</summary>
[Trait("Category", "Unit")]
public class AnonymousRelayCheckTests
{
    private sealed class FixedEmail(EmailConfigView view) : IEmailConfigurationService
    {
        public Task<EmailConfigView> EffectiveAsync(CancellationToken ct = default) => Task.FromResult(view);
        public Task<IReadOnlyList<EmailIssue>> ValidateAsync(EmailSettings settings, string providerKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EmailConfigView> SaveAsync(string providerKey, EmailSettings settings, OperatorIdentity by, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EmailReadinessInfo> ReadinessAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EmailTestView> TestAsync(string recipient, OperatorIdentity by, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EmailTestView>> RecentTestsAsync(int take = 10, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static Task<CheckOutcome> RunAsync(EcpMode mode, string? userRef, string? passwordRef)
    {
        var view = new EmailConfigView(EmailProviderKeys.Smtp, "no-reply@example.invalid", "mail.example.invalid", 25, true, userRef, passwordRef, "saved", 1, "x");
        var services = new ServiceCollection().AddSingleton<IEmailConfigurationService>(new FixedEmail(view)).BuildServiceProvider();
        var env = new SetupEnvironment(mode, Path.GetTempPath(), false, false, mode == EcpMode.Production, "SqlServer", "test", false);
        return new AnonymousRelayCheck().RunAsync(new CheckContext(new SetupDocument(), env, services, default));
    }

    [Fact]
    public async Task Production_with_an_anonymous_relay_is_a_warning()
    {
        var outcome = await RunAsync(EcpMode.Production, null, null);
        Assert.False(outcome.Passed);
        Assert.Equal(CheckLevel.Warning, outcome.Level);
    }

    [Fact]
    public async Task Production_with_credential_references_passes()
        => Assert.True((await RunAsync(EcpMode.Production, "env:U", "env:P")).Passed);

    [Fact]
    public async Task Outside_Production_no_relay_policy_applies()
        => Assert.True((await RunAsync(EcpMode.Test, null, null)).Passed);

    [Fact]
    public void The_check_is_part_of_the_readiness_review()
        => Assert.Contains(typeof(AnonymousRelayCheck), SetupChecks.All());
}
