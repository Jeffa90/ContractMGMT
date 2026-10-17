# Regional settings (S10)

Status: **Implemented** (S10). Owner decisions: `requirements/01-authoritative/CONFIRMED_DECISIONS.md`, section "S10 regional decisions". Architecture decision: `decisions/ADR-0033-regional-settings-and-counterparty-identifiers.md`. Companion: `docs/TAX_IDENTIFIERS.md`.

The platform assumes **no** country, locale, currency, time zone, tax-identifier format or address layout. Every one of them is an organisation setting. A solution pack may *propose* values; the organisation *decides*.

## 1. What an organisation's regional profile holds

| Setting | Meaning | Validated against |
|---|---|---|
| Country or region | A label for people and for the identifier and address profiles. It decides no format by itself. | ISO 3166-1 alpha-2, from the host's culture data |
| Primary locale, supported locales | A BCP 47 tag such as `fr-CA`. It decides the date order, the number format and the currency-symbol habit. **It is not the language of the page text** (that is terminology and resource strings). | BCP 47 shape and the host's culture data |
| Default currency, supported currencies | The currency offered when an amount is entered. Every amount keeps its own currency code; the default never rewrites an amount. | ISO 4217 list in `RegionalCatalogue` (a closed list) |
| Time zone | An IANA zone. Times are **stored in UTC** and converted only for display, notifications, exports and input. Daylight saving follows the zone's own rules. | The zone must resolve on the host; an unavailable zone blocks publication |
| Time display | The locale's habit, a 24-hour clock or a 12-hour clock. | A closed list |
| Phone region | Optional; a country code. No phone number format is checked. | ISO 3166-1 |
| Counterparty identifier profile | Which tax or registration identifier types the organisation records and how their structure is checked (`docs/TAX_IDENTIFIERS.md`). | The closed rule and checksum catalogues |
| Address profile | Which address fields are used and which are required. Labels come from terminology. | The six address field keys |
| Source | The pack and version the profile inherits from for values it leaves out; pinned when the draft is made. | A pack key and a version |

There are no format strings, patterns or expressions in a profile. Date, number and currency formats are *derived from the locale*; nothing a customer types is executed.

## 2. The one resolver and its order

`IRegionalSettingsResolver` (`Ecp.Configuration`) is the only thing that answers "what is this organisation's locale, currency, zone, identifier profile or address profile". Pages, navigation, notifications, reminders, dashboards, exports, migration reports, the setup report, onboarding, the Pack Manager and the search inputs all ask it (through `PageFormat` in the host, or a `LocaleFormatter` obtained from it).

Order of precedence (`RegionalComposer`, a pure function that is unit tested for every order):

1. **The organisation's published profile** (`RegionalProfile` configuration kind), value by value.
2. **The pack version the profile is pinned to** (or, for an organisation with no profile yet, the pack assigned to it).
3. **The organisation's stored settings** (locale, zone and currency recorded when it was set up). This is the compatibility layer: an organisation created before S10 is exactly as it was. Without a published profile it ranks *first*, so existing organisations are never changed by installing S10.
4. **The installation fall-back** (`Ecp:Localisation`), used only where **no organisation exists** (a diagnostic, the first page of setup). It ships empty. Nothing in the repository sets it.
5. **Invariant, machine-safe output** (ISO 8601 dates, UTC, plain numbers, no currency guessed). Production readiness reports an organisation that has no decided locale, currency and time zone.

`RegionalSettings.Sources` records where each value came from; the Regional settings page shows it.

Performance and isolation: settings are resolved **once per request or operation** (a generation-based per-request memo), cached by *tenant, organisation and published version*, invalidated on publish and rollback, and read from persistence when the cache fails. One tenant's or organisation's settings are never returned for another.

## 3. Lifecycle

The profile uses the ordinary configuration lifecycle: **draft, validate, preview, publish, version history, rollback, audit** (permissions `config.draft`, `config.publish`, `config.rollback`, `config.history.read`; second-person publication when the tenant requires it).

* Validation checks every code against the catalogues. An unavailable time zone, an unknown currency or a supported list that omits the default is an error. An incomplete profile (no locale, currency or zone decided) is a warning on publication but **blocks Production readiness**.
* The preview (`/Admin/Regional`) writes sample values in the settings the form would give: a date whose day and month differ, 15 January and 15 July at the same UTC time (so a zone with daylight saving shows both offsets), a number, an amount with and without its code, the address fields and the identifier types.
* Publishing records the effective locale, zone and currency on the organisation (the compatibility columns every older reader still uses) and drops the cached settings. **No stored timestamp changes and no amount changes.** Audit records and migration provenance are never rewritten.
* Rolling back republishes an earlier release as a new release; history is kept.
* **Demo and Test** modes may apply explicit, labelled synthetic defaults (sample data names its own locale, currency and zone). Production never does.

## 4. Packs propose, organisations decide

