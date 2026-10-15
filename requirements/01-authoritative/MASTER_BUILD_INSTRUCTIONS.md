# Master Build Instructions v2

Act as principal enterprise architect, product engineer, senior full-stack developer, UX/accessibility designer, database architect, security engineer, DevOps engineer, migration lead, QA lead and technical writer.

Build a production-shaped, portable enterprise contract-management platform. JBS Australia Group Procurement is the first shipped solution pack, not hard-coded universal behaviour.

## Product outcome
A business administrator can install or onboard an organisation through guided wizards and then configure fields, forms, pages, workflows, business rules, approval matrices, taxonomies, document types, templates, notifications, branding, terminology and feature modules without writing code or asking a developer to deploy changes.

Do not falsely claim that enterprise production hosting requires no technical governance. Initial hosting, identity registration, DNS/TLS, network controls, database operations, backups, monitoring, penetration testing and accreditation may require authorised IT or a managed provider. Minimise those tasks through validation, safe defaults, readiness checks and generated handover instructions.

## Architecture
Use a supportable modular monolith, preferably current supported ASP.NET Core/C#, Entity Framework Core and SQL Server. Include:
- Docker and Docker Compose as the primary portable/demo model
- Windows Server/IIS deployment with no Docker dependency
- Microsoft Azure deployment model
- Same setup and onboarding wizards across all deployments
- Generic OpenID Connect and Microsoft Entra ID
- Provider interfaces for documents, email, reporting, e-signature, ERP, AI and monitoring
- Tenant-aware APIs and background jobs
- Outbox/inbox patterns for reliable integrations
- Optimistic concurrency and structured immutable audit events
- OpenAPI, health/readiness endpoints and automated tests

## Product modes
Support single organisation, multiple organisations in one installation, and optional multi-tenant hosting. Tenant isolation must use TenantId on owned entities, query filters, service policies, database constraints and cross-tenant tests. UI filtering alone is prohibited.

## Installation wizard
On first launch redirect to `/setup`; normal pages are unavailable until complete. The wizard is resumable, idempotent and accessible.

Stages:
1. Welcome, purpose, version, privacy/licence notices and non-production warning
2. Demo, Test or Production Configuration mode
3. Docker, IIS or Azure deployment awareness and environment checks
4. Single organisation, multi-organisation or multi-tenant mode
5. Organisation, legal entities, locale, time zone, currency, terminology and branding
6. Database connection, encryption, creation/validation and migrations
7. Document-storage provider, persistence and connection test
8. Local Demo identity or external OIDC/Entra configuration
9. Initial administrator, delegated-admin model and role mapping
10. Email/notification provider and test
11. Optional integrations and mock/disabled/configured status
12. Select Blank, JBS Procurement or other industry solution pack
13. Import/create taxonomies, sites, contract types and suppliers
14. Select/configure workflows, rules and approval matrices
15. Retention, classification, audit and legal-hold defaults
16. Optional synthetic demonstration data
17. Production-readiness checks and sanitised setup report
18. Complete installation, protect setup routes and require re-authentication for changes

Production mode refuses development identity, default credentials, ephemeral storage, exposed secrets and unresolved critical readiness failures.

## Organisation onboarding wizard
Provide a post-install wizard that a business administrator uses without coding:
- Define organisation/legal-entity/business-unit/site structure
- Invite users or map identity groups
- Assign role templates and delegated administrators
- Import categories, sites and suppliers from CSV/XLSX
- Select contract types and solution packs
- Add custom fields and arrange forms
- Configure lifecycle workflows and approval matrices
- Configure templates, document categories, reminders and escalations
- Preview using synthetic records
- Simulate access, rules, workflows and approvals
- Publish configuration
- Generate business-admin and end-user quick guides

## No-code Configuration Studio
All configuration is versioned, effective-dated where relevant, audited and supports draft, validation, preview, simulation, publish and rollback.

### Field designer
Support text, long text, integer, decimal, currency, percentage, date, datetime, boolean, single/multi choice, lookup, person, email, URL, file reference and safe calculated-display types. Configure requiredness, defaults, help, validation, sensitivity, search/report flags and visibility/editability by role, state, contract type and rule. Do not create a physical database migration for each new field; use a metadata-driven typed-value or validated JSON model with indexed/reporting projections while keeping core domain fields strongly typed.

### Forms/pages designer
Configure sections, tabs, field groups, related lists, help text, action buttons, conditional visibility, required/read-only behaviour and responsive layout. Provide accessible ordering controls in addition to drag/drop.

### Rules designer
Use a constrained condition builder with AND/OR groups and typed operators. Actions include set value, require/show/hide field, assign owner/group, require Legal review, select workflow/approval matrix, create notification, set classification and block transition with an explanation. No arbitrary executable expressions.

### Workflow designer
Versioned state-machine builder for states, transitions, permissions, conditions, required documents/fields, review/approval steps, notifications, timeouts, escalations, rework, parallel/sequential steps, first/all response and backup/delegation. Validate cycles, unreachable states and unsafe changes to in-use versions.

### Approval matrix designer
Configure by value/currency, category, contract type, legal entity, business unit, risk, classification and exceptions. Support sequential, parallel and first-response models, effective dates, delegation and simulation.

### Administration
Manage lookups/taxonomies, feature flags, terminology aliases, branding, notification templates, integration settings, solution packs and configuration package import/export without SQL access.

