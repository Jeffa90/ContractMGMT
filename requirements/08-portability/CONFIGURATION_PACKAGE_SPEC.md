# Configuration Package and Solution Pack Specification

Master: packs and packages are "versioned, dependency-aware, signed/checksummed, previewed and imported with dry run and conflict resolution"; packages include "manifests, compatibility versions, checksums, dependency graphs, identifier remapping, dry run, conflict resolution, rollback and audit" and exclude "secrets, live users and production assignments by default". `requirements/10-solution-packs/SOLUTION_PACK_SPEC.md` and `requirements/13-marketplace/CATALOGUE_SPEC.md` say the same briefly. This file defines the format and trust model (GAP-15). `[CONFIRMED]` requirements, `[PROPOSED]` format.

## 1. Archive layout (`.ecpkg`, a ZIP)

```
manifest.json            required; canonical JSON
signature.json           optional detached signature of manifest.json
definitions/<kind>/<code>.json     one file per configuration definition
assets/                  logos, document templates (allowlisted types only)
seed/                    optional synthetic demonstration data (separate package kind)
docs/README.md, docs/CHANGELOG.md
```

## 2. Manifest

| Field | Notes |
|---|---|
| `formatVersion` | Integer; importer refuses unknown major versions |
| `packageId` | Reverse-domain identifier, for example `org.example.pack.jbs-procurement` |
| `name`, `description`, `version` (semver), `kind` (`SolutionPack`, `ConfigurationExport`, `DemoData`) | |
| `publisher` | `{ name, keyId }` |
| `createdUtc` | UTC |
| `compatibility` | `{ minPlatformVersion, maxPlatformVersion, minSchemaVersion }` |
| `synthetic` | Boolean. `true` for anything shipped with live-looking sample data. Packs with `synthetic = false` and personal data are refused |
| `contents` | List of `{ path, kind, code, sha256 }` covering every file except the manifest |
| `dependencies` | List of `{ packageId, versionRange }` |
| `requires` | Names of features and adapters needed (informational; they are never enabled by import) |
| `excludes` | Statement of what was removed on export (secrets, users, assignments) |

## 3. Safety rules for any import

1. Treated as hostile until verified: unpacked into a quarantine area, never into the application directory.
2. Entry count, total size, compression ratio and path depth limits; path traversal and absolute paths rejected; symbolic links rejected.
3. **Allowlisted file types only:** `.json`, `.md`, `.csv`, `.png`, `.jpg`, SVG (sanitised), `.docx`/`.xlsx`/`.dotx` without macros. Anything executable or macro-enabled is rejected. `[CONFIRMED]` (no arbitrary code)
4. Every `contents` entry is hash-verified; files absent from `contents` are rejected.
5. Each definition is validated against the schema for its kind and the Configuration Studio closed vocabularies (`CONFIGURATION_STUDIO_SPEC.md` section 10). A package cannot contain role assignments to people, identity group ids from another tenant, secrets, URLs to non-allowlisted hosts, or permission grants that exceed the importer's own rights.
6. Importing is itself a privileged, audited action (`packages.import` plus `config.publish` to apply) subject to step-up authentication.

## 4. Signing and trust

- Signature: Ed25519 over the canonical manifest bytes; `signature.json` carries `keyId` and the signature.
- Trust anchors: the platform's built-in key for packs it ships, and tenant-registered publisher public keys. Registration is a Tenant Administrator action with audit. Revocation list supported.
- Policy by mode: Demo/Test may accept unsigned packages with a visible warning; **Production requires a valid signature from a trusted key for catalogue packs**, and allows an unsigned package only through an explicit, reasoned, audited Tenant Administrator override. Checksums are always required.
- Pack-signing key management (generation, storage, rotation) is decided in ADR-0014. Keys are never committed.

## 5. Import procedure

1. Upload → quarantine → verify structure, checksums, signature, compatibility.
2. Dependency analysis: required packages and versions present, conflicts between installed packs.
3. Identifier remapping plan: definitions refer to each other by `code`; internal ids are generated on the target; references resolve to target ids; collisions are listed.
4. Conflict detection per definition against the target: absent (create), identical (skip), different (conflict).
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
