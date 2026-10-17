# Role catalogue: should the persisted role identifiers change? (S5 design, no implementation)

Status: **Accepted by the owner (2026-10-17): Option A+.** Recorded as `decisions/ADR-0029-stable-role-and-permission-identifiers.md`. The design sections below (1 to 12) are kept as written when the document was Proposed; section 13 records the decision and what was built. Production approval remains external governance.

Scope: remediation slice S5 of `docs/PORTABILITY_REMEDIATION_PLAN.md` (R8 step 1, finding P-02b of `docs/PORTABILITY_AUDIT.md`). It decides whether S8 (R8 step 2, "implement the ADR") is still needed.

## 1. Summary of the recommendation

1. **Persisted role identifiers stay stable for the life of the product.** `ProcurementAdministrator`, `CategoryOwner`, `Legal`, `Approver`, `BackupApprover` (and the other seven) remain internal identifiers. `GroupHeadProcurement` is not a role at all: it is an approver-group key that lives in pack data and is already replaceable (the Blank pack uses `Approvers`).
2. **The business problem is names, and S4 already solved names.** A neutral organisation reads *Contract Administrator* for the same stable code that a JBS organisation reads as *Procurement Administrator*; administrator pages show the code beside the label; authority never reads a label.
3. **Renaming would not remove the identifier from the product.** Audit entries are append-only and hash-chained, published configuration releases are content-addressed, and customers' identity-provider mappings name the code. Every reader would have to accept the old code for ever, so a rename adds a second identifier instead of retiring the first.
4. **S8 as written (rename with alias) should be retired.** A different and much larger piece of work (data-driven role *definitions*) is the only thing that would give a business something terminology cannot, and nothing currently requires it. Record it as **Future**, to be designed only on a named business need.
5. A few **small, no-schema follow-ups** are worth doing in place of S8 (section 10). They are optional and can fold into S6 or S9.

## 2. Current architecture (what the repository does today)

### 2.1 The catalogue

* **Twelve role codes**, compiled in `src/Modules/Ecp.Identity/Domain/Roles.cs` (`RoleCodes.All`): System, Tenant, Organisation, Division/Site and Business Configuration administrators, `ProcurementAdministrator`, `CategoryOwner`, `Legal`, `Approver`, `BackupApprover`, `Auditor`, `Viewer`. They were fixed by owner decision D1 in `requirements/01-authoritative/CONFIRMED_DECISIONS.md`, with the matrix in `requirements/02-reference/role-permission-matrix.csv`.
* **The permission model** is that matrix: 82 permissions (`requirements/02-reference/permission-catalogue.csv`) by 12 role columns, with grants `Y` (all in scope) or `S` (scoped by a relationship rule). It is embedded in the Identity assembly, and `PermissionMatrix` refuses to load if a role code is missing from the header.
* **Behaviour attached to a code**, not just a name:

| Code | Behaviour compiled against it |
|---|---|
| `CategoryOwner` | Its `S` grants require *active category ownership* (`GrantRelationship.ActiveCategoryOwnership`); read-all permissions (`contracts.read`, `documents.download`, `suppliers.read`) are not narrowed by category, edits are (`AccessEvaluator.cs`). Ownership itself is a separate effective-dated assignment (`PeopleDirectory`). |
| `Approver`, `BackupApprover` | `S` grants require *assigned approval*; an approver group key is mandatory for these two and forbidden for every other role (`AssignmentRules.Validate`); `ApprovalParticipants` treats them as primary and backup. |
| `ProcurementAdministrator` | Operational administration of all contracts in an organisation (28 permissions). Used by name for `OrganisationAdministrators` notification recipients (`Processing.cs`), by the onboarding rule that warns when a whole identity group is mapped to a privileged administrator role, and by the delegation table (who may grant it). |
| `Legal` | Holders are the legal pool and assigned reviewers (`LegalReviews.cs`, `Processing.cs`). |
| `Viewer` | Viewer kinds and their scope rules (`AssignmentRules`). |
| Administrators | Scope levels each may be assigned at (`AllowedLevels`) and which roles each may delegate (`Grantable`, `AccessEvaluator`). |

Roughly 47 occurrences of the two procurement-flavoured codes appear in 18 source files, and 54 in the tests (29 test files use `RoleCodes`).

