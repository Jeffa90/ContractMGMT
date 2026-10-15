# ADR-0001: Modular Monolith

Status: Proposed

## Decision
Use modular monolith with replaceable providers.

## Context
Master Build Instructions require a "supportable modular monolith", three deployment models (Docker, IIS, Azure), a single schema and setup wizard across them, and replaceable providers for documents, email, reporting, e-signature, ERP, AI and monitoring. The team is small; the product has many functional areas (see `TASKS.md`).

## Drivers
Operational simplicity (one deployable, one database, one transaction scope for change + audit + outbox); strong boundaries to prevent a "big ball of mud"; ability to run web and worker roles from the same binary for scale.

## Alternatives considered
- **Microservices:** rejected: operational cost, distributed transactions for audit/outbox atomicity, three deployment models multiply the burden.
- **Single layered application without module boundaries:** rejected: tenant, security and configuration concerns would leak everywhere; no enforceable seams.
- **Plugin architecture with dynamic assembly loading:** rejected: conflicts with "no arbitrary executable code" and complicates support and security review.

## Decision details
One process hosts all modules (`docs/ARCHITECTURE.md` section 4). Each module owns its schema and public contract. Dependencies follow fixed tiers; upward needs use dependency inversion or events. Provider interfaces (`ADAPTER_CATALOGUE.md`) are the only replaceable seams. Host roles `web`, `worker`, `all` select which hosted services run.

## Consequences
- **Security:** one access evaluator and one audit writer shared by all modules; boundary violations fail architecture tests (ARC-001/002).
- **Operations:** single image; scale-out limited to web instances plus one leased worker.
- **Migration:** the migration module uses only Contracts/Documents public contracts so domain rules apply to imported data.
- **Reversal:** modules with narrow public contracts can be extracted later; cost is moderate. Not planned.

## Verification
ARC-001, ARC-002, ARC-009; build fails on boundary violation.
