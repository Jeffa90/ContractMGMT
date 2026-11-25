# JBS Contracts: design system

Status: agreed in planning, 2026-11-25. Taken from the care platform style guide (`docs/redesign/STYLE_GUIDE.md`), with the JBS brand colours and logo supplied by Jeff. Every contrast figure below was calculated (WCAG 2.2 relative luminance), not estimated.

The aim: **so simple a nervous first-time user feels confident, so polished JBS is proud to show it off.** Think a well-made banking app, not enterprise software.

## 1. Usability rules (every screen)

1. **Know where you are, know what to do next.** A clear title and at most one primary (filled) button per screen. Everything else is quieter.
2. **Two clicks to anything people do every day.** Home to a contract; Home to "Waiting for my approval" to Approve.
3. **Essentials first, details on demand.** The 20% people need sits at the top; the rest is in titled sections below.
4. **Their words, not system words.** Use the glossary in product-spec.md. Never "entity", "record", "tenant", "workflow state", "submit".
5. **Forgiving.** Drafts save as you type. Destructive actions offer Undo or ask once in plain words ("Archive the Linfox freight contract? Its documents and history are kept.").
6. **Never a dead end.** Empty screens say what goes there and offer the one action that fills them. Errors say what happened and what to do next.
7. **Consistent.** Shared components only, never one-off styling.
8. **Fast to feel.** Skeletons shaped like the content, cached data, instant-feeling saves; never a whole-screen spinner.

## 2. Layout

- **Desktop first** (office users), but every screen works at 375px wide (an approver on a phone).
- **Left sidebar**, collapsible ("Collapse menu" at the bottom): JBS logo at the top, the signed-in person (initials avatar, name, roles) at the bottom. Features not built yet are hidden, never greyed out.
- **Top-level pages**: title (24 to 30px, semibold), a one-line plain description under it, at most one primary action on the right ("+ Add contract").
- **Detail pages**: a back link ("< Contracts") that returns to exactly where the person came from, keeping filters, search and scroll position.
- **Lists are tables on desktop, cards on phones.** Sortable headers, search and segmented filters above ("My contracts | All | Waiting on Legal"), "Export CSV" on the right, row count under the table, row click opens the detail.
- **"?" help beside every page title**, opening that screen's steps for the person's roles.
- **Search everywhere**: a search box on Home plus `Ctrl/Cmd + K`.

## 3. Home pattern ("needs attention", not charts for the sake of charts)

- Greeting and date ("Good afternoon, Jeff", "Wednesday 25 November"), then a search box.
- **Number tiles grouped under plain headings** ("Your turn", "Coming up"), shown only while something is waiting. Big number, short label, opens the list behind it.
- **"Choose what I see"**: each person picks their tiles and which group comes first.
- **Urgent** below the tiles: one line on why each item is urgent and its main action right there. Grouped as overdue, due this week, coming up; capped with "Show all (n)".

## 4. JBS brand colours

From the JBS brand palette. Contrast is against white (`#FFFFFF`) unless stated.

| Name | Hex | Pantone | Contrast on white | Use in the app |
|---|---|---|---|---|
| JBS navy | `#232378` | 2372 C | 13.3 : 1 | **Primary**: buttons, links, active nav, focus ring (light theme) |
| Dark navy | `#14143C` | 2765 C | 17.6 : 1 | Main text; primary pressed |
| Blue | `#005ABE` | 2132 C | 6.6 : 1 | Charts; may be used as text |
| Sky | `#1EAAF5` | 2171 C | 2.6 : 1 | Charts and decoration only, never text |
| Cyan | `#41CDF5` | 2190 C | 1.9 : 1 | **Primary in dark theme**; decoration only on light |
| Pale sky | `#C3EBFA` | 277 C | (surface) | Soft surfaces; navy text on it 10.5 : 1 |
| Lavender | `#E1E6F5` | 2706 C | (surface) | **Primary soft**: active nav, selected rows, avatar circles; navy text on it 10.6 : 1 |
| Lime | `#91D700` | 375 C | 1.8 : 1 | Decoration and charts only; dark navy text on it 10.0 : 1, never white |
| Light lime | `#C3E669` | 374 C | (surface) | Decoration and charts only |
| Green | `#82DC64` | 7487 C | 1.7 : 1 | Decoration and charts only |

Pairing rules (from the JBS do's and don'ts):
- **Never pair two colours from the same family**: navy with dark navy, lavender with white or pale sky, lime with light lime or green.
- **White text only on navy, dark navy or blue.** On sky, cyan, the limes, green and the pale colours, use navy or dark navy text.
- **The limes and green never mean a status.** "Effective" and "Approved" use the status green below, with an icon, so brand green is never mistaken for "done".
- **One accent colour.** Navy is the accent; the logo's blues and greens appear only in the logo and in charts.

