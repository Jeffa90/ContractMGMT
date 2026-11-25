# Style guide for the redesigned JBS contracts app

Status: planning reference, 2026-11-25. Captured from Jeff's care platform (the app whose look and feel Jeff wants), reviewed from its code and design docs, so that care platform can be removed from this session. Nothing here is built yet. Nothing here is a decision about the redesign's scope; see `REDESIGN_BRIEF.md`.

The aim, in the care platform's words: **so simple a nervous first-time user feels confident, so polished the owners are proud to show it off.** Think a well-made banking app, not enterprise software.

---

## 1. Usability rules (apply to every screen)

1. **Know where you are, know what to do next.** Every screen has a clear title and at most one primary (filled) button. Everything else is quieter.
2. **Two clicks to anything people do every day.** Dashboard to a contract to its essentials; dashboard to "my approvals" to approve.
3. **Essentials first, details on demand.** The 20% people need sits at the top; the rest is in titled sections below or behind "More".
4. **Their words, not system words.** "Contracts", "Approvals", "Renewals", "Suppliers". Never "entity", "record", "tenant", "lifecycle state", "submit request".
5. **Forgiving.** Drafts save as you type. Destructive actions either offer Undo or ask once in plain words ("Archive the Acme cleaning contract? Its documents and history are kept.").
6. **Never a dead end.** Empty screens say what goes there and offer the one action that fills them. Errors say what happened and what to do next. ("You can't approve this one because you raised it. Ask another approver.")
7. **Consistent.** The same thing looks and behaves the same everywhere: shared components only, never one-off styling.
8. **Fast to feel.** Content shows instantly (skeletons shaped like the content, cached data); saves feel instant; never a whole-screen spinner.

## 2. Layout

- **Desktop first** for this app (office users), but every screen must still work at 375px wide (an approver approving from a phone).
- **Left sidebar**, collapsible ("Collapse menu" at the bottom), with the organisation logo and name at the top and the signed-in person (initials avatar, name, role) at the bottom. Items for features not built are hidden, not greyed out.
- **Top-level pages**: page title (large, 24–30px, semibold) with a one-line plain description under it, and at most one primary action on the right (for example "+ Add contract").
- **Detail pages**: a back link ("< Contracts") that returns to exactly where the person came from, with lists keeping their filters, search and scroll position.
- **Lists are tables on desktop, cards on phones.** Tables: sortable headers, a search box and segmented filters above (for example "Current 6 | Past 1"), "Export CSV" on the right, row count under the table, row click opens the detail.
- **"?" help button beside every page title**: opens plain how-to steps for that screen and the person's role, with a short video where there is one.
- **Search everywhere**: a search box on the dashboard plus `Ctrl/Cmd + K` to jump to any contract, supplier or person.

## 3. Dashboard pattern ("needs attention", not charts for the sake of charts)

- Greeting and date at the top ("Good afternoon, Jeff", "Wednesday 25 November").
- A search box ("Find a contract or supplier").
- **Number tiles grouped under plain headings**, shown only while something is waiting. Each tile is a big number and a short label and opens the list behind it. For contracts, for example: *Today* (Approvals waiting for me, Signatures due), *To approve* (Contracts to register, Variations to approve), *Keeping up to date* (Renewals in 90 days, Reviews due, Documents missing).
- **"Choose what I see"**: each person picks which tiles they see and which group comes first.
- **Urgent section** below the tiles: each item says why it's urgent in one line and has its main action right there (Open, Mark as looked at). Long lists are grouped by urgency (overdue, due this week, coming up), capped with "Show all (n)", and collapsible.

## 4. Visual tokens

All colours are CSS variables, so dark mode and a customer's brand colour are one-line changes. Components use only tokens, never raw colours.

| Token | Light | Dark | Use |
|---|---|---|---|
| `--primary` | Teal 700 `#0F766E` | Teal 400 `#2DD4BF` | Primary buttons, active nav, links, focus ring |
| `--primary-hover` / `--primary-active` | `#115E59` / `#134E4A` | `#5EEAD4` / `#99F6E4` | Hover and press (keeps text contrast AA) |
| `--primary-soft` | Teal 50 `#F0FDFA` | Teal 950 `#042F2E` | Active nav background, avatar initials |
| `--background` | Stone 50 `#FAFAF9` | Stone 950 `#0C0A09` | App background |
| `--card` | White | Stone 900 `#1C1917` | Cards, panels, tables |
| `--foreground` | Stone 900 `#1C1917` | Stone 50 | Main text |
| `--muted` / `--muted-foreground` | `#F5F5F4` / `#78716C` | `#292524` / `#A8A29E` | Quiet surfaces, secondary text |
| `--border` | Stone 200 `#E7E5E4` | Stone 800 | Dividers, card borders |
| `--input` | `#8C857F` | `#78716C` | Input borders (3:1 non-text contrast) |
| `--success` (+ soft) | Green 700 `#15803D` on `#F0FDF4` | `#4ADE80` on `#052E16` | Effective, Approved, Done |
| `--warning` (+ soft) | Amber 700 `#B45309` on `#FFFBEB` | `#FBBF24` on `#451A03` | Expiring soon, Needs review |
| `--danger` (+ soft) | Red 700 `#B91C1C` on `#FEF2F2` | `#F87171` on `#450A0A` | Expired, Overdue, Rejected |
| `--info` (+ soft) | Sky 700 `#0369A1` on `#F0F9FF` | `#38BDF8` on `#082F49` | Neutral notices |

