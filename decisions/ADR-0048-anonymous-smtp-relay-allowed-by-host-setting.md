# ADR-0048: An anonymous SMTP relay is allowed in Production only by a host setting

Status: Implemented; tests pass (`EmailRulesTests` ADR_0048 cases, `AnonymousRelayCheckTests`); **Not Verified on IIS**. Owner decision 2026-11-25 (Jeff). Production approval remains external governance and is not declared.

## Context

Production accepted SMTP only with both credential references (user and password as `env:`, `file:` or `keyvault:` references), so that the platform signs in to the mail server. The JBS mail server `email.jbssa.com.au` relays on port 25 with STARTTLS for the QA web server without a login, and port 25 offers no login at all; IT tested the relay from AUQLDDC01QA2WI and a message was delivered. With the earlier rule, Production-mode setup could not complete the email step.

## Decision

1. A host-only setting `Ecp:Email:AllowAnonymousRelay` (default **false**) lets Production accept SMTP with **neither** credential reference. Only the host configuration sets it (on IIS, the `APPENV_Ecp__Email__AllowAnonymousRelay` variable of the GitHub environment, stamped into web.config). Nothing in the application, the setup wizard or an imported package can turn it on.
2. Everything else stays: TLS is still required in Production; one reference without the other is still refused; a reference must still resolve; email is still ready only after a real, recent, passing test (R-NOT-01); simulated providers are still refused.
3. Setup readiness check **R-NOT-02** ("Email is sent through a relay without signing in") is a **warning** in Production whenever the saved SMTP settings have no credentials. Setup cannot finish until someone records a reason (the readiness acknowledgement, audited with who and when). Outside Production it passes, because no relay policy applies there.

## Consequences

* The mail server, not the platform, decides who may relay. That control (relaying restricted to the web server) is Infrastructure's, and the R-NOT-02 acknowledgement records that it was relied on.
* Turning the setting off makes saved anonymous settings invalid again for the next change and makes the email step fail validation; credential references must then be added.
* Reversal: remove the variable (or set it false).
