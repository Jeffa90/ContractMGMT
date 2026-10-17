# Configuration Package and Solution Pack Specification

Master: packs and packages are "versioned, dependency-aware, signed/checksummed, previewed and imported with dry run and conflict resolution"; packages include "manifests, compatibility versions, checksums, dependency graphs, identifier remapping, dry run, conflict resolution, rollback and audit" and exclude "secrets, live users and production assignments by default". `requirements/10-solution-packs/SOLUTION_PACK_SPEC.md` and `requirements/13-marketplace/CATALOGUE_SPEC.md` say the same briefly. This file defines the format and trust model (GAP-15). `[CONFIRMED]` requirements, `[CONFIRMED]` format.

## 1. Archive layout (`.ecpkg`, a ZIP; format version 1, implemented in S7)

```
manifest.json            required; canonical JSON (sorted keys, UTF-8, no BOM, LF); lists the SHA-256 of every content document
signature.json           detached signature of the canonical manifest (required in Production)
content/<name>.json      one JSON document per configuration kind and scope (flat; at most one folder level)
```

The archive is a closed structure. Only these three locations exist; anything else (extra folders, other file types, hidden files, absolute or climbing paths, backslashes, non-ASCII names, duplicate paths, symbolic links) is refused. Assets, documents and seed data are **future** (a later format version). `docs/PACK_AUTHORING.md` shows a complete example and `docs/PACK_SECURITY.md` lists the limits and error codes.

Because the implemented configuration model is one document per kind per scope, each content entry names a kind and a scope; finer items (a single field, a single workflow state) are items inside a document and are not merged individually in version 1 (ADR-0026 D12).

## 2. Manifest (format version 1, as implemented)

| Field | Notes |
|---|---|
| `formatVersion` | Integer; the importer refuses unknown versions (`PACK-M003`) |
| `packageId` | Dotted lower-case identifier, for example `platform.pack.blank`; the global identity of a package |
| `packKey` | The short key an organisation stores in `SolutionPackKey` (lower case, hyphens); the tenant-local handle |
| `displayName`, `description` | Localised text: an object of language tag to text (for example `en-AU`) |
| `version` | Semantic version; a version never changes once published |
| `publisher` | `{ id, name }` |
| `createdUtc` | UTC. The builder takes it from the recipe so builds are reproducible |
| `compatibility` | `{ minPlatformVersion, maxPlatformVersion, ignoreUnknownOptionalKinds }` |
| `requiredKinds`, `optionalKinds` | Configuration kinds the pack needs and may use. An unknown required kind makes the pack incompatible; an unknown optional kind is a warning only when `ignoreUnknownOptionalKinds` is true |
| `content` | List of `{ path, kind, scope (catalogue, tenant or organisation), schemaVersion, required, sha256 }` covering every document; a file not listed is refused |
| `capabilities` | Names of features the pack declares (informational; never enabled by installing) |
| `dependencies` | List of `{ packKey, minVersion, maxVersion }` |
| `signature` | `{ requirement }`: `Required`, or `AllowUnsignedSynthetic` (a publisher statement only; it can never weaken the environment policy) |
| `synthetic` | Boolean. Only synthetic packs may ever be accepted unsigned |

Not yet implemented and therefore not part of format version 1: `requires`, `excludes`, identifier remapping and `kind` values other than `SolutionPack`.

## 3. Safety rules for any import

1. Treated as hostile until verified: unpacked into a quarantine area, never into the application directory.
2. Entry count, total size, compression ratio and path depth limits; path traversal and absolute paths rejected; symbolic links rejected.
3. **Allowlisted file types only:** `.json`, `.md`, `.csv`, `.png`, `.jpg`, SVG (sanitised), `.docx`/`.xlsx`/`.dotx` without macros. Anything executable or macro-enabled is rejected. `[CONFIRMED]` (no arbitrary code)
4. Every `contents` entry is hash-verified; files absent from `contents` are rejected.
5. Each definition is validated against the schema for its kind and the Configuration Studio closed vocabularies (`CONFIGURATION_STUDIO_SPEC.md` section 10). A package cannot contain role assignments to people, identity group ids from another tenant, secrets, URLs to non-allowlisted hosts, or permission grants that exceed the importer's own rights.
6. Importing is itself a privileged, audited action (`packages.import` plus `config.publish` to apply) subject to step-up authentication.

## 4. Signing and trust (implemented in S7; `docs/PACK_SIGNING.md`)

