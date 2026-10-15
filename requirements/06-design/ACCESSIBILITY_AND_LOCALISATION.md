# Accessibility and Localisation Specification

Added by repository review (GAP-18). Source requirement: Master "Accessibility/localisation": WCAG 2.2 AA design practices, keyboard access, focus management, screen-reader labels, accessible tables/designers, localisation resources, configurable date/currency/time zone and plain-language help. `[CONFIRMED]` The detail below is `[CONFIRMED]`. The platform does not claim certification; it provides design practices and evidence.

## 1. Accessibility requirements

| ID | Requirement |
|---|---|
| A-01 | All functionality is operable by keyboard alone, with a visible focus indicator and a logical focus order |
| A-02 | Skip link on every page; one `h1` per page; landmark regions; page `title` reflects the page and context |
| A-03 | Every form control has a programmatic label; errors are associated with fields, summarised at the top with focus moved to the summary, and described in plain language |
| A-04 | Colour is never the only indicator; text contrast meets WCAG 2.2 AA; target size meets the 2.2 minimum; no content depends on hover |
| A-05 | Data tables use proper table semantics (caption, header cells with scope), support sort and filter announcements, and offer a non-paginated export |
| A-06 | Status changes (save, publish, import progress, background job state) are announced with live regions; long operations show progress without relying on motion |
| A-07 | Dialogs trap and restore focus and are dismissible with Escape |
| A-08 | **Designers** (form, workflow, approval matrix, rules) provide full keyboard operation and explicit "Move up / Move down / Move into section" controls in addition to drag and drop; the visual canvas has a structured text equivalent (outline tree) for screen readers |
| A-09 | The setup and onboarding wizards are resumable, show progress as text ("Step 5 of 18"), keep state on back and forward, and never rely on timeouts that cannot be extended |
| A-10 | Help text is plain language and available in context; each wizard step links to a help topic |
| A-11 | Reduced-motion and 200% / 400% zoom reflow are respected; no horizontal scrolling at 320 CSS px except for data tables |
| A-12 | Business-admin-supplied content (labels, help text, terminology) is plain text or a restricted safe markup; the designers warn on contrast problems in branding colour choices and refuse choices below AA |

Evidence: automated axe-core checks on every page type in CI; manual keyboard and screen-reader checklists per milestone; an accessibility statement template in `docs/` (task DOCS-A11Y). Test IDs A11Y-001 to A11Y-020 in `docs/TESTING.md`.

## 2. Localisation requirements

| ID | Requirement |
|---|---|
| L-01 | All user-visible strings come from resource files keyed by stable codes; no concatenated sentences. Default culture `en-AU`; the JBS reference pack uses Australian English spelling and terminology |
| L-02 | Per-organisation culture, time zone, default currency, date and number formats; per-user display preferences may override within what the organisation permits |
| L-03 | Instants are stored in UTC and displayed in the configured time zone; date-only values are displayed as dates without conversion (CON-15) |
| L-04 | Currency amounts always show the currency code or symbol; mixed-currency totals are never summed without explicit conversion rules |
| L-05 | Terminology aliases (for example "Contract" shown as "Agreement") apply to labels, notifications, exports and help; stable codes are used in APIs and packages |
| L-06 | Container images ship with ICU and time-zone data (no invariant-globalisation mode) so cultures and zones behave identically on Docker, IIS and Azure (RISK-13) |
| L-07 | Right-to-left and additional languages are future work; resource structure and layout use logical CSS properties so they can be added without redesign |
| L-08 | Notification templates are localised per organisation; fallback to the default culture is explicit and logged |
