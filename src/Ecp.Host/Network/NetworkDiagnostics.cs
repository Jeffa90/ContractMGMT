using System.Net;
using System.Text;
using Ecp.Operations;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Ecp.Host.Network;

/// <summary>
/// How the host expects to be reached (G-33). Production always expects HTTPS: either Kestrel terminates TLS itself, or a trusted reverse proxy terminates it and forwards the original scheme. <c>Ecp:Network:RequireHttps</c>
/// applies the same rule outside Production (for rehearsal and tests). Forwarded headers are honoured only from the proxies named here; <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> remains the container shortcut that trusts any sender.
/// </summary>
public sealed class NetworkOptions
{
    public bool RequireHttps { get; set; }
    public bool ForwardedHeadersEnabled { get; set; }
    /// <summary>Set when the framework's own switch (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>) turned forwarded-header handling on.</summary>
    public bool ForwardedHeadersFromEnvironment { get; set; }
    public List<string> KnownProxies { get; set; } = [];
    public List<string> KnownNetworks { get; set; } = [];
    public int ForwardLimit { get; set; } = 1;

    public bool ForwardedProcessing => ForwardedHeadersEnabled || ForwardedHeadersFromEnvironment;

    public static NetworkOptions From(IConfiguration configuration, bool production)
    {
        var o = new NetworkOptions
        {
            RequireHttps = production || configuration.GetValue("Ecp:Network:RequireHttps", false),
            ForwardedHeadersEnabled = configuration.GetValue("Ecp:Network:ForwardedHeaders:Enabled", false),
            ForwardedHeadersFromEnvironment = string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
            ForwardLimit = Math.Clamp(configuration.GetValue("Ecp:Network:ForwardedHeaders:ForwardLimit", 1), 1, 5),
        };
        o.KnownProxies.AddRange(Split(configuration["Ecp:Network:ForwardedHeaders:KnownProxies"]));
        o.KnownNetworks.AddRange(Split(configuration["Ecp:Network:ForwardedHeaders:KnownNetworks"]));
        return o;
    }

    private static IEnumerable<string> Split(string? list) => (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Applies the configured proxies to the framework's forwarded-header options. With no proxy named, only a proxy on the same machine is trusted (the framework default).</summary>
    public void Apply(ForwardedHeadersOptions target)
    {
        target.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        target.ForwardLimit = ForwardLimit;
        foreach (var p in KnownProxies) if (IPAddress.TryParse(p, out var ip)) target.KnownProxies.Add(ip);
        foreach (var n in KnownNetworks)
        {
            var parts = n.Split('/');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var length))
                target.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, length));
        }
    }
}

public enum NetworkFinding { Ok, PlainHttp, ForwardedHeadersUntrusted }

public sealed record NetworkDiagnosis(NetworkFinding Finding, string Code, string Title, string Explanation, string OperatorAction)
{
    public static readonly NetworkDiagnosis Ok = new(NetworkFinding.Ok, "", "", "", "");
}

/// <summary>
/// Decides, from facts about one request, whether the host is being reached the way it requires. Pure, so the five situations (direct HTTPS, a proxy that forwards the scheme, a proxy that does not, forwarded headers from a
/// sender the host does not trust, and plain HTTP) are each tested without a server.
/// </summary>
public static class NetworkDiagnoser
{
    public const string PlainHttp = "ECP-NET-001";
    public const string Untrusted = "ECP-NET-002";

    public static NetworkDiagnosis Evaluate(NetworkOptions options, string scheme, string? forwardedProto)
    {
        if (!options.RequireHttps || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)) return NetworkDiagnosis.Ok;
        if (!string.IsNullOrWhiteSpace(forwardedProto) && string.Equals(forwardedProto.Split(',')[0].Trim(), "https", StringComparison.OrdinalIgnoreCase))
            return new(NetworkFinding.ForwardedHeadersUntrusted, Untrusted, "The reverse proxy's forwarded headers were not accepted",
                "The request says it reached the proxy over HTTPS, but this server did not accept the proxy's forwarded headers, so it sees an insecure connection and refuses to sign anyone in.",
                options.ForwardedProcessing
                    ? "Add the reverse proxy's address to Ecp:Network:ForwardedHeaders:KnownProxies (or its network to KnownNetworks), then restart the application. Forwarded headers from any other sender are ignored on purpose."
                    : "Turn on forwarded-header handling: set Ecp:Network:ForwardedHeaders:Enabled to true and name the reverse proxy in Ecp:Network:ForwardedHeaders:KnownProxies (a container deployment may set ASPNETCORE_FORWARDEDHEADERS_ENABLED=true instead), then restart the application.");
        return new(NetworkFinding.PlainHttp, PlainHttp, "This site must be reached over HTTPS",
            "The request reached the server over plain HTTP, and no forwarded HTTPS scheme was supplied. For your security, sign-in and forms are refused over an insecure connection.",
            "Use the https:// address. If this address is already behind a reverse proxy that handles HTTPS, configure the proxy to send X-Forwarded-Proto and X-Forwarded-For, and tell this application to trust it (Ecp:Network:ForwardedHeaders). See docs/REVERSE_PROXY.md.");
    }

    /// <summary>The page shown to a person. Plain language, a stable code and the correlation id so support can find it; nothing from the request's headers, no address, no stack trace.</summary>
    public static string Html(NetworkDiagnosis d, string correlationId)
    {
        string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(E(d.Title)).Append("</title><link rel=\"stylesheet\" href=\"/css/site.css\"></head><body><main id=\"main\" class=\"container\">");
        sb.Append("<h1>").Append(E(d.Title)).Append("</h1>");
        sb.Append("<p>").Append(E(d.Explanation)).Append("</p>");
        sb.Append("<h2>What to do</h2><p>").Append(E(d.OperatorAction)).Append("</p>");
        sb.Append("<dl class=\"facts\"><dt>Diagnostic code</dt><dd><code>").Append(E(d.Code)).Append("</code></dd><dt>Reference</dt><dd><code>").Append(E(correlationId)).Append("</code></dd></dl>");
        sb.Append("<p>Give the diagnostic code and the reference to whoever runs this installation.</p></main></body></html>");
        return sb.ToString();
    }
}

