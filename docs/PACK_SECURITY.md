# Solution pack security

Status: **Implemented and tested** unless a row says otherwise. The threats, controls and tests below are the ones S7 delivers; `docs/THREAT_MODEL.md` carries the platform-wide model. Nothing here declares production approval.

## 1. Principle

A pack is **data**, never code. It is treated as hostile until it has been shown to be a closed, small, correctly hashed, correctly signed set of JSON documents that the owning modules' own validators accept. Verification is repeated at every use: trust and compatibility are judged on each read, not remembered.

## 2. Limits (`PackLimits`, applied before anything is parsed further)

| Limit | Default |
|---|---|
| Package size | 8 MB |
| Entries | 64 |
| One entry (expanded) | 2 MB |
| All entries (expanded) | 16 MB |
| Compression ratio (entries above 64 KB) | 50 : 1 |
| JSON nesting depth | 32 |
| Path length / depth | 120 characters / 3 segments |

Paths use ASCII letters, digits, `.`, `_`, `-` and `/` only. Refused: absolute paths, `..`, backslashes, hidden names, empty segments, duplicates, symbolic links and special ZIP features, any file outside `manifest.json`, `signature.json` and `content/*.json`, and any file not listed in the manifest.

## 3. Threats and controls

| Threat | Control | Test |
|---|---|---|
| Zip bomb, path traversal, oversized or unexpected entries, duplicate entries | Limits and path rules above; reading is bounded | `PackFormatTests`, `Hostile_packages_are_rejected_with_plain_messages_and_nothing_is_stored` |
| Tampered content or manifest | SHA-256 per document; canonical manifest enforced; signature over the manifest | `PackSigningTests`, `PackFormatTests` |
| Unsigned, wrongly signed, unknown, revoked or expired key, bad algorithm in Production | Trust policy by environment (section 5 of `docs/PACK_SIGNING.md`) | `PackSigningTests`, `Production_refuses_unsigned_and_untrusted_packs_until_the_publisher_key_is_trusted` |
| A manifest that claims it may be unsigned | The manifest can never weaken the environment policy | `PackSigningTests` |
| Code or scripts smuggled in a document (`$type`, script, web addresses, assembly-qualified names) | Content scan; JSON parsed into closed shapes by the owning validators; ARC-007 forbids dynamic code | `PackFormatTests` |
| Pack content that grants more than the installer may | Assignment only creates **drafts**; publishing needs the usual permission and second-person rules; role assignments to people are not a packable kind | `Assigning_a_pack_creates_drafts_only_and_never_overwrites_published_configuration` |
| Overwriting live configuration or changing pinned contracts | Drafts only; published releases untouched; contracts stay pinned to their release | same |
| Changing a published version (supply-chain swap) | Same key and version with different bytes is refused; identical bytes are idempotent | `A_pack_is_uploaded_checked_previewed_installed_and_repeat_uploads_are_harmless` |
| Cross-tenant disclosure or use | Tables are tenant-owned with query filter and row-level security; storage is keyed by tenant; an upload belongs to its uploader and tenant and expires | `Packs_and_keys_belong_to_one_tenant_and_an_upload_cannot_be_used_by_another_person_or_tenant`, tenant isolation table tests |
| Privilege misuse (upload, install, trust) | Eight `packs.*` permissions checked in the service on every call, plus a recent sign-in for changes | `Pack_permissions_follow_the_confirmed_matrix_and_trust_keys_stay_with_the_tenant_administrator`, `PackManagerUiTests` |
| Key theft or leakage | Only public keys are accepted, stored, shown or logged; a pasted private key is refused and not echoed; no private key in the repository | `Trust_keys_reject_private_keys_and_never_echo_key_material`, `No_private_signing_key_is_committed` |
| Stored XSS from pack text | Razor encoding everywhere; no raw HTML from packs | `Text_from_a_pack_is_always_encoded_and_never_becomes_markup` |
| CSV formula injection from the validation report | Cells beginning `=`, `+`, `-` (non-numeric) or `@` are defused | `The_validation_report_csv_defuses_formulas_and_shows_no_server_paths` |
| Information leakage in errors | Issues carry a stable code, a plain message, an entry name and a fix; no server path, stack trace or key material | `Hostile_packages_...`, `Diagnostics_report_...` |
| CSRF on upload and install | Antiforgery on every post | `PackManagerUiTests` |
| Race: concurrent upload or install of one pack | Unique index on (tenant, key, version); a concurrent loser is treated as a repeat or a conflict | `Concurrent_uploads_and_installs_of_one_pack_leave_exactly_one_installed_copy` |
| Pack documents becoming compiled resources again | Architecture rules | `PackRuleTests` |
| Stale or forged cache entry | Cache keyed by tenant and package hash; trust and compatibility re-evaluated per read; authoritative storage is the fallback | `The_package_cache_is_keyed_by_tenant_and_package_so_one_tenant_never_reads_anothers_bytes` |
| Pack signed with a key that is later revoked | Re-evaluated on every read: the pack becomes unusable for new assignments; existing published configuration is unaffected | `Production_refuses_...` |

