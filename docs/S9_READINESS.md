# S9 readiness: what must be fixed before the second business proof

Status: **Assessment only. Nothing in this document is implemented.** Written 2026-10-17 after the S9 design (`docs/SECOND_BUSINESS_PROOF.md`). It classifies the ten platform findings G1 to G10 of that design, adds what the re-review found, and recommends an order. It builds, changes and approves nothing and does not declare production approval.

## 1. Result at a glance

The design's ten findings were written from a first read. This pass re-read the code behind every one, and it found that **two of the ten matter much more than the design said, one is not a gap at all, and one important problem was missed.**

| Rank | ID | Finding (short) | Class | Effort | Risk if left | Risk of fixing |
|---|---|---|---|---|---|---|
| 1 | **G11 (new)** | Pack-time validation judges organisation-dependent documents with no organisation, so a legitimate pack can be refused at upload | **Must fix** | S to M | **High** | Low |
| 2 | **G8** | The five newer pack kinds (approval matrix, custom fields with lookups, list views, classification policy, branding) have never been installed, assigned and published from a pack | **Must fix** (as a verification spike, folded into G11) | S | **High** | Low |
| 3 | **G1a** | Country- and ERP-specific core labels (Vendor Number, ABN, SAP number) and the ABN message reach every business | **Must fix** | S | High for PRT-004 and PRT-013 | Low |
| 4 | **G6** | Locale fall-backs (R-03) may leak Australian defaults into a US organisation | **Should fix** (probe first) | S | Medium | Low |
| 5 | **G1b** | The other core labels (dates, risk level, function, owner, evergreen, extension type, notice period) are not terminology keys | **Should fix** | M | Medium (PRT-004 can only be claimed in part) | Medium |
| 6 | G2 | The ABN eleven-digit rule is in the domain | Can defer (S10), with a stated limit | M | Medium (a non-AU tax identifier cannot be stored) | Medium |
| 7 | G7 | Scale with no privileged level; "Legal Privileged" compiled | Can defer: **works today** if the pack sets its content rules explicitly | none | Low | none |
| 8 | G3 | One state per role; a state with no role gets no reminders | Can defer: avoided by a design amendment | none for S9 | Low | none |
| 9 | G5 | A locked contract has no way to record a progress fact | Can defer: avoided by the same amendment | none for S9 | Low | none |
| 10 | G9 | The approval matrix judges the resulting value of a change order | Can defer: document it | none | Low | none |
| 11 | G4 | Approval cannot precede signing | Can defer (Future): a documented limit | L, not for S9 | Low | n/a |
| 12 | G10 | "Reference prefix pattern" unconfirmed | **Closed**: not a gap | none | none | none |

**Recommendation in one paragraph.** Do three things before S9 starts: a one-day-scale **content-kind smoke test** (G8) and its fix (G11), the **G1a label change**, and four **decisions** that remove G3, G5, G7 and G10 by design. Do the **locale probe** (G6) at the same time because it is cheap and decides whether a fix is needed at all. Do **G1b** if the owner wants PRT-004 claimed in full; otherwise S9 proceeds and reports PRT-004 as partial. Everything else waits for S10 or later and is recorded as a limit of the proof. After that, S9 can start with no known blocker.

## 2. Method and classes

Every finding was re-checked in code, not in documents. "Verified" below means the file and the behaviour were read in this pass. Evidence is in section 11.

| Class | Meaning here |
|---|---|
| **Must fix before S9** | Without it the pack cannot be installed or an acceptance criterion would fail for a reason that is a platform defect, not a result |
| **Should fix before S9** | S9 can run without it, but one claim becomes partial or a probable defect will cost more once the pack exists |
| **Can defer until after S9** | Avoidable by design, harmless to the proof, or a limit that is stated rather than fixed |

Effort is relative, not days: **S** a few focused edits and tests, **M** a cross-cutting change with test updates, **L** a new capability and an ADR. Risk is split in two: the risk to the proof if the item is left, and the risk the change itself introduces.