- Signature: **ECDSA P-256 with SHA-256 (`ES256`)**, IEEE P1363 (64-byte) signature, over `"ECP-PACK-MANIFEST-V1\n"` followed by the canonical manifest bytes. `signature.json` carries `formatVersion`, `algorithm`, `keyId`, `signedUtc` and the base64 `signature`. The algorithm is stored in the package so another can be added later. (This replaces the earlier Ed25519 line: .NET 10 has no Ed25519, and the owner approved ES256 and no extra cryptographic dependency; ADR-0026 D3.)
- Trust anchors: public keys from host configuration (platform operator) and public keys registered by a Tenant Administrator (`packs.trust.manage`, audited, recent sign-in). A tenant key cannot shadow a platform key id. A revoked key is kept and refused. Keys may have validity dates.
- Policy by mode: **Production requires a valid signature from a trusted, active, in-date key with a supported algorithm.** There is no override in version 1. Demo and Test additionally accept an unsigned package only if it is marked `synthetic`, its manifest allows it and `Ecp:Packs:AllowUnsignedSyntheticPacks` is on, and always with a visible warning. Hashes are always verified.
- Private keys are never committed, logged, stored or displayed. Key generation, custody and rotation are operational responsibilities (`docs/PACK_SIGNING.md`; ADR-0014 and ADR-0026).

## 5. Import procedure

1. Upload → quarantine → verify structure, checksums, signature, compatibility.
2. Dependency analysis: required packages and versions present, conflicts between installed packs. *(Implemented: missing dependencies, same key with a different package identity, same key and version with different bytes.)*
3. Identifier remapping plan. *(Future: version 1 documents carry no cross-references that need remapping.)*
4. Conflict detection per definition against the target: absent (create), identical (skip), different (conflict). *(Implemented per document in version 1: an open draft is kept and reported; no per-definition merge.)* Packages that carry reference numbering rules or seed records are also checked for **Agreement Reference conflicts**: a prefix or numbering range that overlaps another organisation's rule in the tenant, or a seed `agreementReference` already used in the tenant, is reported in the dry run and must be resolved **before anything is committed** (D3). `[CONFIRMED]` *(Future: version 1 packs carry no numbering rules or seed records, so this check has nothing to examine yet.)*
5. **Dry run:** produces a report: counts created/updated/skipped/conflicting, validation results, simulation results on sample records, and a human-readable diff. No target change is made.
6. Conflict resolution per definition: *Keep target*, *Replace with package*, *Keep both with new code*, or (for lookups) *Merge values*. Locally modified definitions are never silently overwritten.
7. **Apply** creates a **draft** configuration for the target scope, never a published one. It then follows the normal lifecycle (validate, preview, simulate, publish).
8. **Rollback:** the normal lifecycle rollback plus an import-level undo that discards the draft or restores the prior release. Records created from a pack's seed data carry a batch-like marker so they can be removed.

## 6. Export

An administrator selects scope (a pack-style selection of definitions, or the whole organisation/tenant configuration). The export includes the dependency closure, strips secrets, users and assignments, writes `excludes`, computes checksums, and optionally signs with a tenant publisher key. The export is reproducible: exporting the same release twice produces identical `contents` hashes.

## 7. Upgrading an installed pack

New pack version vs installed version vs local modifications: a three-way comparison per definition. Unmodified definitions update; modified definitions become conflicts needing resolution. Removed definitions are deprecated, not deleted. In-use rules in `CONFIGURATION_LIFECYCLE_SPEC.md` section 5 apply.

## 8. Catalogue (`requirements/13-marketplace/CATALOGUE_SPEC.md`)

An administrator-controlled list of approved packages, each showing publisher, version, signature status, compatibility, dependencies and changelog. Nothing installs automatically; each install goes through section 5. A catalogue source can be a local folder, an internal repository URL (https, allowlisted) or the built-in shipped packs. The Master list of framework pack types (Procurement, Manufacturing, Construction, Government, Utilities, Mining, Healthcare) is supported by the same format; only Blank and JBS Procurement ship in the first release.

## 9. Required pack contents

**Blank pack:** minimal workflow (Draft, Active, Ended), no rules, default lookups, default terminology (en-AU), platform default branding, default role templates.

**JBS Procurement pack** (`templates/packs/jbs-procurement/`; the 6-key file `sample-data/jbs-procurement-solution-pack.json` is a stub, ASM-19): field definitions (including those in `sample-data/demo-custom-fields.json`), forms by state and role, the contract workflow (`status-transitions.csv` plus accepted supplemental transitions), the Variation workflow, rule sets (for example Legal required by category/value), approval matrix (Group Head tier), lookups (statuses, extension types, document types, classification), terminology, branding defaults, notification templates, reminder policies (Evergreen and Critical Notice placeholders), role templates with the permission mapping, default retention policy (Indefinite), and category-ownership model `ReadAllEditAssignedCategory`. Demonstration records are a separate `DemoData` package.