### 2.2 Where a code is persisted or travels

| Place | What is stored | Mutable? |
|---|---|---|
| `RoleAssignment.RoleCode` (50 characters) | One row per grant, per tenant, with scope and effective dates | Yes (data) |
| `GroupRoleMapping.RoleCode` (50 characters) | Identity-provider group to role mappings; customers configure them | Yes (data) |
| `ApproverGroupKey` on both tables (100 characters) | Free-form group key for Approver and Backup Approver | Yes (data) |
| Audit entries (`role.granted`, `role.revoked`, `rolemapping.created`, ...) | Role code in the structured payload | **No** (append-only, hash-chained; `ADR-0011`) |
| Configuration releases | Onboarding documents (mapping entries name a role code); published **custom-field definitions** (`VisibleToRoles`, `EditableByRoles`, validated against `RoleCodes.All`) | **No** (releases are immutable and hashed) |
| Packs | Notification policies name recipient *rules* (`CategoryOwners`, `LegalPool`, `OrganisationAdministrators`), not role codes; workflow definitions name approver *group keys* (JBS `GroupHeadProcurement`, Blank `Approvers`) | Signed, hashed, versioned |
| Requirements and docs | Matrix CSV header, permission catalogue, decisions, the sample demo users | Files |
| Customer-side | IdP group mappings, onboarding CSVs, support runbooks, any script that reads the audit export | Outside the repository |

### 2.3 What users see (after S4)

Role labels come from terminology (`TermCatalogue.KeyForRole`): `ProcurementAdministrator` is *Contract Administrator* by default and *Procurement Administrator* in the JBS pack; `CategoryOwner` is *Contract Owner* or *Category Owner*; `Legal` is *Reviewer* or *Legal*; `Approver` and `BackupApprover` are *Registration Approver* and *Backup Approver*. Home, user and role administration, the studio, simulation, onboarding review and the sign-in list use the label; administrator pages show the stable code in a `code` element. Tests prove the same role code shows different words in different organisations and that the pages reachable are identical.

### 2.4 What the portability criteria actually say

PRT-013: "Role names, state names and permission codes are never hard-coded in domain logic; they come from definitions and the catalogue." Test: "static scan plus test with renamed roles and states". Today the codes are constants in domain logic, and a "renamed roles" test is only possible at the label level, which S4 provides. The criterion can be read two ways: *names* (labels: met by S4) or *identifiers* (not met, and unmet by construction while behaviour is compiled against them). Decision 3 below asks the owner to settle which reading binds.

## 3. The questions, answered

### 3.1 Should persisted role identifiers stay stable for ever?

**Yes, with one stated exception path:** a code is never renamed or reused. If a genuinely new role is ever needed, it gets a new code; an obsolete one is retired by being unassignable, not renamed. Reasons: audit and releases cannot be rewritten; customers hold references the platform cannot see; a code is read by machines and administrators, not by ordinary users; and every rename needs a permanent alias, which is more identifier surface, not less.

### 3.2 Should `ProcurementAdministrator`, `CategoryOwner` and `GroupHeadProcurement` remain internal identifiers?

* **`ProcurementAdministrator`, `CategoryOwner`: yes.** They are the two codes with a business flavour. Both are visible only to administrators, beside a label that comes from terminology. Their meaning is stable and generic: *operational administrator of all contracts in an organisation* and *owner of a portfolio of contracts who edits what they own*. The word is the only procurement-specific part.
* **`GroupHeadProcurement`: it is not a platform identifier.** It is an approver-group key chosen by the JBS pack in its workflow definitions and typed into role assignments and mappings. The platform treats any non-blank string up to 100 characters as a group key. The Blank pack and the third synthetic test pack use other keys with no code change. It can stay in the JBS pack for ever. Note that the S4 brief listed `GroupHeadApprover`, which does not exist as an identifier; the persisted names are the role `Approver` and this group key (recorded in decision D7b).
* `Legal`, `Approver`, `BackupApprover` are neutral enough as codes and stay.

### 3.3 Impact of renaming them

Illustrative rename: `ProcurementAdministrator` to a neutral code and `CategoryOwner` to a neutral code, with the old codes accepted for ever.

