---
name: accessibility-review
description: Review pages, wizards and designers against WCAG 2.2 AA design practices and the project accessibility and localisation requirements, with automated axe checks and keyboard walkthroughs. Use after any UI change, new page type, wizard or designer.
---

# Accessibility Review

## Read first
`requirements/06-design/ACCESSIBILITY_AND_LOCALISATION.md` (A-01 to A-12, L-01 to L-08), `docs/TESTING.md` section 11 (A11Y-001 to A11Y-020), ADR-0013 (in `decisions/`).

## Procedure
1. Enumerate page types from the route list; run axe-core on each (A11Y-001). Serious or critical violations are findings.
2. Keyboard-only walkthrough of: setup wizard, onboarding, create and edit contract, workflow transitions, approvals, migration wizard (A11Y-002). Verify visible focus, logical order, no traps.
3. Check dialogs, error summaries, tables, live regions, skip links, landmarks, titles, zoom and reflow at 320 CSS px, reduced motion, target sizes, contrast including branding choices.
4. Check designers have move up/down/into controls and an outline equivalent (A11Y-008).
5. Check localisation: resource strings (no concatenation), en-AU defaults, time zone and currency display, terminology aliases keep accessible names meaningful (A11Y-014).

## Rules
Automated checks do not prove accessibility: record manual checks separately and label anything not manually checked as **Not verified**. Do not claim certification.

## Output
Per-page-type table (axe result, keyboard result, notes) and findings by severity.
