using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JbsContracts.Api.Tests;

public sealed class EmailTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task Adding_a_person_queues_one_welcome_email_and_the_sender_delivers_it()
    {
        var user = Unique("mail");
        // A name with characters that must be encoded in HTML.
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var response = await admin.PostAsJsonAsync("/api/people", new
        {
            account = user, name = "<Mia> O'Brien", email = $"{user}@example.com",
            roles = new { contractOwner = true, legal = true, approver = false, groupHead = false, admin = false, auditor = false },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).GetProperty("id").GetInt32();

        await using var db = await app.OpenAsync();
        var queued = await db.QuerySingleAsync("SELECT Id, Kind, ToAddress, Subject, BodyText, BodyHtml, SentUtc FROM contracts.EmailOutbox WHERE PersonId = @id AND Kind = 'access-granted'", new { id });
        Assert.Equal("access-granted", (string)queued.Kind);
        Assert.Equal("You've been given access to JBS Contracts", (string)queued.Subject);
        Assert.Contains("Contract owner, Legal", (string)queued.BodyText);
        Assert.Contains($@"TEST\{user}", (string)queued.BodyText);
        Assert.Contains("Hi &lt;Mia&gt;,", (string)queued.BodyHtml);
        Assert.DoesNotContain("<Mia>", (string)queued.BodyHtml);

        var dispatcher = app.Factory.Services.GetRequiredService<EmailDispatcher>();
        var capture = app.Factory.Services.GetRequiredService<CaptureEmailSender>();
        Assert.Equal(1, await dispatcher.SendDueAsync(Ct, (long)queued.Id));
        Assert.Contains(capture.Sent, e => e.ToAddress == (string)queued.ToAddress);
        Assert.NotNull(await db.ExecuteScalarAsync<DateTime?>("SELECT SentUtc FROM contracts.EmailOutbox WHERE PersonId = @id AND Kind = 'access-granted'", new { id }));

        // Sending again sends nothing more.
        var before = capture.Sent.Count(e => e.ToAddress == (string)queued.ToAddress);
        Assert.Equal(0, await dispatcher.SendDueAsync(Ct, (long)queued.Id));
        Assert.Equal(before, capture.Sent.Count(e => e.ToAddress == (string)queued.ToAddress));
    }

    private sealed class FailingSender : IEmailSender
    {
        public Task SendAsync(OutgoingEmail email, CancellationToken ct) => throw new InvalidOperationException("Relay unreachable");
    }

    [Fact]
    public async Task A_failed_send_is_kept_and_retried_later_not_lost()
    {
        var user = Unique("fail");
        var person = await app.AddPersonAsync(user, "Fay Fail", AppFixture.NoRoles);
        var id = person.GetProperty("id").GetInt32();
        await using var db = await app.OpenAsync();
        var emailId = await db.ExecuteScalarAsync<long>("SELECT Id FROM contracts.EmailOutbox WHERE PersonId = @id AND Kind = 'access-granted'", new { id });
        var failing = new EmailDispatcher(app.Factory.Services.GetRequiredService<Db>(), new FailingSender(), app.Factory.Services.GetRequiredService<AppSettings>(), NullLogger<EmailDispatcher>.Instance);
        Assert.Equal(0, await failing.SendDueAsync(Ct, emailId));
        var row = await db.QuerySingleAsync("SELECT Attempts, LastError, SentUtc, FailedUtc, NextTryUtc, CreatedUtc, SYSUTCDATETIME() AS NowUtc FROM contracts.EmailOutbox WHERE PersonId = @id AND Kind = 'access-granted'", new { id });
        Assert.True((int)row.Attempts == 1, $"attempts {row.Attempts}, sent {row.SentUtc}, next {row.NextTryUtc:O}, created {row.CreatedUtc:O}, now {row.NowUtc:O}");
        Assert.Equal("Relay unreachable", (string)row.LastError);
        Assert.Null((DateTime?)row.SentUtc);
        Assert.Null((DateTime?)row.FailedUtc);
        Assert.True((DateTime)row.NextTryUtc > DateTime.UtcNow.AddSeconds(30)); // waits before trying again
    }

    [Fact]
    public async Task The_relay_sender_speaks_SMTP_to_a_mail_server()
    {
        using var server = new TinySmtpServer();
        var settings = new AppSettings
        {
            ConnectionString = "unused",
            Email = new EmailSettings { Host = "127.0.0.1", Port = server.Port, From = "no-reply@jbssa.com.au", FromName = "JBS Contracts" },
        };
        var content = EmailTemplates.AccessGranted("Sam", "Jeff Judd", "Legal", @"AMH\slee", "https://jbs-contracts-qa.jbssa.com.au");
        await new SmtpEmailSender(settings).SendAsync(new OutgoingEmail(1, "sam.lee@example.com", "Sam Lee", content.Subject, content.Text, content.Html), Ct);
        var received = await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains("MAIL FROM:<no-reply@jbssa.com.au>", received);
        Assert.Contains("RCPT TO:<sam.lee@example.com>", received);
        Assert.Contains("You've been given access to JBS Contracts", received);
        Assert.Contains("https://jbs-contracts-qa.jbssa.com.au", received);
    }

    /// <summary>A minimal SMTP server for one message (no TLS offered, so the sender carries on in plain text, as STARTTLS-when-available allows).</summary>
    private sealed class TinySmtpServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public int Port { get; }
        public TaskCompletionSource<string> Received { get; } = new();

        public TinySmtpServer()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                var log = new StringBuilder();
                await writer.WriteLineAsync("220 test ready");
                var inData = false;
                while (await reader.ReadLineAsync() is { } line)
                {
                    log.AppendLine(line);
                    if (inData)
                    {
                        if (line == ".") { inData = false; await writer.WriteLineAsync("250 queued"); }
                        continue;
                    }
                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO": await writer.WriteLineAsync("250-test"); await writer.WriteLineAsync("250 8BITMIME"); break;
                        case "DATA": inData = true; await writer.WriteLineAsync("354 go ahead"); break;
                        case "QUIT": await writer.WriteLineAsync("221 bye"); Received.TrySetResult(log.ToString()); return;
                        default: await writer.WriteLineAsync("250 ok"); break;
                    }
                }
                Received.TrySetResult(log.ToString());
            }
            catch (Exception ex) { Received.TrySetException(ex); }
        }

        public void Dispose() => listener.Stop();
    }
}
