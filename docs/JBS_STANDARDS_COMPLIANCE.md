# JBS standards compliance review

Status: **Review only. No application code was changed.** Reviewed branch ux/guided-administration at commit `9f5317e` on 2026-11-10. Companion: `docs/JBS_DEPLOYMENT_GAP_PLAN.md` (what to do about it, ranked). Production approval is external governance and nothing here declares it.

Source of the standards: NEW-APP-STANDARDS-GUIDE.md ("Building a New Web App at JBS Australia"), read in full. This platform is a product with a JBS reference configuration, not a JBS-only application (CLAUDE.md, PORTABILITY_AND_NO_CODE.md); several findings turn on that.

## Update: state after the JBS IIS integration slice (branch jbs/iis-integration)

The review below describes the code as it was reviewed. Since then branch jbs/iis-integration changed the following (details and evidence in `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`; none of it has run on IIS, so each moved item is at best **COMPLIANT** with the IIS part **NOT VERIFIED**):

| Area | Reviewed as | Now |
|---|---|---|
| IIS hosting, ASP.NET Core Module V2 (`src/Ecp.Host/web.config`) | Scaffold only | Published with `dotnet publish`; in-process; checked in CI. NOT VERIFIED on IIS |
| Windows Authentication | NON-COMPLIANT (not built) | COMPLIANT WITH DIFFERENT IMPLEMENTATION: IIS authenticates; the application authorises by invitation and its own roles (owner decision). NOT VERIFIED on IIS |
| Configuration conventions, safe defaults | Demo values in the base settings file | Production-safe base file; Development file never published; Production refuses blank, temporary or in-application storage and automatic migration |
| Secrets and connection strings | Production forced a secret file | Integrated authentication needs no secret; anything with a credential must be a secret reference |
| Logging (Serilog, correlation, redaction, durable file) | NON-COMPLIANT | Foundation COMPLIANT; Application Insights and fatal-error email are configurable and NOT VERIFIED |
| Data protection | Per-user DPAPI by default, undocumented | Explicit settings, readiness R-SEC-04, documented; NOT VERIFIED on Windows |
| Request limits and timeouts | Defaults only; document uploads would have stopped at 30 MB | Explicit per-URL limits and timeout policy, tested for consistency |
| Backups owned by the DBA | The application's backup feature was the only path | External recovery points, declared and audited; JUSTIFIED DEVIATION recorded in ADR-0043 pending DBA confirmation |
| Pipeline | Pull request 1 as written | Waits for green CI on the commit, one verified build, rollback of a half-copied site, no secrets in `web.config`, rewritten environment setup script (not executed) |

Items that belong to JBS Infrastructure or the DBA remain **REQUIRES JBS INFRASTRUCTURE CONFIRMATION** and are listed in `docs/JBS_IIS_IMPLEMENTATION_STATUS.md`.

## 1. How to read this document

**Method.** Each rule was compared with the real code, project files, configuration, authentication, persistence, logging, deployment files, tests and CI. Evidence names files so it can be checked. Nothing was run on Windows, IIS, Azure DevOps or a JBS SQL Server; those are labelled **NOT VERIFIED** rather than assumed.

**Classifications.**

