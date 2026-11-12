# ADR-0042: Windows Authentication establishes identity only; access stays invite-only

Status: Implemented on branch jbs/iis-integration. Approved by the owner (decision 1 of the JBS IIS integration decisions); production approval remains external governance and is not declared.
Builds on the identity rules already in force (accounts link by provider and subject, never by email alone) and on ADR-0035/0039 for People & Access.

## Context

The JBS deployment runs behind IIS with Windows Authentication. With ASP.NET Core in-process hosting, the server puts an authenticated Windows principal on the request before the application runs. A principal the application did not create must not become a session, and "the person is on the network" must not mean "the person may use the application".

## Decision

1. **Identity only.** Windows Authentication proves who the person is. It supplies no role, scope, category ownership, classification or group. Existing role assignments and every server-side rule apply exactly as for any other sign-in method.
2. **Invite-only.** A Windows-authenticated person signs in only if (a) an invitation names their Windows account (or their SID is already linked), (b) the account is active (not disabled), and (c) they hold effective access (at least one active role). Signing in creates nothing and grants nothing. AD-group-driven onboarding is deferred.
3. **Account name to SID.** The invitation names `DOMAIN\name`. At the first sign-in it is linked to the stable SID; later sign-ins match by SID. A later account with the same name and a different SID is refused. The email address is never used to link a Windows account. The link reuses the existing external-identity table (subjects `account:` and `sid:`, issuer `windows`), so no schema change is needed.
4. **The server's principal is not a session.** A middleware before `UseAuthentication` removes it from `HttpContext.User` and keeps only the account name and SID for the sign-in page. Otherwise the authorisation fallback policy would admit it.
5. **Friendly, audited refusal.** A refused sign-in redirects to a plain page naming the Windows account (what an administrator needs to invite the person) and writes `auth.signin_refused` (reason code, account, SID, correlation id). A refusal changes nothing about the person.
6. **Sign-out and re-authentication.** Signing out lands on a page that does not sign straight back in. Step-up re-authentication verifies the same Windows identity is still behind the browser; Windows cannot prompt again.
7. **Provider kind, not special case.** `Windows` is an identity-provider kind configured in setup and administration like the others (with an optional list of allowed domains). Test and Demo identities are unchanged and still refused in Production.

## Consequences

* A person whose directory account is deleted and re-created does not inherit access.
* A person who is invited but has no role cannot sign in until one is granted; this is deliberate.
* Real IIS behaviour (the module, Kerberos, the pool identity) is Not Verified until the first QA run; the application-side behaviour is tested with a start-up filter that places a Windows-style principal exactly where IIS does.
* Interactive step-up and AD-group onboarding are open questions for Infrastructure and the owner.

## Alternatives considered

* Auto-provision accounts on first Windows sign-in: rejected by the owner (invite-only).
* Match by email or UPN: rejected; the web server does not supply a verified email and email must never link an account.
* Map AD groups to roles: deferred by the owner.
