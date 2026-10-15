# Identity Specification

Master: "Generic OpenID Connect and Microsoft Entra ID", local Demo identity, role mapping, delegated administration. This file adds the detail (GAP-16). `[CONFIRMED]` principle, `[PROPOSED]` detail unless tagged.

## 1. Identity modes

| Mode | Allowed in | Notes |
|---|---|---|
| Demo identity | Demo, Test | Seeded synthetic users for the shipped roles; clearly bannered. **Refused in Production** by a server-side guard and by readiness check R-ID-01 `[CONFIRMED]` |
| Generic OIDC | All | Authorization Code flow with PKCE; discovery document; configurable scopes and claim names |
| Microsoft Entra ID | All | Configured as OIDC with Entra-specific defaults (tenant-restricted issuer, `oid`, group claims) |

## 2. Authentication requirements

1. Authorization Code flow with PKCE; the implicit and password grants are not used. State and nonce are validated; open-redirect protection on return URLs (allow only local paths).
2. Tokens validated for issuer, audience, signature (keys refreshed from discovery), expiry and clock skew (maximum 2 minutes).
3. Accounts are linked by `(issuer, subject)` and never by email address alone, to prevent account takeover by a changed or re-used email.
4. Session cookies are `Secure`, `HttpOnly`, `SameSite=Lax` (or `Strict` where compatible), with idle and absolute lifetimes configurable per tenant (defaults 30 minutes idle, 8 hours absolute).
5. Multi-factor authentication is the identity provider's responsibility. The platform can require that the `amr`/`acr` claim shows MFA for configured permissions (step-up) and sends `prompt=login`/`max_age` to force re-authentication for sensitive actions: running or changing setup, publishing configuration, importing packages, committing legacy loads, emergency unlock, elevation.
6. Logout ends the platform session and, where supported, performs RP-initiated logout.

## 3. User provisioning and membership

- Production default: **invite-only or group-mapped**. A user who authenticates but has no membership gets an "access not granted" page and an audit event. Just-in-time provisioning is an explicit tenant setting, off by default in Production.
- Users are tenant members with organisation memberships. A person in two tenants has two independent memberships.
- Identity-group-to-role mapping is configuration: `(group id or name) -> role template + organisation/division/site scope`. Mapping changes are audited and subject to `roles.delegate`.
- Entra group overage (token carries a link instead of the group list) is handled by an optional directory lookup through the identity directory interface; if unavailable, group-derived roles are not granted and the user sees the access-not-granted page (fail closed).

## 4. Roles at runtime

- Roles are assigned directly or via group mapping, with optional effective dates.
- Role evaluation happens per request against current assignments (cached for at most 60 seconds, keyed by tenant and user); removal takes effect within that bound. `[PROPOSED]`
- Break-glass: the first Tenant Administrator and System Administrator accounts are created by the setup wizard; the platform refuses to remove the last holder (ACCESS_CONTROL_SPEC section 5).

## 5. Service principals

Non-interactive callers (Qlik export, integrations, CI smoke tests) use OAuth client credentials with a tenant-bound principal, scoped permissions, secret rotation support, and no UI login.

## 6. Demo identity details `[PROPOSED]`

Synthetic users `*@example.test` for each shipped role in two tenants (used by isolation tests). Credentials are not secret by design, are displayed only in Demo mode, and are generated per installation, never fixed in source. The sign-in page states that Demo identity is for demonstration.
