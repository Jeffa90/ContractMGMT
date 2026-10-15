# Glossary

Added by repository review. Tags: `[CONFIRMED]` traceable to an authoritative source; `[PROPOSED]` assumption (see `docs/REPOSITORY_REVIEW.md`).

## Structure and scope

| Term | Definition |
|---|---|
| **Installation** | One deployed instance of the platform with one database. `[CONFIRMED]` (Master: single/multi-organisation/multi-tenant) |
| **Tenant** | The hard isolation boundary. Every tenant-owned row, file, cache entry, job, report and export carries a `TenantId` and is unreachable from another tenant. A single-organisation installation has exactly one tenant, created by the setup wizard. `[PROPOSED]` ASM-04 |
| **Organisation** | A business inside a tenant (for example "JBS Australia Group Procurement"). Organisations share a tenant's identity provider and platform administrators but have their own structure, configuration scope, roles and data scope. Access between organisations in one tenant is governed by policy, not by the hard boundary. `[PROPOSED]` ASM-04 |
| **Legal entity** | A registered company within an organisation that is party to contracts. `[PROPOSED]` |
| **Business unit** | An operating unit within an organisation; may span legal entities. `[PROPOSED]` |
| **Division** | A grouping of business units, used for delegated administration scope. `[CONFIRMED]` (Master: "scoped Division/Site administrators") |
| **Site** | A physical location or plant. `[CONFIRMED]` (Master: sites; Medius profile: plant) |
| **Single organisation / multiple organisations / multi-tenant** | The three product modes: one tenant+one organisation; one tenant+many organisations; many tenants hosted on one installation. `[CONFIRMED]` |
| **Mode (Demo / Test / Production)** | The configuration mode chosen at setup. Production refuses development identity, default credentials, ephemeral storage and exposed secrets. `[CONFIRMED]` |

## People and authority

| Term | Definition |
|---|---|
| **Security role** | A named bundle of permissions, listed in `requirements/05-security/roles-and-permissions.csv`. `[CONFIRMED]` |
| **Permission** | A code such as `legal.review`, listed in `permission-catalogue.csv`. `[PROPOSED]` catalogue, codes `[CONFIRMED]` where used in `status-transitions.csv` |
| **Scope rule** | A condition narrowing a scoped grant (for example "category the user actively owns"). `[PROPOSED]` |
| **Category owner** | A user with an *active category assignment*; reads all contracts in scope and edits contracts in assigned, active categories that are not locked. `[CONFIRMED]` |
| **Category assignment** | An effective-dated link between a user (or group) and a category. Ownership follows the *active* assignment. `[CONFIRMED]` |
| **Approver group** | A named set of users (for example "Group Head") holding the `Approver` role for approval-matrix purposes. `[PROPOSED]` ASM-08 |
| **Group Head** | The executive who, or whose backup who, performs final register authorisation after execution. `[CONFIRMED]` |
| **Backup approver** | A user who may act as first responder when the primary approver is unavailable. `[CONFIRMED]` |
| **Delegated administrator** | An administrator at System, Tenant, Organisation, Business Configuration, or scoped Division/Site level, with least privilege. `[CONFIRMED]` |
| **Elevated (emergency/support) access** | Reason-based, time-limited, visible and fully audited access beyond a user's normal rights. `[CONFIRMED]` |

## Contract lifecycle