Rules:
- **One accent colour.** Status colours are for status only. (A JBS brand colour can replace teal through the tokens; check contrast if it does.)
- **Colour never carries meaning alone.** A status badge is always icon + word ("Expired", not just red).
- Dark theme follows the device by default; a setting can force either.

**Type**: Inter (variable), tabular numbers for money, dates and tables. Scale 30 / 24 / 20 / 17 / 15 / 13px and nothing else. Body 15px on desktop, 16–17px on phones; inputs at least 16px. Weights 400 body, 500 labels, 600 headings. Sentence case; no all-caps except tiny overline labels. Reading text 60–75 characters wide.

**Shape and space**: 4px grid (common steps 8, 12, 16, 24, 32). Radius 10px buttons and inputs, 12px cards and dialogs, full for avatars and badges. Depth is mostly borders; a soft shadow only for floating things (menus, dialogs, bars). Click and tap targets at least 44x44px with 8px between.

**Icons**: Lucide, 20px, 1.75 stroke. People shown as initials on a soft teal circle when there's no photo. No stock illustrations; empty states use one large icon and a sentence.

**Motion**: 150–200ms ease-out; dialogs fade and scale in, sheets slide up on phones; respect "reduce motion". Motion explains, it never decorates.

## 5. Components (build once, use everywhere)

| Component | Behaviour |
|---|---|
| Button | primary, secondary (outlined), ghost, danger, link. Heights 44px (md) and 52px (lg). Loading spinner keeps the width |
| Input, Textarea, Select, Combobox, Date picker | Label above, help text below, plain-words error under the field. No placeholder-as-label. "(required)", not an asterisk |
| Card | Basic building block; white on the stone background, 12px radius, border |
| Status badge | Pill with icon + word, from the status tokens (success, warning, danger, info, neutral) |
| Avatar | Photo or initials |
| Page header | Title, "?" help, optional back link, at most one primary action |
| Section | Titled group inside a detail page; collapsible on phones |
| Data table | Sortable, filterable, CSV export, row click opens detail; becomes cards under 768px |
| Empty state | Icon, one sentence, one action |
| Skeleton | Same shape as what's loading |
| Dialog / Sheet | Centred dialog on desktop, bottom sheet on phones |
| Toast | Success and Undo only. Errors that need action stay on the screen |
| Sticky action bar | Save / Next stays visible at the bottom of long forms |
| Alert banner | Important warnings at the top of a record (for example "This contract expires in 14 days and has no renewal decision") |
| Stepper | Long forms broken into short steps with progress |
| Detail list | Label / value pairs for read-only facts |

## 6. Patterns

- **List to detail.** Lists show just enough to choose (name, supplier, status, the one date that matters). Detail pages lead with status, then essentials, then sections.
- **Status first.** Anything with a status shows it before the detail.
- **Forms**: one column, short titled sections, validate when leaving a field, autosave drafts, "+ Add new..." at the end of any picker of things people create (opens a small form in place and selects the new item).
- **Dates**: Australian format, "Tue 4 Nov, 3:00pm"; relative where it helps ("in 2 hours", "expires in 12 days").
- **Confirmations** only for destructive or irreversible actions, saying what will happen.
- **Back goes back**, and lists keep their filters, search and scroll position.
- **Passwords** (if any) have show/hide.

## 7. Writing

Australian English, plain words, sentence case, short sentences, talk to the person ("your approvals", "you're all done"). One word per concept, kept in a glossary.

| Don't | Do |
|---|---|
| Submit | Send for approval |
| Record not found | We couldn't find that contract. It may have been archived. |
| Lifecycle transition refused | You can't change this contract while it's waiting for approval. |
| Are you sure? | Withdraw this contract from approval? |
| No data | No contracts yet. Contracts you add will appear here. |
| Error 403 | You don't have access to this contract. Ask your admin if you need it. |

## 8. Help built into the app

- A "?" on every screen opens that screen's topic for the person's role.
- Short guides for the main jobs (for example "Raising a contract", "Approving", "Renewals").
- A one-page quick card per role.
- Troubleshooting entries for each error a person can see, linked from the error itself ("How to fix this").
- Short captioned how-to clips recorded automatically from the real screens (Playwright), re-recorded when a screen changes.
- "Help updated" is part of the definition of done for every screen change.

## 9. Accessibility and quality bar

- WCAG 2.2 AA: contrast, visible focus, a label on every input, names for icon buttons; full keyboard use; text enlarges to 200% without breaking.
- Every screen designed in its loading, empty, error and success states.
- Checked at 375px and 1280px, light and dark.
- Usability test before each review: give someone non-technical three tasks, don't help, fix every hesitation.
- Demo data is realistic and fictional; no placeholder text ever shown.

## 10. Care platform's tech, for reference only

React + TypeScript (strict), Vite, React Router, Tailwind CSS v4 + shadcn/ui (Radix), Lucide icons, TanStack Query, React Hook Form + Zod, date-fns, sonner (toasts), Inter font; Playwright (375px and 1280px projects, axe accessibility checks, screenshots and help clips), Vitest. Its backend (Supabase, Cloudflare) does **not** carry over: JBS runs on its own IIS server and SQL Server with Windows sign-in (see `REDESIGN_BRIEF.md`).
