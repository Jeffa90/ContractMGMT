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
    public static async Task QueueAsync(SqlConnection c, SqlTransaction tx, string kind, string toAddress, string toName, (string Subject, string Text, string Html) content,
        int? personId = null, int? contractId = null, string? dedupeKey = null)
    {
        await c.ExecuteAsync("""
            IF @dedupeKey IS NULL OR NOT EXISTS (SELECT 1 FROM contracts.EmailOutbox WHERE DedupeKey = @dedupeKey)
            INSERT INTO contracts.EmailOutbox (Kind, ToAddress, ToName, Subject, BodyText, BodyHtml, PersonId, ContractId, DedupeKey)
            VALUES (@kind, @toAddress, @toName, @subject, @text, @html, @personId, @contractId, @dedupeKey)
            """, new { kind, toAddress, toName, subject = content.Subject, text = content.Text, html = content.Html, personId, contractId, dedupeKey }, tx);
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

    public static (string Subject, string Text, string Html) AccessGranted(string firstName, string addedBy, string roles, string account, string? siteUrl)
    {
        const string subject = "You've been given access to JBS Contracts";
        var paragraphs = new[]
        {
            $"Hi {firstName},",
            $"{addedBy} has added you to JBS Contracts, where JBS raises, reviews, approves and keeps track of supplier contracts.",
            $"What you can do: {roles}.",
            $"You sign in automatically with your Windows account ({account}). There's no password to remember.",
        };
        var text = string.Join("\n\n", paragraphs) + (siteUrl is not null ? $"\n\nOpen JBS Contracts: {siteUrl}" : "") + "\n\nThis email was sent by JBS Contracts. Please don't reply to it.";
        return (subject, text, Html("Welcome to JBS Contracts", paragraphs, siteUrl is null ? null : "Open JBS Contracts", siteUrl));
    }

    private static (string Subject, string Text, string Html) Contract(string subject, string heading, string[] paragraphs, string? link)
    {
        var text = string.Join("\n\n", paragraphs) + (link is not null ? $"\n\nOpen the contract: {link}" : "") + "\n\nThis email was sent by JBS Contracts. Please don't reply to it.";
        return (subject, text, Html(heading, paragraphs, link is null ? null : "Open the contract", link));
    }

    private static string Said(string who, string? note) => note is null ? "" : $"{who} wrote: \u201c{note}\u201d";

    public static (string Subject, string Text, string Html) SentToLegal(string firstName, string sentBy, string reference, string title, string? note, bool isNew, string? link) =>
        Contract($"{reference} {title} is with Legal", isNew ? "A contract needs a Legal review" : "A contract is back with you", [
            $"Hi {firstName},",
            isNew
                ? $"{sentBy} has sent {reference} {title} to Legal. Nobody in Legal has picked it up yet: open it and choose I'll take it."
                : $"{sentBy} has sent {reference} {title} back to you. It's Legal's turn.",
            .. note is null ? Array.Empty<string>() : [Said(sentBy, note)],
        ], link);

    public static (string Subject, string Text, string Html) LegalAssigned(string firstName, string assignedBy, string reference, string title, bool legalsTurn, string? link) =>
        Contract($"{reference} {title}: the Legal review is yours", "You have a Legal review", [
            $"Hi {firstName},",
            $"{assignedBy} has given you the Legal review of {reference} {title}.",
            legalsTurn ? "It's Legal's turn now." : "It's with the owner at the moment. You'll get an email when it comes back to Legal.",
        ], link);

    public static (string Subject, string Text, string Html) BackFromLegal(string firstName, string legalName, string reference, string title, string note, string? link) =>
        Contract($"{reference} {title} is back with you from Legal", "Legal has sent a contract back to you", [
            $"Hi {firstName},",
            $"{legalName} (Legal) has sent {reference} {title} back to the owner. It's your turn.",
            Said(legalName, note),
            "When you're ready, open the contract, make any changes and choose Send to Legal.",
        ], link);

    public static (string Subject, string Text, string Html) LegalCleared(string firstName, string legalName, string reference, string title, string outcome, string? link) =>
        Contract($"{reference} {title} has been cleared by Legal", "Legal has cleared a contract", [
            $"Hi {firstName},",
            $"{legalName} (Legal) has cleared {reference} {title}. It has moved to Signing.",
            $"Outcome: \u201c{outcome}\u201d",
            "Next: sign it in DocuSign, then upload the signed copy on the contract page.",
        ], link);

    public static (string Subject, string Text, string Html) WaitingForApproval(string firstName, string sentBy, string reference, string title, string? link) =>
        Contract($"{reference} {title} is waiting for your approval", "A contract is ready to approve", [
            $"Hi {firstName},",
            $"{sentBy} has sent {reference} {title} for approval. It has been signed in DocuSign and the signed copy is attached to the contract.",
            "Check the signed copy and details. Approve and register makes it Effective; if something isn't right, send it back with a reason.",
        ], link);

    public static (string Subject, string Text, string Html) Approved(string firstName, string approver, string reference, string title, string? link) =>
        Contract($"{reference} {title} is approved and Effective", "Your contract is Effective", [
            $"Hi {firstName},",
            $"{approver} has approved and registered {reference} {title}. It is now Effective and locked.",
        ], link);

    public static (string Subject, string Text, string Html) SentBackByApprover(string firstName, string approver, string reference, string title, string reason, string? link) =>
        Contract($"{reference} {title} was sent back from approval", "A contract was sent back from approval", [
            $"Hi {firstName},",
            $"{approver} sent {reference} {title} back to Signing.",
            Said(approver, reason),
            "Fix what's needed (for example, add the right signed copy), then choose Send for approval again.",
        ], link);
}
