using Ecp.Notifications;
using Ecp.Notifications.Policy;
using Ecp.Operations;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public sealed class NotificationRendererTests
{
    private static readonly NotificationRenderer Renderer = new();
    private static readonly ClassificationScale Scale = ClassificationScale.Default;
    private static readonly NotificationPolicy Policy = Parse();

    private static NotificationPolicy Parse() { var (p, m) = NotificationPolicyParser.Parse(PackFiles.Read("notifications/notification-policy.json")); Assert.Empty(m); return p!; }
    private static readonly MessageTemplate Template = new("{{contract.reference}}: {{contract.title}}", "{{actor.name}} acted on {{contract.title}}. Open {{link}} (due {{due.date}}, {{days.remaining}} days, window {{window.days}}, step {{step.name}})");

    private static RenderContext Context(string classification, string title = "Secret supplier terms", string actor = "Pat") => new("AGR-1", title, classification, actor, "/Contracts/Details/1", new DateOnly(2027, 1, 31), 12, 30, "Review");

    [Theory]
    [InlineData("Public")]
    [InlineData("Internal")]
    [InlineData("Confidential")]
    public void Below_the_thresholds_the_message_carries_the_reference_and_title(string level)
    {
        var (subject, body) = Renderer.Render(Policy, Template, Context(level), Scale, Ecp.SharedKernel.TermSet.Neutral());
        Assert.Equal("AGR-1: Secret supplier terms", subject);
        Assert.Contains("Pat acted on Secret supplier terms. Open /Contracts/Details/1 (due 2027-01-31, 12 days, window 30, step Review)", body);
    }

    [Fact]
    public void At_the_restricted_level_the_title_is_withheld_but_the_reference_remains()
    {
        var (subject, body) = Renderer.Render(Policy, Template, Context("Restricted"), Scale, Ecp.SharedKernel.TermSet.Neutral());
        Assert.Equal("AGR-1: (title withheld)", subject);
        Assert.DoesNotContain("Secret supplier terms", subject + body);
    }

    [Fact]
    public void At_the_privileged_level_nothing_about_the_record_is_in_the_message()
    {
        var (subject, body) = Renderer.Render(Policy, Template, Context("Legal Privileged"), Scale, Ecp.SharedKernel.TermSet.Neutral());
        Assert.DoesNotContain("AGR-1", subject + body);
        Assert.DoesNotContain("Secret", subject + body);
        Assert.DoesNotContain("Pat", subject + body);
        Assert.Contains("restricted record", subject + body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Values_cannot_inject_headers_or_unbounded_text()
    {
        var (subject, body) = Renderer.Render(Policy, Template, Context("Internal", title: "Line one\r\nBcc: attacker@example.test", actor: new string('x', 500)), Scale, Ecp.SharedKernel.TermSet.Neutral());
        Assert.DoesNotContain('\r', subject);
        Assert.DoesNotContain('\n', subject);
        Assert.True(subject.Length <= 300);
        Assert.True(body.Length <= 3000);
        // Markup stays text: the renderer never interprets it.
        var (_, markup) = Renderer.Render(Policy, new MessageTemplate("s", "{{contract.title}}"), Context("Internal", title: "<script>alert(1)</script>"), Scale, Ecp.SharedKernel.TermSet.Neutral());
        Assert.Equal("<script>alert(1)</script>", markup);
    }

    [Fact]
    public void A_placeholder_outside_the_list_renders_as_nothing()
    {
        var (_, body) = Renderer.Render(Policy, new MessageTemplate("s", "x{{user.password}}y"), Context("Internal"), Scale, Ecp.SharedKernel.TermSet.Neutral());
        Assert.Equal("xy", body);
    }

    [Fact]
    public void Term_tokens_in_a_template_become_the_organisations_words_and_data_is_never_read_as_a_token()
    {
        var third = Ecp.SharedKernel.TermSet.Build("en", new[] { new Ecp.SharedKernel.TermEntry("renewal", "en", "Rollover", "Rollovers"), new Ecp.SharedKernel.TermEntry("contract", "en", "Agreement", "Agreements") }, null, null, null);
        var template = new MessageTemplate("[[renewal|sentence]] for {{contract.reference}}", "This [[contract|lower]] needs attention: {{contract.title}}");
        var (subject, body) = Renderer.Render(Policy, template, Context("Internal", title: "[[renewal]] not a token"), Scale, third);
        Assert.Equal("Rollover for AGR-1", subject);
        Assert.Equal("This agreement needs attention: [[renewal]] not a token", body);     // record data is substituted after the tokens, so it is not expanded
        var (neutralSubject, _) = Renderer.Render(Policy, template, Context("Internal"), Scale, Ecp.SharedKernel.TermSet.Neutral());
        Assert.Equal("Renewal for AGR-1", neutralSubject);
    }

    [Fact]
    public void A_template_with_an_unknown_term_token_or_stray_brackets_fails_validation_not_sending()
    {
        string PolicyJson(string subject) => """
            { "schemaVersion": 1, "events": [ { "event": "reminder.endDate", "recipients": ["Submitter"], "template": "t", "channels": ["InApp"], "mandatory": true } ],
              "templates": { "t": { "subject": "SUBJECT", "body": "Body" } } }
            """.Replace("SUBJECT", subject);
        IReadOnlyList<Ecp.Configuration.ValidationMessage> Check(string subject)
        {
            var (p, parse) = NotificationPolicyParser.Parse(PolicyJson(subject));
            return p is null ? parse : NotificationPolicyParser.Validate(p, Ecp.SharedKernel.ClassificationScale.Default);
        }
        Assert.DoesNotContain(Check("[[contract]] due"), m => m.Code == "term_token");
        Assert.Contains(Check("[[nope]] due"), m => m.Code == "term_token");
        Assert.Contains(Check("[[contract|bogus]] due"), m => m.Code == "term_token");
        Assert.Contains(Check("[[contract due"), m => m.Code == "term_token");
    }

}

[Trait("Category", "Unit")]
public sealed class EmailProviderTests
{
    private sealed class FakeSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }

    private sealed class FakeFactory(FakeSender sender, params EmailIssue[] issues) : IEmailSenderFactory
    {
        public IReadOnlyList<EmailIssue> Validate(EmailSettings settings, EcpMode mode) => issues;
        public IEmailSender Create(EmailSettings settings) => sender;
    }

    private static EmailDelivery Message() => new(Guid.NewGuid(), Guid.NewGuid(), "owner@example.test", "Subject", "Body", "corr-1");
    private static IOptions<NotificationsOptions> Options(EmailSettings? smtp) => Microsoft.Extensions.Options.Options.Create(new NotificationsOptions { Smtp = smtp, Mode = EcpMode.Production });

    [Fact]
    public async Task Smtp_sends_through_the_platform_sender_once_the_settings_validate()
    {
        var sender = new FakeSender();
        var provider = new SmtpProvider(new FakeFactory(sender), Options(new EmailSettings(EmailProviderKind.Smtp, "noreply@example.test", "smtp.example.test")));
        await provider.SendAsync(Message());
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("owner@example.test", sent.To);
        Assert.Equal("Subject", sent.Subject);
        Assert.False(provider.IsSimulated);
    }

    [Fact]
    public async Task Smtp_fails_visibly_when_not_configured_or_when_the_settings_are_unusable()
    {
        var sender = new FakeSender();
        var none = await Assert.ThrowsAsync<DomainException>(() => new SmtpProvider(new FakeFactory(sender), Options(null)).SendAsync(Message()));
        Assert.Equal("provider_not_configured", none.Code);
        var bad = await Assert.ThrowsAsync<DomainException>(() => new SmtpProvider(new FakeFactory(sender, new EmailIssue("tls", "TLS is required in Production.")), Options(new EmailSettings(EmailProviderKind.Smtp, "a@example.test", "h"))).SendAsync(Message()));
        Assert.Equal("provider_not_configured", bad.Code);
        Assert.Contains("TLS", bad.Message);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Graph_is_a_scaffold_that_fails_closed()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => new GraphProvider().SendAsync(Message()));
        Assert.Equal("provider_not_configured", ex.Code);
    }

    [Fact]
    public void The_demonstration_providers_are_disabled_in_Production_and_the_factory_says_so()
    {
        var factory = new EmailDeliveryProviderFactory();
        var production = factory.Describe(EcpMode.Production).ToDictionary(p => p.Key);
        Assert.False(production["demo-log"].Available);
        Assert.False(production["dev-capture"].Available);
        Assert.True(production["smtp"].Available);
        Assert.Contains("Not Verified", production["smtp"].Status);
        Assert.Contains("Not Verified", production["graph"].Status);
        var demo = factory.Describe(EcpMode.Demo).ToDictionary(p => p.Key);
        Assert.True(demo["dev-capture"].Available);
        Assert.Equal("Mocked", demo["dev-capture"].Status);
        Assert.True(factory.IsSimulated("demo-log"));
        Assert.False(factory.IsSimulated("smtp"));
    }

    [Theory]
    [InlineData("owner@example.test", "o***@example.test")]
    [InlineData("a@example.test", "***")]
    public void The_demonstration_log_masks_recipients(string address, string expected) => Assert.Equal(expected, DemoLogProvider.Mask(address));
}
