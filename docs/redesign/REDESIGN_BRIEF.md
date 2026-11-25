# Redesign brief: a simpler, friendlier JBS contracts app

Status: planning notes, 2026-11-25. **Nothing here is decided.** Jeff is considering replacing the current Enterprise Contract Platform with a smaller app built only for JBS, in the style of his care platform (`STYLE_GUIDE.md`). This file keeps what was learned getting the current app onto the JBS QA server, so a new session can start without losing it.

## 1. Why

- The current app was built as a product to sell to many companies: about 12 roles with organisation scopes, solution packs, terminology layers, a setup wizard, three deployment types and 49 recorded design decisions. That flexibility makes screens busy and permissions confusing (for example, the administrator was refused when adding a person on day one of QA).
- Almost every QA blocker came from rules written for unknown future customers, not for JBS: pack signing, SMTP that had to log in, a built-in malware scanner, SQL Row-Level Security.
- Jeff's verdict after first use: he doesn't like the layout or the functionality, and wants it as simple and friendly as the care platform.

## 2. What we know works at JBS (reuse, don't rediscover)

The full record of every setting, variable and fix is `docs/jbs/QA_CONFIGURATION_RECORD.md`. Read it before any deployment work.

| Area | What works today | Where it's written down |
|---|---|---|
| Server | IIS on AUQLDDC01QA2WI; site and pool `JbsContracts`; app `C:\inetpub\JbsContracts\app`, data `...\Data`, logs `...\logs`; https binding `jbs-contracts-qa.jbssa.com.au` with the IT-issued certificate | `docs/DEPLOYMENT.md`, `docs/jbs/jbs-github-environments.env` |
| Pipeline | GitHub Actions: CI must be green on the commit, then a self-hosted runner (label `iis-qa`) builds and deploys to QA; Production only from a manual run with promote-to-production ticked, behind a required reviewer; smoke test on `/health/live`; on failure the log prints the ASP.NET Core Module / .NET event log entries and app log error lines | `.github/workflows/deploy.yml`, `iis-deploy.yml`, `scripts/ci/*.ps1` (ASCII only) |
| Identity | IIS Windows Authentication; app pool and runner both run as `AMH\svc_jbsai_qa` in QA; a separate runner account agreed for Production | `docs/DEPLOYMENT.md` |
| Database | SQL Server AUQLDDC01QA2Xi, database `JBSContracts`, Windows integrated auth, `TrustServerCertificate=True` until IT issues a trusted certificate; migration account db_ddladmin + reader + writer, runtime reader + writer only; no Row-Level Security | ADR-0044 |
| Email | Anonymous relay `email.jbssa.com.au` port 25 with STARTTLS, sender `no-reply@jbssa.com.au`; tested end to end | ADR-0048 |
| Malware scanning | Done by JBS before files can be uploaded; the app scans nothing | ADR-0049 |
| Data protection keys | Key folder permissions reset every deploy so the pool can read them (a cause of an earlier start-up crash) | `scripts/ci/Initialize-IisSite.ps1` |
| GitHub variables | Set by hand on the QA environment, e.g. `APPENV_...` values stamped into web.config | `scripts/setup/setup-github-environments.sh` (lists them) |

Domain rules worth keeping whatever the design (from `requirements/`): category ownership; Legal reads everything and can do several review rounds; approval by the right Group Head or their backup; Registered Date kept separate; an Effective contract is locked and changes only through a Variation (or an audited emergency unlock); renewals, closure and archive-before-delete; reminders; a tamper-evident audit trail; times stored in UTC and shown in Brisbane time.

## 3. Proposed shape (to be confirmed in planning)