## 5. Tokens

All colours are CSS variables. Components use only tokens, never raw colours. Dark theme follows the device by default; a setting can force light or dark.

| Token | Light | Dark | Check |
|---|---|---|---|
| `--primary` | `#232378` | `#41CDF5` | 13.3 : 1 on white; 10.0 : 1 on dark background |
| `--primary-foreground` | `#FFFFFF` | `#14143C` | 13.3 : 1; 9.4 : 1 on cyan |
| `--primary-hover` / `--primary-active` | `#1B1B5E` / `#14143C` | `#7DDCF8` / `#C3EBFA` | text stays above 11 : 1 |
| `--primary-soft` | `#E1E6F5` | `#232378` | navy text 10.6 : 1; dark: cyan 7.1 : 1, lavender 10.6 : 1 |
| `--background` | `#F7F8FC` | `#0F1029` | |
| `--card` | `#FFFFFF` | `#1A1C3A` | |
| `--foreground` | `#14143C` | `#E1E6F5` | 17.6 : 1; 15.0 : 1 (13.3 : 1 on card) |
| `--muted` / `--muted-foreground` | `#EEF0F7` / `#5B6280` | `#2A2D4F` / `#A3AAC4` | 6.0 : 1 on white, 5.3 : 1 on muted; dark 8.1 : 1, 7.2 : 1 on card |
| `--border` | `#DDE1EE` | `#2A2D4F` | dividers (decorative) |
| `--input` | `#8890AE` | `#7A819F` | 3.2 : 1 on white; 4.3 : 1 on dark card (non-text needs 3 : 1) |
| `--ring` | `#232378` | `#41CDF5` | 2px ring, 2px offset |
| `--success` (+ soft) | `#15803D` on `#F0FDF4` | `#4ADE80` on `#052E16` | 4.8 : 1; 8.6 : 1 |
| `--warning` (+ soft) | `#B45309` on `#FFFBEB` | `#FBBF24` on `#451A03` | 4.8 : 1; 9.0 : 1 |
| `--danger` (+ soft) | `#B91C1C` on `#FEF2F2` | `#F87171` on `#450A0A` | 5.9 : 1; 5.8 : 1 |
| `--info` (+ soft) | `#0369A1` on `#F0F9FF` | `#38BDF8` on `#082F49` | 5.6 : 1; 6.5 : 1 |
| `--chart-1` to `--chart-6` | navy, blue, sky, lime, cyan, green | cyan, pale sky, sky, lime, light lime, green | see section 10 |

Neutrals are cool (a hint of navy) rather than the care platform's warm stone, so they sit with the JBS palette (D-23).

**Colour never carries meaning alone.** A status badge is always icon + word ("Expired", not just red).

## 6. Logo

- File: brand/jbs-logo.webp (supplied by Jeff). Use it as supplied: never recolour, stretch, crop, rotate or add effects.
- Sidebar: at the top, at least 32px high; collapsed sidebar shows the logo scaled down, never a made-up mark.
- Only on white or very light backgrounds. In dark theme, until a reversed logo is supplied, it sits on a small white rounded tile (open question in product-spec.md).
- Browser tab icon: made from the logo only once JBS supplies or approves one.

## 7. Type, shape, space, icons, motion

- **Type**: Inter (variable, bundled with the app, not loaded from the internet), tabular numbers for money, dates and tables. Scale 30 / 24 / 20 / 17 / 15 / 13px only. Body 15px on desktop, 16 to 17px on phones; inputs at least 16px. Weights 400 body, 500 labels, 600 headings. Sentence case; no all-caps except tiny overline labels. Reading text 60 to 75 characters wide.
- **Shape and space**: 4px grid (common steps 8, 12, 16, 24, 32). Radius 10px for buttons and inputs, 12px for cards and dialogs, full for avatars and badges. Depth mostly from borders; a soft shadow only for floating things (menus, dialogs, bars). Click and tap targets at least 44 x 44px with 8px between.
- **Icons**: Lucide, 20px, 1.75 stroke. People shown as initials on a lavender circle (navy text) when there's no photo. No stock illustrations; empty states use one large icon and a sentence.
- **Motion**: 150 to 200ms ease-out; dialogs fade and scale in, sheets slide up on phones; respect "reduce motion". Motion explains, it never decorates.

## 8. Components (build once, use everywhere)