| Area | Impact of a rename |
|---|---|
| **Permissions** | The matrix CSV header, `RoleCodes`, `PermissionMatrix`'s completeness check, the delegation table, the allowed scope levels and every `RelationshipOf` branch change. Permission *codes* (such as `categories.manage`, `suppliers.manage`, `legal.clear`) carry business words too; renaming the roles but not these leaves the vocabulary inconsistent, and renaming these is a second, larger change (policies, tests, handlers). No grant would change if done correctly; the risk is a typo that silently drops a grant. |
| **Role assignments** | An idempotent `UPDATE` of `RoleAssignment.RoleCode` per tenant under row-level security, inside the migration runner; effective dates and scopes unaffected. Mid-flight edits during rollout need an alias window. |
| **Group mappings and the identity provider** | `GroupRoleMapping.RoleCode` updates the same way, but the customer's own IdP-side configuration, mapping CSVs and runbooks name the old code. Either the old code must keep being accepted (permanent alias) or customers must edit their files at the cut-over. |
| **Approval groups** | None for the role rename (the group key is a separate string). The `Approver` / `BackupApprover` rule that requires a group key is unchanged. |
| **Onboarding** | Onboarding documents inside immutable releases name role codes in mapping entries. They cannot be edited in place; either the reader accepts both for ever or new releases are cut. The onboarding CSV header and validation (`OnboardingRules`) change. |
| **Audit** | Existing entries keep the old code and are never rewritten (append-only, hash-chained). Every audit reader, export and evidence report must understand both codes for ever. |
| **Packs** | No shipped pack names a role code in a document today, but `CustomField` pack content can (`VisibleToRoles`), as can published field definitions in tenant releases, which are content-hashed. A renamed code would invalidate either old content or the new validator, or demand a permanent alias in the parser. Pack versions and hashes would need regenerating wherever a role appears. |
| **Migrations** | One SQL Server migration touching two tables per tenant (and `Down` considerations), verified only in CI. The lightweight provider builds from the model, so it needs a separate data-fix path. A rename is forward-only in production. |
| **Exports** | Audit exports and any downstream analysis keyed on role code change mid-history. The configuration export/import that is still Future (PKM-01) would have to carry an alias table. |
| **Code and tests** | About 47 source occurrences in 18 files and 54 test occurrences; the architecture guard and the S4 stable-identifier tests are rewritten. |

The cost is real and entirely paid to rename a string that ordinary users never see.

### 3.4 Does terminology already solve the business problem?

**For naming, yes, completely.** A business that wants *Contract Administrator*, *Agreement Manager*, *Reviewer* or *Formal Review* gets them by publishing terminology or by installing a pack, with no code change (proved for the Blank pack, the JBS pack and the synthetic third pack). Labels appear on every page, message, notification and export header, and the code appears only next to the label on administrator pages. It does **not** solve:

1. *A different set of roles* (a business with no Legal function). Today the dropdown always offers all twelve roles. This is a hiding problem: S6 feature flags can hide a role the pack's capabilities do not use, with no rename.
2. *A different authority model* (a business whose "category owner" may also approve). That is not naming; it needs data-driven role definitions (option C) and is a security change, not a portability clean-up.
3. *A reviewer who reads source or a database and sees `ProcurementAdministrator`.* This is the aesthetic objection of the remediation plan; the guard (stage B) and the stable-identifier policy answer it.

### 3.5 Is S8 still justified?

**As written (implement a rename with aliases): no.** It would change security-critical data and code, add permanent alias handling, and deliver no capability that terminology does not already give. **A different S8 (data-driven roles) is not justified by any current requirement** and should stay Future until a named business needs it. Retire S8 now; keep the number only as the placeholder for that future work.

## 4. Options

### Option A: Stable identifiers, names from terminology (status quo made policy)

Record that the twelve role codes, the permission codes and the approver-group keys are stable platform identifiers; labels come from terminology (S4). Amend PRT-013's reading accordingly.

