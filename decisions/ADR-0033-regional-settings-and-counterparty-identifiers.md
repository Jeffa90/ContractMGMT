# ADR-0033: Regional settings are organisation settings; counterparty tax identifiers are configurable types

Status: Implemented by slice S10 (2026-10-17). Proposed until the owner reviews it; production approval remains external governance.
Builds on ADR-0024 (pack resolved by organisation), ADR-0026 (external packs), ADR-0028 (terminology), ADR-0029 (stable identifiers), ADR-0030 (capabilities), ADR-0031 (pack validation) and ADR-0032 (second business as configuration only).

## Context

S9 proved a second business (Harborline Construction, en-US, USD, America/Chicago) but left generic defects G2 and G12 and a set of Australian assumptions in compiled code: a default locale, zone and currency in `appsettings.json` and `LocalisationOptions`, a fixed `en-AU` formatter fallback in several pages, ISO timestamps with the word UTC in business pages and notifications, an eleven-digit "ABN" rule in the supplier domain, and fixed "Evergreen" text. A portable platform cannot assume any country.

## Decision

1. **Organisation settings are the authority** for locale, currency and time zone. A pack may propose; the organisation may override; nothing is forced back. The platform defaults are empty.
2. **A `RegionalProfile` configuration kind** (draft, validate, preview, publish, version, rollback, audit through the ordinary lifecycle) holds country or region, primary and supported locales, default and supported currencies, IANA zone, time display, phone region, identifier profile and address profile. Every code is validated against a closed catalogue (`RegionalCatalogue`); there are no format strings.
3. **One resolver** (`IRegionalSettingsResolver`, Configuration module) answers every regional question, in the order: organisation's published profile, pinned pack version, organisation's stored settings (compatibility layer), installation fall-back only where no organisation exists, invariant machine-safe output. It is resolved once per request, cached by tenant, organisation and version, invalidated on publish and rollback, and falls back to persistence if the cache fails.
4. **Compatibility layer.** The `Organisation` columns for locale, zone and currency stay; publishing a profile writes them. An existing organisation with no profile keeps its stored values *first*, so installing S10 changes nothing for it. Stored timestamps stay UTC; amounts are never converted; audit text and migration provenance are never rewritten.
5. **Tax identifiers are types, not ABNs.** A `SupplierIdentifier` table holds typed, normalised values with verification status (structural only; external statuses reserved and Not Implemented), classification, provenance and uniqueness scoped to the organisation. Structure is checked by a closed rule catalogue (`free`, `digits`, `alphanumeric`, `mask`) and a closed checksum catalogue (`none`, `luhn`, `mod89-weighted`); no customer regular expression is accepted. The platform claims no legal validation and performs no registry lookup.
6. **The old column is kept as a projection.** `Supplier.Abn` is renamed in code to `TaxIdentifier` but keeps its column name (widened to 64) as the projection of the primary type. The additive migration backfills every existing value as an `au.abn` row of source `Legacy`. Vendor Number and ERP reference stay separate.
7. **Packs carry regional documents.** Blank proposes nothing (the organisation must choose); JBS proposes `en-AU`, `AUD`, Australia/Brisbane and the `au.abn` type; Harborline proposes `en-US`, `USD`, America/Chicago and `us.ein`. Pack versions: Blank 1.6.0, JBS Procurement 2.6.0, Construction Subcontracts 1.1.0. Assignment creates drafts only; the regional document is covered by the pack hash and signature.
8. **Formatting is the resolver's.** Pages use `PageFormat` (`Fmt`), notifications use constrained placeholders (`due.date`, `due.date.iso`, `now.date`, `now.datetime`, `now.utc`, `contract.value`, `contract.currency`), business exports use the requesting organisation's formats with currency codes, and a machine export (ISO 8601, UTC, invariant numbers) is a separate, explicit choice.
9. **Address is a minimal profile.** Six free-text fields with labels from terminology (`address.*`), optional or required per organisation; no postal validation for any country.
10. **"Evergreen" is a stable code, not a label.** The extension type code `Evergreen` is unchanged (`ExtensionTypes.OpenEnded` in code); what people read is the terminology key `field.evergreen`.
11. **Guard stage C** extends the vocabulary guard to any country's locale, currency, zone, identifier word, address word, date order, currency symbol, fixed "Evergreen" wording and fixed ISO timestamp in a page. Allowances are for one named file each with a written reason; stale allowances fail the build.

## Consequences

* PRT-012 (culture, zone and currency per organisation) is now evidenced for creation, configuration, display, notification and export, not only for storage.
* The platform has more moving parts at run time: a regional cache and a one-statement-per-organisation read on the first page of a request. The page-statement ceiling in `SearchPlanShapeScenarios` moved from 42 to 44.
* Limits stay documented: page-text language is not localised; engine-written explanation text is ISO/UTC; address fields are not validated; no registry verification; no FX.
* Alternatives rejected: a single global default in appsettings (it hides the Australian assumption behind configuration); a customer-supplied regular expression for identifiers (a ReDoS and review surface); renaming the `Abn` column (risk disproportionate to benefit); a per-user locale (the organisation owns the formats of its records).