| Component | Behaviour |
|---|---|
| Button | primary, secondary (outlined), ghost, danger, link. Heights 44px (md) and 52px (lg). Loading spinner keeps the width |
| Input, Textarea, Select, Combobox, Date picker | Label above, help text below, plain-words error under the field. No placeholder-as-label. "(required)", not an asterisk |
| Field renderer | Draws any field (built-in or added) from its definition: the right input, label, help and validation for its kind. Forms and contract pages never hand-code a field |
| Card | White on the background, 12px radius, border |
| Status badge | Pill with icon + word, from the status tokens. Stages: Draft (neutral), Legal review (info), Signing (info), Approval (warning while waiting), Effective (success), Ended (neutral), Archived (neutral), Withdrawn (neutral), Rejected (danger) |
| Whose-turn chip | Avatar + "Legal's turn, since Mon" or "Your turn" |
| Progress tracker | Horizontal stepper of stages with who has it, since when, and what happens next; vertical on phones |
| Avatar | Photo or initials |
| Page header | Title, "?" help, optional back link, at most one primary action |
| Section | Titled group inside a detail page; collapsible on phones |
| Data table | Sortable, filterable, column picker, CSV export, row click opens detail; becomes cards under 768px |
| Empty state | Icon, one sentence, one action |
| Skeleton | Same shape as what's loading |
| Dialog / Sheet | Centred dialog on desktop, bottom sheet on phones |
| Toast | Success and Undo only. Errors that need action stay on the screen |
| Sticky action bar | Save / Next visible at the bottom of long forms |
| Alert banner | Important warnings at the top of a contract |
| Stepper | Long forms in short steps (one per section) with progress |
| Detail list | Label / value pairs for read-only facts |
| Timeline | Activity and Legal conversation: plain sentences, newest first, with avatars and times |
| Number tile | Big number, short label, opens a list; hidden at zero |

## 9. Patterns

- **List to detail.** Lists show just enough to choose (title, supplier, stage, whose turn, the one date that matters). Detail pages lead with stage and whose turn, then essentials, then sections.
- **Status first.** Anything with a stage shows it before the detail.
- **Forms**: one column, short titled sections, validate on leaving a field, autosave drafts, "+ Add new..." at the end of any picker of things people create (opens a small form in place and selects the new item).
- **Dates**: Australian format, "Tue 4 Nov, 3:00pm"; relative where it helps ("in 2 hours", "notice deadline in 12 days"). Brisbane time.
- **Money**: "$1,200,000.00 AUD" in detail, "$1.2m" in tiles and lists, tabular numbers.
- **Confirmations** only for destructive or irreversible actions, saying what will happen.
- **Back goes back**, and lists keep their filters, search and scroll position.

## 10. Charts

Only where they help someone decide or act (renewal radar, pipeline). Categorical series use `--chart-1` to `--chart-6` in order. Because sky, cyan, lime and green are light, every mark is labelled directly or carries a 1px navy outline, and no chart relies on colour alone. Anything that means "late" or "done" uses the status tokens, not brand colours. Validate chart colours in both themes when the first chart is built (slice 6).

## 11. Writing

Australian English, plain words, sentence case, short sentences, talk to the person ("your approvals", "you're all done").

| Don't | Do |
|---|---|
| Submit | Send to Legal / Send for approval |
| Record not found | We couldn't find that contract. It may have been archived, or you may not have access. |
| Lifecycle transition refused | You can't change this contract while it's waiting for approval. |
| Are you sure? | Withdraw this contract? Its documents and history are kept. |
| No data | No contracts yet. Contracts you add will appear here. |
| Error 403 | You don't have access to this. Ask your admin if you need it. |
| Validation failed | Add an end date, or tick "Evergreen" if it has none. |

## 12. Help built into the app

- "?" on every screen opens that screen's topic for the person's roles.
- Short guides for the main jobs: Raising a contract, Working with Legal, Getting it signed and approved, Renewals, Variations, Adding fields (admins).
- A one-page quick card per role.
- Each error a person can see links to "How to fix this".
- Short captioned how-to clips recorded automatically from the real screens (Playwright), re-recorded when a screen changes.
- "Help updated" is part of the definition of done for every screen change.

## 13. Accessibility and quality bar

- WCAG 2.2 AA: contrast (section 5), visible focus, a label on every input, names for icon buttons, full keyboard use, text enlarges to 200% without breaking.
- Every screen designed in its loading, empty, error and success states.
- Checked at 375px and 1280px, light and dark, with axe in Playwright.
- Before each review: give someone non-technical three tasks, don't help, fix every hesitation.
- Demo data is realistic and fictional; no placeholder text is ever shown.
