---
name: documentation-review
description: Check that documentation, ADRs, TASKS.md and requirements stay consistent with the code and with each other, links resolve, status labels are honest, and the Master documentation deliverables exist. Use at each milestone exit and before handover.
---

# Documentation Review

## Read first
`docs/DOCUMENT_INDEX.md`, `requirements/README.md`, `docs/IMPLEMENTATION_PLAN.md` section 8 (documentation schedule), Master "Documentation deliverables".

## Procedure
1. Run the repository consistency checker (links, backticked paths, CSV and JSON validity, ID cross-references, permission/role/guard/state consistency, manifest completeness). Fix every failure.
2. Compare `TASKS.md` with reality: a task is ticked only if code and passing tests exist. Verify the status labels (Implemented / Mocked / Disabled / Future / Scaffolded / Not Verified).
3. Check each ADR status and that every architecture change has an ADR. Check the threat model change log has an entry for the milestone.
4. Check requirement tags: `[CONFIRMED]` text traces to a decision record; `[OPEN]` items have a question ID; no file silently contradicts another. Record contradictions rather than guessing.
5. Check handover documents exist for the milestone (install guides, runbooks, admin guides) and exact commands in them still work.

## Output
Findings list (broken reference, stale statement, missing document), fixes applied, and open items.