| Pros | Cons |
|---|---|
| No code, schema, data, audit or pack change; zero migration risk | Two codes keep a procurement-flavoured spelling in source, tests and the matrix CSV |
| Already implemented and tested by S4 | Reviewers must accept the policy ("a code is an identifier, not a name") |
| Easy to reverse (the decision can be revisited at no cost) | PRT-013's literal text ("never hard-coded in domain logic") remains unmet for identifiers |
| Keeps customer IdP mappings, audit exports and runbooks valid | A business with a different role set is not helped (section 3.4, points 1 and 2) |

### Option A+ (recommended): Option A plus three small, no-schema hardening items

Option A, plus: (1) a golden test that freezes the twelve codes and the permission codes so nobody renames them by accident; (2) optional display names for approver-group keys, supplied by the pack (an unlabelled key is shown raw today on the approvals and user pages); (3) the S6 flag work hides a role that the organisation's capabilities do not use. See section 10.

| Pros | Cons |
|---|---|
| All the pros of A | A little work (section 9) |
| Fixes the one place where a business-flavoured *key* is still shown raw (the approver group) | Item 2 needs a small pack-format addition (additive, optional, no migration) |
| Prevents drift | |

### Option B: Rename to neutral codes, keep the old codes as aliases (the plan's original S8)

| Pros | Cons |
|---|---|
| Source and matrix read neutrally; the literal PRT-013 text is easier to argue | Largest compatibility surface of any option (section 3.3) |
| Cheapest it will ever be, because no production install exists yet | The old code must be accepted for ever (audit, releases, IdP files, exports), so identifiers double |
| | Touches grants and every role check; a mistake silently changes authority |
| | Forward-only migration verifiable only on SQL Server in CI |
| | Does not help a business that needs different roles or authority |
| | Permission codes still carry business words unless also renamed (a second change) |

### Option C: Data-driven role catalogue (roles and their permission sets as tenant or pack data)

Roles become configuration: a code, a label, a permission list, a relationship rule chosen from a closed list, allowed scope levels, delegation rights. The twelve become the default catalogue.

| Pros | Cons |
|---|---|
| A business can define its own roles and drop unused ones; the strongest reading of PRT-013 | The highest-risk work in the product: it changes the authorisation core. A bad role definition is a privilege escalation |
| Packs could ship roles | Needs a safe, closed relationship vocabulary (category ownership, assigned approval, tenant-level definition, none, policy-authorised) so no code is ever configured |
| | Needs publish controls, simulation, impact analysis and rollback for role definitions, plus second-person and step-up rules, plus an evaluator rewrite and a complete access test re-proof on both providers |
| | No requirement asks for it today |

### Option D: A+ now, custom roles later (hybrid)

Adopt A+ and write down that, if a business needs a role the twelve cannot express, the answer is an **additive** custom-role capability built on option C's closed vocabulary, without renaming or removing any of the twelve. This keeps the door open at no cost. It is A+ plus a recorded intention; it needs no work now.

## 5. Migration impacts by option

| | A / A+ | B | C |
|---|---|---|---|
| Schema | None | None structurally, but a data migration of two tables per tenant | New tables for role definitions and permission lists, plus a seed migration of the twelve |
| Data rewrites | None | `RoleAssignment.RoleCode`, `GroupRoleMapping.RoleCode` | None if the twelve codes stay; otherwise as B |
| Immutable content | None | Old codes persist in audit and releases for ever | None |
| Provider verification | n/a | SQL Server migration test in CI only; lightweight needs a separate path | Both providers, plus a full re-proof of the access suite |
| Rollback | n/a | Restore the pre-upgrade backup; alias window | Restore backup; disable custom roles |

## 6. Compatibility impacts by option

| Item | A / A+ | B | C |
|---|---|---|---|
| Existing role assignments, mappings, scopes, effective dates | Unchanged | Unchanged in effect, rewritten in storage | Unchanged |
| Customer IdP mappings and import CSVs | Unchanged | Old spelling must keep working | Unchanged |
| Audit history | Unchanged | Two spellings for ever | Unchanged |
| Published releases and pack hashes | Unchanged | Two spellings, or re-cut content | Unchanged |
| Permission codes and the matrix | Unchanged | Header renamed, codes aliased | Matrix becomes data |
| Terminology (S4) | Unchanged | Role-to-key map re-pointed | Role label becomes part of the definition |
| Other slices | Free to proceed | S6 and S9 wait on the migration | S6 and S9 wait on a large rewrite |