## 3. The findings, one by one

### 3.1 G11 (new): pack-time validation judges organisation-dependent documents with no organisation

**Not in the design. Found while re-reading how a pack is validated.**

When a pack is uploaded, each document goes through its kind's own validator with an **empty organisation** (`src/Modules/Ecp.Configuration/Packs/PackValidation.cs`, `ValidateDocumentAsync`). Only one error code, `matrix_missing`, is treated as "depends on the organisation, so only a warning here" (`ContextDependent`). Every other error rejects the pack. Several validators depend on the organisation:

| Kind | Org-dependent rule | What a construction pack would do | Verified |
|---|---|---|---|
| ApprovalMatrix | `fact_unavailable` for a condition on a custom field, because no custom field is published for the empty organisation (`src/Modules/Ecp.Approvals/Definitions/MatrixKind.cs`) | The design's matrix row "a custom field marked usable in approvals" is **refused at upload** | read |
| ContractListView | `column_unavailable`, `filter_unavailable`, `sort_unavailable` for a column or filter on a custom field (`src/Modules/Ecp.Contracts/Register/Views.cs`) | The design's "Insurance expiring" view, which filters on a custom date, is **refused at upload** | read |
| CustomField | The validator is given the organisation's published workflow states to check visible-in and editable-in state lists against | A field restricted to named states can fail until the lifecycle is published; that is a publication-order cycle (section 3.2). The exact effect with an empty organisation is **to verify in the spike** | context read, effect not run |
| ContractListView | `DefinitionCatalogueAsync` requires a tenant-wide configuration grant and throws a not-found error otherwise (`src/Modules/Ecp.Contracts/Register/RegisterEngine.cs`); the pack validator catches only domain, JSON, invalid-operation and forbidden errors | An uploader without a tenant-wide grant could get an unhandled error instead of a report. **To verify in the spike** | read, not run |

This means the two features the second business exists to show, a matrix with custom-field conditions and views over custom fields, would fail at the first step. The shipped packs never hit it because they carry none of these kinds.

**Class: Must fix.** **Effort: S to M.** **Risk if left: High** (the pack cannot install, or the design must give up its strongest features). **Risk of fixing: Low**, provided the change is narrow.

Options:

| Option | What | Verdict |
|---|---|---|
| A. Design around it | The pack carries only core-field conditions and views; custom-field matrix rows and views are added by the administrator after assignment | Works, but the proof then shows less: the pack no longer carries the whole business |
| **B. Generic fix (recommended)** | Extend the context-dependent list to the specific organisation-dependent codes (`fact_unavailable` for custom keys, `column_unavailable`, `filter_unavailable`, `sort_unavailable`, the state-list codes) so they are warnings at upload and errors when the draft is validated for a real organisation, exactly as `matrix_missing` already is; and catch not-found in the pack validator | Small, generic, uses an existing mechanism, and keeps real errors as errors. Needs a unit test per code and a test that the same document is refused for a real organisation that lacks the field |
| C. Validate against the pack's own fields | Validate the pack as a whole, so a view may use the pack's fields | Correct in principle, large, and a new design; Future |

**Recommendation: B**, and keep the narrowness checkable: a test asserts that a malformed document (unknown JSON shape, a bad level) is still refused at upload.

### 3.2 G8: the five newer pack kinds have never run from a pack

The design said "the proof drives the publication order". The re-review shows that is not enough, because **nobody has ever done it**: no shipped pack carries an approval matrix, custom fields and lookups, a classification policy, list views or branding (`docs/PORTABILITY_AUDIT.md` section 0.2). Each is a place the first run can fail, and G11 shows one already does.

The dependencies between the kinds, as the validators read them:

```
classification policy ──► custom fields ──► approval matrix (custom conditions) ──► lifecycle, change-order workflow
                              │                                                          ▲
                              ├──► list views (custom columns and filters)               │
                              └──► (only if a field lists states) ── needs the published lifecycle ──┘   cycle
notification policy ──► classification policy (its levels)
terminology, capabilities, branding: no dependencies
```

The cycle appears only if a custom field restricts itself to named lifecycle states. The pack should not do that; the design should say so.

**Class: Must fix, as verification.** **Effort: S** for the spike, plus whatever it finds. **Risk if left: High.** **Risk of the spike: Low.**

What to do: build, at test time and not in the repository, a throwaway pack with one small but representative document per packable kind (including a matrix with a custom-field condition, a view over a custom field, a four-level classification with no privileged level, and branding). Install it through the Pack Manager, assign it to an organisation, validate and publish every draft in the dependency order, and record each failure. This is the single best use of the time before S9: it turns the largest unknown into a list.

### 3.3 G1: core field labels are literal (split into G1a and G1b)

Re-reading `CoreFields.All` and the pages, the finding is really two findings with different urgency.

**G1a: country- and ERP-specific labels. Must fix. Effort S.**

| Where | What the second business sees today | Verified |
|---|---|---|
| `CoreFields.All` | "Vendor Number", "ABN", "SAP Outline [[agreement]] Number" | read |
| Supplier create, list and details pages; the contract details and form | "ABN", "Vendor number", "SAP outline agreement number", "Eleven digits. The format is checked" | read |
| The domain message | "ABN must be eleven digits." | read |

These are not neutral platform words. They name an Australian identifier and an ERP product, and a US contractor would see them on the supplier form and in register columns and exports. They fail the plain meaning of PRT-013 (no business-specific wording shown to people from compiled code) regardless of how PRT-004 is read.

Change (generic, small): three new terminology keys with neutral English defaults ("Tax identifier", "Vendor number", "ERP reference"), used by the core fields, the pages and the message; the reference pack's terminology sets them to today's words ("ABN", "Vendor number", "SAP outline agreement number") so the JBS organisation sees no change; the reference pack's version rises. The new keys are optional, so existing packs stay valid. The 42-key count, the required-key tests and `docs/TERMINOLOGY.md` change to match.

Risk of fixing: **Low**, with one known cost: tests that assert "ABN" on neutral pages must assert it through the reference organisation. Risk if left: **High** for PRT-004 and PRT-013.

**G1b: the other core labels. Should fix. Effort M.**

About 33 label literals in pages and 15 more in `CoreFields.All`: Start, Effective, Review and End date, Risk level, Function, Owner, Evergreen, Extension type, Notice period, Critical Notice, Currency. Most are ordinary platform words, not business words, so the **proof can pass without this**: the sweep in SBP-AC-08 is defined over the keys the pack sets and over business-specific words. What is lost is that a business cannot rename "End Date" to "Scheduled Completion Date" and PRT-004's wording ("every UI label") can only be claimed in part.

