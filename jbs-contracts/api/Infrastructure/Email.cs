using System.Collections.Concurrent;
using System.Net;
using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.SqlClient;
using MimeKit;

namespace JbsContracts.Api.Infrastructure;

/// <summary>An email ready to send.</summary>
public sealed record OutgoingEmail(long Id, string ToAddress, string ToName, string Subject, string BodyText, string BodyHtml);

public interface IEmailSender
{
    Task SendAsync(OutgoingEmail email, CancellationToken ct);
}

/// <summary>Sends through the JBS relay: email.jbssa.com.au, port 25, STARTTLS, no login (the relay allows the web server).</summary>
public sealed class SmtpEmailSender(AppSettings settings) : IEmailSender
{
    public async Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.Email.FromName, settings.Email.From));
        message.To.Add(new MailboxAddress(email.ToName, email.ToAddress));
        message.Subject = email.Subject;
        message.Body = new BodyBuilder { TextBody = email.BodyText, HtmlBody = email.BodyHtml }.ToMessageBody();
        using var client = new SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(settings.Email.Host!, settings.Email.Port, SecureSocketOptions.StartTlsWhenAvailable, ct);
        await client.SendAsync(new SmtpSendRequest(message), ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Local development and tests: keeps emails in memory and logs them instead of sending.</summary>
public sealed class CaptureEmailSender(ILogger<CaptureEmailSender> logger) : IEmailSender
{
    public ConcurrentQueue<OutgoingEmail> Sent { get; } = new();

    public Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        Sent.Enqueue(email);
        logger.LogInformation("Email {EmailId} kept, not sent (no mail relay set): {Subject}", email.Id, email.Subject);
        return Task.CompletedTask;
    }
}

/// <summary>Queues emails in the same transaction as the change that causes them.</summary>
public static class EmailOutbox
{
    /// <summary>Nothing is queued about a demo contract or demo supplier (D-97): QA testers aren't emailed about made-up data.</summary>
    public static async Task QueueAsync(SqlConnection c, SqlTransaction tx, string kind, string toAddress, string toName, (string Subject, string Text, string Html) content,
        int? personId = null, int? contractId = null, string? dedupeKey = null, int? supplierId = null)
    {
        await c.ExecuteAsync("""
            IF (@dedupeKey IS NULL OR NOT EXISTS (SELECT 1 FROM contracts.EmailOutbox WHERE DedupeKey = @dedupeKey))
               AND NOT EXISTS (SELECT 1 FROM contracts.Contract WHERE Id = @contractId AND IsDemo = 1)
               AND NOT EXISTS (SELECT 1 FROM contracts.Supplier WHERE Id = @supplierId AND IsDemo = 1)
            INSERT INTO contracts.EmailOutbox (Kind, ToAddress, ToName, Subject, BodyText, BodyHtml, PersonId, ContractId, DedupeKey)
            VALUES (@kind, @toAddress, @toName, @subject, @text, @html, @personId, @contractId, @dedupeKey)
            """, new { kind, toAddress, toName, subject = content.Subject, text = content.Text, html = content.Html, personId, contractId, dedupeKey, supplierId }, tx);
    }
}