- **Users**: Requester / contract owner, Legal, Approver (Group Head and backup), Admin. Possibly Viewer. Four or five roles instead of twelve.
- **Main jobs**: raise a contract, add documents, send for Legal review, send for approval, approve or reject, register, renew or close, find anything quickly, get reminded on time.
- **Stack**: keep the care platform's front end (React, TypeScript, Vite, Tailwind, shadcn/ui, TanStack Query, React Hook Form + Zod, Playwright) for the look and feel, served by a small ASP.NET Core API on the same IIS site, with SQL Server and Windows sign-in, reusing the existing pipeline and scripts. (The care platform's Supabase and Cloudflare can't be used: JBS data stays on its own servers.)
- **Configuration**: only what JBS will really change (approval thresholds and approvers, categories, reminder timings, lists, email wording), each with history; no packs, terminology layers or multi-company features.
- **Build order**: sign-in and people; contracts and documents; Legal review and approvals; register and lock; renewals and reminders; dashboard and reports; moving existing contracts in. Each slice deploys to QA for Jeff to try.

## 4. Making it intuitive: ideas from good apps

| Idea | Seen in | For contracts |
|---|---|---|
| "Needs attention" dashboard with number tiles and one-click fixes | Care platform | My approvals, renewals due, reviews due, documents missing, stuck contracts |
| Pipeline board (columns by stage, cards you can open) | Trello, Linear, contract tools such as Ironclad and Juro | Draft → Legal review → Approval → Signing → Effective, with how long each has waited |
| Progress tracker on each contract | Parcel tracking, Linear issue timeline | A horizontal stepper showing where the contract is, who has it now, since when, and what happens next |
| Activity timeline | GitHub, Linear | Everything that happened to a contract, in plain sentences, newest first |
| Command bar (`Ctrl/Cmd + K`) | Linear, Slack, Notion | Jump to any contract, supplier or person; quick actions ("New contract") |
| Calendar and timeline views | Google Calendar, Gantt charts | Expiry and notice dates on a calendar; a renewal timeline with 30/60/90-day bands |
| Saved views and filters | Linear, Airtable | "My contracts", "Expiring this quarter", "Waiting on Legal" |
| Inline approval from email and phone | DocuSign, Expensify | Approve or reject from a clear summary page that works on a phone |
| Guided first-run checklist | Stripe, care platform's setup checklist | "Add your categories, add approvers, add your first contract" |

## 5. Visualisations and tracking worth considering

- **Renewal radar**: contracts expiring in the next 30/60/90/180 days, with notice-period deadlines highlighted (a missed notice date is the costly mistake).
- **Pipeline funnel**: how many contracts sit in each stage and the average days in each, to see where things get stuck.
- **Approval turnaround**: time from "sent for approval" to decision, by approver and category; overdue approvals flagged.
- **Spend and value**: contract value by category, supplier and business unit; top suppliers; contracts with no value recorded.
- **Contract health**: a simple per-contract indicator (documents complete, owner assigned, review date set, not expiring without a decision).
- **Supplier page**: every contract with a supplier, total value, upcoming dates, in one place.
- **Obligations and key dates**: reminders for notice periods, price reviews and insurance certificates, shown on the contract and the calendar.
- **Audit-ready reports**: the register as CSV and PDF, and "who approved what, when" for any contract in one click.

Keep the care platform's rule: charts only where they help someone decide or act; the dashboard is a to-do list first.

## 6. Questions for planning

1. Who will use it day to day, and how many people? Which roles are really needed?
2. What do people do today without the system (spreadsheets, email, SharePoint)? What hurts most?
3. Which screens of the current app, if any, should be kept?
4. Do existing contracts need to be moved in at the start, and from where?
5. Are e-signature (DocuSign or similar) and links to SAP or other systems needed in the first version?
6. Should the new app replace the current one at the same address and database, or run beside it until it's ready?
7. Is a JBS brand colour wanted instead of the care platform's teal?

## 7. How to carry this into a new session

- Start a **new Claude Code session** for the redesign planning and build. It gets a clean context while this repository and these notes keep everything learned so far.
- Put the new app in a **new GitHub repository** (for example `jbs-contracts`), so the old code doesn't steer the new one. Copy `STYLE_GUIDE.md` and this brief into it on day one, plus a short `CLAUDE.md` in the care platform's style (purpose, non-negotiables, stack, definition of done).
- Bring across the proven deployment pieces from this repository (`.github/workflows/deploy.yml`, `iis-deploy.yml`, `scripts/ci/*.ps1`, the GitHub environment variables list) and adapt them, rather than writing new ones.
- Keep the current app running on QA until the new one does what JBS needs.
