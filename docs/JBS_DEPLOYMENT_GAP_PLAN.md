# JBS deployment gap plan

Status: **Plan only. Nothing here is implemented.** Derived from `docs/JBS_STANDARDS_COMPLIANCE.md` (rule IDs `J-nn`, details `D-nn`, JBS questions `I-nn` and `DB-nn` refer to it). Branch ux/guided-administration, commit `9f5317e`, 2026-11-10. Production approval is external governance and nothing here declares it.

## Update: state after the JBS IIS integration slice (branch jbs/iis-integration)

This plan was written before any code changed. The slice on branch jbs/iis-integration has since built the items below; each is **Implemented** and tested here and **Not Verified on IIS** until the first QA run. Details and evidence: `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`.

| Item | Was | Now |
|---|---|---|
| P0-1 Windows sign-in | Not built | Windows provider kind, **identity only and invite-only** (owner decision; ADR-0042); first administrator can sign in after Setup. Not Verified on IIS |
| P0-2 IIS hosting hardening | `web.config` not published | `src/Ecp.Host/web.config` is published; per-URL upload limits; request-timeout policy; publish check in CI. Not Verified on IIS |
| P0-3 Background jobs survive IIS | Script only | Unchanged script (Always Running, no idle time-out, warm-up) plus **load user profile**; Not Verified |
| P0-4 Durable redacted log | None | Serilog, durable rolling file, redaction, shared context; readiness R-LOG-01 |
| P0-5 Run on Windows and IIS | Not done | **Still not done for IIS.** A Windows CI job builds and runs every non-SQL test on Windows, and checks the publish output; that is not an IIS run |
| P1-1 Serilog per Sections 1 and 5 | Not built | Foundation built. Application Insights and fatal-error email are **configurable and Not Verified** (no Application Insights sink in this build) |
| P1-3 Configuration conventions | Demo defaults in the base file | Production-safe base file; Demo values only in `appsettings.Development.json`, never published |
| P1-5 Scripted migrations for a DBA | Open | Unchanged: migrations are a deliberate step, not part of the pipeline (NEEDS DBA ANSWER on who runs them and when) |
| P1-6 Integrated-authentication guard | Open | Done: a credential-free connection string may be ordinary configuration; anything with a credential must be a secret reference; R-DB-02 reports the runtime login and its privileges |
| DB-06 / DB-07 backups | Open | `Ecp:Recovery:Mode=External`: DBA-owned recovery points are declared, audited and labelled "Declared by DBA; not verified by this application" (ADR-0043) |

Still open and not guessed: the P3 questions (Infrastructure and DBA answers) are listed at the end of `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`.

CI: commit e0d2ac7 passed every job (Linux, Windows, SQL Server 2022, container); see `deploy/STATUS.md`. Nothing has run on IIS.

## 1. Ranking

| Rank | Meaning |
|---|---|
| **P0** | Required before JBS deployment: without it the application cannot be signed into, hosted or operated on JBS's platform |
| **P1** | Required for JBS standard compliance: the application would run, but breaks an agreed or likely-binding standard |
| **P2** | Justified deviation or documentation: no behaviour change; record the reason and get acknowledgement |
| **P3** | JBS Infrastructure or DBA confirmation: a decision or fact only they hold; several change the shape of P0 and P1 |

Effort: **S** about a day, **M** a few days, **L** a week or more of build plus a Windows test environment. Efforts are estimates, not commitments.

## 2. P0: required before JBS deployment

| ID | Item | Rules | Needs | Effort | What done looks like |
|---|---|---|---|---|---|
| P0-1 | **Windows sign-in** (Negotiate), or the JBS-approved alternative | J-45, J-52, D-45 | I-05, I-12 | L | A Windows identity provider kind that authenticates through Windows, matches an *invited* account by a stable Windows identifier, maps AD groups through the existing group-mapping engine, then issues the existing signed session. Anonymous paths limited and listed. Tests for linking, group mapping, refusal of an un-invited account, CSRF, and the Production guard. **If I-12 says Entra OIDC is acceptable, this becomes configuration and a live-tenant verification (M)** |
| P0-2 | **IIS hosting hardening** | J-49, J-50, J-51, J-89, D-49, D-50 | I-08, I-11 | S | `web.config` ships with `dotnet publish`; request limits agreed with the settings they mirror (documents 100 MB, packs 9 MB, migration upload 2 GB); long-request decision recorded; README says what to copy and what to set |
| P0-3 | **Background jobs survive IIS** | J-06, D-06 | I-03, I-04 | S | The deployment guide requires an *Always Running* pool with preload and no idle time-out (or names a worker host); a readiness check reports when no job cycle has run recently |
| P0-4 | **A durable, redacted log on the IIS host** | J-10, J-41, D-10 | I-09, I-10 | M | At minimum Serilog behind the existing `ILogger<T>` calls with a rolling file or Event Log sink and the redactor; canary-secret tests. (Application Insights and the Fatal email sink follow in P1-1.) Without this an IIS host has no reliable record of errors |
| P0-5 | **Run the scaffold on Windows and IIS** | J-55, J-62 | I-01, I-02, I-03, I-06 | L | In a Windows test environment: publish, create the site and pool, start, reach `/health/live`, `/health/startup`, `/health/ready`, run `migrate` under a migration identity, set up through the wizard, sign in (P0-1), send a large export and a large migration upload, recycle the pool and watch jobs resume, and try a gMSA with integrated authentication. Record results in `deploy/STATUS.md` as Verified or Failed, never assumed |

