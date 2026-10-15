# ADR-0013: UI Technology and API Style

Status: Proposed

## Decision
Server-rendered ASP.NET Core **Razor Pages** for the UI with antiforgery, strict CSP (nonces, no inline/eval) and small self-hosted TypeScript islands for the designers; a versioned JSON API under `/api/v1` with OpenAPI generated from code.

## Context
WCAG 2.2 AA is mandatory; designers need keyboard-accessible alternatives to drag/drop; server-side enforcement is mandatory; one deployable across three hosting models; IIS and Azure scale-out make sticky real-time connections undesirable. Testability with `WebApplicationFactory` and Playwright + axe in the available environment.

## Alternatives considered
- Blazor Server: SignalR circuits need sticky sessions and complicate scale-out and IIS idle behaviour.
- Blazor WebAssembly or a React/Angular SPA: larger attack surface, separate build and CSP complexity, duplicated validation; chosen only for islands.
- MVC controllers with views: equivalent to Razor Pages; Razor Pages chosen for page-focused wizards.

## Decision details
- Pages call application services directly; the API serves integrations and islands; both use the same evaluator (ARC-008).
- Designers are progressive enhancement over server-rendered forms with an outline equivalent (A-08).
- Static assets are bundled and self-hosted; no third-party script origins (CSP, SRI not needed).
- API: problem details, ETag/`If-Match`, idempotency keys, cursor paging, rate limits (`API_AND_WEBHOOKS_SPEC.md`).

## Consequences
- **Security:** small client logic surface; no client-side template evaluation.
- **Accessibility:** native semantics by default.
- **Reversal:** islands and API allow later SPA surfaces without changing domain code.

## Verification
SEC-001/002/005, A11Y-001 to A11Y-020, AUT-014/015.
