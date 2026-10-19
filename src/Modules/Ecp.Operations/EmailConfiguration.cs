using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Audit;
using Ecp.Operations.Domain;
using Ecp.Operations.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Operations;

public static class EmailProviderKeys
{
    public const string Smtp = "smtp";
    public const string Graph = "graph";
    public const string DevCapture = "dev-capture";
    public const string DemoLog = "demo-log";
    public const string Disabled = "disabled";
    public static bool IsSimulated(string key) => key is DevCapture or DemoLog;
    public static readonly string[] All = [Smtp, Graph, DevCapture, DemoLog, Disabled];
}

/// <summary>The settings the host was started with (<c>Notifications:*</c>). They apply until an administrator saves settings in the application, which then take over.</summary>
public sealed record EmailEnvironmentDefaults(string ProviderKey, EmailSettings? Smtp);

public sealed class EmailReadinessOptions
{
    /// <summary>How long a passed test counts. After this the provider reads "Stale" until it is tested again (<c>Ecp:Email:ReadinessMaxAgeHours</c>, default seven days).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);
}

public sealed record EmailConfigView(string ProviderKey, string? FromAddress, string? Host, int Port, bool UseTls, string? UserRef, string? PasswordRef, string Source, int Revision, string Fingerprint)
{
    public bool IsSimulated => EmailProviderKeys.IsSimulated(ProviderKey);
    public EmailSettings ToSettings() => new(ProviderKey switch { EmailProviderKeys.Smtp => EmailProviderKind.Smtp, EmailProviderKeys.Disabled => EmailProviderKind.Disabled, EmailProviderKeys.Graph => EmailProviderKind.Disabled, _ => EmailProviderKind.Mock }, FromAddress, Host, Port, UseTls, UserRef, PasswordRef);
}

public enum EmailState { NotConfigured, NotVerified, Healthy, Simulated, Failed, Stale }

public sealed record EmailTestView(DateTime TestedUtc, string ProviderKey, string RecipientMasked, string ConnectivityOutcome, string ConnectivityCode, string ConnectivityDetail, string DeliveryOutcome, string DeliveryCode,
    string DeliveryDetail, bool Simulated, bool Succeeded, string CorrelationId, bool ForCurrentSettings);

public sealed record EmailReadinessInfo(EmailState State, string ProviderKey, string Summary, string? Code, EmailTestView? LastTest, DateTime? LastSuccessUtc, DateTime? LastFailureUtc, DateTime? ExpiresUtc, string Source, int Revision)
{
    /// <summary>The evidence verdict: only a Healthy provider is Passed; Simulated is Not Verified because nothing real was exercised.</summary>
    public string Verdict => State switch { EmailState.Healthy => "Passed", EmailState.Failed => "Failed", _ => "Not Verified" };
}

public sealed class EmailValidationException(IReadOnlyList<EmailIssue> issues) : DomainException("email_invalid", "The email settings are not valid: " + string.Join(" ", issues.Select(i => i.Message)))
{
    public IReadOnlyList<EmailIssue> Issues { get; } = issues;
}

/// <summary>A failure to talk to a mail server, with a stable diagnostic code and a plain sentence that is safe to show (no server banner, no secret).</summary>
public sealed class EmailDeliveryException(string diagnosticCode, string message) : DomainException(diagnosticCode, message);

/// <summary>
/// The installation's email settings, in one place (G-12): saved settings override the host's starting configuration, secrets are only ever references, and "Healthy" is earned only by a test that really connected to the
/// provider and delivered a fixed synthetic message. A changed setting changes the fingerprint, so it cannot inherit an earlier pass; a pass expires by <see cref="EmailReadinessOptions.MaxAge"/>.
/// </summary>
public interface IEmailConfigurationService
{
    Task<EmailConfigView> EffectiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EmailIssue>> ValidateAsync(EmailSettings settings, string providerKey, CancellationToken ct = default);
    Task<EmailConfigView> SaveAsync(string providerKey, EmailSettings settings, OperatorIdentity by, CancellationToken ct = default);
    Task<EmailReadinessInfo> ReadinessAsync(CancellationToken ct = default);
    Task<EmailTestView> TestAsync(string recipient, OperatorIdentity by, CancellationToken ct = default);
    Task<IReadOnlyList<EmailTestView>> RecentTestsAsync(int take = 10, CancellationToken ct = default);
}