## Core reference functionality
Ship a configurable JBS Procurement solution pack implementing:
- Category owners read all and edit current assigned categories
- Ownership follows active category assignment
- Legal reads all and performs structured multi-round review
- Group Head or backup performs final register authorisation after execution
- DocuSign initially handles execution and Delegation of Authority
- Approval stamps Registered Date separately and locks Effective records
- Changes to Effective records use Variations
- Evergreen and Critical Notice reminder behaviour
- Archive-before-delete
- AI assists schedule/details only, never core T&Cs
- Qlik as external full-reporting destination

## Migration and bulk attachment ingestion: Phase 1 mandatory
Implement the full requirements under `requirements/09-migration/`.

At minimum:
- CSV, XLSX, JSON and ZIP-package imports
- Connector extension points for SharePoint and source-system exports
- Source-to-target mapping profiles
- Dry-run validation and reconciliation
- Bulk contract metadata import
- Bulk supplier/taxonomy import
- Attachment ZIP ingestion and folder ingestion where deployment permits
- Configurable matching by agreement reference, source ID, manifest and folder path
- Manual unmatched-file resolution
- Duplicate/version strategy
- File hash and malware-scanning extension point
- Resumable batches, idempotency and checkpoints
- Error workbook/report
- Batch-level audit, counts and reconciliation
- Rollback before final acceptance, and compensating archive where later actions make destructive rollback unsafe
- Preservation of source IDs and provenance
- Cutover/migration freeze support

## Documents
Use `IDocumentStorageProvider` with local demo, filesystem/network where approved, SharePoint/Graph and future object-storage providers. Version documents, authorise every download, validate MIME/type/size, prevent path traversal and provide malware-scanning hooks. Never expose public unauthenticated file URLs.

## Industry solution packs and catalogue
Ship Blank and JBS Procurement packs and a framework for Procurement, Manufacturing, Construction, Government, Utilities, Mining and Healthcare packs. A pack can contain metadata definitions, forms, workflows, rules, approval matrices, lookups, terminology, branding defaults, notification templates and authorised document templates. Packs are versioned, dependency-aware, signed/checksummed, previewed and imported with dry run and conflict resolution. Never include secrets or live identities by default.

## AI assistance
AI is optional and cannot be required for core operation. Provide interfaces for guided organisation onboarding, contract metadata extraction, summary, schedule drafting and supplier classification. All output is reviewable, permission-aware, audited and never approves, signs, registers, bypasses access or alters locked records or controlled T&Cs.

## Supplier intelligence
Provide adapter interfaces for ABN validation, supplier master/ERP sync, website-description retrieval, taxonomy classification and risk data. These are optional and must not block manual supplier creation or imports.

## Delegated administration
Support System, Tenant, Organisation, Business Configuration and scoped Division/Site administrators with least privilege. Emergency/support access is reason-based, time-limited, visible and fully audited. Never expose credentials.

## Import/export and configuration portability
Support records, documents, metadata, audit evidence and configuration exports subject to permission. Configuration packages include manifests, compatibility versions, checksums, dependency graphs, identifier remapping, dry run, conflict resolution, rollback and audit. Exclude secrets, live users and production assignments by default.

## Security and governance
Enforce all access server-side. Protect against IDOR/BOLA, tenant breakout, workflow bypass, approval spoofing, unsafe uploads, custom-field injection, malicious package imports and privilege escalation. Use CSRF protection, CSP/security headers, secure cookies, parameterised data access, rate limits, safe errors, secret redaction, dependency scanning and SBOM generation where practical. Do not claim legal compliance; provide controls and evidence for customer assessment.

## Retention/privacy
Support classifications, retention policies by tenant/legal entity/contract type, legal holds, masking/export restrictions, archive-before-delete, disposition approval and evidence exports. Ordinary users cannot hard-delete legal records.

## Audit and operations
Provide Audit Explorer, immutable event history, old/new values where safe, correlation IDs, actor/source/outcome, evidence export with hashes, health/readiness dashboards, monitoring interfaces, backup/restore and restore validation, upgrades/pre-flight/rollback, maintenance mode, support bundles with automatic redaction, DR documentation and configurable RPO/RTO placeholders.

## Accessibility/localisation
Target WCAG 2.2 AA design practices, keyboard access, focus management, screen-reader labels, accessible tables/designers, localisation resources, configurable date/currency/time zone and plain-language help.

## Testing
Create unit, integration, authorisation, tenant-isolation, migration, attachment matching, no-code builder, workflow, approval, import/export, package, concurrency, notification-idempotency, accessibility, backup/restore smoke, upgrade and security regression tests.

Critical migration tests:
- Dry run changes no target data
- Batch resume does not duplicate records or files
- Manifest matching is deterministic
- Duplicate/source-ID policies work
- Unmatched files remain visible and are not silently dropped
- Rollback removes only the selected batch's reversible changes
- Hash and provenance persist
- Cross-tenant migration is impossible
- Error and reconciliation totals equal processed input totals

## Documentation deliverables
Create installation guides for Docker/IIS/Azure, first-run and onboarding guides, no-code admin guides, migration runbook, attachment naming/manifest guide, cutover and reconciliation guides, API/integration docs, security/threat model, operations/backup/restore/upgrade, data dictionary, architecture diagrams, accessibility statement template, JBS solution-pack guide and production-readiness checklist.

## Definition of done
A clean demo installation completes setup, onboards an organisation, creates/publishes custom fields/forms/rules/workflows/approvals without code, imports synthetic contracts and attachments through dry run and committed migration, resolves unmatched files, enforces tenant and role security, processes Legal/approval lifecycle, locks Effective records, creates Variations, exports/imports a configuration pack, exposes reporting/API interfaces, produces audit evidence and passes automated tests across the implemented deployment artefacts.

Begin by inspecting the repository, creating implementation/domain/security plans and ADRs, then implement tested vertical slices. Do not stop after planning or scaffolding.
