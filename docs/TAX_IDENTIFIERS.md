# Counterparty tax identifiers (S10)

Status: **Implemented** (S10). Related: `docs/REGIONAL_SETTINGS.md`, `decisions/ADR-0033-regional-settings-and-counterparty-identifiers.md`, `docs/MIGRATION.md`, `docs/SECURITY.md`.

A tax or registration identifier is an **organisation-configured counterparty identity**, not "an ABN". The platform stores identifiers by *type*; the organisation's regional profile says which types exist, what they are called and how their structure is checked.

## 1. What is and is not claimed

* The platform checks the **structure** of a value against the rule the organisation configured: its length, its character classes, a mask, and optionally a checksum from a closed catalogue.
* It does **not** say that an identifier is legally valid, issued, active, registered or belongs to the counterparty. "Format Valid" means "the structure matches the configured rule", nothing more.
* **No live registry lookup exists** (S10 decision 16). "Externally Verified" and "Verification Failed" are *reserved* statuses with reserved provider-key and timestamp fields; nothing in S10 sets them, and the interface for them is labelled **Not Implemented**.
* The Harborline profile uses neutral US wording ("Federal Tax ID") with a structural mask only. It makes no claim about the tax authority, and external verification is Not Implemented.

## 2. The identifier type (a profile entry)

| Field | Meaning |
|---|---|
| Key | Stable machine key such as `au.abn` or `us.ein`. Never renamed once data uses it. |
| Label | What people read (terminology-style plain text; no markup). |
| Country | Optional applicability (ISO 3166-1). |
| Required | Whether a counterparty must have a value when it is created through the form. |
| Unique | Duplicate prevention scope: no two counterparties **in the organisation** may hold the same normalised value of this type. |
| Rule | One of the closed structural rules below. |
| Length / Mask | Parameters of the rule. |
| Checksum | `none`, `luhn`, or `mod89-weighted`. |
| Classification | The classification level a reader needs to see the value (default `Internal`). |
| Order | The lowest order is the *primary* type, shown in lists and used by the single-value form. |

Structural rules (a closed list; **no customer-supplied regular expression is accepted**):

| Rule | Accepts |
|---|---|
| `free` | 1 to 64 letters, digits, spaces, hyphens, dots and slashes |
| `digits` | Digits only (spaces and hyphens ignored); `length` is a number or a range such as `8-9` |
| `alphanumeric` | Letters and digits (spaces and hyphens ignored); `length` as above |
| `mask` | `#` digit, `A` letter, `*` letter or digit; any other character is a separator, e.g. `##-#######` |

Checksums: `luhn` (all digits), `mod89-weighted` (the weighted modulus-89 scheme used by one national business-number system; it applies only to eleven digits and only if a profile selects it). JBS keeps its existing behaviour: eleven digits, no checksum (`checksum: none`).

## 3. The stored identifier (`SupplierIdentifier`)

Tenant, organisation, counterparty, type key, original value, **normalised value**, verification status (`NotChecked`, `FormatValid`, `FormatInvalid`, `ExternallyVerified`*, `VerificationFailed`*; *reserved), classification, source kind (`Manual`, `Migration`, `Legacy`), source system, migration batch, effective from and to. An ended identifier stays as history.

* Several identifiers per counterparty are allowed when the profile has several types (one active value per type and value).
* **Vendor Number and ERP reference stay separate** fields; they are not identifiers.
* The counterparty keeps one **projection** (`Supplier.TaxIdentifier`, database column `Abn` retained and widened to 64 characters) equal to the newest active value of the primary type. Lists, search and exports use it, so existing queries and data stay valid (decision 9).

## 4. Compatibility for existing ABN data

* The additive migration `SupplierIdentifiers` creates the table and, on SQL Server, **backfills every existing non-empty `Supplier.Abn` as an active `au.abn` identifier** (source `Legacy`, status `NotChecked`, classification `Internal`), per organisation. No value is changed, dropped or re-formatted.
* Existing values remain readable and searchable under the same column and the same list/search behaviour. JBS and any organisation whose profile has the `au.abn` type show them under their label ("ABN" in the JBS pack's wording).
* An organisation with **no** identifier profile (a Blank-pack organisation that has not chosen one) shows no identifier field and the form does not ask for one; an old value stays in the column and in the identifier table and is not lost. An organisation whose profile has other types but no `au.abn` type still lists an old `au.abn` value under the generic "Tax Identifier" label (its terminology word), so it remains readable.
* Audit records and migration provenance written before S10 are not rewritten.
* Rollback of the schema is by restore from backup, as for every migration.

## 5. Access and leakage

* Reading a value needs `suppliers.read` **and** the identifier's classification within the caller's grant. A reader without the classification never sees the value and is **never told one exists**: it is omitted from the list; ending it by id answers as "not found"; the same gate hides the projection from the register list, search text, filters and exports (`TaxIdentifierGate`, resolved once per query per organisation).
* A person records only what they may read, so a duplicate check never reveals a hidden value: the "already in use" answer comes only after the classification gate passes, and uniqueness is scoped to the organisation.
* Counts, error messages and duplicate checks do not disclose a hidden identifier.
* Every add and end is audited with the identifier's classification.

## 6. Migration

* Mapping target `supplier.taxIdentifier` (the profile's primary type). Existing profiles that mapped the old ABN field keep working (the source column is mapped by the profile, not by the platform).
* Validation codes: `MIG-E027` (the value does not satisfy the organisation's configured structure), `MIG-E028` (no identifier type is set up for the organisation). A dry run checks values with the same rules without storing anything.
* Committed rows record provenance (source kind Migration, batch, source system). Rolling back a batch removes only the identifiers that batch created.
* The shipped Medius profile still maps its source column; the compatibility reference is a migration profile, not platform logic.

## 7. Where to configure it

* **Onboarding / setup**: the organisation step offers the identifier profiles that installed packs carry; "(none for now)" is allowed. The Blank pack proposes none. Nothing is chosen for the organisation.
* **Regional settings page** (`/Admin/Regional`): tick "Use my own list instead of the pack's", add a profile the packs offer or define a custom type from the closed rule and checksum lists, then validate, preview and publish.
* **Shipped profiles**: JBS `au.abn` (label "ABN", country AU, `digits` length `11`, checksum `none`); Harborline `us.ein` (label "Federal Tax ID", country US, `mask` `##-#######`, unique). Tests add synthetic profiles (a Canadian nine-digit business number with Luhn, an alphanumeric VAT-style identifier) to prove nothing is Australia- or US-specific.

## 8. Not done

External registry verification, certificate or licence checks, per-country legal validation, identifier hashing for search privacy, and bulk re-verification are Future.
