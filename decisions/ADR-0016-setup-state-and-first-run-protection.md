# ADR-0016: Setup state and first-run protection

Status: Proposed (accepted with the T0.3 pass)

## Context

The first-run wizard must work before any database exists, survive restarts mid-way, be unreachable by anyone but the person who controls the server, and leave no way back in once installation is complete other than a re-authenticated administrator. `SETUP_AND_READINESS_SPEC.md` section 1 sketches a bootstrap store, a printed token, loopback-only access and route removal.

## Decision

1. **Two copies of one document.** `SetupDocument` (revision, mode, stage records, acknowledgements, token and session *hashes*) is held in a Data-Protection-protected file in the data directory from the first start and mirrored to `setup.SetupRecord` (system tenant, RLS) when the database exists. The higher revision wins on load. An unreadable file is a hard stop.
2. **Token.** Issued by `Ecp.Host setup-token` (24 h, single use, 256 bits, hash only stored, printed to the terminal that ran the command, never logged). It is exchanged for a **single** session (60 min sliding, hash only stored, HttpOnly, SameSite=Strict, path `/Setup`).
3. **Network.** `/Setup` is `404` for callers that are not loopback unless `Ecp:Setup:AllowRemote`.
4. **Gate.** Middleware: incomplete installation redirects browsers to `/Setup` and answers `503` on the API; complete installation makes every `/Setup` path `404`. Seeded Demo is complete from the start.
5. **Stages** are idempotent classes with declared dependencies; the engine serialises execution, records the result, and turns unexpected failures into a safe message while logging the cause.
6. **Reopening** is a different, authorised page (`/Admin/Setup`, `setup.run`) and changes need a recent authentication (`RecentAuthentication` policy, `/Reauth`). Only amendable stages can be rerun.
7. Secrets are never stored: secret *references* are validated and held as references; the report omits even those.

## Consequences

* A restart at any point resumes; a lost file is recovered from the database and vice versa.
* The module sits at tier 9 and may use every lower module's public contract; the host (tier 10) supplies the protector, the Demo data applier and the pages.
* Deviation from the specification: token printed by command instead of at start-up (ASM-20).
* Residual: the token is a bearer credential on a terminal; operators must treat their shell history and terminal recordings accordingly. Remote setup, once allowed, depends on the token alone plus network controls.