**Residual risks (stated, not hidden).** Production key custody is external. A trusted publisher can ship a *valid but unwise* definition; the controls are the draft, validate, simulate, publish and second-person steps, not the signature. There has been no external penetration test. Row-level security on the three new tables is verified on SQL Server only in CI (the local environment has no SQL Server), and is reported as **not verified locally**.

## 4. Audit events

`pack.upload.verified`, `pack.upload.rejected`, `pack.installed`, `pack.retired`, `pack.reinstated`, `pack.assigned`, `pack.start.recorded` (provenance when an organisation first starts from a pack), `pack.trust.registered`, `pack.trust.revoked`. Each records actor, tenant, outcome and sanitised details; the history page needs `packs.audit.read`.

## 5. Validation outcomes and error codes

Outcomes: *Valid*, *ValidWithWarnings*, *Invalid*, *Unsupported*, *Untrusted*, *Duplicate*, *Conflict*. Only the first two can be installed. A *Duplicate* is the identical file already installed (installing it is a harmless repeat).

| Code | Meaning |
|---|---|
| `PACK-A001` | Not a valid package archive |
| `PACK-A002` | Larger than the size limit |
| `PACK-A003` | Too many entries |
| `PACK-A004` | One entry is too large |
| `PACK-A005` | Expands to more than the total limit |
| `PACK-A006` | Compression ratio too high |
| `PACK-A007` | Unsafe path |
| `PACK-A008` | Duplicate path |
| `PACK-A009` | Archive feature not allowed (for example a symbolic link) |
| `PACK-A010` | A file is present but not listed in the manifest |
| `PACK-A011` | A file is in a place packages may not use |
| `PACK-A012` | An entry is corrupt |
| `PACK-M001` | `manifest.json` is missing |
| `PACK-M002` | The manifest is not valid JSON |
| `PACK-M003` | Format version not supported |
| `PACK-M004` | A manifest field is missing or invalid |
| `PACK-M005` | A content entry is listed twice |
| `PACK-M006` | A required kind has no document |
| `PACK-M007` | The kind lists disagree with the content entries |
| `PACK-M008` | The manifest is not in canonical form |
| `PACK-H001` | A document does not match its recorded hash |
| `PACK-H002` | A listed document is missing |
| `PACK-S001` | Signature missing |
| `PACK-S002` | Signature invalid |
| `PACK-S003` | Signing key unknown |
| `PACK-S004` | Signing key revoked |
| `PACK-S005` | Signing key expired |
| `PACK-S006` | Signing key not yet valid |
| `PACK-S007` | Signature algorithm not supported |
| `PACK-S008` | Signature file malformed |
| `PACK-S009` | Unsigned synthetic pack accepted in Demo or Test (warning) |
| `PACK-S010` | Unsigned packs are not permitted here |
| `PACK-C001` | Needs a newer platform version |
| `PACK-C002` | Needs an older platform version |
| `PACK-C003` | A document's schema version is not supported |
| `PACK-C004` | A required kind is unknown to this platform |
| `PACK-C005` | An optional kind is unknown (warning) |
| `PACK-C006` | A dependency is not installed |
| `PACK-C007` | Version rule violated |
| `PACK-D001` | This exact package is already installed or shipped (informational) |
| `PACK-D002` | Same key and version with different content |
| `PACK-V001` | A document is not valid configuration (message names the document and field) |
| `PACK-V002` | A document contains something packs may not (script, web address, type name) |
| `PACK-V003` | A document disagrees with the manifest (for example the pack key) |
| `PACK-V004` | A document warning from the owning validator |
| `PACK-I001` | Not installed |
| `PACK-I002` | Retired |
| `PACK-I003` | Not permitted |
| `PACK-I004` | Conflict (for example the pack key belongs to a different package identity) |

Domain error codes used by the services (surfaced as plain messages): `upload_not_found`, `pack_not_installable`, `pack_version_exists`, `pack_not_available`, `pack_not_usable`, `pack_dependency_missing`, `shipped_pack_read_only`, `pack_content_missing`, `configuration_pending_publication`.
