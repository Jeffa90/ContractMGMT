using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Ecp.Host;
using Ecp.Host.Logging;
using Ecp.Host.Security;
using Ecp.Persistence;
using Ecp.SharedKernel.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>Structured logging, the shared context, redaction and the durable file. The destinations that need real infrastructure (a mail server, Application Insights) are NOT VERIFIED here.</summary>
[Collection("Api")]
[Trait("Category", "Api")]
public class LoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ecp-log-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { /* the file may still be flushing */ } }

    private static IConfiguration Config(Dictionary<string, string?> v) => new ConfigurationBuilder().AddInMemoryCollection(v).Build();

    private sealed class Capture : ILogEventSink { public List<string> Lines { get; } = []; public void Emit(LogEvent e) { var w = new StringWriter(); new RedactingJsonFormatter().Format(e, w); Lines.Add(w.ToString()); } }

    [Theory]
    [InlineData("Server=db;Database=x;User Id=u;Password=SuperSecret123;Encrypt=True", "SuperSecret123")]
    [InlineData("connection failed for Server=db;pwd=SuperSecret123;", "SuperSecret123")]
    [InlineData("{\"Password\":\"SuperSecret123\",\"User\":\"u\"}", "SuperSecret123")]
    [InlineData("{\"connectionString\": \"Server=a;Password=SuperSecret123\"}", "SuperSecret123")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234567890", "abcdefghijklmnop1234567890")]
    [InlineData("GET /cb?code=1&access_token=SuperSecret123&state=2", "SuperSecret123")]
    [InlineData("id_token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlMTIz", "eyJhbGciOiJSUzI1NiJ9")]
    [InlineData("client_secret=SuperSecret123&grant_type=authorization_code", "SuperSecret123")]
    public void Secrets_are_removed_from_a_log_line(string line, string secret)
    {
        var redacted = LogRedactor.Redact(line);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(LogRedactor.Mask, redacted);
        Assert.Equal(redacted, LogRedactor.Redact(redacted));   // idempotent
    }

    [Fact]
    public void Ordinary_text_and_json_structure_are_left_alone()
    {
        const string benign = "{\"@t\":\"2026-11-12T01:00:00Z\",\"@m\":\"Contract CON-0001 saved by user 4\",\"Elapsed\":12.5}";
        Assert.Equal(benign, LogRedactor.Redact(benign));
        var json = LogRedactor.Redact("{\"Password\":\"x\",\"Level\":\"Error\"}");
        using var doc = JsonDocument.Parse(json);   // still valid JSON after redaction
        Assert.Equal("Error", doc.RootElement.GetProperty("Level").GetString());
    }

    [Fact]
    public void Every_event_carries_application_version_environment_process_thread_and_a_credential_free_database_identifier()
    {
        var capture = new Capture();
        var database = EcpLogEnricher.DatabaseIdentifier(DatabaseProvider.SqlServer, "Server=sql01;Database=Ecp;User Id=app;Password=SuperSecret123;Encrypt=True");
        using var log = new LoggerConfiguration().Enrich.With(new EcpLogEnricher("1.2.3", "Production", database)).WriteTo.Sink(capture).CreateLogger();
        log.Information("Contract {Reference} saved", "CON-0001");
        using var doc = JsonDocument.Parse(Assert.Single(capture.Lines));
        var e = doc.RootElement;
        Assert.Equal("EnterpriseContractPlatform", e.GetProperty("Application").GetString());
        Assert.Equal("1.2.3", e.GetProperty("Version").GetString());
        Assert.Equal("Production", e.GetProperty("Environment").GetString());
        Assert.Equal(Environment.ProcessId, e.GetProperty("ProcessId").GetInt32());
        Assert.True(e.GetProperty("ThreadId").GetInt32() > 0);
        Assert.Equal("sql01/Ecp", e.GetProperty("Database").GetString());
        Assert.Equal("Contract \"CON-0001\" saved", e.GetProperty("@m").GetString());
        Assert.DoesNotContain("SuperSecret123", capture.Lines[0]);
    }

    [Fact]
    public void A_request_adds_the_correlation_id_and_the_login_identifier_but_not_the_persons_name_or_email()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "corr-123" };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(EcpClaims.UserId, "8f0c2d1e-0000-0000-0000-000000000001"), new Claim(EcpClaims.Provider, "windows"),
            new Claim(ClaimTypes.Name, "Alex Example"), new Claim(ClaimTypes.Email, "alex@example.org")], "ecp.session"));
        var capture = new Capture();
        using var log = new LoggerConfiguration().Enrich.With(new EcpLogEnricher("1", "Test", "x", new HttpContextAccessor { HttpContext = context })).WriteTo.Sink(capture).CreateLogger();
        log.Warning("Something happened");
        var line = Assert.Single(capture.Lines);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("corr-123", doc.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal("8f0c2d1e-0000-0000-0000-000000000001", doc.RootElement.GetProperty("Login").GetString());
        Assert.Equal("windows", doc.RootElement.GetProperty("LoginProvider").GetString());
        Assert.DoesNotContain("Alex", line); Assert.DoesNotContain("alex@example.org", line);
    }

    [Fact]
    public void An_exception_message_that_carries_a_connection_string_is_redacted_in_the_written_line()
    {
        var capture = new Capture();
        using var log = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        log.Error(new InvalidOperationException("Login failed. Server=sql01;Database=Ecp;User Id=app;Password=SuperSecret123;"), "Database call failed");
        Assert.DoesNotContain("SuperSecret123", Assert.Single(capture.Lines));
    }

    [Fact]
    public async Task The_durable_file_sink_writes_a_rolling_redacted_file_and_survives_a_second_writer()
    {
        var settings = LoggingSettings.From(Config(new() { ["Ecp:Logging:Directory"] = _dir, ["Ecp:Logging:Console"] = "false" }), _dir);
        using (var log = LoggingConfiguration.Apply(new LoggerConfiguration(), settings, "1.0", "Test", "db", null, new EnvironmentAndFileSecretProvider(), () => DateTime.UtcNow).CreateLogger())
        {
            log.Information("first writer {Detail}", "Password=SuperSecret123;");
            // an overlapping worker during an application-pool recycle writes to the same file
            using var second = LoggingConfiguration.Apply(new LoggerConfiguration(), settings, "1.0", "Test", "db", null, new EnvironmentAndFileSecretProvider(), () => DateTime.UtcNow).CreateLogger();
            second.Information("second writer");
        }
        await Task.Delay(50);
        var file = Assert.Single(Directory.GetFiles(_dir, "ecp-*.log"));
        var text = File.ReadAllText(file);
        Assert.Contains("first writer", text); Assert.Contains("second writer", text);
        Assert.DoesNotContain("SuperSecret123", text);
        Assert.All(text.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => JsonDocument.Parse(l).Dispose());   // one JSON object per line
    }

    private sealed class FakeSender : IFatalEmailSender { public List<(string Subject, string Body)> Sent { get; } = []; public bool Throw; public void Send(string s, string b) { if (Throw) throw new IOException("mail down"); Sent.Add((s, b)); } }

    [Fact]
    public void Fatal_email_is_sent_only_for_fatal_events_redacted_and_not_more_often_than_the_interval()
    {
        var sender = new FakeSender(); var now = new DateTime(2026, 11, 12, 1, 0, 0, DateTimeKind.Utc);
        using var log = new LoggerConfiguration().WriteTo.Sink(new FatalEmailSink(sender, TimeSpan.FromMinutes(15), () => now), LogEventLevel.Verbose).CreateLogger();
        log.Error("not fatal"); log.Warning("not fatal either");
        Assert.Empty(sender.Sent);
        log.Fatal(new InvalidOperationException("Server=a;Password=SuperSecret123"), "Database is gone {Detail}", "Pwd=SuperSecret123;");
        var message = Assert.Single(sender.Sent);
        Assert.DoesNotContain("SuperSecret123", message.Body);
        log.Fatal("again straight away"); Assert.Single(sender.Sent);
        now = now.AddMinutes(16);
        log.Fatal("a later one"); Assert.Equal(2, sender.Sent.Count);
        sender.Throw = true; now = now.AddMinutes(16);
        log.Fatal("mail server is down");   // must not throw
    }

    [Fact]
    public void Settings_are_validated_and_secrets_must_be_references()
    {
        Assert.Empty(LoggingSettings.From(Config(new()), "/var/lib/ecp").Problems());
        Assert.Equal(Path.Combine("/var/lib/ecp", "logs"), LoggingSettings.From(Config(new()), "/var/lib/ecp").Directory);
        var bad = LoggingSettings.From(Config(new()
        {
            ["Ecp:Logging:MinimumLevel"] = "Loud", ["Ecp:Logging:ApplicationInsightsConnectionRef"] = "InstrumentationKey=00000000-0000-0000-0000-000000000000",
            ["Ecp:Logging:FatalEmail:Enabled"] = "true", ["Ecp:Logging:FatalEmail:To"] = "not-an-address", ["Ecp:Logging:FatalEmail:CredentialRef"] = "hunter2",
        }), "/var/lib/ecp").Problems();
        Assert.Contains(bad, p => p.Contains("MinimumLevel")); Assert.Contains(bad, p => p.Contains("ApplicationInsightsConnectionRef"));
        Assert.Contains(bad, p => p.Contains("FatalEmail:To")); Assert.Contains(bad, p => p.Contains("SmtpHost")); Assert.Contains(bad, p => p.Contains("CredentialRef"));
        Assert.DoesNotContain(bad, p => p.Contains("InstrumentationKey")); Assert.DoesNotContain(bad, p => p.Contains("hunter2"));
    }

    [Fact]
    public async Task The_running_host_writes_request_events_with_the_correlation_id_to_the_durable_file_and_reports_the_log_as_ready()
    {
        using var app = DemoApp.Start(s => { s["Ecp:Logging:Directory"] = _dir; s["Ecp:Logging:Console"] = "false"; });
        using var client = app.NewClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/info"); request.Headers.Add("X-Correlation-Id", "corr-from-test-42");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var ready = await client.GetStringAsync("/health/ready");
        Assert.Contains("R-LOG-01", ready);
        Serilog.Log.CloseAndFlush();
        await Task.Delay(1200);
        var text = string.Join('\n', Directory.GetFiles(_dir, "ecp-*.log").Select(f => { using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(s).ReadToEnd(); }));
        Assert.Contains("corr-from-test-42", text);
        Assert.Contains("\"Application\":\"EnterpriseContractPlatform\"", text);
        Assert.Contains("\"Environment\":\"Demo\"", text);
        Assert.Contains("/api/v1/info", text);
    }
}