/// <summary>Talks SMTP just far enough to prove the server is reachable and, when TLS is required, that it offers and completes STARTTLS with a certificate the platform trusts. Sends no mail.</summary>
internal static class SmtpProbe
{
    public sealed record Result(bool Passed, string Code, string Detail);

    public static async Task<Result> RunAsync(string host, int port, bool useTls, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            try { await client.ConnectAsync(host, port, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "EML-CONN-001", "The mail server did not answer in time. Check the host name, the port and any firewall between the platform and the mail server."); }
            catch (SocketException) { return new(false, "EML-CONN-001", "The platform could not connect to the mail server. Check the host name, the port and any firewall between the platform and the mail server."); }
            Stream stream = client.GetStream();
            var greeting = await ReadReplyAsync(stream, cts.Token);
            if (!greeting.StartsWith("220", StringComparison.Ordinal)) return new(false, "EML-CONN-002", "A service answered on that port but it does not look like a mail server.");
            var caps = await CommandAsync(stream, "EHLO ecp.invalid", cts.Token);
            if (!caps.StartsWith("250", StringComparison.Ordinal)) return new(false, "EML-CONN-002", "The mail server did not accept the platform's greeting.");
            if (useTls)
            {
                if (!caps.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)) return new(false, "EML-TLS-001", "TLS is required but the mail server does not offer it on this port. Use the server's submission port (usually 587) or turn off the requirement only outside Production.");
                var go = await CommandAsync(stream, "STARTTLS", cts.Token);
                if (!go.StartsWith("220", StringComparison.Ordinal)) return new(false, "EML-TLS-001", "The mail server refused to start TLS.");
                try
                {
                    using var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
                }
                catch (System.Security.Authentication.AuthenticationException) { return new(false, "EML-TLS-002", "The mail server's certificate is not trusted or does not match the host name. Install the issuing certificate authority on the server or correct the host name."); }
                return new(true, "", "Connected and completed TLS.");
            }
            await CommandAsync(stream, "QUIT", cts.Token);
            return new(true, "", "Connected (no TLS requested).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "EML-CONN-001", "The mail server did not answer in time."); }
        catch (Exception ex) when (ex is IOException or SocketException) { return new(false, "EML-CONN-002", "The connection to the mail server was closed unexpectedly."); }
    }

    private static async Task<string> CommandAsync(Stream stream, string command, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), ct);
        await stream.FlushAsync(ct);
        return await ReadReplyAsync(stream, ct);
    }

    /// <summary>Reads one (possibly multi-line) SMTP reply and returns all of it.</summary>
    private static async Task<string> ReadReplyAsync(Stream stream, CancellationToken ct)
    {
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (all.Length < 8192)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            var ch = (char)buffer[0];
            if (ch == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                all.AppendLine(text);
                if (text.Length < 4 || text[3] == ' ') break;   // last line of the reply
            }
            else line.Append(ch);
        }
        return all.ToString();
    }
}

/// <summary>A short hash of the settings a test result is tied to. It holds no secret: only references, which are not secret.</summary>
public static class EmailConfigurationFingerprint
{
    public static string Of(string providerKey, string? from, string? host, int port, bool tls, string? userRef, string? passwordRef)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', providerKey, from?.Trim().ToLowerInvariant(), host?.Trim().ToLowerInvariant(), port, tls, userRef?.Trim(), passwordRef?.Trim())))).ToLowerInvariant();
}

/// <summary>The rules every email setting must meet, independent of where it is entered (Setup, the Email page, or the host's starting configuration).</summary>
public static class EmailRules
{
    public static IReadOnlyList<EmailIssue> Validate(IEmailSenderFactory senders, EmailSettings settings, string providerKey, EcpMode mode)
    {
        var issues = new List<EmailIssue>();
        if (!EmailProviderKeys.All.Contains(providerKey)) { issues.Add(new("provider", "Choose one of the listed email providers.")); return issues; }
        var production = mode == EcpMode.Production;
        if (production && EmailProviderKeys.IsSimulated(providerKey)) issues.Add(new("simulated_in_production", "The demonstration and development-capture providers deliver nothing and are refused in Production."));
        else if (production && providerKey == EmailProviderKeys.Disabled) issues.Add(new("email_required", "Production requires a delivering email provider."));
        else if (providerKey == EmailProviderKeys.Smtp) issues.AddRange(senders.Validate(settings with { Provider = EmailProviderKind.Smtp }, mode));
        else if (providerKey == EmailProviderKeys.Graph)
        {
            if (string.IsNullOrWhiteSpace(settings.From) || !MailAddress.TryCreate(settings.From, out _)) issues.Add(new("from", "A valid From address is required."));
        }
        else if (providerKey is EmailProviderKeys.DevCapture or EmailProviderKeys.DemoLog && !string.IsNullOrWhiteSpace(settings.From) && !MailAddress.TryCreate(settings.From, out _))
            issues.Add(new("from", "The From address is not valid."));   // a simulated provider sends nothing, so a From address is optional
        if (production && providerKey == EmailProviderKeys.Smtp && (string.IsNullOrWhiteSpace(settings.UserRef) || string.IsNullOrWhiteSpace(settings.PasswordRef)))
            issues.Add(new("credentials_required", "Production requires the SMTP user and password as secret references (env:, file: or keyvault:)."));
        return issues;
    }
}

