# ADR-0007: Identity and Authorisation Model

Status: Proposed

## Decision
OIDC (Authorization Code + PKCE) for generic providers and Microsoft Entra ID; Demo identity only outside Production; a single `IAccessEvaluator` implementing eight ordered layers; permissions from a catalogue and role-permission matrix held as data.

## Context
Master: generic OIDC and Entra, Demo identity, role mapping, delegated administration, least privilege, server-side enforcement. Review found no permission catalogue or matrix (GAP-10) and an undefined "read all" bound (CON-05).

## Decision details
- Accounts link by `(issuer, subject)`; invite-only or group-mapped in Production; JIT provisioning off by default.
- Roles/permissions seeded from `requirements/02-reference/*.csv` as versioned data; role templates are pack data.
- Evaluator layers: authentication, tenant, organisation, permission, relationship, classification, lifecycle/lock, segregation of duties. Relationship facts via `IRelationshipProvider` (dependency inversion, ADR-0001).
- Delegation bounded by grantor rights; elevation time-boxed and audited; last-admin protection.
- Role assignments cached ≤ 60 s per tenant+user.
- Step-up re-authentication for sensitive actions.
- Alternatives: ASP.NET Identity local accounts (rejected: password handling and Production policy), claims-only roles from the IdP (rejected: ownership and scope are relationship-based), external policy engine such as OPA (rejected for the first release: extra runtime; reconsider if rules outgrow the evaluator).

## Consequences
- **Security:** one place to reason about and test; matrix tests prevent drift (AUT-001).
- **Operations:** group-claim overage requires directory lookup or fails closed.
- **Reversal:** the evaluator interface isolates the implementation.

## Verification
AUT-001 to AUT-016, SEC-003/004/010, TEN-018/019.