| Class | Meaning |
|---|---|
| COMPLIANT | The rule is met as written |
| COMPLIANT WITH DIFFERENT IMPLEMENTATION | The rule's purpose is met by a different means; JBS should see and accept the difference |
| JUSTIFIED DEVIATION | The rule is not followed, for a reason that is stated here; needs JBS acknowledgement, not necessarily a change |
| NON-COMPLIANT | The rule is not met and there is no justification; a change is needed |
| NOT APPLICABLE | The rule concerns something this application does not have |
| REQUIRES JBS INFRASTRUCTURE CONFIRMATION | The answer belongs to JBS Infrastructure or the DBA team (the guide's own "Confirm" items) |
| NOT VERIFIED | The artefact exists, or the behaviour is expected, but it was not proved in a JBS-like environment |

**Which sections bind.** The guide's own status note says Sections 1 to 4 and 6 to 8 are the agreed development standards, and that items marked "Confirm" are owned by Infrastructure or the DBA team. It does not say that of Sections 5 (Logging), 9 (Testing), 10 (Source control and delivery) or 11 (Definition of done). Those four are reviewed and classified separately below and are **not treated as binding until JBS confirms them** (question I-14). One wrinkle matters: the Section 1 table, which is agreed, names Serilog and xUnit + Moq + FluentAssertions, so the *choice* of those libraries is treated as agreed even though the detail in Sections 5 and 9 is not.

| Guide section | Status per the guide | Treated here as |
|---|---|---|
| 1 Pick your stack | Agreed | Binding |
| 2 Solution layout | Agreed | Binding |
| 3 Code structure rules | Agreed | Binding |
| 4 Configuration | Agreed | Binding |
| 5 Logging | Not listed as agreed | Guide content; binding status unconfirmed |
| 6 Hosting on IIS | Agreed, but 6.4 is "Confirm with Infrastructure" | Binding, except 6.4 |
| 7 SQL Server | Agreed, but 7.4 is "Confirm with the DBA team" | Binding, except 7.4 |
| 8 Security | Agreed ("non-negotiable") | Binding |
| 9 Testing | Not listed as agreed | Guide content; binding status unconfirmed |
| 10 Source control and delivery | Not listed as agreed | Guide content; binding status unconfirmed |
| 11 Definition of done | Not listed as agreed | Guide content; binding status unconfirmed |

## 2. Scoreboard

| Classification | Agreed sections (1-4, 6-8) | Unconfirmed sections (5, 9-11) | Total |
|---|---:|---:|---:|
| COMPLIANT | 25 | 8 | 33 |
| COMPLIANT WITH DIFFERENT IMPLEMENTATION | 9 | 7 | 16 |
| JUSTIFIED DEVIATION | 6 | 1 | 7 |
| NON-COMPLIANT | 8 | 11 | 19 |
| NOT APPLICABLE | 8 | 1 | 9 |
| REQUIRES JBS INFRASTRUCTURE CONFIRMATION | 3 | 0 | 3 |
| NOT VERIFIED | 3 | 0 | 3 |
| **Rules classified** | **62** | **28** | **90** |

The Section 11 checklist repeats rules from earlier sections, and J-38 repeats J-10, so the totals count some findings twice. The distinct non-compliances are: Serilog not adopted (J-10, J-38); enrichers and sinks (J-40, J-41); Windows Negotiate (J-45); `requestTimeout` and request limits (J-50); csproj metadata (J-21); the per-site configuration file (J-31); an unsafe base default (J-33); undiscoverable settings (J-34); user secrets (J-36); the Azure DevOps pipeline and its triggers (J-74, J-75); and branch and commit naming (J-76, J-77). Only the first four groups and the pipeline affect whether JBS can run and operate the application.

## 3. The rule-by-rule matrix

Columns: ID, guide section, the rule, classification, and the evidence or reason. "Detail" points to section 5, which gives the seven required fields for every non-compliance and deviation.

### 3.1 Section 1: stack (agreed)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-01 | 1 | Target .NET 10 for new projects | COMPLIANT | `Directory.Build.props` `net10.0`; `global.json` SDK 10.0.112; EF Core 10.0.12; Docker images `dotnet/sdk:10.0` and `aspnet:10.0` |
| J-02 | 1 | Server-rendered UI: ASP.NET Core MVC + Razor, cookie auth, antiforgery | COMPLIANT WITH DIFFERENT IMPLEMENTATION | **Razor Pages** (page models and `.cshtml`), not MVC controllers and views. Cookie session and antiforgery as required (`Program.cs`). Detail D-02 |
| J-03 | 1 | Web API for a SPA or other system, Windows Negotiate internally | NOT APPLICABLE | There is no SPA and no API consumer. `/api/v1/info`, `/health/*` and OpenAPI (non-Production) are the only API surface. Negotiate for people is J-45 |
| J-04 | 1 | SPA front end: Vue 3, Vuetify, Vite | NOT APPLICABLE | Server-rendered; no SPA |
| J-05 | 1 | Long-running background work: Generic Host `IHostedService` / Worker Service | COMPLIANT | `BackgroundJobService : BackgroundService` registered with `AddHostedService` |
| J-06 | 1 | Scheduled / queued jobs: Hangfire (SQL Server storage) | JUSTIFIED DEVIATION | Timer plus database operation leases and an outbox (ADR-0034, `docs/OPERATION_LEASES.md`). Detail D-06 |
| J-07 | 1 | Service-to-service messaging: MassTransit over RabbitMQ | NOT APPLICABLE | One process; no service-to-service messaging. Outbound email, scanner and signature adapters are provider interfaces, not a message bus |
| J-08 | 1 | Relational data, rich model: EF Core | COMPLIANT | 14 module `DbContext`s over EF Core 10 (`src/Ecp.Persistence`, `src/Modules/*/Persistence`) |
| J-09 | 1 | Relational data, read-heavy: Dapper + parameterised SQL | NOT APPLICABLE | Dapper is not used. Read-heavy register queries are EF Core, with SQL Server performance baselines |
| J-10 | 1 | Logging: Serilog | NON-COMPLIANT | `Microsoft.Extensions.Logging` with the framework default providers; no Serilog package anywhere. Detail D-10 |
| J-11 | 1 | Tests: xUnit | COMPLIANT | xUnit 2.9.3 in all five test projects (2,085 tests locally at this commit) |
| J-12 | 1 | Tests: Moq | JUSTIFIED DEVIATION | No mocking library. Hand-written fakes (`FakeClock`, mock providers), real in-process hosts and real SQL Server. Detail D-12 |
| J-13 | 1 | Tests: FluentAssertions | JUSTIFIED DEVIATION | `Assert` from xUnit throughout; also a licensing point. Detail D-12 |

### 3.2 Section 2: solution layout and project files (agreed)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-14 | 2 | Organise by responsibility (Web / BusinessLogic / Data / Service / Tests / Libraries) | JUSTIFIED DEVIATION | A modular monolith: one Host, 15 modules (each with domain, services and its own persistence), shared kernel and persistence, a pack tool, five test projects. Tier rules enforced by 113 architecture tests. Not reshaped, per instruction. Detail D-14 |
| J-15 | 2 | Project names `Jbssa.<App>.*` | JUSTIFIED DEVIATION | `Ecp.*` throughout. Detail D-15; question I-13 |
| J-16 | 2 | The business-logic layer must not reference ASP.NET types | COMPLIANT | Only `Ecp.Host` references `Microsoft.AspNetCore.*`. No module, the shared kernel or persistence does. Enforced: architecture test ARC-009 (purity) forbids `Microsoft.AspNetCore` in module domain code |
| J-17 | 2 | SDK-style project files only | COMPLIANT | All 26 project files are `Sdk=` projects |
| J-18 | 2 | Commit `packages.lock.json` (`RestorePackagesWithLockFile`) | COMPLIANT | 26 lock files committed; `Directory.Build.props` sets it; CI restores with `--locked-mode`; central package management with transitive pinning |
| J-19 | 2 | `TreatWarningsAsErrors=true`, warning-clean build | COMPLIANT | Set in `Directory.Build.props`; `NU1900` (NuGet audit source unreachable) is the single exception; builds locally and in CI with 0 warnings |
| J-20 | 2 | No copyright headers in source files | COMPLIANT | None found in `src/` |
| J-21 | 2 | csproj metadata `Company=JBS Australia`, `Copyright`, `NeutralLanguage=en-AU` | NON-COMPLIANT | None of the three is set. Detail D-21 |

### 3.3 Section 3: code structure (agreed)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-22 | 3 | Thin controllers; logic in workflow services or managers; identity and `HttpContext` handled at the boundary | COMPLIANT WITH DIFFERENT IMPLEMENTATION | There are no controllers. Page-model handlers validate input and call module services (`ISecured*Service`, `I*Studio`, and so on); decisions live in the services, which also enforce access. Detail D-02 |
| J-23 | 3 | Constructor injection into `private readonly` fields | COMPLIANT WITH DIFFERENT IMPLEMENTATION | C# primary constructors (the parameters are captured; no explicit fields). Same effect; a style difference. Detail D-23 |
| J-24 | 3 | Register services per layer with `AddX(this IServiceCollection, IConfiguration)` | COMPLIANT WITH DIFFERENT IMPLEMENTATION | 15 `AddXModule` extensions plus `AddEcpPersistence` and `AddEcpPlatformModules`; most take an options delegate or typed arguments, and the Host does the configuration binding (`PlatformComposition`). Keeps modules free of `IConfiguration`. Detail D-23 |
| J-25 | 3 | Lifetimes chosen deliberately; no Scoped service captured by a Singleton | NOT VERIFIED | Lifetimes are deliberate (per-request services Scoped; hosts, providers and options Singleton; the background service creates scopes through `IServiceScopeFactory`). No test asserts the absence of captive dependencies; the framework's scope validation applies in Development hosts, which the API tests use, but it was not confirmed for Production |
| J-26 | 3 | Named `HttpClient`s through `IHttpClientFactory`, matching names | NOT APPLICABLE | There is no outbound `HttpClient` in `src/`. The OpenID Connect back-channel is the framework's. Any future HTTP adapter must use the factory |
| J-27 | 3 | Interface and implementation in separate files | JUSTIFIED DEVIATION | Each module's public contract (interfaces, records, options) lives in one `*Api.cs`-style file beside its internal implementations. Detail D-27 |
| J-28 | 3 | Prefer constants to magic strings; XML doc comments on public members | COMPLIANT | Constants for permission, kind and type codes; public members are documented (also relied on by `check-docs`) |
| J-29 | 3 | Match the file being edited | COMPLIANT | Process rule; the repository's own instruction (CLAUDE.md) says the same |

### 3.4 Section 4: configuration (agreed)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-30 | 4 | Layered configuration: `appsettings.json`, `appsettings.{Env}.json`, user secrets, per-site file, environment variables | COMPLIANT WITH DIFFERENT IMPLEMENTATION | `WebApplication.CreateBuilder` supplies the standard layers (JSON, environment-specific JSON, user secrets in Development, environment variables, command line). The per-site layer is missing (J-31) |
| J-31 | 4 | A per-site / per-deployment override file (`appsettings.{Env}.{SiteCode}.json`) | NON-COMPLIANT | Not added. Tenant and organisation differences are configuration in the database, not files. Detail D-31 |
| J-32 | 4 | Prefer config over hardcoding; no branching on a site code | COMPLIANT | Business differences are packs and configuration documents; the vocabulary guard test forbids business literals in generic source |
| J-33 | 4 | A missing or unparseable setting falls back to the safe, current-behaviour state | NON-COMPLIANT | In code, yes: an unknown `Ecp:Mode` becomes Production and `ProductionGuard` refuses unsafe Production settings. But the shipped base `appsettings.json` sets `Ecp:Mode` to **Demo** with `DemoSeed` true, so a host started without overrides runs as a demonstration. Detail D-33 |
| J-34 | 4 | Every setting is discoverable as an empty key in the base `appsettings.json` | NON-COMPLIANT | Keys read by the code but absent from the base file include `Notifications`, `Contracts`, `Ecp:Network:*`, `Ecp:Setup:*`, `Ecp:Email:*`, `Ecp:Localisation`, `Ecp:Governance:SecondPerson`, `Identity:Demo` and `Documents:Scanner`. They are documented in the guides, not in the file. Detail D-34 |
| J-35 | 4 | Never commit secrets; connection strings and keys come from user secrets or pipeline variables | COMPLIANT | Secrets are references (`env:`, `file:`), never values; Production refuses inline connection strings; `Redactor`; CI fails if evidence contains `Password=`/keys; test strings are visibly fake |
| J-36 | 4 | User secrets on developer machines | NON-COMPLIANT | The Host has no `UserSecretsId`; developers use environment variables or secret files (`docs/WINDOWS_LAPTOP_QUICKSTART.md`). Detail D-36 |
| J-37 | 4 | Read with `GetValue<T>` and `GetConnectionString` | COMPLIANT WITH DIFFERENT IMPLEMENTATION | `GetValue` and `GetSection().Bind` are used; the connection string is `Database:ConnectionString` or a secret reference `Database:ConnectionStringRef`, not `ConnectionStrings:<name>`. Detail D-37 |

### 3.5 Section 5: logging (guide content; binding status unconfirmed)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-38 | 5 | Serilog, configured in code | NON-COMPLIANT | See J-10. Detail D-10 |
| J-39 | 5 | Structured properties, never interpolation | COMPLIANT | `LoggerMessage.Define` and message templates; no interpolated log call found (`OperationalLog`, host and module loggers) |
| J-40 | 5 | Standard enrichers: thread id, process id, exception details, `FromLogContext`, and ApplicationName, ApplicationVersion, SiteCode, ApplicationDatabase, Login | NON-COMPLIANT | A correlation id is in the operational messages and the request pipeline, and actor context carries the user; none of the listed enrichers exists. Detail D-40 |
| J-41 | 5 | Sinks by level: Console (development), Application Insights at Information and above, email for Fatal only | NON-COMPLIANT | Only the framework providers and an in-memory redacted buffer for the support bundle (`RecentLogBuffer`). No Application Insights, no email-on-Fatal. Detail D-40 |
| J-42 | 5 | Inject `ILogger<T>`; static `Log` only at startup and shutdown | COMPLIANT | `ILogger<T>` injected (29 uses); the host uses `app.Logger` at start-up; there is no static logger |
| J-43 | 5 | Log exceptions as objects | COMPLIANT | `LogError(ex, "...")` pattern (for example `BackgroundJobService`) |
| J-44 | 5 | Application Insights connection strings are environment config | NOT APPLICABLE | Application Insights is not used (J-41). When adopted this must hold |

### 3.6 Section 6: hosting on IIS (agreed; 6.4 is for Infrastructure)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-45 | 6.3 | Internal apps use Windows (Negotiate) authentication via `Microsoft.AspNetCore.Authentication.Negotiate` | NON-COMPLIANT | Not present. People sign in through OpenID Connect (generic and a Microsoft Entra adapter, live tenant Not Verified), or the Demo identity (Demo/Test only). Detail D-45 |
| J-46 | 6.1 | ASP.NET Core Module V2 | COMPLIANT | `src/Ecp.Host/web.config` maps `AspNetCoreModuleV2`. Scaffold only; see J-55 |
| J-47 | 6.1 | In-process hosting model | COMPLIANT | `hostingModel="inprocess"` in the scaffold |
| J-48 | 6.1 | Needs the .NET 10 Hosting Bundle | REQUIRES JBS INFRASTRUCTURE CONFIRMATION | Documented as step 1 of `deploy/iis/README.md`. Which bundle versions are on JBS servers is Infrastructure's (J-52) |
| J-49 | 6.1 | `web.config` shape; `stdoutLogEnabled="false"` normally | COMPLIANT WITH DIFFERENT IMPLEMENTATION | Handlers, location and `stdoutLogEnabled="false"` match. `processPath="dotnet" arguments=".\Ecp.Host.dll"` (framework-dependent) instead of an `.exe`. Environment values in the file are non-secret. **The file is not part of the publish output** (J-51). Detail D-49 |
| J-50 | 6.1 | Set `requestTimeout` explicitly for long requests and document why | NON-COMPLIANT | Not set. Large exports and data-migration uploads are long requests. Detail D-50 |
| J-51 | 6.1 | Environment-specific values come from the environment, not code | COMPLIANT | `Ecp__*` and `Database__*` environment variables in `web.config`; secrets by file or environment reference. The deliverable gap is that `dotnet publish` generates its own `web.config` and the README does not say to overlay `src/Ecp.Host/web.config` (Detail D-49) |
| J-53 | 6.1 | `ASPNETCORE_HTTPS_PORT` for a non-default HTTPS port behind IIS | NOT APPLICABLE | The application does not redirect to HTTPS; in Production it requires HTTPS and refuses plain HTTP with an explanation (`docs/REVERSE_PROXY.md`). Revisit only if a non-default port is used |
| J-54 | 6.2 | Vue SPA rewrite rule, URL Rewrite module, API as a separate IIS application | NOT APPLICABLE | No SPA |
| J-55 | 6.1-6.3 | The IIS deployment has been run | NOT VERIFIED | The build workspace is Linux. IIS, Windows Server, Windows authentication and the scaffold were never executed (`deploy/STATUS.md`) |
| J-56 | 6.3 | Because browsers attach credentials automatically, CSRF and scope validation are mandatory | COMPLIANT | See J-63 and J-64; they hold independently of how the user signs in |
| J-52 | 6.4 | Infrastructure confirms: IIS and Windows Server versions, Hosting Bundle versions, app pool identity/recycle/idle/No Managed Code, site/host/port, TLS and renewal, Windows Authentication on and Anonymous off, how deployments and per-environment `appsettings` reach the server | REQUIRES JBS INFRASTRUCTURE CONFIRMATION | Questions I-01 to I-11. "Anonymous off" conflicts with the application's own anonymous endpoints (D-55) |

### 3.7 Section 7: SQL Server (agreed; 7.4 is for the DBA team)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-57 | 7.1 | One data-access approach per application; do not mix casually | COMPLIANT WITH DIFFERENT IMPLEMENTATION | EF Core for all application data. ADO.NET is used only inside operations tooling that EF cannot express: backup, restore, validation, upgrade pre-flight and inventory (`Ecp.Recovery`), the session-context call (`ScopedConnection`), and the reference-number and audit-lock statements. Detail D-57 |
| J-58 | 7.1 | EF Core: fluent `OnModelCreating`, explicit schemas | COMPLIANT | Each module's context sets `HasDefaultSchema` (`access`, `audit`, `configuration`, `contracts`, `documents`, `identity`, `lifecycle`, `notifications`, `ops`, `setup`, `tenancy`, and others); the `__EFMigrationsHistory` table is per schema. Nothing lives in `dbo` |
| J-59 | 7.1 | EF Core: migrations checked in | COMPLIANT | 72 migrations committed under each module; the upgrade manifest and frozen baselines are verified in CI (N-1 to N rehearsal on SQL Server) |
| J-60 | 7.2 | All SQL parameterised | COMPLIANT | EF raw SQL uses `{0}` placeholders (parameters, not interpolation); ADO.NET uses `@parameters`. Reviewed exceptions: `DBCC CHECKDB (N'<name>')`, which cannot take a parameter and uses a name checked by `SafeFiles.IsTargetDatabaseName`; and internal constant table names. No SQL is built from request values. Detail D-60 |
| J-61 | 7.3 | Explicit schemas per domain area | COMPLIANT | See J-58 |
| J-62 | 7.3 | Connection strings from configuration; no committed credentials; prefer Windows/integrated authentication | NOT VERIFIED | From configuration and secrets, yes. Integrated authentication is just a connection-string choice and is used in the Windows quick start, but the Production guides use a SQL login (`ecp_app`), and an integrated string from an IIS pool identity was never run (the quick start says so). Detail D-62 |
| J-65 | 7.3 | Hangfire uses SQL Server storage in the application database | NOT APPLICABLE | Hangfire is not used (J-06) |
| J-66 | 7.3 | A parameter that scopes which rows are touched is validated server-side against the allowed set | COMPLIANT | See J-64 |
| J-67 | 7.4 | DBA confirms: SQL Server version and compatibility level; collation; naming conventions; service account and least-privilege roles; Encrypt/TrustServerCertificate policy; who runs migrations in production; backup, retention and maintenance policy | REQUIRES JBS INFRASTRUCTURE CONFIRMATION | Questions DB-01 to DB-10 in section 7. Tested on SQL Server 2022 only |

### 3.8 Section 8: security (agreed, "non-negotiable")

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-63 | 8.1 | CSRF defence on every state-changing endpoint, registered globally | COMPLIANT | Razor Pages validate antiforgery tokens on every non-GET handler by framework default; the `ecp.csrf` cookie is `SameSite=Strict`; `AntiforgeryTests.Every_state_changing_page_handler_is_protected_by_antiforgery_validation` walks every page and fails on an opt-out (the Error page, which only re-renders, is the one named exception). There is no state-changing minimal-API endpoint |
| J-64 | 8.2 and 8.3 | Validate scoping parameters server-side; authorise on scope, not only on resource type | COMPLIANT | An eight-layer access evaluator (tenant, organisation, business unit, site, category, classification ceiling, role permission) applied in the services; pages resolve an organisation only from the person's grants and answer "not found" otherwise; SQL Server row-level security by tenant. Tested (`AccessEvaluatorTests`, tenant isolation suites, SQL Server RLS suites) |
| J-68 | 8.4 | Authenticate by default: `[Authorize]` at class level; `[AllowAnonymous]` per action with a comment saying who calls it and why | COMPLIANT WITH DIFFERENT IMPLEMENTATION | A **fallback authorisation policy** requires a signed-in user for every endpoint (ARC-008), so nothing is anonymous by accident; pages add permission policies. `[AllowAnonymous]` is used for sign-in, sign-out, error, setup and health pages and endpoints, but only some carry a comment naming the caller and the reason. Detail D-68 |
| J-69 | 8.5 | Dependency scanning before a change is done: `dotnet list package --vulnerable --include-transitive`; `npm audit` for front ends | COMPLIANT | `tools/supply-chain.sh` runs exactly that command and gates CI, plus `NuGetAudit` (all, low) at restore, a CycloneDX SBOM and a licence-policy scan. `npm audit` is not applicable (no front-end packages) |

### 3.9 Section 9: testing (guide content; binding status unconfirmed)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-70 | 9 | Test names `{Action}{Condition}{ExpectedResult}` | COMPLIANT WITH DIFFERENT IMPLEMENTATION | Sentence names with underscores (for example `Unknown_permissions_are_rejected_rather_than_silently_denied`): same three parts, different shape. Detail D-12 |
| J-71 | 9 | Arrange / Act / Assert with mock-factory classes | COMPLIANT WITH DIFFERENT IMPLEMENTATION | Unit tests follow the shape informally; scenario tests share scenario bodies across SQLite and SQL Server. Fakes replace mock factories |
| J-72 | 9 | Test business logic (services and managers), not controllers | COMPLIANT | Domain and services are tested directly (979 unit, 731 integration); the page layer is covered by HTTP journeys as an addition |
| J-73 | 9 | The test project mirrors the source folder structure | JUSTIFIED DEVIATION | Five suites by kind (unit, architecture, integration, API, shared helpers) rather than one per source project. Detail D-12 |

### 3.10 Section 10: source control and delivery (guide content; binding status unconfirmed)

| ID | § | Rule | Classification | Evidence and notes |
|---|---|---|---|---|
| J-74 | 10 | Azure DevOps YAML pipelines | NON-COMPLIANT | CI is GitHub Actions (`.github/workflows/ci.yml`, three jobs). There is no azure-pipelines.yml. Detail D-74 |
| J-75 | 10 | Triggered on `master` with path filters, `release` configuration, one job per deployable | NON-COMPLIANT | Triggers on every push to every branch and on pull requests; the default branch is `main`; no path filters. `Release` configuration is used. Deployables are one web host and one tool. Detail D-74 |
| J-76 | 10 | Branches `<ticket>-<kebab-description>`; release branches `releases/<YYYY>R<NN>` | NON-COMPLIANT | ux/guided-administration, claude/.... No ticket system and no release branches exist. Detail D-76 |
| J-77 | 10 | Commits lead with the work-item number | NON-COMPLIANT | Commits lead with the slice ("UX-10: ..."). No JBS work items exist. Detail D-76 |
| J-78 | 10 | Design first for non-trivial work: a dated markdown doc under `docs/` with Goal, Approach, Architecture, Components, What does NOT change, Out of scope | COMPLIANT WITH DIFFERENT IMPLEMENTATION | 41 numbered ADRs under `decisions/` (Context, Decision, Alternatives considered, Consequences) plus guides under `docs/`; not date-named, not in the guide's section order. Detail D-78 |
| J-79 | 10 | Plans use numbered tasks with `- [ ]` checkboxes | COMPLIANT WITH DIFFERENT IMPLEMENTATION | `TASKS.md` and `docs/IMPLEMENTATION_PLAN.md` are numbered and status-labelled, without checkboxes |

### 3.11 Section 11: definition of done (guide content; binding status unconfirmed)

| ID | § | Item | Classification | Evidence and notes |
|---|---|---|---|---|
| J-80 | 11 | Style matches neighbours; namespace matches folder path | COMPLIANT | Namespaces follow folders (`Ecp.Host.Pages.Admin.Fields` under Pages/Admin/Fields); enforced by review and the compiler-clean build |
| J-81 | 11 | Business logic in the services layer, not the controller | COMPLIANT WITH DIFFERENT IMPLEMENTATION | See J-22 |
| J-82 | 11 | DI by constructor injection, registered in an `AddX` extension, lifetimes correct | COMPLIANT WITH DIFFERENT IMPLEMENTATION | See J-23, J-24; lifetimes J-25 NOT VERIFIED |
| J-83 | 11 | New settings use the layered scheme; no secrets committed | NON-COMPLIANT | Secrets: compliant. Settings: new keys are not added to the base file (J-34) |
| J-84 | 11 | Logging is structured Serilog with the standard enrichers | NON-COMPLIANT | See J-10, J-40 |
| J-85 | 11 | All SQL parameterised; one data-access approach used consistently | COMPLIANT | See J-57, J-60 |
| J-86 | 11 | Every state-changing endpoint has CSRF defence and scope validation | COMPLIANT | See J-63, J-64 |
| J-87 | 11 | New services have unit tests named `{Action}{Condition}{ExpectedResult}` | COMPLIANT WITH DIFFERENT IMPLEMENTATION | See J-70 |
| J-88 | 11 | Build warning-clean, `packages.lock.json` updated, dependency scan clean | COMPLIANT | See J-18, J-19, J-69. Two licence items remain pending owner review (recorded, not hidden) |
| J-89 | 11 | IIS `web.config` reviewed (module, timeout, stdout logging off) | NON-COMPLIANT | Module and stdout are right; timeout is not set (J-50); the file is not wired into publish (J-49); never run on IIS (J-55) |
| J-90 | 11 | Branch and commit named per convention; design doc for non-trivial work | NON-COMPLIANT | See J-76, J-77; design docs J-78 |

## 4. Cross-reference: the 41 points of attention

| # | Topic | Rule IDs | Result in one line |
|---|---|---|---|
| 1 | .NET 10 | J-01 | Compliant |
| 2 | ASP.NET Core MVC/Razor | J-02, J-22 | Razor Pages; different implementation |
| 3 | Project/solution structure | J-14, J-15 | Justified deviation (modular monolith, `Ecp.*`) |
| 4 | Business logic separation from ASP.NET | J-16 | Compliant, and enforced by an architecture test |
| 5 | SDK-style csproj | J-17 | Compliant |
| 6 | packages.lock.json | J-18 | Compliant |
| 7 | Warnings as errors | J-19 | Compliant |
| 8 | DI conventions | J-23 to J-25 | Different implementation; lifetimes not asserted |
| 9 | IHttpClientFactory | J-26 | Not applicable today |
| 10 | Layered configuration | J-30, J-31 | Standard layers yes; per-site file missing |
| 11 | Secrets handling | J-35, J-36 | Compliant; user-secrets convention missing |
| 12 | Serilog requirements | J-10, J-38 to J-43 | Non-compliant (not adopted) |
| 13 | Standard enrichers | J-40 | Non-compliant |
| 14 | Application Insights | J-41, J-44 | Non-compliant (not adopted) |
| 15 | IIS ASP.NET Core Module V2 | J-46 | Scaffolded, compliant in content, Not Verified |
| 16 | In-process hosting | J-47 | Scaffolded, compliant in content, Not Verified |
| 17 | .NET 10 Hosting Bundle | J-48 | Documented; versions are Infrastructure's |
| 18 | web.config | J-49 to J-51 | Different implementation; not in publish; no `requestTimeout` |
| 19 | Windows Negotiate authentication | J-45 | **Non-compliant** |
| 20 | Anonymous authentication policy | J-52, J-68 | Needs Infrastructure; conflicts with anonymous app endpoints |
| 21 | EF Core | J-08, J-58 | Compliant |
| 22 | Explicit schemas | J-58, J-61 | Compliant |
| 23 | Migrations | J-59 | Compliant; scripted delivery for a DBA not provided (D-62) |
| 24 | SQL Server | J-62, J-67 | Tested on 2022 only; DBA items open |
| 25 | Integrated authentication | J-62 | Supported by the connection string; Not Verified |
| 26 | Parameterised SQL | J-60 | Compliant, with two reviewed exceptions |
| 27 | Hangfire | J-06 | Justified deviation |
| 28 | MassTransit/RabbitMQ applicability | J-07 | Not applicable |
| 29 | CSRF protection | J-63 | Compliant, test-enforced |
| 30 | Server-side scope validation | J-64, J-66 | Compliant |
| 31 | Authorisation scope | J-64 | Compliant |
| 32 | Authentication by default | J-68 | Compliant with different implementation (fallback policy) |
| 33 | xUnit | J-11 | Compliant |
| 34 | Moq | J-12 | Justified deviation |
| 35 | FluentAssertions | J-13 | Justified deviation |
| 36 | Dependency vulnerability scanning | J-69 | Compliant |
| 37 | Azure DevOps YAML | J-74, J-75 | Non-compliant (GitHub Actions) |
| 38 | Branch naming | J-76 | Non-compliant (cosmetic) |
| 39 | Commit naming | J-77 | Non-compliant (cosmetic) |
| 40 | Design documentation | J-78, J-79 | Different implementation (ADRs) |
| 41 | Definition-of-done compliance | J-80 to J-90 | See 3.11: 4 compliant, 3 different implementation, 4 non-compliant |

## 5. Detail for every non-compliance and deviation

Each entry gives: current implementation; the JBS requirement; risk; whether code must change; whether documentation alone is enough; whether JBS Infrastructure or the DBA team must confirm; and the recommended remediation. Rank and sequencing are in `docs/JBS_DEPLOYMENT_GAP_PLAN.md`.

### D-45 Windows Negotiate authentication (J-45; also J-52 anonymous policy)

* **Current implementation.** An invite-only account model that links a person by (issuer, subject). Sign-in is OpenID Connect (generic, plus a Microsoft Entra adapter validated only against a mock) or the Demo identity. A signed cookie session (HttpOnly, SameSite Lax, Secure in Production, 8 hours sliding) is issued after a verified sign-in. Identity-group mappings grant and end roles at each sign-in from provider claims. There is no Windows or Negotiate handler, no Windows account in the user model, and no mapping from Active Directory groups.
* **JBS requirement.** Internal apps use Windows (Negotiate) authentication via `Microsoft.AspNetCore.Authentication.Negotiate`; Windows Authentication enabled and Anonymous disabled on the IIS site (the latter is an Infrastructure item).
* **Risk.** **High.** Without it, JBS staff cannot sign in through the JBS standard. It is the one gap that blocks use rather than merely differing in style. Secondary risks: an IIS site with Anonymous disabled would also block `/health/*` probes, `/SignIn` and the setup pages the application deliberately leaves anonymous; and the Production guard and onboarding assume an OpenID Connect provider.
* **Code must change.** Yes, unless JBS confirms that its Entra ID tenant through OpenID Connect is acceptable in place of Negotiate (question I-12).
* **Documentation only.** No.
* **JBS Infrastructure confirmation.** Yes: whether Entra OIDC is acceptable; the domain and UPN conventions; the AD groups that should map to roles; the IIS authentication settings per path (I-05, I-12).
* **Recommended remediation.** Add a third identity-provider kind, Windows, that uses the Negotiate handler to establish who the person is on one sign-in page, matches an **invited** account by a stable Windows identifier (SID or UPN, decided with JBS), maps AD groups through the existing group-mapping engine, then issues the same signed session cookie, so nothing downstream changes and Negotiate is not repeated per request. Keep CSRF protection (J-56). Under in-process IIS the Windows identity is usually supplied by IIS itself; whether the guide's Negotiate package or IIS's own handler is the right one is to be settled in the Windows test environment (the Negotiate package is designed for Kestrel and HTTP.sys). Allow anonymous only on the paths that need it, or give monitors a separate anonymous health path. Add the matching Production-guard check and tests; mark live Windows verification Not Verified until run.

### D-10 / D-40 Serilog, enrichers, Application Insights and the Fatal alert (J-10, J-38, J-40, J-41, J-44)

* **Current implementation.** The framework's logging with its default providers; a compact set of structured `LoggerMessage` events carrying a correlation id; an in-memory, redacted recent-lines buffer used only by the support bundle; operational metrics through .NET `Meter` with no exporter configured (stated in `OperationalTelemetry`: OpenTelemetry and SIEM sinks are INT-04, Not Verified). Under IIS in-process with `stdoutLogEnabled="false"`, console output goes nowhere; whatever the framework's other default providers keep (on Windows, the Event Log at Warning and above) is expected to be the only durable record, a point to confirm in the Windows test.
* **JBS requirement.** Serilog configured in code (Section 1, agreed); enrichers (thread id, process id, exception details, `FromLogContext`, ApplicationName, ApplicationVersion, SiteCode, ApplicationDatabase, Login); sinks by level (Console for development, Application Insights at Information and above, email only for Fatal) (Section 5, unconfirmed).
* **Risk.** **Medium to high operationally, and a confidentiality point.** No durable, searchable log on an IIS host is an operational blocker. Adding Application Insights carries contract data off the server: request URLs, exception text and properties can contain identifiers, supplier names or classified content, so a sink without a redaction policy would breach the platform's rule never to log live confidential data. Tenant isolation also means log properties must never mix tenants' data into one view without care.
* **Code must change.** Yes.
* **Documentation only.** No.
* **JBS Infrastructure confirmation.** Yes: the Application Insights resource and data-residency position; the SMTP route and recipients for Fatal alerts; the permitted contents of telemetry (I-09, I-10).
* **Recommended remediation.** Adopt Serilog as the logging provider behind the existing `ILogger<T>` calls so no call site changes. Add the listed enrichers, mapping "SiteCode" to a deliberate organisation or installation identifier agreed with JBS, and "Login" to the actor id rather than a name. Keep the redactor in the pipeline for every sink. Add a rolling file or Event Log sink as the durable minimum, Application Insights at Information and above with a telemetry processor that drops request bodies and query strings, and a Fatal-only email sink. Keep `RecentLogBuffer` and the support bundle working (they read the same log stream). Add tests with canary secrets, as the existing redaction tests do. Treat OpenTelemetry as an alternative only if JBS accepts it.

### D-06 Hangfire versus the hosted-job and lease architecture (J-06)

* **Current implementation.** `BackgroundJobService`, a `BackgroundService` on a `PeriodicTimer`, runs once per active tenant each cycle (notifications from lifecycle events, email delivery, reminders, expired emergency unlocks, document scanning). Each tenant's cycle first takes a database operation lease (`background.jobs`, per tenant), so two instances sharing a database never run the same tenant's jobs together; every job is idempotent, the lease is renewed and a crashed holder's lease expires and is taken over with an audit event. Job runs and outbox items are recorded and shown on the operations dashboard. Operator-only work (backup, restore, upgrade) shares the lease table. Design: ADR-0034, `docs/OPERATION_LEASES.md`.
* **JBS requirement.** Scheduled and queued jobs use Hangfire with SQL Server storage in the application's database; jobs are resolved from DI.
* **Risk.** **Low to medium.** The deviation is functional, not unsafe. Reasons against switching: Hangfire adds its own schema to the application database with no tenant column and no row-level security, while tenant isolation is a platform rule; job arguments and failure text are stored in plain tables; Hangfire's core library carries the LGPL-3.0 licence, which `tools/licence-policy.json` lists as *restricted* and would need an owner-recorded exception; and the existing leases also protect non-recurring operator work Hangfire does not model. Reasons for it: JBS operators would get a familiar dashboard, retries and recurring-job UI. The real operational risk is the same either way: under IIS an application pool that idles or recycles stops in-process timers, and Hangfire has the same dependency.
* **Code must change.** No, unless JBS insists. If it does, the seam is small: the cycle calls `INotificationJobs.RunAllAsync` and `IDocumentScanJob.RunAsync` per tenant, which a Hangfire recurring job could call without changing the services.
* **Documentation only.** Yes: add the Hangfire alternative and the reasons to ADR-0034 or a new ADR, as Section 1 asks ("write the reason in your design doc").
* **JBS Infrastructure confirmation.** Yes, for the pool settings the in-process timers need under IIS: *Always Running* start mode with preload and no idle time-out, or a separate worker host (I-04).
* **Recommended remediation.** Keep the architecture. Record the justified deviation. Add the IIS pool requirements to the deployment guide and a readiness check that reports when no job cycle has run recently (the dashboard already shows cycles). Offer a Hangfire adapter only if JBS requires it, as a separate, owner-approved slice that first resolves the licence and tenant-isolation points.

### D-49 / D-50 `web.config` content, packaging and long requests (J-49, J-50, J-51, J-89)

* **Current implementation.** `src/Ecp.Host/web.config` (Scaffolded, never executed) with the V2 module, in-process hosting, `stdoutLogEnabled="false"`, non-secret environment variables, request filtering with a 100 MB content limit, and header removal. The Host project has no `web.config`, so `dotnet publish` generates a default one; `deploy/iis/README.md` says to publish and copy the output, and does not say to overlay the supplied file. No `requestTimeout` is set (Microsoft's reference describes this attribute for out-of-process hosting, so whether it matters in-process is to be confirmed on the target server). The data-migration upload page accepts up to 2 GB (`[RequestSizeLimit(2_147_483_648)]`, `Migration:MaxUploadBytes`) while the supplied IIS limit is 100 MB, and the pack upload is 9 MB.
* **JBS requirement.** A correct `web.config`; `requestTimeout` set explicitly and documented where long requests exist.
* **Risk.** **Medium.** A default generated `web.config` omits the environment variables Production needs, so the host would start as the base file says (J-33) and be refused or run wrongly; the 100 MB limit would silently block large migration uploads that work under Kestrel.
* **Code must change.** Small: include `web.config` in the publish output (or generate it from the supplied file) and align the limits. No application logic.
* **Documentation only.** Partly: the README step and the long-request rationale.
* **JBS Infrastructure confirmation.** Yes: whether JBS wants environment values in `web.config`, in machine-level variables or in a deployment overlay (I-11).
* **Recommended remediation.** Ship the supplied `web.config` with `dotnet publish`; state the limits next to the application settings they mirror (document, migration upload, pack upload) and choose them with JBS; test a large export and a large migration upload on IIS; record the timeout decision.

### D-14 / D-15 Layout and `Jbssa.<App>` naming (J-14, J-15)

* **Current implementation.** Solution `src/Ecp.sln`; projects `Ecp.Host`, `Ecp.SharedKernel`, `Ecp.Persistence`, `Ecp.Packs`, `Ecp.Formats`, `Ecp.PackTool`, 15 modules `Ecp.<Module>`, and five `Ecp.*Tests` projects; root namespace `Ecp`. Each module separates domain, services and its own persistence, and its public contract from its internals; architecture tests enforce nine tiers and forbid ASP.NET and EF in domain code.
* **JBS requirement.** Organise by responsibility; the example names projects `Jbssa.<App>.Web / BusinessLogic / Data / <Job>.Service / Tests` and a Libraries/ folder.
* **Risk.** **Low.** The responsibility split the guide wants (a Web host, a business-logic layer with no ASP.NET dependency, a data layer, tests) is present, in a finer-grained form. The only risk is perception, or tooling at JBS that assumes a prefix.
* **Code must change.** No, unless JBS says the prefix is mandatory.
* **Documentation only.** Yes: a short mapping table (this document's D-14) in the architecture guide, so a JBS reviewer can find each layer.
* **JBS Infrastructure confirmation.** Question I-13: is `Jbssa.` mandatory for a vendor-neutral, multi-business product?
* **Recommended remediation.** Do not reshape the monolith. Record both as justified deviations. Reasons for keeping `Ecp`: the platform is deliberately not JBS-specific (a test forbids "JBS" in generic source, PRT-001), so a company prefix on every assembly would contradict the portability design; and moving 15 modules, tests, embedded resources, pack tooling and the upgrade manifest to new names is a large mechanical change that gains nothing functional. If JBS does require the prefix, do it as one late, owner-approved rename of assembly and root-namespace names (database schema names and migration identifiers do not carry the namespace; the embedded-resource names, pack and tool names and the upgrade manifest do and would need care).

### D-21 Project metadata (J-21)

* **Current implementation.** `Directory.Build.props` sets version, nullable, warnings, determinism and lock files; no `Company`, `Copyright` or `NeutralLanguage`.
* **JBS requirement.** Metadata in the csproj: `Company=JBS Australia`, `Copyright`, `NeutralLanguage=en-AU`.
* **Risk.** **Low.** Cosmetic, but visible in assembly properties and installers.
* **Code must change.** A build property change only; no source change.
* **Documentation only.** No.
* **JBS Infrastructure confirmation.** The wording of `Copyright`.
* **Recommended remediation.** Supply the three values from the JBS build (pipeline properties or a JBS-owned `Directory.Build.targets`) rather than committing the company name into a vendor-neutral repository (the vocabulary guard scans source for business names). Check that `NeutralLanguage=en-AU` does not change resource fallback in the localisation tests before adopting it.

### D-23 / D-27 Dependency-injection and file conventions (J-23, J-24, J-27)

* **Current implementation.** Primary constructors for injection; 15 `AddXModule` extensions that take typed arguments or option delegates; interfaces, records, options and implementations of one module in the same file (for example `CustomFieldStudio.cs`), with implementations `internal sealed` and the module's public surface in one place.
* **JBS requirement.** Constructor injection into `private readonly` fields; `AddX(IServiceCollection, IConfiguration)`; interface and implementation in separate files.
* **Risk.** **Low.** Style only; behaviour is the same, and encapsulation is enforced by architecture test ARC-009.
* **Code must change.** No.
* **Documentation only.** Yes: record the three conventions as deliberate.
* **JBS Infrastructure confirmation.** No.
* **Recommended remediation.** Justified deviation for existing code. Do not retrofit by a mass edit (high churn, merge risk). If JBS wants the file split, apply it to new code only. Keep modules free of `IConfiguration`; the Host binds configuration (this is stricter than the guide).

### D-02 Razor Pages instead of MVC controllers (J-02, J-22)

* **Current implementation.** Razor Pages with a page model per page; shared partials; the same cookie session, antiforgery and authorisation pipeline as MVC.
* **JBS requirement.** ASP.NET Core MVC + Razor with Controllers/, Views/, ViewModels/.
* **Risk.** **Low.** Razor Pages is part of ASP.NET Core and uses the same filters, tag helpers and view engine; page models play the controller's role and are thin.
* **Code must change.** No.
* **Documentation only.** Yes: record it, and add Razor Pages to the standards as an accepted shape for server-rendered pages if JBS agrees.
* **JBS Infrastructure confirmation.** No (an architecture-owner acknowledgement).
* **Recommended remediation.** Justified; do not convert.

### D-12 Testing libraries (J-12, J-13, J-70, J-73)

* **Current implementation.** xUnit with `Assert`; hand-written doubles; real in-process web hosts; the same scenarios run on SQLite and a real SQL Server 2022; sentence-style test names; five suites by kind.
* **JBS requirement.** xUnit + Moq + FluentAssertions; names `{Action}{Condition}{ExpectedResult}`; a test project that mirrors the source structure (Sections 1 and 9).
* **Risk.** **Low.** The tests prove more than mocks would (real SQL Server, real RLS), and the licence position of newer FluentAssertions releases is a consideration for commercial use that JBS should check against its own policy; the repository's licence gate would flag a package outside its allowed list.
* **Code must change.** No.
* **Documentation only.** Yes: a short "Test strategy" statement.
* **JBS Infrastructure confirmation.** The FluentAssertions version and licence JBS permits.
* **Recommended remediation.** Justified. Adopt Moq or FluentAssertions only in new tests if JBS requires it, after the licence check; do not rewrite 2,085 tests.

### D-31 / D-33 / D-34 / D-36 / D-37 Configuration conventions (J-31, J-33, J-34, J-36, J-37)

* **Current implementation.** Framework layering without a per-site file; a base `appsettings.json` whose `Ecp:Mode` is Demo (with demo seeding and demo tenant resolution on) and that lists only some keys; no user-secrets id; connection string as `Database:ConnectionString` or `Database:ConnectionStringRef`.
* **JBS requirement.** A per-site override layer; a safe default for a missing value; every setting discoverable as an empty key; user secrets on developer machines; `GetConnectionString`.
* **Risk.** **Medium for J-33, low for the rest.** A host run without its environment variables comes up as a demonstration; the Production guard refuses an unsafe Production start and `web.config` sets Production explicitly, so the exposure is a misdeployment, not a standing hole.
* **Code must change.** Configuration and small start-up changes: add the optional site layer; make the shipped base file safe (empty or Production defaults, with demo values in a Demo/Development file the tests and the Docker demo select); add the missing empty keys; add a `UserSecretsId`.
* **Documentation only.** For J-36 and J-37 a documented convention may be enough.
* **JBS Infrastructure confirmation.** The per-site naming (what "SiteCode" means for a multi-tenant product) and where per-site files live on the servers (I-11).
* **Recommended remediation.** One small configuration slice (P1 in the plan). Keep the secret-reference scheme; offer `GetConnectionString("Default")` as a fallback that resolves to the same reference.

### D-57 / D-60 / D-62 Data access, parameterised SQL and integrated authentication (J-57, J-60, J-62)

* **Current implementation.** EF Core for application data; ADO.NET in the recovery, upgrade and inventory tooling because `BACKUP`, `RESTORE`, `DBCC` and catalogue queries are not model operations. All values are parameters. The two non-parameter forms are `DBCC CHECKDB` with a name validated against a strict pattern, and internal constant table names. Production guides use a SQL login for the application and a separate one for backup; the Windows quick start uses integrated authentication; `Database:ConnectionStringRef` keeps any string out of `web.config`.
* **JBS requirement.** One approach per application; all SQL parameterised; prefer Windows/integrated authentication where the environment supports it.
* **Risk.** **Low.** The exceptions are reviewed and bounded. Integrated authentication through an IIS pool identity is plausible but unproven; a gMSA must be a database principal with the application's least-privilege roles, and the migration identity needs more rights (schema creation, security policies) than the runtime one.
* **Code must change.** No for SQL. A guard tweak may be needed so an integrated connection string, which contains no secret, does not have to sit in a file.
* **Documentation only.** Mostly: record the reviewed exceptions; add a static check that fails a build on string-built SQL outside the two named places (P2).
* **JBS DBA confirmation.** Yes (section 7).
* **Recommended remediation.** Keep as is; document; prove integrated authentication in the Windows test; generate idempotent migration scripts per module for a DBA who wants scripted deployment (the application today applies migrations with its own `migrate` command under a separate identity).

### D-55 Anonymous endpoints versus an IIS site with Anonymous disabled (J-52, J-68)

* **Current implementation.** A fallback policy requires sign-in everywhere, with deliberate anonymous endpoints: `/health/live`, `/health/startup`, `/health/ready`, `/api/v1/info`, branding CSS, sign-in, sign-out, sign-in challenge, the error page, and the first-run setup pages (setup answers 404 to anyone but the server itself). Only some `AllowAnonymous` uses carry an explanatory comment.
* **JBS requirement.** Authenticate by default; `AllowAnonymous` per action with a comment saying who calls it and why; Windows authentication on and Anonymous off for the site (Infrastructure).
* **Risk.** **Medium.** If IIS refuses anonymous requests, container-style probes, monitors and the sign-in page itself stop working unless they authenticate too.
* **Code must change.** Comments only, plus the Negotiate design in D-45.
* **Documentation only.** The comments and a list of the anonymous endpoints with their callers.
* **JBS Infrastructure confirmation.** Yes: whether anonymous access is enabled per path or whether monitors can use Windows credentials (I-05).
* **Recommended remediation.** Add a one-line comment to every `AllowAnonymous`; publish the list; agree path-level IIS settings with Infrastructure.

### D-68 AllowAnonymous comments (J-68)

Covered in D-55; comments only, no behaviour change.

### D-74 / D-76 / D-78 Delivery conventions (J-74 to J-79; unconfirmed section)

* **Current implementation.** GitHub Actions with three jobs (build, tests, packs, checks, supply chain; SQL Server integration including the N-1 to N upgrade rehearsal; container build and smoke); `main`; slice-named branches; commits that start with the slice and end with attribution lines; 41 ADRs and numbered, status-labelled task lists.
* **JBS requirement.** Azure DevOps YAML, triggered on `master` with path filters, `release` configuration, one job per deployable; branches and commits named by ticket and work item; dated design docs; checkbox plans.
* **Risk.** **Medium for the pipeline, low for naming.** JBS cannot build, test or release this through its pipeline today. The convention differences are cosmetic until JBS work items exist.
* **Code must change.** No application code. A new pipeline definition and, for a Windows deployment, a publish step that produces the IIS package.
* **Documentation only.** Naming: yes, once JBS says whether the unconfirmed section binds.
* **JBS Infrastructure confirmation.** Yes: where the repository will live (Azure Repos or GitHub), the agent pools, and whether agents can run a disposable SQL Server (I-07, I-08).
* **Recommended remediation.** Add azure-pipelines.yml mirroring the existing jobs (restore with `--locked-mode`, warnings-as-errors build, tests, supply chain, SQL Server integration, publish artefact), keep GitHub Actions as the development pipeline, and adopt the naming conventions when work items exist. Do not rename existing branches or rewrite commit history.

## 6. The seven areas that need particular attention

**A. IIS deployment.** The scaffold matches the standard in shape (V2 module, in-process, stdout off, environment variables) and was never run. Gaps: the file is not in the publish output; the 100 MB request limit contradicts the 2 GB migration upload; no `requestTimeout` decision; in-process background timers need an always-running pool; the Windows-only data-protection key ring (DPAPI) assumes a single server unless a shared location is chosen; HTTPS is required by the application, which suits a TLS binding on IIS. Result: addable without redesign; needs one verification pass on Windows.

**B. Windows Negotiate authentication.** Absent; the largest functional gap (D-45). The identity model (invite-only, link by issuer and subject, group mappings, signed session) can take a Windows provider without change downstream.

**C. Serilog and Application Insights.** Absent (D-10/D-40). Confidentiality and tenant-isolation rules make the redaction design part of the work, not an afterthought.

**D. Hangfire versus hosted jobs and leases.** Justified deviation with a small, optional adapter seam (D-06); the real requirement is the IIS pool setting.

**E. GitHub Actions versus Azure DevOps.** Non-compliant with an unconfirmed section; an additive pipeline file closes it (D-74).

**F. SQL Server operational requirements.** Tested on 2022 only, with default collation; row-level security and session-context require a version that supports them; the application needs distinct runtime and migration identities, `sp_getapplock`, catalogue read access for readiness, and, if its own backup feature is used, backup rights, which may conflict with a DBA-owned backup policy. Section 7 lists the questions.

**G. Project naming.** `Ecp.*` versus `Jbssa.<App>`: justified deviation (D-14/D-15); do not rename without a mandatory ruling.

## 7. Questions for JBS

### JBS Infrastructure

| # | Question | Why it matters |
|---|---|---|
| I-01 | IIS and Windows Server versions per environment | Module and TLS behaviour |
| I-02 | Which .NET Hosting Bundle versions are installed, and who installs 10.x | The application is .NET 10 |
| I-03 | App pool identity (gMSA or service account), `No Managed Code`, recycle and idle policy | Identity for SQL integrated authentication; in-process timers |
| I-04 | May the pool be set to *Always Running* with preload and no idle time-out, or should background work run as a separate worker host | Notifications, reminders, scanning stop otherwise |
| I-05 | Is Windows Authentication enabled and Anonymous disabled site-wide; can anonymous be allowed for named paths (health, sign-in) | Conflicts with the application's anonymous endpoints (D-55) |
| I-06 | Site, host name and port allocation; TLS certificate source and renewal | Production requires HTTPS |
| I-07 | Where the repository will live and which build agents (Windows or Linux), and can an agent run a disposable SQL Server or a service container | Pipeline design, SQL Server tests |
| I-08 | How a deployment reaches the server and how per-environment `appsettings` are applied | Where `web.config` environment values, secrets files and the per-site layer live |
| I-09 | Application Insights resource, data residency and what telemetry may contain | Contract data and tenant isolation |
| I-10 | SMTP route and recipients for Fatal alerts | Fatal-only email sink |
| I-11 | Where secret files or secret stores live on the server (the application supports `env:` and `file:` references; Key Vault and DPAPI references are declared but not implemented) | Production secret handling |
| I-12 | Is Entra ID through OpenID Connect acceptable in place of Windows Negotiate for this application | Decides whether D-45 is a build or a configuration |
| I-13 | Is the `Jbssa.` assembly prefix mandatory for a vendor-neutral product | Decides whether a rename is needed |
| I-14 | Do Sections 5, 9, 10 and 11 bind, or are they guidance | Decides the status of J-38 to J-44 and J-70 to J-90 |
| I-15 | Which FluentAssertions and Moq versions JBS permits | Licence position |

### JBS DBA team

| # | Question | Why it matters |
|---|---|---|
| DB-01 | SQL Server version and compatibility level per environment (the platform is tested on 2022) | Row-level security and session context need a supported level |
| DB-02 | Collation standard | Tests ran on the container default (case-insensitive); text comparisons and indexes assume it |
| DB-03 | Naming conventions for tables, columns, keys, indexes and procedures | Module schemas are lower-case (`contracts`, `audit`), tables are singular Pascal case; a convention clash is a rename in migrations |
| DB-04 | Service account and least-privilege roles: runtime, migration, and (if used) backup | The runtime needs no schema rights; migration needs schema and security-policy rights |
| DB-05 | `Encrypt` and `TrustServerCertificate` policy | Tests use `TrustServerCertificate=True`; Production guidance uses `False` |
| DB-06 | Who runs migrations in production, and in what form: the application's `migrate` command or reviewed scripts | Scripted delivery is not provided today (D-62) |
| DB-07 | Backup, retention and maintenance policy; may the application run its own backups | The platform has a backup, restore and validation feature that may duplicate DBA tooling |
| DB-08 | One database per installation, and how many tenants per database | Row-level security isolates tenants inside one database |
| DB-09 | Permission for `sp_getapplock`, `sp_set_session_context` and catalogue views | Used by audit chaining, row-level security and readiness |
| DB-10 | Is integrated authentication required for the application's runtime login | Production guide uses a SQL login today |

## 8. Limits of this review

* Nothing was executed on Windows, IIS, Windows Authentication, Azure DevOps, Azure, a JBS SQL Server or a JBS Application Insights resource.
* Statements about framework defaults on Windows (the default log providers, the scope of `requestTimeout`, which Windows-authentication handler suits in-process hosting) come from knowledge of the framework, not from a run, and are marked for confirmation in the Windows test.
* No code, project, configuration, workflow or test was changed. The repository's own documentation checks cover this file and its companion.
* Classifications of Sections 5 and 9 to 11 are provisional until question I-14 is answered.