/// <summary>
/// Sends queued emails every few seconds, oldest first, and retries failures (after 1, 5, 15, 60 minutes, then hourly) up to
/// 10 attempts before marking them failed. Only one sender takes each email (READPAST), and an email is marked sent straight after
/// the relay accepts it.
/// </summary>
public sealed class EmailDispatcher(Db db, IEmailSender sender, AppSettings settings, ILogger<EmailDispatcher> logger) : BackgroundService
{
    private static readonly int[] RetryMinutes = [1, 5, 15, 60];
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Email.SendInBackground) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SendDueAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "The email sender could not check the outbox."); }
            try { await Task.Delay(TimeSpan.FromSeconds(settings.Email.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Sends whatever is due now (or only the given email, for tests). Returns how many were sent.</summary>
    public async Task<int> SendDueAsync(CancellationToken ct = default, long? onlyId = null)
    {
        var sent = 0;
        for (var i = 0; i < 50; i++)
        {
            await using var c = await db.OpenAsync(ct);
            await using var tx = c.BeginTransaction();
            var email = await c.QuerySingleOrDefaultAsync<OutgoingEmail>("""
                SELECT TOP (1) Id, ToAddress, ToName, Subject, BodyText, BodyHtml FROM contracts.EmailOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE SentUtc IS NULL AND FailedUtc IS NULL AND NextTryUtc <= SYSUTCDATETIME() AND (@onlyId IS NULL OR Id = @onlyId) ORDER BY Id
                """, new { onlyId }, tx);
            if (email is null) { await tx.CommitAsync(ct); break; }
            try
            {
                await sender.SendAsync(email, ct);
                await c.ExecuteAsync("UPDATE contracts.EmailOutbox SET SentUtc = SYSUTCDATETIME(), Attempts = Attempts + 1, LastError = NULL WHERE Id = @Id", new { email.Id }, tx);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var attempts = await c.ExecuteScalarAsync<int>("SELECT Attempts + 1 FROM contracts.EmailOutbox WHERE Id = @Id", new { email.Id }, tx);
                var wait = RetryMinutes[Math.Min(attempts - 1, RetryMinutes.Length - 1)];
                var error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                await c.ExecuteAsync("""
                    UPDATE contracts.EmailOutbox SET Attempts = @attempts, LastError = @error,
                        NextTryUtc = DATEADD(MINUTE, @wait, SYSUTCDATETIME()), FailedUtc = CASE WHEN @attempts >= @max THEN SYSUTCDATETIME() END
                    WHERE Id = @Id
                    """, new { email.Id, attempts, error, wait, max = MaxAttempts }, tx);
                logger.LogWarning(ex, "Email {EmailId} not sent (attempt {Attempts}); will try again.", email.Id, attempts);
            }
            await tx.CommitAsync(ct);
        }
        return sent;
    }
}

/// <summary>The emails' wording. Plain words; every value is HTML-encoded; no images or anything loaded from elsewhere.</summary>
public static class EmailTemplates
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Html(string heading, string[] paragraphs, string? buttonText, string? link) => $"""
        <!doctype html><html><body style="margin:0;background:#F7F8FC;font-family:Segoe UI,Arial,sans-serif;color:#14143C">
        <div style="max-width:560px;margin:0 auto;padding:24px">
          <p style="margin:0 0 16px;font-weight:600;color:#232378">JBS Contracts</p>
          <div style="background:#FFFFFF;border:1px solid #DDE1EE;border-radius:12px;padding:24px">
            <h1 style="margin:0 0 12px;font-size:20px;color:#14143C">{E(heading)}</h1>
            {string.Join("", paragraphs.Select(p => $"<p style=\"margin:0 0 12px;font-size:15px;line-height:22px\">{E(p)}</p>"))}
            {(buttonText is not null && link is not null ? $"<p style=\"margin:20px 0 0\"><a href=\"{E(link)}\" style=\"display:inline-block;background:#232378;color:#FFFFFF;text-decoration:none;font-weight:600;padding:12px 20px;border-radius:10px\">{E(buttonText)}</a></p>" : "")}
          </div>
          <p style="margin:16px 0 0;font-size:13px;color:#5B6280">This email was sent by JBS Contracts. Please don't reply to it.</p>
        </div></body></html>
        """;

    /// <summary>
    /// An email from wording an admin wrote (reminders): the subject is the heading, blank lines separate paragraphs. Everything is
    /// treated as plain text and HTML-encoded, so wording can never add HTML, links or code.
    /// </summary>
    public static (string Subject, string Text, string Html) FromWording(string subject, string[] paragraphs, string? link, string button)
    {
        var text = string.Join("\n\n", paragraphs) + (link is not null ? $"\n\n{button}: {link}" : "") + "\n\nThis email was sent by JBS Contracts. Please don't reply to it.";
        return (subject, text, Html(subject, paragraphs, link is null ? null : button, link));
    }
}
