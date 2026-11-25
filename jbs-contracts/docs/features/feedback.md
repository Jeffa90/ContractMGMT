# Feature (later): in-app Feedback

Status: **planned, not built**. Added at Jeff's request on 25 Nov 2026, from the JBS feedback feature reference (the version built for the JBS AI Initiatives app, in Vue). This page adapts it to JBS Contracts' stack (React) and design system. The rules, database and API stay the same as the reference, so every JBS app shares one feedback store.

## What people get

- A **Feedback** button on every screen (bottom right, above the sticky save bar on forms, out of the way on phones). It opens a small dialog: **What kind of feedback?** (Bug, Improvement, Data looks wrong, Question, Other; Bug by default) and **Tell us what happened** (required). The current screen is sent with it automatically.
- After sending: *"Thanks, your feedback has been recorded. You can see it any time on the Feedback page."*
- A **Feedback** page in the menu: *"Feedback and actions. Use the Feedback button on any screen to tell us what's working and what isn't."*
  - Number tiles: Open, In progress, Closed (Done plus Won't do), Archived, Total.
  - Everyone sees **My feedback**; admins see **All feedback**.
  - Filters: status, kind, search (comment, screen, person) and **Include archived** (off by default).
  - Each item: kind and status badges, the comment, then screen · person · date. Opening it shows the reply thread, a reply box and a status picker for the person who raised it and for admins; admins also get **Archive** or **Unarchive**.

## Rules (enforced on the server)

| Who | Can |
|---|---|
| Anyone signed in | Send feedback from any screen; see their own items; reply to and change the status of their own items |
| Admins (JBS Contracts Admin tick) | See all of this app's items; reply to and change the status of any; archive and unarchive |

- Kinds: `Bug`, `Improvement`, `Data looks wrong`, `Question`, `Other` (anything else is saved as Other).
- Statuses: `Open` (default), `In Progress`, `Done`, `Won't Do` (anything else is refused).
- Who sent, replied or acted is always the signed-in Windows account, never a value from the request.
- Every query filters on this app's name, so JBS Contracts never sees or changes another app's feedback.
- Every change is also written to JBS Contracts' own audit trail.

## Design system fit

| Reference | JBS Contracts |
|---|---|
| Vuetify button, dialog, selects, expansion panels | Our Button, Dialog (bottom sheet on phones), Select, Textarea, Card and Section components |
| Hard-coded hex colours per kind and status | Status tokens with icon plus word: Bug = danger, Data looks wrong = warning, Improvement = success, Question = info, Other = neutral; Open = warning, In progress = info, Done = success, Won't do = neutral |
| KPI cards | Our number tiles (shown at zero on this page, since it is a report rather than a to-do list) |
| Material icons | Lucide (MessageSquareWarning for the button) |
| Comments rendered with `{{ }}` | React text rendering only (never HTML) |

The button has a visible label ("Feedback"), is reachable by keyboard and at least 44 by 44 px.

## Database: the shared JBS store

Feedback lives in the shared `JBSAppsCommon` database, `common` schema (`common.Feedback` and `common.FeedbackReply`), not in JBSContracts, using the reference's idempotent scripts unchanged. JBS Contracts writes with its own app name (to agree once, then never change; proposed: `JBS Contracts`).

## Open questions before building

1. Does `JBSAppsCommon` exist on the QA and Production SQL Servers, or only on the development server? If not, who creates it (DBA)?
2. Connection string for each environment (Windows integrated authentication, no password) as a new GitHub environment variable `APPENV_ConnectionStrings__JbsAppsCommon`, and grants for the app pool account: SELECT, INSERT and UPDATE on both tables.
3. The app name to use (proposed `JBS Contracts`).

## Tests (from the reference)

A non-admin sees only their own items and replies; gets refused when replying to, changing or archiving someone else's; an empty comment or reply is refused; an unknown kind is saved as Other; an unknown status is refused; and a browser check that feedback sent from two screens records each screen.