Recommendation: do it if the owner wants PRT-004 claimed in full at S9; it is mostly mechanical (a neutral default equals today's English, so nothing changes until a pack overrides it) and is protected by the page-literal guard. If it is not done, S9 reports PRT-004 as **partial**, names the labels, and does not tick it.

Risk of fixing: **Medium** because it touches many pages. Mitigation: neutral defaults equal today's text; the render sweep and guard stage B catch a slip.

### 3.4 G6: locale fall-backs (R-03) and the Evergreen literal (R-04)

Re-read: the per-organisation locale exists and is used; the fall-backs are in six named places that the guard already lists (`src/Ecp.SharedKernel/Localisation/LocaleFormatter.cs`, `src/Ecp.Host/Pages/OrgLocale.cs`, `src/Ecp.Host/Pages/Migration/MigrationPageModel.cs`, `src/Modules/Ecp.Contracts/CustomFields.cs`, the globalisation probe and `src/Ecp.Host/appsettings.json`). Whether any of them is reached for a US organisation is **unknown until a page is rendered**. The Evergreen literal (`src/Modules/Ecp.Notifications/Reminders.cs`) affects only a business whose extension type is literally "Evergreen"; the construction pack has no such type, so it is harmless to S9.

**Class: Should fix, by probe first.** **Effort: S** for the probe; the fix, if any, is S per leak. **Risk if left: Medium** (SBP-AC-13 fails late and in the middle of the pack work). **Risk of the probe: none.**

The probe: create an organisation with en-US, USD, America/Chicago, two sites in two zones, a few contracts and a reminder, then render about twenty pages and run the exports, scanning for `AUD`, `Brisbane`, `en-AU`, day-first dates and Australian spelling. If clean, close G6 for S9. R-04 stays with S10.

### 3.5 G2: the ABN eleven-digit rule

`Supplier` normalisation refuses a value that is not eleven digits (`src/Modules/Ecp.Contracts/Domain/Contracts.cs`, `NormaliseAbn`). It runs only when a value is given. The proof can leave the field empty; the real defect is that **a US tax identifier cannot be stored**.

The right fix is the one the plan already chose: make it a rule that a pack or organisation supplies (S10, R-01), not move the column. That needs a new place to keep a per-organisation format rule, which is more than S9 should carry.

**Class: Can defer.** **Effort: M.** **Risk if left: Medium**, but contained: G1a removes the vocabulary leak, and the S9 report states the limit. **Risk of fixing now: Medium** (domain change, a new configuration surface, migration tests).

Required with the deferral: the S9 report says in one sentence that the tax identifier field accepts only the eleven-digit form until S10, and no S9 scenario enters one.

### 3.6 G7: a scale with no privileged level

Re-read and **corrected**. The design called this untested and a probable defect. The code shows the scale handles it: a privileged level is optional, and a role grant of privileged access without one is refused with a clear error (`src/Ecp.SharedKernel/DomainErrors.cs`, `ClassificationScale`; `src/Modules/Ecp.Identity/Domain/RoleAssignment.cs`). The one trap is in the notification policy: `withholdEverythingFromLevel` **defaults to "Legal Privileged"** when absent, and the policy validator then rejects it because that level is not in the scale (`src/Modules/Ecp.Notifications/Policy/NotificationPolicy.cs`). Both shipped packs state the value, so the trap has never been hit.

**Class: Can defer.** The pack sets both content levels explicitly (title withheld from Restricted, everything withheld from Restricted). **Effort: none.** **Risk: Low.** The S9 unit test for the pack asserts the policy validates against the pack's own scale. A generic fix (derive the default from the scale) is a small tidy-up for S10.

### 3.7 G3 and G5: one state per role, and no way to record a progress fact on a locked contract

Both verified (`role_duplicate` in `src/Modules/Ecp.Lifecycle/Definitions/WorkflowDefinition.cs`; custom values on a protected contract are refused in `src/Modules/Ecp.Contracts/CustomFields.cs`). The design worked around them with an extra state that has no role and an administrative change order. **A simpler amendment removes both problems:**

> Drop the Defects Liability Period state. Practical completion is recorded by an **administrative change order** that sets the completion date field and moves End Date to the end of the defects period. The contract stays in its one live state, so reminders continue through the defects period, and Final Completion closes it.

This keeps every contract inside the platform's immutability rule (change only by an authorised Variation), needs no unroled state and no platform change, and makes the Variations capability do real work. The cost: a change order carries the contract's full resulting value for approval (G9), so a date-only change order lands in the contract's own value tier. That is acceptable and is worth showing.

**Class: Can defer** (both). **Effort: none for S9.** **Risk: Low.** The generic fixes (a live flag on a state; an "editable while locked" field flag) are Future and need their own ADRs.

### 3.8 G9: the matrix judges the resulting value

Verified in `src/Modules/Ecp.Lifecycle/ApprovalPlanning.cs`: a change order is judged on the contract "as it would be once applied", using the proposed value when one is given and the current value otherwise. **There is no "amount of change" fact.** The matrix tiers in the design are on resulting value, as the design said.

**Class: Can defer.** **Effort: none.** **Risk: Low.** Document it in the pack guide. A delta fact is a generic addition for later if a business needs it.

### 3.9 G4: approval cannot precede signing

Verified: `executed_copy_guard` and `approve_must_lock` in the lifecycle validator. This is a property of the register-authorisation capability, shared with the reference configuration. The construction pack authorises after signing.

**Class: Can defer** (Future, **L**, needs an ADR). **Risk: Low for S9**, **Medium for credibility** with a construction reader. State it in the pack guide.

### 3.10 G10: the numbering rule

**Closed.** The design left this to be confirmed. It is: an organisation has a numbering rule of **prefix, separator and digit count** (`src/Modules/Ecp.Tenancy/Domain/Tenancy.cs`, `ReferenceNumberingRule`), with the prefix 2 to 16 upper-case letters or digits. The construction organisation uses a short prefix and the default form. No pattern beyond this exists and none is needed. Nothing to fix.

## 4. Corrections to the S9 design

These follow from sections 3.1 to 3.10 and should be applied to `docs/SECOND_BUSINESS_PROOF.md` when the owner accepts this assessment. They are recorded here so the design is not silently changed.

| # | Correction |
|---|---|
| 1 | Add G11 to section 7, and note that matrix conditions on custom fields and views over custom fields only upload once G11 is fixed |
| 2 | Replace the Defects Liability Period state with the administrative change order (3.7); the lifecycle has about nine states |
| 3 | The notification policy states both content levels explicitly (3.6); G7 is no longer a risk |
| 4 | G10 is closed |
| 5 | The pack must not restrict a custom field to named lifecycle states (3.2) |
| 6 | SBP-AC-08 is worded over the keys the pack sets and business-specific words; PRT-004 is ticked in full only if G1b is done (3.3) |
| 7 | A new acceptance criterion: the content-kind smoke test passes before pack authoring starts (it becomes the entry gate, section 6) |

## 5. Ranking and why

Ranked by what each one can do to the proof, weighted by how likely it is, divided by how much it costs:

1. **G11** fails the upload of the pack's most distinctive content. Likely (it is certain for custom-field conditions and views), cheap to fix.
2. **G8** is the unknown that contains G11 and any sibling. Cheap to find, expensive to find late.
3. **G1a** makes a non-JBS business show Australian and ERP wording on its supplier form. Certain, cheap.
4. **G6** may or may not exist. Cheap to find out.
5. **G1b** limits a claim, not the run.
6. to 12. Everything else is avoidable, stated, or closed.

## 6. Recommendation and the entry gate for S9

S9 may start when **all** of these hold:

| # | Entry condition | Evidence |
|---|---|---|
| E1 | The content-kind smoke test (G8) passes on SQLite and SQL Server: a throwaway pack with one representative document per packable kind installs, assigns, validates and publishes in dependency order | A test in the repository, green in CI |
| E2 | G11 is fixed, or the owner has accepted option A (design around) in writing | Fix with tests, or a recorded decision |
| E3 | G1a is done: no Australian or ERP wording appears on any page of a neutral or non-JBS organisation; the reference organisation's pages are unchanged | Render tests; reference pack version raised |
| E4 | The locale probe (G6) has run, and every leak it found is fixed or recorded | Probe result in `docs/PORTABILITY_AUDIT.md` |
| E5 | Decisions D1 to D4 below are recorded | `requirements/01-authoritative/CONFIRMED_DECISIONS.md` |
| E6 | The design corrections of section 4 are applied | Edited `docs/SECOND_BUSINESS_PROOF.md` |
| E7 (if PRT-004 is to be claimed in full) | G1b is done | Render sweep and guard |

### Decisions to record (D1 to D4)

| # | Decision | Recommendation |
|---|---|---|
| D1 | Completion facts are recorded by an administrative change order; no role-less state | Yes (resolves G3 and G5 for S9) |
| D2 | G11 is fixed by option B, not designed around | Yes |
| D3 | G1b is in or out of the pre-S9 scope | In, if PRT-004 is to be ticked at S9; otherwise out, with PRT-004 reported as partial |
| D4 | G2 is deferred to S10 with the stated limit | Yes |

## 7. Implementation order

Each step ends with tests green on SQLite locally and on SQL Server in CI. Steps in the same row can run in parallel.

| Step | Work | Items | Size | Depends on | Exit |
|---|---|---|---|---|---|
| **R0** | Record D1 to D4; apply the design corrections | decisions, section 4 | S | none | E5, E6 |
| **R1** | Content-kind smoke test (throwaway pack, one document per kind) and its findings list | G8 | S | none | A test that currently fails where G11 bites |
| **R2** | Fix the findings of R1, starting with G11 (option B) and the not-found handling; add the narrowness tests | G11 | S to M | R1 | E1, E2 |
| **R3** (parallel with R1, R2) | Three terminology keys; core fields, pages and message use them; reference pack terminology and version; tests, docs, term counts | G1a | S | none | E3 |
| **R4** (parallel) | Locale probe; fix what it finds | G6 | S (+ S per leak) | none | E4 |
| **R5** (optional, after R3) | Terminology keys for the remaining core labels; pages and `CoreFields` use them; reference pack terminology; guard and render tests | G1b | M | R3 | E7 |
| **Gate** | Review E1 to E7 | | | R0 to R5 | S9-1 may start |

Why this order: R1 first because it is the cheapest way to find unknowns, and everything after it can be sized on what it finds. R3 and R4 are independent of R1 and R2, so they run beside them. R5 is last because it is the broadest and the only one that is optional.

## 8. Effort

Relative sizes, not days. The point is the shape: the pre-S9 work is one small spike, one small generic fix, one small label change and one probe, plus an optional medium change.

| Step | Size | What dominates the effort |
|---|---|---|
| R0 | S | Writing, not code |
| R1 | S | One test that builds a pack with ten small documents and drives the Pack Manager |
| R2 | S to M | Depends on R1: the context-dependent list is a few lines; the tests and any sibling findings are the work |
| R3 | S | Three keys, about ten call sites, one pack terminology edit, test and document updates |
| R4 | S | One scenario and a scan; fixes are per leak |
| R5 | M | About 48 labels and their call sites, new keys, tests |
| **Total (R0 to R4)** | **M** | |
| **Total with R5** | **M to L** | |

For comparison, S9 itself is M to L (`docs/SECOND_BUSINESS_PROOF.md` section 13). The readiness work is smaller than the proof and removes its two largest sources of surprise.

## 9. Risk

### 9.1 Risk of the readiness work

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | The smoke test finds more than G11 (for example a kind whose draft cannot be created from a pack at all) | **High** | Medium | That is its purpose; each finding is sized before S9 is planned. Keep the smoke test in the repository as a regression test |
| 2 | Option B for G11 downgrades a real error | Low | Medium | The change names specific codes only; a test proves a malformed document is still refused at upload and the same document is refused for a real organisation |
| 3 | R3 changes what the JBS organisation sees | Low | **High** | The reference terminology sets today's words; render tests for the reference organisation run before and after; the reference pack's version rises |
| 4 | R3 and R5 break tests that assert literal labels | Medium | Low | Expected; update to assert through terminology |
| 5 | R5 grows (more labels than counted) | Medium | Medium | It is optional; stop at the page set the sweep needs and report PRT-004 as partial |
| 6 | The locale probe finds many leaks | Low to medium | Medium | Fix only leaks reachable from a US organisation; leave the rest to S10 with a record |
| 7 | Reference pack version bump invalidates pack-version assertions in tests | High | Low | Known pattern from S5 and S6; update the assertions |
| 8 | The work expands into S10 (the ABN rule, the Evergreen literal) | Medium | Medium | D4 and the deferral list in section 10 are explicit |

### 9.2 Risk to the proof if the items are left

| If this is left | Consequence |
|---|---|
| G11 | The pack cannot be uploaded with its distinctive content, or the design silently loses its strongest features |
| G8 | The first real run of five untested kinds happens in the middle of S9, with no list of unknowns |
| G1a | A US contractor sees "ABN" and "SAP"; PRT-013 fails on plain reading |
| G6 | SBP-AC-13 fails late |
| G1b | PRT-004 is reported partial |

## 10. Deferred, with owners

| ID | Deferred to | The limit stated in the S9 report |
|---|---|---|
| G2 | S10 (R-01: make the format a rule) | The tax identifier field accepts only the eleven-digit form |
| G3 | Future (a live flag on a state) | None for S9, because the pack uses one live state |
| G4 | Future (a pre-signature approval capability, ADR) | Approval follows signing, as in the reference configuration |
| G5 | Future (an "editable while locked" field flag, ADR) | Progress facts are recorded by change order |
| G7 | S10 tidy-up (derive the default content level from the scale) | None; the pack sets both levels |
| G9 | Future (a "change in value" approval fact) | Change-order tiers use the resulting value |
| R-04 | S10 | Harmless to S9 |

## 11. Evidence

| Claim | Where it was read |
|---|---|
| Only `matrix_missing` is context-dependent at pack time | `src/Modules/Ecp.Configuration/Packs/PackValidation.cs`, `ContextDependent` and `ValidateDocumentAsync` |
| Matrix custom conditions need a published, usable custom field | `src/Modules/Ecp.Approvals/Definitions/MatrixKind.cs` |
| Views are validated against the organisation's definition catalogue | `src/Modules/Ecp.Contracts/Register/Views.cs`, `src/Modules/Ecp.Contracts/Register/RegisterEngine.cs` |
| Custom field state lists are checked against the organisation's published states | `src/Modules/Ecp.Metadata/MetadataApi.cs` |
| Shipped packs carry only five documents | `templates/packs/blank`, `templates/packs/jbs-procurement` |
| Core labels and call sites | `src/Modules/Ecp.Metadata/FieldModel.cs`, `src/Ecp.Host/Pages/Suppliers/Create.cshtml`, `src/Ecp.Host/Pages/Contracts/Details.cshtml`, `src/Ecp.Host/Pages/Shared/_ContractFields.cshtml` |
| ABN rule | `src/Modules/Ecp.Contracts/Domain/Contracts.cs` |
| Scale handles no privileged level; policy default trap | `src/Ecp.SharedKernel/DomainErrors.cs`, `src/Modules/Ecp.Notifications/Policy/NotificationPolicy.cs` |
| One state per role; approval guards; unlock rule | `src/Modules/Ecp.Lifecycle/Definitions/WorkflowDefinition.cs` |
| Locked contracts refuse custom value edits | `src/Modules/Ecp.Contracts/CustomFields.cs` |
| Change order judged on resulting value | `src/Modules/Ecp.Lifecycle/ApprovalPlanning.cs` |
| Numbering rule | `src/Modules/Ecp.Tenancy/Domain/Tenancy.cs` |
| Reminders use the live roles | `src/Modules/Ecp.Notifications/Reminders.cs` |

Not run in this pass: any test or probe. Statements marked "to verify in the spike" or "unknown until a page is rendered" are exactly the ones R1 and R4 are for.

## 12. What this assessment does not claim

* It does not claim that G11 or any other finding is fixed, or that S9 is ready: it lists what has to be true first.
* It does not claim the effort sizes are estimates in days.
* It does not claim the smoke test will find only G11. It is expected to find more, and that is its value.
* It changes no code, pack, test or platform behaviour, and it does not declare production approval.