/// <summary>What the host has seen, kept in memory only so readiness can say a misconfiguration is happening now. Holds a code and a time, never a request.</summary>
public sealed class NetworkObservations
{
    private long _lastTicks;
    private int _count;
    private string _lastCode = "";
    public void Record(string code) { Volatile.Write(ref _lastTicks, DateTime.UtcNow.Ticks); Interlocked.Increment(ref _count); _lastCode = code; }
    public (int Count, string Code, DateTime? LastUtc) Snapshot() { var t = Volatile.Read(ref _lastTicks); return (Volatile.Read(ref _count), _lastCode, t == 0 ? null : new DateTime(t, DateTimeKind.Utc)); }
}

/// <summary>Refuses a request that does not arrive over HTTPS when HTTPS is required, with a safe explanation instead of the unhandled error that a secure cookie over plain HTTP would otherwise cause.</summary>
public sealed class NetworkGuardMiddleware(RequestDelegate next, NetworkOptions options, NetworkObservations observations, ILogger<NetworkGuardMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Health probes come from the platform itself, usually on a local HTTP port; they carry no session and stay reachable.
        if (!options.RequireHttps || context.Request.Path.StartsWithSegments("/health")) { await next(context); return; }
        var d = NetworkDiagnoser.Evaluate(options, context.Request.Scheme, context.Request.Headers["X-Forwarded-Proto"].ToString());
        if (d.Finding == NetworkFinding.Ok) { await next(context); return; }

        observations.Record(d.Code);
        log.LogWarning("Request refused: {Code} ({Title}). Correlation {Correlation}", d.Code, d.Title, context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            await context.Response.WriteAsJsonAsync(new { type = "about:blank", title = d.Title, status = 400, code = d.Code, detail = d.Explanation, action = d.OperatorAction, correlationId = context.TraceIdentifier },
                options: null, contentType: "application/problem+json");
            return;
        }
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(NetworkDiagnoser.Html(d, context.TraceIdentifier));
    }
}

/// <summary>R-NET-01: the transport the host expects, and whether a misconfiguration has been seen recently. Degraded, not Unhealthy: a wrong proxy setting must not take the health endpoint down.</summary>
public sealed class NetworkReadinessCheck(NetworkOptions options, NetworkObservations observations, HostModeAccessor mode) : IReadinessCheck
{
    public string Id => "R-NET-01";

    public Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        const string description = "HTTPS and reverse-proxy configuration";
        var (count, code, last) = observations.Snapshot();
        if (last is { } seen && DateTime.UtcNow - seen < TimeSpan.FromMinutes(15))
            return Task.FromResult(new ReadinessResult(Id, description, ReadinessStatus.Degraded, $"Requests were refused in the last 15 minutes because they did not arrive over HTTPS ({code}, {count} so far). See the diagnostic page the visitor was shown, and docs/REVERSE_PROXY.md."));
        if (!options.RequireHttps)
            return Task.FromResult(new ReadinessResult(Id, description, ReadinessStatus.Healthy, mode.Mode == EcpMode.Production ? "HTTPS is not required by configuration." : "HTTPS is not required outside Production unless Ecp:Network:RequireHttps is set."));
        var how = options.ForwardedProcessing
            ? options.ForwardedHeadersFromEnvironment && options.KnownProxies.Count == 0 && options.KnownNetworks.Count == 0
                ? "Forwarded headers are accepted from any sender (ASPNETCORE_FORWARDEDHEADERS_ENABLED); name the proxy in Ecp:Network:ForwardedHeaders:KnownProxies for a stricter setting."
                : $"Forwarded headers are accepted from {options.KnownProxies.Count + options.KnownNetworks.Count} named proxy address(es) or network(s)."
            : "Forwarded headers are not enabled: the application must be reached by HTTPS directly. If a reverse proxy terminates TLS, enable forwarded headers (docs/REVERSE_PROXY.md).";
        return Task.FromResult(new ReadinessResult(Id, description, ReadinessStatus.Healthy, "HTTPS is required. " + how + " No refused request has been seen."));
    }
}
