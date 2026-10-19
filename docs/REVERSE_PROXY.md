# HTTPS and reverse proxy

**Status.** The diagnostics described here are implemented and tested over real HTTP (direct HTTPS, a TLS-terminating proxy that forwards the scheme, a proxy that does not, forwarded headers from an untrusted sender, plain HTTP). A specific proxy product (nginx, IIS ARR, Azure Front Door, Traefik) is **Not Verified**: no such proxy was part of any test. Rehearse yours.

**Who does this.** System Administrator (technical task). A Business Administrator never edits proxy or application settings.

## 1. What the application expects

* In **Production** the application requires HTTPS. Its session and antiforgery cookies are `Secure`, it sends HSTS, and it refuses a request that does not arrive over HTTPS with a plain explanation (below). This cannot be switched off in Production.
* Outside Production the same rule applies when `Ecp:Network:RequireHttps` is `true` (used for rehearsals and tests).
* There are two valid ways to be reached:
  1. **Direct HTTPS.** The application itself terminates TLS.
  2. **TLS-terminating reverse proxy.** The proxy ends TLS and talks plain HTTP to the container (port 8080), and tells the application the visitor used HTTPS with `X-Forwarded-Proto: https` (and `X-Forwarded-For`, `X-Forwarded-Host`).

## 2. Trusting the proxy

Forwarded headers change what the application believes about a request, so they are honoured only from senders it trusts.

| Setting | Meaning |
|---|---|
| `Ecp:Network:ForwardedHeaders:Enabled` | `true` turns forwarded-header handling on. |
| `Ecp:Network:ForwardedHeaders:KnownProxies` | Comma-separated addresses of the proxies to trust, for example `10.0.0.5`. |
| `Ecp:Network:ForwardedHeaders:KnownNetworks` | Comma-separated networks in CIDR form, for example network 10.0.0.0 with a 24-bit prefix. |
| `Ecp:Network:ForwardedHeaders:ForwardLimit` | How many proxies deep to read (default 1). |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` | The framework's container shortcut. It trusts **any** sender, so only use it where the container is reachable from the proxy alone (the supplied Docker images set it, and the published port is bound to 127.0.0.1). Prefer the settings above for anything wider. |

With forwarded handling enabled and no proxy named, only a proxy on the same machine is trusted.

## 3. What a person sees when it is wrong

A refused request gets a short page (or, for `/api`, a problem document) with status 400, a stable code, a plain explanation, what the operator should do, and a **reference** (the correlation id, also sent as the `X-Correlation-Id` header). It never shows a request header, an address, a secret or a stack trace. `/health/*` stays reachable over HTTP so container probes keep working.

| Code | Meaning | Operator action |
|---|---|---|
| `ECP-NET-001` | The request arrived over plain HTTP and no HTTPS scheme was forwarded. Either the visitor used `http://`, or the proxy ends TLS but does not forward the scheme. | Use the `https://` address. Behind a proxy, make the proxy send `X-Forwarded-Proto` and `X-Forwarded-For`. |
| `ECP-NET-002` | The request carried `X-Forwarded-Proto: https` but the application did not accept it: forwarded handling is off, or the sender is not a known proxy. | Enable `Ecp:Network:ForwardedHeaders:Enabled` and name the proxy in `KnownProxies` or `KnownNetworks`, then restart. |

The readiness check **R-NET-01** (Administration, Health, and the setup wizard's readiness step) reports the expected transport and goes **Degraded** for 15 minutes after a request was refused for one of these reasons. It does not take the health endpoint down.

## 4. Proxy checklist

* Ends TLS with a certificate the visitors' browsers trust; redirects HTTP to HTTPS.
* Forwards `Host`, `X-Forwarded-For`, `X-Forwarded-Proto` (set by the proxy, never passed through from the visitor).
* Reaches the container on its private port; the application is not otherwise exposed.
* Its address is in `KnownProxies` / `KnownNetworks` (or the container shortcut is acceptable for your network).
* After a change, open `https://<address>/SignIn`: a normal sign-in page means it works; a page with `ECP-NET-001` or `ECP-NET-002` says which part is wrong.
