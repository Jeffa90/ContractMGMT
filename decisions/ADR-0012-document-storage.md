# ADR-0012: Document Storage and Content Addressing

Status: Proposed

## Decision
`IDocumentStorageProvider` with a local persistent-volume provider first and an Azure Blob provider next. Keys are `t/{tenantId}/{sha256-prefix}/{sha256}`: content-addressed **within a tenant**. Uploads stream through limits, signature sniffing, hashing, quarantine, scan, then promotion. Downloads are authorised per request and streamed; there are no public URLs.

## Context
Master: versioned documents, authorise every download, validate MIME/type/size, prevent path traversal, malware hooks, no public unauthenticated URLs. `ATTACHMENT_INGESTION.md` offers "link existing content where storage permits". Cross-tenant deduplication would create an existence oracle (T-038, ASM-14).

## Decision details
- Metadata (`documents.Document`, `DocumentVersion`) is separate from blobs; the platform computes SHA-256 from stored bytes (INV-30).
- A reference-count table governs deletion and rollback; a sweeper removes unreferenced blobs after a grace period.
- Scan status gates download (INV-32/34); Demo may use a mock scanner, Production requires a scanner or a recorded risk acceptance (R-SEC-07).
- File names are display-only and sanitised; keys never contain user input.
- Local provider requires a persistent volume; ephemeral paths are refused in Production (R-ST-01).
- Alternatives: database BLOBs (backup size, cost, streaming), random-GUID keys without dedupe (simpler but loses "link existing content"), SharePoint first (dependency on tenancy of Graph; deferred).

## Consequences
- **Security:** traversal impossible by construction; tenant prefix enforced by the provider (TEN-033).
- **Operations:** backups must cover both database and storage consistently (BRS-001/002).
- **Migration:** idempotent blob writes enable safe resume.
- **Reversal:** other providers implement the same interface; key format migrates by copy.

## Verification
INV-30 to INV-36, SEC-011 to SEC-016, TEN-003/004/021/033, MIG-002, MIG-025.
