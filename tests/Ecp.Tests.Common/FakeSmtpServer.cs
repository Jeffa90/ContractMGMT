using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ecp.Tests.Common;

public enum SmtpBehaviour
{
    /// <summary>Accepts the message.</summary>
    Accept,
    /// <summary>Refuses the recipient (550).</summary>
    RejectRecipient,
    /// <summary>Answers something that is not SMTP.</summary>
    NotSmtp,
    /// <summary>Requires authentication before it will take mail (530), which the platform never offers in these tests.</summary>
    RequireAuthentication,
}

public sealed record ReceivedMail(string From, string To, string Data);

/// <summary>
/// A minimal real SMTP server on a loopback socket for tests: it speaks enough of the protocol (greeting, EHLO, MAIL, RCPT, DATA, QUIT) for the platform's probe and mail library to talk to it over a real TCP connection.
/// It offers no STARTTLS, so a "TLS required" setting fails against it exactly as it would against a server without TLS. Only for tests; nothing here is a real mail server.
/// </summary>
public sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<ReceivedMail> _mail = [];
    public SmtpBehaviour Behaviour { get; set; }
    public int Port { get; }
    public IReadOnlyList<ReceivedMail> Mail { get { lock (_mail) return [.. _mail]; } }
    public int Connections { get; private set; }

    public FakeSmtpServer(SmtpBehaviour behaviour = SmtpBehaviour.Accept)
    {
        Behaviour = behaviour;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>A port nothing listens on (the listener is opened and closed).</summary>
    public static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            Connections++;
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, new ASCIIEncoding()) { NewLine = "\r\n", AutoFlush = true };
            if (Behaviour == SmtpBehaviour.NotSmtp) { await writer.WriteLineAsync("HTTP/1.1 400 Bad Request"); return; }
            await writer.WriteLineAsync("220 fake.smtp.test ready");
            string from = "", to = "";
            while (await reader.ReadLineAsync() is { } line)
            {
                var verb = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO": case "HELO": await writer.WriteLineAsync("250-fake.smtp.test"); await writer.WriteLineAsync("250 SIZE 1000000"); break;
                    case "MAIL":
                        if (Behaviour == SmtpBehaviour.RequireAuthentication) { await writer.WriteLineAsync("530 5.7.0 Authentication required"); break; }
                        from = line; await writer.WriteLineAsync("250 OK"); break;
                    case "RCPT":
                        if (Behaviour == SmtpBehaviour.RejectRecipient) { await writer.WriteLineAsync("550 5.1.1 No such mailbox"); break; }
                        to = line; await writer.WriteLineAsync("250 OK"); break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } d && d != ".") data.AppendLine(d);
                        lock (_mail) _mail.Add(new ReceivedMail(from, to, data.ToString()));
                        await writer.WriteLineAsync("250 OK queued");
                        break;
                    case "RSET": await writer.WriteLineAsync("250 OK"); break;
                    case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                    default: await writer.WriteLineAsync("502 not implemented"); break;
                }
            }
        }
        catch (Exception) { /* a client that disconnects mid-conversation is normal in a probe */ }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
}
