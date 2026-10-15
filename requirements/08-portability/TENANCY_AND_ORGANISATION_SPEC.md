# Tenancy and Organisation Specification

Resolves CON-07 and GAP-25. Terms are defined in `requirements/02-reference/GLOSSARY.md`. `[CONFIRMED]` principle (Master: "Tenant isolation must use TenantId on owned entities, query filters, service policies, database constraints and cross-tenant tests"), `[CONFIRMED]` detail.

## 1. Model

```
Installation
 └── Tenant (hard isolation boundary)
      ├── Organisation (policy scope)
      │    ├── Legal entities
      │    ├── Divisions ── Business units
      │    ├── Sites
      │    └── Categories, suppliers, contracts, documents, configuration scope
      └── Tenant-level: identity provider, users, tenant administrators, tenant configuration defaults, integrations
```

## 2. Modes

| Mode | Tenants | Organisations | Notes |
|---|---|---|---|
| Single organisation | 1 (fixed id created by setup) | 1 | Tenant plumbing remains active but invisible to users |
| Multiple organisations | 1 | N | Organisations share identity provider and tenant administrators; data scope and configuration are per organisation |
| Multi-tenant hosting | N | N per tenant | Tenants are strangers to each other. Created only by System Administrator. Each tenant has its own identity provider configuration, administrators, adapters and configuration |

Switching mode after installation is only supported upward (single organisation, then multiple organisations, then multi-tenant) because the tenant and organisation ids already exist from the start (ASM-05).

## 3. Tenant resolution

- Authenticated requests: tenant comes from the verified identity (issuer + subject membership) or service-principal registration. `[CONFIRMED]`
- Unauthenticated entry points (sign-in page, health, callbacks): tenant resolved from a configured host-name mapping or a per-tenant callback path secret, never from a client-supplied header or query value alone. Unknown hosts get a generic response.
- A request with an identity valid for tenant A can never be switched to tenant B by changing a header, token claim copied from another system, route value or body field.
- Background jobs and integration callbacks run inside an **explicit tenant scope** established from the job record; code that reads tenant data without a scope fails closed.

## 4. Isolation surfaces (each has a test; see `docs/TESTING.md` section 5)

Database rows (query filters + Row-Level Security + constraints), blob storage keys and listings, caches (tenant in every key), background jobs and the outbox, search and autocomplete, reports and reporting views, exports and package builds, imports and migration staging, notifications and email content, webhooks and integration configuration, audit queries, logs and support bundles, configuration and packages, and APIs including error messages.

## 5. Organisation scope rules

1. Every organisation-scoped row carries `OrganisationId`; policies check membership in addition to the tenant. `[CONFIRMED]`
2. Cross-organisation access exists only where a role is granted at tenant level for that permission (for example Auditor at tenant scope). The default for contract roles is organisation scope.
3. Configuration can be defined at tenant level (defaults) and overridden per organisation; resolution is organisation first, then tenant, then pack default. `[CONFIRMED]`
4. Suppliers, categories and contracts are organisation-scoped. Sharing suppliers across organisations is a future option.
5. **Agreement Reference is unique across the tenant** (`(TenantId, AgreementReference)`). Each organisation configures a reference prefix and numbering rule so organisations in one tenant cannot collide; numbers are allocated transactionally and never reused. Organisation remains a mandatory policy and reporting scope on every contract. `[CONFIRMED]` D3

## 6. Installation-level data

Setup state, platform signing keys, installation administrators, migration history and a reserved "system tenant" id for installation-level audit events live outside any customer tenant and are never returned by tenant-scoped queries.

## 7. Offboarding

Tenant offboarding is an audited export followed by archive; hard deletion follows the retention policy and legal hold rules (`RETENTION_AND_CLASSIFICATION_SPEC.md`), never a single administrative click.
