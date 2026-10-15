# Retention, Classification and Legal Hold Specification

Added by repository review (GAP-17, CON-05, CON-13). Tags: `[CONFIRMED]` / `[PROPOSED]`.

## 1. Confirmed position

- Keep indefinitely pending formal retention confirmation. `[CONFIRMED]` (Confirmed Decisions)
- Archive-before-delete. Ordinary users cannot hard-delete legal records. `[CONFIRMED]`
- Retention policies are configurable by tenant, legal entity and contract type; legal holds; masking and export restrictions; disposition approval; evidence exports. `[CONFIRMED]` (Master: Retention/privacy)

## 2. Classification `[PROPOSED]` levels, `[CONFIRMED]` need

| Level | Intended use | Default read | Export |
|---|---|---|---|
| Public | Approved for external release | All roles in scope | Allowed |
| Internal | Default for contracts | Roles with `contracts.read` | Allowed with `records.export` |
| Confidential | Commercially sensitive (pricing, rebates) | Roles with clearance Confidential or higher | Restricted; audited |
| Restricted | Legally privileged or board-level | Named users or groups only | Blocked unless explicitly allowed per tenant |

Levels, names and clearance mapping are configuration (a lookup plus a role-to-clearance map), shipped with these defaults in the JBS and Blank packs (Q-06).

- Classification applies to contracts, documents, suppliers (optional) and reports.
- Rules (the rules designer) may set classification. `[CONFIRMED]` (Master: "set classification")
- A user's clearance caps what they can see (`ACCESS_CONTROL_SPEC.md` section 3). Masked fields show a marker and are omitted from exports.
- Lowering a classification is a privileged, audited action; raising is allowed to record owners.

## 3. Retention policy model `[PROPOSED]`

A `RetentionPolicy` is a configuration definition (versioned like any other) with: scope (tenant, legal entity, contract type, document type), trigger date (end date, effective date, ended date, last activity), period (or `Indefinite`), action at expiry (`Review`, never automatic delete), approval route, and an optional exclusion for classification. Default policy: **Indefinite**, per the confirmed decision.

## 4. Disposition `[CONFIRMED]` principle

1. `Ended -> DisposedArchived` requires guard `RetentionChecksPass` and `NoLegalHold`, and permission `disposition.approve`.
2. **Archive** moves the record and its documents to a restricted, read-only archive state. Content and audit remain intact.
3. **Deletion** (only after archive, only after the retention period, only with legal-hold clear, and only with a second approval) is a separate action that removes content and replaces the record with a minimal tombstone (identifier, dates, disposition evidence). The platform never offers deletion in the default Indefinite policy.
4. Disposition evidence (who, when, policy, hold check results, hash of removed content) is exportable.

## 5. Legal hold `[CONFIRMED]`

- `legalhold.manage` places and releases holds on a contract, a supplier's contracts, or a query result; holds record reason, requester and scope.
- A hold blocks archive, deletion, disposition, and restricts export of unreviewed content as configured.
- Holds are audited; holds on records can be listed and exported.

## 6. Masking and export `[CONFIRMED]`

- Fields can be flagged sensitive in their definition. Sensitive values are masked for roles without `contracts.read` at sufficient clearance, redacted in audit values and excluded from AI prompts unless the tenant enables it.
- Exports (records, documents, reports) apply the same visibility rules as interactive reads, plus classification export restrictions. Export files neutralise spreadsheet formula injection (cells beginning `=`, `+`, `-`, `@` are prefixed). `[CONFIRMED]` (Master: injection controls)

## 7. Privacy

The platform provides controls and evidence for customer assessment and does not claim legal compliance. `[CONFIRMED]` (Master: "Do not claim legal compliance"). Personal information minimisation: audit address capture is configurable (full, truncated, none); user records hold only identity-provider claims required for operation. Erasure requests are handled as legal-hold-aware, documented processes outside automatic deletion; the audit hash chain is preserved by tombstoning identity references, not by editing events (ADR-0011).
