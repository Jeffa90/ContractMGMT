# JBS Contracts: instructions for Claude

## Purpose

A simple, friendly contracts app for JBS Australia. People raise a contract, work it through Legal, get it signed, have the Group Head approve it, and then never miss a notice date. It replaces the Enterprise Contract Platform (the old app in this repository's `src/` folder) on the same JBS server.

The test of every screen: **a nervous first-time user feels confident, and the owner is proud to show it off.** Think a well-made banking app, not enterprise software.

This folder is the whole new app. The old app's code, `requirements/` and `decisions/` folders describe a different product (12 roles, solution packs, multi-company). Read them only for business-rule detail this folder doesn't cover.

## Authority order (for work inside this folder)

1. This file
2. docs/decisions.md (decisions made with Jeff; newest wins)
3. docs/product-spec.md (what we're building and in what order)
4. docs/design-system.md (how it looks, reads and behaves)
5. `docs/jbs/QA_CONFIGURATION_RECORD.md` at the repository root (server, database, email, pipeline: reuse it, don't rediscover it)
6. The repository root `requirements/` for contract rule detail, only where 1 to 4 are silent

The repository root `CLAUDE.md` security rules still apply. Its product principles (packs, portability, many companies) do **not** apply here (decision D-02).

## Non-negotiables

- **JBS data stays on JBS servers.** IIS, SQL Server and Windows sign-in only. No cloud services, CDNs, analytics or web fonts loaded at run time. Fonts and icons are bundled.
- **Never commit secrets or real data.** This repository is public. Demo and test data are realistic and fictional. No real contract, supplier or person data, no passwords, no keys.
- **Every rule is checked on the server.** Role, Confidential, stage, whose turn and lock are enforced by the API on every request. Hiding a button is a courtesy, not security.
- **Windows sign-in is sent automatically by browsers**, so every request that changes something needs the anti-forgery token. No exceptions.
- **Effective contracts are locked.** They change only through an approved Variation or an admin's emergency unlock (reason, expiry, audited). A contract's stage changes only through its actions, never by editing.
- **Nothing is hard-deleted.** Contracts are withdrawn or archived, fields are hidden, people are deactivated. History is kept.
- **Everything is audited.** Every change writes an append-only, hash-chained audit event with who, when, what, old and new values, and a plain sentence for the Activity timeline.
- **Admin settings are closed choices.** No formulas, scripts, SQL, HTML or code in anything an admin can type. Email wording uses a fixed list of placeholders.
- **Times are stored in UTC and shown in Brisbane time.** Dates are shown Australian style ("Tue 4 Nov, 3:00pm").
- **Australian English, plain words, sentence case.** Use the glossary in docs/product-spec.md. One word per concept.
- **Use the design system.** Tokens only (no raw colours), shared components only, at most one primary button per screen, status is always icon plus word.
- **Production deploys only when Jeff starts it by hand** (Deploy workflow on `main`, promote-to-production ticked, then the Production reviewer approves). Never claim production approval; that is JBS governance.
- **Keep `scripts/ci/*.ps1` ASCII-only.**
- **Keep it simple.** If in doubt, leave it out and ask Jeff. Record every decision and reversible assumption in docs/decisions.md.
- **Be honest about status.** Say what is built, hidden, mocked or later. Never show a placeholder, a "coming soon" or a greyed-out feature to users; hide unbuilt things.

## Stack

| Part | Choice |
|---|---|
| Front end | React + TypeScript (strict), Vite, React Router, Tailwind CSS v4 + shadcn/ui (Radix), Lucide icons, TanStack Query, React Hook Form + Zod, date-fns, sonner, Inter (bundled) |
| API | ASP.NET Core on .NET 10 (minimal APIs), serving the built front end from the same IIS site |
| Sign-in | IIS Windows Authentication; the app maps the Windows account to a person an admin has added |
| Database | SQL Server, the existing `JBSContracts` database, tables in the `contracts` schema |
| Schema changes | Numbered `.sql` files in the database folder, run once each by `scripts/ci/Invoke-SqlMigrations.ps1` |
| Data access | Dapper with parameterised SQL (no ORM migrations) |
| Background work | A hosted service in the API (the app pool is Always Running) for reminders and escalations |
| Email | The JBS anonymous relay (port 25, STARTTLS), sender no-reply |
| Files | On the server's data folder, checked on every download; JBS scans files before upload |
| Tests | xUnit (API, including real SQL Server in CI), Vitest (front end), Playwright at 375px and 1280px, light and dark, with axe accessibility checks |
| Deploy | The existing GitHub Actions pipeline and self-hosted QA runner, adapted (see docs/product-spec.md, "Deployment") |

Folder layout:

```
jbs-contracts/
  CLAUDE.md          this file
  docs/              product-spec.md, design-system.md, decisions.md, brand/
  web/               React front end (Playwright tests in web/e2e)
  api/               ASP.NET Core API
  api.tests/         API and SQL Server tests
  database/          01_schema.sql, 02_..., run in order, never edited once applied
```

## Definition of done (every slice and every screen change)

1. Works on QA: deployed by the pipeline and tried at the QA address, not just on a laptop.
2. Rules enforced on the server, with API tests for allowed and refused cases, including Confidential and locked contracts.
3. Every screen designed in its loading, empty, error and success states; checked at 375px and 1280px, light and dark; axe passes; full keyboard use.
4. Plain words everywhere, from the glossary; every error says what happened and what to do next.
5. Every change writes an audit event and shows in Activity.
6. The "?" help for the screen is written or updated.
7. Restore, build, database scripts and all tests run green; failures are fixed, not skipped. Anything not verified is labelled Not verified.
8. docs/product-spec.md (slice status) and docs/decisions.md are updated.
9. Jeff can try it: a short "what to try on QA" note in the slice summary.