## 7. Risks

| Risk | Option | Severity | Control |
|---|---|---|---|
| Reviewers read `ProcurementAdministrator` as hard-coding a business | A, A+ | Low | Policy in this ADR; guard allows the identifier; administrator pages show label and code together |
| PRT-013 literally unmet | A, A+ | Low to Medium | Owner amends or re-reads PRT-013 (decision 3) |
| Silent loss of a grant during a rename | B | **High** | Matrix and evaluator tests on both providers before and after |
| Audit and release readers diverge on old and new spellings | B | Medium | A permanent alias table; golden tests |
| Customer IdP mappings break at cut-over | B | Medium | Alias window; communication |
| Privilege escalation through a mis-defined role | C | **High** | Closed vocabulary, simulation, second person, step-up |
| Effort spent on S8 while pack feature flags (S6) and the second pack (S9) wait | B, C | Medium | Retire S8 |
| A future business really needs different authority | A, A+ | Medium, unproven | Option D keeps the additive path open |

## 8. Recommendation

**Adopt Option A+ (recorded with Option D's intention). Retire S8.** The product already gives a business its own names for every role; the codes cannot be retired by renaming because history and customers hold them; and the only thing a rename would buy is cosmetic. The effort is better spent on S6 (capabilities and hiding unused roles) and S9 (the second business end to end), which test portability with real data rather than spelling.

## 9. Estimated implementation complexity if approved

| Option | Size | Rough effort | Notes |
|---|---|---|---|
| A (policy only) | XS | Under 1 day | Accept the ADR; amend PRT-013's wording; update the plan, audit and status |
| **A+ (recommended)** | **S** | **About 1 to 2 days** | The golden test (hours); amend the requirement text; optional approver-group display names (about 1 day: an optional pack document section or terminology keys, a resolver call on two pages, tests); the role-hiding piece belongs to S6 |
| B (rename with aliases, the old S8) | L | About 8 to 12 working days | Source and tests (about 100 occurrences), two data migrations, parser aliases for releases, audit readers, matrix and importer, IdP-mapping compatibility, SQL Server CI verification, documentation. High regression risk |
| C (data-driven roles) | XL | Several weeks | Evaluator and matrix as data, authoring pages, simulation, publish controls, a complete access re-proof. Should be its own project |
| D (A+ and a recorded intention) | S | Same as A+ | No extra work now |

Estimates are for the code and tests; SQL Server verification is CI-only in this workspace.

## 10. Small follow-ups proposed instead of S8 (all optional, none touch the schema)

1. **Freeze the identifiers.** A unit or architecture test asserting `RoleCodes.All` equals the twelve codes and that the permission catalogue only grows. (Hours.)
2. **Approver-group display names.** A group key such as `GroupHeadProcurement` is shown raw on the approvals page and on user administration. Let a pack (or terminology) supply a display name per group key, optional and additive. The key stays the identifier. (About 1 day.)
3. **Hide unused roles** when a capability is off (S6), so a business without Legal does not see the Legal role in a dropdown. (Part of S6.)
4. **Neutral `Approver` label.** The neutral label for `Approver` is *Registration Approver*, but the role also approves dispositions where the matrix assigns it. Consider *Approver* as the neutral label and let packs narrow it. (A one-line change to the catalogue and pack data; a terminology wording decision for the owner.)
5. **Label, not code, in one message.** The onboarding warning about a privileged group mapping prints the raw role code; it could print the label with the code beside it. (An hour.)
6. **Document the closed vocabularies as stable too.** The notification recipient rules (`CategoryOwners`, `LegalPool`, `AssignedLegal`, `OrganisationAdministrators`) and permission codes follow the same policy: stable identifiers, labels from terminology.

## 11. Owner decisions required

| # | Decision | Recommendation |
|---|---|---|
| 1 | Persisted role codes, permission codes and approver-group keys are stable for the life of the product (never renamed or reused; a new need gets a new code)? | Yes |
| 2 | Choose the option: A, **A+**, B, C or D | A+ with D's recorded intention |
| 3 | PRT-013: confirm it is read as "names come from the catalogue and terminology", with the codes treated as stable platform identifiers? If the literal reading must bind, say so now, because that selects B or C | Amend the text of PRT-013 to say so |
| 4 | Retire S8 (rename with alias) and replace it with the follow-ups in section 10? | Yes |
| 5 | Is a different role *set* or a different *authority model* a known requirement for any business? If yes, name it, and option C becomes a planned project; if no, it stays Future | No known requirement; Future |
| 6 | Approve follow-up 2 (approver-group display names) and follow-up 4 (the neutral `Approver` label)? | Yes to 2; owner's wording choice for 4 |
| 7 | Confirm `GroupHeadProcurement` remains a JBS-pack approver-group key with no platform meaning | Yes |

## 12. What this document did not do

It did not change source, schema, packs, requirements or tests. It did not touch the permission matrix. It does not decide S6, S9 or S10. It does not declare production approval.

## 13. Decision and what was built (2026-10-17)

The owner approved **Option A+** with these decisions, which supersede the "owner decisions required" table in section 11:

| # | Decision |
|---|---|
| 1 | Stable role codes are permanent identifiers. |
| 2 | Stable permission codes are permanent identifiers. |
| 3 | Stable approver-group keys are permanent identifiers. |
| 4 | PRT-013 is satisfied by terminology and display-layer abstraction (the requirement text was amended; `requirements/08-portability/PORTABILITY_ACCEPTANCE_CRITERIA.md`). |
| 5 | **S8 (rename with aliases) is retired.** |
| 6 | S8 is recorded as **superseded by S4 (ADR-0028) and this ADR**. |
| 7 | The A+ follow-ups are implemented: frozen-identifier tests; approver-group display names; role-visibility support for S6. |
| 8 | No persisted identifier is renamed. |
| 9 | No alias migrations are created. |

**Built (no schema change, no migration, no new table):**

* **Frozen identifiers.** `tests/Ecp.UnitTests/FrozenIdentifierTests.cs` (10 tests) freezes the twelve role codes and their order, the matrix header, the 82 permission codes (a removal or rename fails; an addition must be added on purpose), the role-to-terminology mapping, the shipped approver-group keys, the free-text rule for a group key, the audit action codes for role changes, and the role columns' names and sizes. It also asserts that no label is accepted as a role code (no alias) and that no Identity migration rewrites role data.
* **Approver-group display names.** The terminology document gained an optional `groups` section (`key`, `locale`, `name`, optional `description`). The key is the stable identifier, validated as a key (letters, digits, dot, hyphen, underscore, at most 100 characters); the name is plain text of at most 80 characters. Resolution is organisation, then pack, then the key itself. `TermSet.Group(key)` and the page helpers `Terms.Group` (ordinary pages: the name) and `Terms.GroupLabel` (administrator pages: the name with the key beside it) are used on the approvals queue and detail, the approval matrix and simulator pages, delegations and user administration. The Edit page has an "Approver group names" section (listed groups plus three empty rows for groups not listed yet), and the Overview and Preview pages show them. The JBS pack (2.3.0) names `GroupHeadProcurement` *Group Head of Procurement*; the Blank pack (1.3.0) names `Approvers` *Registration Approvers*.
* **Role visibility (S6 support).** The same document gained an optional `roles` section (`code`, `offered`). Hiding a role removes it from the **role picker only**: nobody loses access, the role's holders are still listed, the trusted grant path and every permission check are unchanged, and the three administrative roles (`SystemAdministrator`, `TenantAdministrator`, `OrganisationAdministrator`) can never be hidden, so nobody can be locked out. The organisation's choice wins over the pack's; with neither, every role is offered. The user-administration picker offers a role when at least one organisation the grantor can delegate in offers it, and says which roles are not offered. When S6 adds capabilities, it can feed the same `TermSet.IsRoleOffered` decision (for example, a pack without Legal review hiding the Legal role); no further S5/S8 work is needed. No flag mechanism was built here.
* **Existing documents are unaffected.** The new sections are written only when present, so packs and releases without them serialise byte-for-byte as before; the older pack versions still load.

**Left as written in section 10:** follow-up 4 (the neutral `Approver` label) and follow-up 5 (the onboarding warning printing a raw role code) are small wording items for the owner and were not part of the decision.
