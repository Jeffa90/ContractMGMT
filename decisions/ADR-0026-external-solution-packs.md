# ADR-0026: Solution packs are external, signed, tenant-scoped data

Status: Accepted by the owner (S7 brief, 2026-10-17). Production approval remains external governance.
Supersedes the embedded-resource pack mechanism of ADR-0024's "packs are compiled in" assumption; amends ADR-0014 (signature algorithm).

## Context

The owner chose the Enterprise Platform path: the product is the Enterprise Contract Platform and JBS Procurement is one optional solution pack. Until S7 the Blank and JBS packs were eight `EmbeddedResource` entries in three assemblies, so a second business needed a rebuild and redeploy (portability finding P-03). `docs/PACK_ARCHITECTURE.md` is the design; this ADR records the decisions that implement it.

## Decisions

| # | Decision |
|---|---|
| D1 | A pack is **data only**: a `.ecpkg` ZIP of JSON documents. It never carries or runs code, SQL, JavaScript, scripts or templates that execute. A closed archive shape, size limits, a content scan (no `$type`, script, URLs, assembly-qualified names) and the owning modules' own validators enforce this. |
| D2 | Format version 1: `manifest.json` (canonical JSON, lists SHA-256 of every document), optional `signature.json`, and `content/*.json`. Paths are ASCII, relative, at most 3 deep. Deterministic ZIP (sorted, fixed time, stored) so the same sources give the same bytes. |
| D3 | Signing algorithm for v1 is **ECDSA P-256 / SHA-256 (`ES256`)**, IEEE P1363 signature, SPKI public keys. The algorithm is stored in `signature.json`, so another can be added later without a format change. No new cryptographic dependency was added: the .NET 10 BCL has no Ed25519 (checked). This departs from the Ed25519 line of the earlier package spec, with the owner's approval. |
| D4 | The signing input is `"ECP-PACK-MANIFEST-V1\n"` + the canonical manifest bytes (domain separation). The verifier re-canonicalises the manifest and refuses a manifest whose bytes differ. |
| D5 | **Production refuses unsigned, invalidly signed, unknown-key, revoked-key, expired-key, unsupported-algorithm, hash-mismatch and tampered packs.** Demo and Test may accept an unsigned pack only if the manifest marks it synthetic and allows it **and** the environment setting `Ecp:Packs:AllowUnsignedSyntheticPacks` is on, with a visible warning. A manifest cannot weaken the policy. |
| D6 | Trust anchors: platform keys from host configuration (`Ecp:Packs:TrustedKeys`, `Ecp:Packs:TrustFile`), and tenant keys registered by a user holding `packs.trust.manage` (audited, step-up). A tenant key cannot shadow a platform key id. Revocation keeps the row. Only public keys are ever accepted, shown or stored. |
| D7 | **No signing private key is committed.** Production key custody (generation, storage, rotation, HSM or Key Vault) is an operational responsibility outside this repository. The shipped packs are built unsigned; an operator signs them (or installs operator-signed packs) before Production. CI signs with an ephemeral key to prove the path. |
| D8 | One loader (`IPackLoader`) reads packs from two sources, the shipped read-only directory and the tenant's installed packs, and treats them alike: it verifies, judges trust and compatibility afresh on every read, and returns documents by pack key, version and kind. Nothing outside `src/Modules/Ecp.Configuration/Packs/`, `src/Ecp.Packs/` and `src/Ecp.PackTool/` may open a package (architecture test). |
| D9 | Installed packs are tenant-scoped rows (`InstalledPacks`, `PackAssignments`, `TrustedKeys`) with row-level security, plus bytes in a storage provider outside any public path. An upload is quarantined, belongs to the uploader, expires, is verified again at install, and installing neither assigns nor changes configuration. The same key and version with different bytes is a conflict; identical bytes are idempotent. |
| D10 | **Assignment creates drafts only.** Installed documents enter the normal lifecycle (draft, validate, publish, version, rollback, audit, second-person publication). Published releases are never overwritten and existing contracts stay pinned. |
| D11 | `Organisation.SolutionPackKey` behaves as before; no existing data is rewritten. Organisations whose key cannot be resolved, or that received starting definitions from a different pack than the one assigned (the old silent fallback), are **reported** by diagnostics and never changed automatically. Manual options are in `docs/PACK_MANAGER.md`. |
| D12 | Upgrade conflicts are handled **per document** in v1; there is no per-definition or per-item merge. A new version is a new installed row; organisations move to it by assignment, which creates drafts. |
| D13 | The Pack Manager is form-based and accessible: no JSON editing, no marketplace, no graphical designer. Eight permissions `packs.read`, `packs.upload`, `packs.validate`, `packs.install`, `packs.assign`, `packs.retire`, `packs.trust.manage`, `packs.audit.read` are allocated by role (table in `docs/PACK_MANAGER.md`); trust-key administration is held only by the Tenant Administrator and not by Business Configuration Administrators. Install, retire, assign and trust changes need a recent sign-in. |
| D14 | The platform has no pack-specific code: loader and library name no pack, `ARC-010` forbids business names in `src`, and no pack document may be an `EmbeddedResource` (architecture tests). Blank and JBS are ordinary external packages built from `templates/packs/<key>/pack-source.json`. |
| D15 | The host reports the shipped packs in readiness: a missing pack folder or an unusable installation default pack is Unhealthy; no usable shipped pack (for example unsigned in Production) is Degraded with instructions. |
| D16 | Not in this slice: marketplace, online catalogue, billing, automatic download, per-definition merge, pack export, multi-version upgrade workflow, AI-generated packs, graphical designer. |

## Consequences

* A second business is a file, not a release. P-03 is closed for pack content; see `docs/PORTABILITY_AUDIT.md` for the evidence and what remains.
* Production needs an operator action before any pack is usable: sign the shipped packs or register a trusted key. This is deliberate and documented.
* Reproducibility is defined on the **unsigned** package and its content digest, because ECDSA signatures are not deterministic.
* A database migration (`AddSolutionPacks`) adds three tenant tables with row-level security. It is additive and idempotent; rollback is by restoring the database backup taken before the upgrade.