internal sealed class EmailConfigurationService(OperationsDbContext db, IUnitOfWork uow, IAuditWriter audit, TenantContext tenant, IClock clock, IActorContext actor, IEmailSenderFactory senders, ISecretProvider secrets,
    EmailReadinessOptions readiness, EmailEnvironmentDefaults? environmentDefaults = null, HostModeAccessor? hostMode = null) : IEmailConfigurationService
{
    // Where the host states neither, the installation is treated as Production (the stricter reading) and email as not configured.
    private EmailEnvironmentDefaults defaults => environmentDefaults ?? new EmailEnvironmentDefaults(EmailProviderKeys.Disabled, null);
    private HostModeAccessor mode => hostMode ?? new HostModeAccessor(EcpMode.Production);

    public static string Fingerprint(string providerKey, string? from, string? host, int port, bool tls, string? userRef, string? passwordRef)
        => EmailConfigurationFingerprint.Of(providerKey, from, host, port, tls, userRef, passwordRef);

    private EmailConfigView FromEnvironment()
    {
        var s = defaults.Smtp;
        return new(defaults.ProviderKey, s?.From, s?.Host, s?.Port ?? 587, s?.UseTls ?? true, s?.UserRef, s?.PasswordRef, "environment", 0,
            Fingerprint(defaults.ProviderKey, s?.From, s?.Host, s?.Port ?? 587, s?.UseTls ?? true, s?.UserRef, s?.PasswordRef));
    }

    public async Task<EmailConfigView> EffectiveAsync(CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        EmailConfiguration? row;
        try { row = await db.EmailConfigurations.AsNoTracking().FirstOrDefaultAsync(ct); }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or Microsoft.Data.Sqlite.SqliteException) { return FromEnvironment(); }   // the table does not exist before the upgrade that adds it
        return row is null ? FromEnvironment() : new(row.ProviderKey, row.FromAddress, row.Host, row.Port, row.UseTls, row.UserRef, row.PasswordRef, "saved", row.Revision, row.Fingerprint);
    }

    public Task<IReadOnlyList<EmailIssue>> ValidateAsync(EmailSettings settings, string providerKey, CancellationToken ct = default)
        => Task.FromResult(EmailRules.Validate(senders, settings, providerKey, mode.Mode));

    public async Task<EmailConfigView> SaveAsync(string providerKey, EmailSettings settings, OperatorIdentity by, CancellationToken ct = default)
    {
        var issues = await ValidateAsync(settings, providerKey, ct);
        if (issues.Count > 0) throw new EmailValidationException(issues);
        using var _ = tenant.UseInstallationScope();
        var now = clock.UtcNow;
        var print = Fingerprint(providerKey, settings.From, settings.Host, settings.Port, settings.UseTls, settings.UserRef, settings.PasswordRef);
        return await uow.ExecuteAsync(async c =>
        {
            var row = await db.EmailConfigurations.FirstOrDefaultAsync(c);
            var before = row?.Fingerprint;
            if (row is null) { row = EmailConfiguration.Create(); db.EmailConfigurations.Add(row); }
            row.Set(providerKey, settings.From, settings.Host, settings.Port, settings.UseTls, settings.UserRef, settings.PasswordRef, print, by.Id, now);
            audit.Record(new AuditEntry("operations.email.configured", "EmailConfiguration", EmailConfiguration.RowId.ToString(),
                NewValuesJson: JsonSerializer.Serialize(new { provider = providerKey, host = settings.Host, port = settings.Port, tls = settings.UseTls, userRef = settings.UserRef, passwordRef = settings.PasswordRef, revision = row.Revision, changed = before != print, by = by.Id })));
            await db.SaveChangesAsync(c);
            return new EmailConfigView(row.ProviderKey, row.FromAddress, row.Host, row.Port, row.UseTls, row.UserRef, row.PasswordRef, "saved", row.Revision, row.Fingerprint);
        }, ct);
    }

    private static EmailTestView Map(EmailTestResult r, string currentFingerprint) => new(r.TestedUtc, r.ProviderKey, r.RecipientMasked, r.ConnectivityOutcome, r.ConnectivityCode, r.ConnectivityDetail, r.DeliveryOutcome, r.DeliveryCode,
        r.DeliveryDetail, r.Simulated, r.Succeeded, r.CorrelationId, r.ConfigFingerprint == currentFingerprint);

    public async Task<IReadOnlyList<EmailTestView>> RecentTestsAsync(int take = 10, CancellationToken ct = default)
    {
        var current = await EffectiveAsync(ct);
        using var _ = tenant.UseInstallationScope();
        try { return [.. (await db.EmailTestResults.AsNoTracking().OrderByDescending(r => r.TestedUtc).Take(Math.Clamp(take, 1, 50)).ToListAsync(ct)).Select(r => Map(r, current.Fingerprint))]; }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or Microsoft.Data.Sqlite.SqliteException) { return []; }
    }

    public async Task<EmailReadinessInfo> ReadinessAsync(CancellationToken ct = default)
    {
        var cfg = await EffectiveAsync(ct);
        var production = mode.Mode == EcpMode.Production;
        var tests = await RecentTestsAsync(50, ct);
        var forCurrent = tests.Where(t => t.ForCurrentSettings).ToList();
        var last = forCurrent.FirstOrDefault();
        var lastSuccess = tests.FirstOrDefault(t => t.Succeeded)?.TestedUtc;
        var lastFailure = tests.FirstOrDefault(t => !t.Succeeded && t.ConnectivityOutcome != "NotVerified")?.TestedUtc;
        EmailReadinessInfo Info(EmailState state, string summary, string? code = null, DateTime? expires = null) => new(state, cfg.ProviderKey, summary, code, last, lastSuccess, lastFailure, expires, cfg.Source, cfg.Revision);

        if (cfg.ProviderKey == EmailProviderKeys.Disabled) return Info(EmailState.NotConfigured, "Email is disabled: notifications stay in the in-app inbox only.");
        var issues = await ValidateAsync(cfg.ToSettings(), cfg.ProviderKey, ct);
        if (issues.Count > 0) return Info(EmailState.Failed, "The email settings are not valid: " + string.Join(" ", issues.Select(i => i.Message)), "EML-CFG-001");
        foreach (var (label, value) in new[] { ("user", cfg.UserRef), ("password", cfg.PasswordRef) })
            if (cfg.ProviderKey == EmailProviderKeys.Smtp && SecretReference.TryParse(value, out var reference) && secrets.Resolve(reference) is null)
                return Info(EmailState.Failed, $"The SMTP {label} secret reference does not resolve on this server.", "EML-SEC-001");
        if (last is null) return Info(EmailState.NotVerified, EmailProviderKeys.IsSimulated(cfg.ProviderKey)
            ? "A simulated provider is selected and has not been tested. Nothing is delivered to a real mailbox."
            : "The provider is configured but has not been tested with these settings. Send a test message to prove it works.", "EML-RDY-001");
        if (cfg.ProviderKey == EmailProviderKeys.Graph) return Info(EmailState.NotVerified, "Microsoft Graph is not connected in this release; nothing was exercised.", "EML-GRAPH-001");
        if (!last.Succeeded) return Info(EmailState.Failed, "The last test failed: " + (last.ConnectivityOutcome == "Failed" ? last.ConnectivityDetail : last.DeliveryDetail), last.ConnectivityOutcome == "Failed" ? last.ConnectivityCode : last.DeliveryCode);
        if (last.Simulated) return Info(EmailState.Simulated, "Simulated provider: the test message was captured, not delivered to a real mailbox.");
        var expires = last.TestedUtc + readiness.MaxAge;
        if (clock.UtcNow > expires) return Info(EmailState.Stale, $"The last passing test was on {last.TestedUtc:yyyy-MM-dd} and is older than the {(int)readiness.MaxAge.TotalDays}-day policy. Test again.", "EML-RDY-002", expires);
        return Info(EmailState.Healthy, "The provider was reached and a test message was accepted.", null, expires);
    }

    public async Task<EmailTestView> TestAsync(string recipient, OperatorIdentity by, CancellationToken ct = default)
    {
        var cfg = await EffectiveAsync(ct);
        var issues = await ValidateAsync(cfg.ToSettings(), cfg.ProviderKey, ct);
        if (issues.Count > 0) throw new EmailValidationException(issues);
        if (cfg.ProviderKey == EmailProviderKeys.Disabled) throw new DomainException("email_disabled", "Email is disabled. Choose a provider before sending a test message.");
        if (!MailAddress.TryCreate(recipient, out var to) || to.Address != recipient.Trim()) throw new DomainException("recipient", "Enter a valid test recipient address.");
        var settings = cfg.ToSettings();
        var correlation = actor.CorrelationId;
        string cOutcome, cCode = "", cDetail, dOutcome, dCode = "", dDetail;
        var simulated = EmailProviderKeys.IsSimulated(cfg.ProviderKey);
        var message = new EmailMessage(to.Address, "Enterprise Contract Platform email test",
            $"This is a test message from the Enterprise Contract Platform. It contains no contract information and needs no action.\r\nTest reference: {correlation}");

        if (cfg.ProviderKey == EmailProviderKeys.Graph)
        {
            cOutcome = dOutcome = "NotVerified"; cCode = dCode = "EML-GRAPH-001";
            cDetail = dDetail = "Microsoft Graph is not connected in this release, so nothing was exercised.";
        }
        else if (simulated)
        {
            cOutcome = "NotVerified"; cCode = "EML-SIM-001"; cDetail = "A simulated provider has no external server to connect to.";
            try { await senders.Create(settings with { Provider = EmailProviderKind.Mock }).SendAsync(message, ct); dOutcome = "Passed"; dDetail = "The test message was written to the outbox folder. Nothing was delivered."; }
            catch (Exception ex) when (ex is not OperationCanceledException) { dOutcome = "Failed"; dCode = "EML-DLV-001"; dDetail = "The simulated outbox could not be written."; }
        }
        else
        {
            var probe = await SmtpProbe.RunAsync(settings.Host!, settings.Port, settings.UseTls, TimeSpan.FromSeconds(10), ct);
            cOutcome = probe.Passed ? "Passed" : "Failed"; cCode = probe.Code; cDetail = probe.Detail;
            if (!probe.Passed) { dOutcome = "NotVerified"; dCode = "EML-DLV-000"; dDetail = "No delivery was attempted because the server could not be reached."; }
            else
            {
                try { await senders.Create(settings).SendAsync(message, ct); dOutcome = "Passed"; dDetail = "The mail server accepted the test message."; }
                catch (EmailDeliveryException ex) { dOutcome = "Failed"; dCode = ex.Code; dDetail = ex.Message; }
                catch (DomainException ex) { dOutcome = "Failed"; dCode = "EML-DLV-001"; dDetail = ex.Message; }
            }
        }

        var result = EmailTestResult.Record(cfg.Fingerprint, cfg.ProviderKey, clock.UtcNow, by.Id, Mask(to.Address), cOutcome, cCode, cDetail, dOutcome, dCode, dDetail, simulated, correlation);
        using var _ = tenant.UseInstallationScope();
        await uow.ExecuteAsync(async c =>
        {
            db.EmailTestResults.Add(result);
            audit.Record(new AuditEntry("operations.email.tested", "EmailConfiguration", EmailConfiguration.RowId.ToString(), result.Succeeded ? AuditOutcome.Success : AuditOutcome.Failed,
                NewValuesJson: JsonSerializer.Serialize(new { provider = cfg.ProviderKey, connectivity = cOutcome, connectivityCode = cCode, delivery = dOutcome, deliveryCode = dCode, simulated, recipient = result.RecipientMasked, by = by.Id })));
            await db.SaveChangesAsync(c);
        }, ct);
        return Map(result, cfg.Fingerprint);
    }

    private static string Mask(string address) { var at = address.IndexOf('@'); return at <= 1 ? "***" + address[Math.Max(at, 0)..] : address[0] + "***" + address[at..]; }
}

/// <summary>The installation mode, available to services in this module without a dependency on the host.</summary>
public sealed record HostModeAccessor(EcpMode Mode);