| Term | Definition |
|---|---|
| **Agreement / contract** | The managed record. `agreementReference` is its unique business identifier (uniqueness scope: organisation, `[PROPOSED]` Q-07). |
| **Start date** | When the agreement term begins. `[CONFIRMED]` field |
| **Effective date** | The contractual effective date. Independent of the `Effective` status. `[PROPOSED]` CON-15 |
| **Effective (status)** | The state entered when final authorisation is granted. The record is locked. `[CONFIRMED]` |
| **Registered Date** | A timestamp stamped when final register authorisation occurs. Separate from Effective date. Not editable. Imported only under a legacy-load policy. `[CONFIRMED]`; legacy exception `[PROPOSED]` ASM-06 |
| **Register authorisation** | The final approval by the Group Head or backup after DocuSign execution, performed by the `Approve` transition. `[CONFIRMED]` |
| **Locked** | A record in an Effective-family state that cannot be edited except by an applied Variation or an audited emergency unlock. `[CONFIRMED]` |
| **Variation** | A controlled change to an Effective contract, itself reviewed, signed and approved, which on approval changes the parent in a controlled way. `[CONFIRMED]`; lifecycle `[PROPOSED]` |
| **Legacy record** | A contract imported from a prior system in a state of Effective or later, under a legacy-load policy. `[CONFIRMED]` (Medius profile); policy `[PROPOSED]` |
| **Evergreen** | A contract with no fixed end date that continues until terminated (`endDate` blank). `[CONFIRMED]` |
| **Critical Notice** | A reminder behaviour for contractual notice deadlines. Offsets and recipients `[PROPOSED]`, Q-04 |
| **Legal review round** | One cycle of Legal examination ending in Cleared or Returned for rework. Rounds accumulate; closed rounds are immutable. `[PROPOSED]` |
| **Archive-before-delete** | A record is archived (retained, read-only, restricted) before any deletion is considered; ordinary users cannot hard-delete legal records. `[CONFIRMED]` |
| **Legal hold** | A flag that prevents disposition regardless of retention policy. `[CONFIRMED]` |
| **Classification** | A sensitivity label on a record or document controlling read, masking and export. `[CONFIRMED]`; levels `[PROPOSED]` |

## Configuration and portability

| Term | Definition |
|---|---|
| **Configuration definition** | One versioned unit of business configuration: field, form, rule set, workflow, approval matrix, lookup, terminology, branding, feature flags, notification template, document type, retention policy. `[PROPOSED]` |
| **Draft / Published / Superseded** | States of a configuration version. Published versions are immutable. `[PROPOSED]` |
| **Configuration release** | An atomic set of definitions published together, validated as a whole. Records pin the release in force. `[PROPOSED]` |
| **Rollback** | Publishing a new version whose content equals an earlier one. History is never rewritten. `[PROPOSED]` |
| **Solution pack** | A versioned, checksummed, dependency-aware configuration package for an industry or business (Blank, JBS Procurement, ...). `[CONFIRMED]` |
| **Configuration package** | The portable archive format that carries a pack or an export of a tenant's configuration. `[CONFIRMED]` |
| **Reference configuration** | A pack shipped as sample behaviour. JBS is *a* reference configuration, not product logic. `[CONFIRMED]` |
| **Terminology alias** | A display-name override for a platform concept (for example "Contract" shown as "Agreement"). Codes remain stable. `[CONFIRMED]` |

## Migration

| Term | Definition |
|---|---|
| **Migration project** | The container for one source system's migration (source, extraction date, owner, freeze window, mock cycles). `[CONFIRMED]` |
| **Migration batch** | One execution unit: dry run, mock or final load; every imported row, file and audit event carries its `MigrationBatchId`. `[CONFIRMED]` |
| **Staging** | Quarantined storage of source rows and files, separate from target tables. A dry run writes only to staging. `[CONFIRMED]` |
| **Target** | Domain tables and committed document storage. `[PROPOSED]` precise definition |
| **Provenance** | Source system, source record ID, hash, extraction timestamp, source status, batch and mapping-profile version kept for every imported item. `[CONFIRMED]` |
| **Idempotency key** | The tuple that makes re-running safe: `(TenantId, OrganisationId, SourceSystem, SourceRecordId)`. `[CONFIRMED]` partly; organisation `[PROPOSED]` |
| **Reconciliation** | Evidence that source totals equal accepted + rejected + skipped, with distributions and samples. `[CONFIRMED]` |

## Platform

| Term | Definition |
|---|---|
| **Provider / adapter** | A replaceable implementation behind an interface (documents, email, reporting, e-signature, ERP, AI, monitoring, identity, malware scan). `[CONFIRMED]` |
| **Outbox / inbox** | Transactional tables that make outbound integration messages reliable (outbox) and inbound callbacks idempotent (inbox). `[CONFIRMED]` |
| **Bootstrap store** | A protected local store holding setup state and secret *references* before the database exists. `[PROPOSED]` ASM-10 |
| **Readiness check** | A named verification run by the setup wizard and the operations dashboard (`SETUP_AND_READINESS_SPEC.md`). `[PROPOSED]` |