Each shipped pack carries a `RegionalProfile` document (mode `defaults`):

| Pack | Version | Proposes |
|---|---|---|
| Blank | 1.6.0 | Nothing for locale, currency or zone. The organisation must choose. No identifier type is proposed. Address: every field used, none required. |
| JBS Procurement | 2.6.0 | `en-AU`, `AUD`, Australia/Brisbane, country AU, identifier type `au.abn` labelled "ABN" (eleven digits). |
| Construction subcontracts (Harborline) | 1.1.0 | `en-US`, `USD`, America/Chicago, country US, identifier type `us.ein` labelled "Federal Tax ID" (mask `##-#######`). |

* Assigning a pack **creates drafts only**; it never changes a published setting. A pack upgrade does not change published settings either: the profile is pinned to the pack version it was made from.
* Setup and onboarding show the proposal in the form fields; the person can override any value, and an override is never silently forced back.
* The pack hash and signature cover the regional document like any other content.
* A non-Australian organisation never sees "ABN" unless its own profile (or a pack it chose) configures that type.

## 5. Currency

* Every amount has an explicit currency code. There is no global default and no inference from a symbol.
* A symbol is used only for the culture's own currency; any other currency is written with its ISO code (`fmt.Money`). Exports and notifications use the code beside the number (`fmt.MoneyWithCode`).
* Amounts without a currency and without an organisation default cannot be shown (`missing_currency`); pages show the number with "(currency not set)" instead of guessing.
* The approval matrix names its currency and effective-dated rates; a missing rate fails closed (`docs/APPROVAL_MATRIX.md`). No foreign-exchange integration exists.
* A migration needs an explicit currency per row or an authorised default; a pack default never rewrites an amount.
* Tests cover AUD, USD and CAD (`LocalisationTests`, `RegionalSettingsScenarios`).

## 6. Dates, times and daylight saving

* Date-only values (a contract's end date) are **never shifted by a time zone**. Instants are stored in UTC and converted with the organisation's IANA zone, so daylight-saving transitions follow the zone's rules (tested in a zone that observes it and in one that does not, for both offsets).
* `Fmt.Date`, `Fmt.DateTime`, `Fmt.Number`, `Fmt.Money` are the page-level formatters. A page that prints an instant with a fixed `yyyy-MM-dd HH:mm` plus the word UTC is refused by guard stage C.
* Where a value is explicitly labelled UTC or ISO 8601 and read by a program (API payloads, the audit hash input, machine exports, file names, `reporting` column values) it stays stable and invariant.

## 7. Notifications

Business-facing notification text renders dates and amounts with the organisation's settings. A template may use only these placeholders (a closed list checked at publication; unknown or unsafe tokens fail validation):

| Placeholder | Output |
|---|---|
| `{{due.date}}` | The due date in the organisation's locale (a date, not shifted by a zone) |
| `{{due.date.iso}}` | The due date as `yyyy-MM-dd` (invariant machine value) |
| `{{now.date}}`, `{{now.datetime}}` | The send time as a local date, or local date and time with offset, in the organisation's zone |
| `{{now.utc}}` | The send time as ISO 8601 UTC (`2026-10-16T04:30:00Z`) |
| `{{contract.value}}` | The amount with its ISO currency code, in the organisation's number format (empty if the contract has no value or currency) |
| `{{contract.currency}}` | The ISO 4217 code |
| `{{contract.reference}}`, `{{contract.title}}`, `{{actor.name}}`, `{{link}}`, `{{days.remaining}}`, `{{window.days}}`, `{{step.name}}` | As before |

Reminder "today" is the organisation's own calendar day in its zone. Dashboard and reminder-schedule lines use the organisation's date format.

## 8. Exports

See `docs/EXPORTS.md`. Business exports (the default) use the requesting organisation's formats and include a currency code beside every amount; a machine export has to be asked for and uses ISO 8601 and plain numbers regardless of locale. Formula-injection protection and classification-safe content are unchanged.

## 9. Compatibility

* Existing organisations keep their locale, zone and currency and their behaviour. The `Organisation` columns remain the stored layer.
* Existing ABN values are preserved and readable (`docs/TAX_IDENTIFIERS.md`).
* Existing audit text, migration provenance and signed package bytes are not rewritten.
* The migration is additive and value-preserving; rollback of the schema is by restore from backup (as for every other migration).

## 10. Limits (stated honestly)

* Locale selection controls formats, not the language of the page text. Page text is English with terminology overrides; translated resource sets are Future.
* Text produced inside the approval engine and the lifecycle module for explanations (the matrix decision trace, "acts on behalf of … until …" notes) is written in ISO 8601 / UTC form and stored as written. It is classified as a machine-readable technical format.
* Address fields are bounded free text. No postal format, state list or ZIP/postcode check exists for any country.
* No registry lookup, currency conversion or tax-law claim is made.
* Reporting integration, public API formats and the SaaS control plane are not part of S10.