P0 order: P3 answers first (I-12, I-05, I-04, DB-04, DB-06), then P0-2 and P0-3 (small, unblock P0-5), then P0-4, then P0-1, then P0-5 as the proof.

## 3. P1: required for JBS standard compliance

| ID | Item | Rules | Needs | Effort | Notes |
|---|---|---|---|---|---|
| P1-1 | **Serilog fully per Section 1 and 5**: the standard enrichers, Application Insights at Information and above, email for Fatal only | J-10, J-38, J-40, J-41, J-44, D-10, D-40 | I-09, I-10 | M | "SiteCode" mapped to an agreed installation or organisation identifier; "Login" is the actor id, not a name. Redaction applies to every sink; the telemetry processor drops request bodies and query strings so contract data never leaves the server. Connection string comes from the environment |
| P1-2 | **Azure DevOps YAML pipeline** mirroring the existing jobs, plus a publish artefact for IIS | J-74, J-75, D-74 | I-07 | M | Restore `--locked-mode`, warnings-as-errors build, tests, `tools/supply-chain.sh`, SQL Server integration job, `release` configuration, one job per deployable; triggers per the standard once JBS confirms them. GitHub Actions stays |
| P1-3 | **Configuration conventions** | J-31, J-33, J-34, J-36, J-37, D-31, D-33, D-34, D-36, D-37 | I-08 | S to M | Optional per-site layer; a base `appsettings.json` that is safe (demo values moved to a Demo or Development file the tests and Docker demo select); every key present and empty; `UserSecretsId`; `GetConnectionString("Default")` accepted as a fallback for the same reference |
| P1-4 | **Project metadata** | J-21, D-21 | wording | S | `Company`, `Copyright`, `NeutralLanguage` supplied by the JBS build, not committed into the vendor-neutral repository; check localisation tests first |
| P1-5 | **Scripted migrations for a DBA, if required** | J-59, D-62 | DB-06 | M | Idempotent SQL scripts per module, produced in CI and attached to the release, including the row-level-security patches; keeps the `migrate` command for installations that prefer it |
| P1-6 | **Integrated-authentication guard** | J-62, D-62 | DB-04, DB-10 | S | A connection string with no secret in it should not need a secret file; the Production guard accepts an integrated string from the environment |

## 4. P2: justified deviations and documentation

Each needs a short written reason (the guide says "write the reason in your design doc") and a JBS acknowledgement. No behaviour changes.

| ID | Item | Rules | Reason to record |
|---|---|---|---|
| P2-1 | Razor Pages instead of MVC controllers | J-02, J-22, D-02 | Same framework, filters and view engine; page models are thin; services hold the logic |
| P2-2 | Modular monolith layout and `Ecp.*` naming | J-14, J-15, D-14, D-15 | Finer responsibility split than the guide's example; vendor-neutral product with a test that forbids business names in generic source. A rename is only for a mandatory ruling (I-13) |
| P2-3 | Hosted timer, operation leases and outbox instead of Hangfire | J-06, D-06 | Tenant isolation, licence class, single-surface leases for operator work; adapter seam described |
| P2-4 | xUnit `Assert` instead of Moq and FluentAssertions; test names and layout | J-12, J-13, J-70, J-73, D-12 | Real hosts and real SQL Server prove more; licence check; no mass rewrite |
| P2-5 | Primary constructors; module-level `AddX` signatures; interface and implementation in one file | J-23, J-24, J-27, D-23 | Style only; encapsulation enforced by architecture test; modules free of `IConfiguration` |
| P2-6 | ADO.NET inside operations tooling; the two reviewed non-parameter SQL forms | J-57, J-60, D-57 | `BACKUP`, `RESTORE`, `DBCC` and catalogue queries are not EF operations. Add a static check that rejects string-built SQL outside the two named places |
| P2-7 | Comment every `[AllowAnonymous]`; publish the list of anonymous endpoints and their callers | J-68, D-55 | Comment-only change |
| P2-8 | Branch, commit and design-doc conventions | J-76 to J-79, D-74 to D-78 | Adopt JBS conventions when JBS work items exist; do not rewrite history |
| P2-9 | Acknowledge the unchanged shape of tests, DI lifetimes and captive-dependency checking | J-25 | Add a Production-mode scope-validation test if JBS wants it asserted |

## 5. P3: JBS Infrastructure and DBA confirmation

These are listed in full, with reasons, in section 7 of the compliance document. The ones that change the plan:

| Question | Changes |
|---|---|
| I-12 Entra OIDC acceptable instead of Negotiate? | P0-1 from a build (L) to configuration and proof (M) |
| I-05 Anonymous off for the whole site? | Whether health, sign-in and setup need path-level exceptions or a separate monitor path |
| I-04 *Always Running* pool or a worker host? | P0-3 form |
| I-09 and I-10 Application Insights and Fatal route | P1-1 scope; P0-4 sink choice |
| I-07 Repository home and agents | P1-2 shape |
| I-13 `Jbssa.` mandatory? | Whether a rename slice exists |
| I-14 Do Sections 5, 9, 10 and 11 bind? | Whether P1-1 (the Section 5 part), P1-2 and P2-4 to P2-8 are required or advisory |
| DB-01, DB-02 SQL version and collation | Whether more SQL Server versions and a case-sensitive collation must be tested |
| DB-04, DB-10 Accounts and integrated authentication | P0-5 and P1-6 |
| DB-06 and DB-07 Migrations and backups | P1-5; whether the application's own backup feature is switched off |

## 6. Answers to the questions in the review request

**Overall compliance assessment.** Substantially compliant on the architectural and security standards that matter most, and non-compliant on three operational ones. Of 90 rules classified, 33 are compliant, 16 compliant with a different implementation, 7 justified deviations, 9 not applicable and 19 non-compliant (about 13 distinct findings, because the definition-of-done checklist repeats earlier rules). Of the agreed sections (1-4, 6-8), 62 rules: 25 compliant, 9 different implementation, 6 justified deviations, 8 not applicable, 8 non-compliant, 3 for Infrastructure or the DBA and 3 not verified. The business-logic separation from ASP.NET, packages locking, warnings as errors, explicit schemas, parameterised SQL, global antiforgery protection, server-side scope enforcement, authenticate-by-default and dependency scanning all hold, most of them by automated test. What does not hold is Windows sign-in, Serilog with Application Insights, the Azure DevOps pipeline, and verified IIS hosting.

**P0 gaps.** Windows sign-in (or an agreed alternative); IIS packaging and request limits; background jobs under an always-running pool; a durable, redacted log on the IIS host; and one verified run on Windows with IIS.

**P1 gaps.** Full Serilog with enrichers, Application Insights and the Fatal alert; an Azure DevOps pipeline; configuration conventions (per-site layer, safe base file, discoverable keys, user secrets); project metadata; scripted migrations for the DBA if wanted; an integrated-authentication guard change.

**Justified deviations.** Razor Pages; the modular monolith and `Ecp.*` names; hosted jobs with leases instead of Hangfire; xUnit without Moq and FluentAssertions; primary constructors and one-file module contracts; ADO.NET in operations tooling.

**Infrastructure questions.** I-01 to I-15 (compliance document section 7): chiefly the Windows and IIS versions, Hosting Bundle, app pool identity and idle policy, anonymous access per path, TLS, the repository and agents, how deployments and per-environment settings arrive, Application Insights and the Fatal email route, secret storage, whether Entra OIDC will do, whether `Jbssa.` is mandatory, and whether Sections 5 and 9 to 11 bind.

**DBA questions.** DB-01 to DB-10: version and compatibility level, collation, naming, accounts and least privilege, encryption policy, who runs migrations and in what form, backup and maintenance policy, tenants per database, the system procedures the platform needs, and whether integrated authentication is required.

**Can the current backend architecture be retained?** **Yes.** Nothing in the standards requires reshaping the modular monolith, and the standard's central structural rule (business logic with no ASP.NET dependency) is already enforced by an architecture test. The changes are at the edges: one new identity-provider kind, the logging provider, packaging and configuration files, and a pipeline definition.

**Can IIS be added without redesigning the application?** **Yes.** The host already starts under the in-process model's expectations (framework-dependent publish, environment-variable and secret-reference configuration, health endpoints, HTTPS required in Production, DPAPI-protected key ring on Windows, explicit `migrate` and `setup-token` commands, a Production guard). The work is packaging (`web.config` in the publish output), limits, the pool settings the in-process timers need, durable logging, and Windows authentication. None of it touches the domain, persistence or access model. It has never been run on IIS, so P0-5 is the real test.

**Recommended next implementation slice.** **JBS-1: IIS hosting and Windows sign-in, proved on Windows**, in this order: (1) get answers to I-12, I-05, I-04, DB-04 and DB-06; (2) P0-2 and P0-3, the small packaging and pool changes; (3) P0-4, Serilog with a durable redacted sink, built so P1-1 only adds sinks and enrichers; (4) P0-1, the Windows identity provider (or Entra configuration if I-12 allows); (5) P0-5, the Windows and IIS verification run, recorded in `deploy/STATUS.md`. Follow with JBS-2 (the rest of P1-1 and P1-3), JBS-3 (P1-2, the Azure DevOps pipeline, and P1-5 if the DBA wants scripts). Record the P2 deviations in one ADR for JBS to acknowledge, alongside JBS-1.

## 7. What this plan does not do

It does not change code, rename anything, move the repository, or add packages. It does not declare any environment production-ready. The licence position of any package it names (Serilog, Application Insights, Hangfire, Moq, FluentAssertions) must pass the repository's licence gate and JBS's own policy before it is added.
