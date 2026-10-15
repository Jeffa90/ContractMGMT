# Adapter Catalogue

Interfaces are named for the design; exact signatures are decided at implementation and recorded in `docs/ARCHITECTURE.md`. Status values: Disabled, Mock, Configured. `[CONFIRMED]` detail unless the interface itself is in Master.

| Kind | Interface | Purpose | Default in Demo | Production implementations | If unavailable |
|---|---|---|---|---|---|
| Documents | `IDocumentStorageProvider` | Store, read, delete, list document content by opaque key; streaming; per-tenant prefix | Local filesystem (persistent volume) | Network filesystem where approved; Azure Blob Storage; SharePoint/Graph (future); object storage (future) `[CONFIRMED]` | Uploads and downloads fail clearly; metadata remains; readiness check fails |
| Email | `IEmailProvider` | Send notification email | Local capture mailbox (no external send) | SMTP; Microsoft Graph; transactional email service | Notifications queue in outbox; in-app notifications continue |
| Reporting | `IReportingProvider` / export | Publish curated data to external analytics | Disabled | SQL views, REST export, file drop (Qlik for JBS) `[CONFIRMED]` | Reporting is stale; transactions unaffected |
| E-signature | `IESignatureProvider` | Send documents for signature, receive status, fetch executed copy and certificate | Mock (simulated sign events) | DocuSign (initial) `[CONFIRMED]` | Manual executed-copy upload remains available |
| ERP | `IErpSupplierSync` | Supplier master synchronisation; outline agreement reference lookups | Disabled | SAP-style supplier sync via file or API (future) | Manual suppliers and bulk import remain available |
| AI | `IAiAssistProvider` | Onboarding proposals, metadata extraction, summaries, schedule drafting, supplier classification `[CONFIRMED]` | Disabled | Provider chosen by tenant | Features hidden; core unaffected |
| Monitoring | `IMonitoringSink` | Metrics, traces, health, SIEM export of audit | Console/structured log | OpenTelemetry exporter; Application Insights; syslog/SIEM | Local logs only |
| Identity | OIDC handlers + `IIdentityDirectory` | Sign-in, group claims, optional directory lookup for people pickers | Demo identity | Generic OIDC; Microsoft Entra ID `[CONFIRMED]` | No sign-in; setup only |
| Malware scan | `IMalwareScanner` | Scan every uploaded and ingested file | Quarantine-and-skip with warning (**Demo only**) | ClamAV service; Microsoft Defender-based service; vendor ICAP | Files stay `Pending`; downloads blocked; Production readiness fails without a scanner or an explicit recorded risk acceptance `[CONFIRMED]` |
| Migration source | `IMigrationSourceConnector` | Read legacy data from SharePoint lists/libraries, source-system exports, future APIs `[CONFIRMED]` | File upload (CSV, XLSX, JSON, ZIP) | SharePoint/Graph connector (future); Medius, Ariba/Icertis-style flat export profiles | File-based import remains |
| Supplier registry | `ISupplierRegistryProvider` | ABN validation `[CONFIRMED]` | Disabled (format check only) | Public ABN lookup service | Format check only; never blocks |
| Supplier website | `ISupplierWebsiteProvider` | Retrieve a website description | Disabled | Allowlisted HTTP fetcher with SSRF controls | Field stays manual |
| Supplier risk | `ISupplierRiskProvider` | Risk data | Disabled | Tenant's chosen risk source (future) | Field stays manual |
| Secrets | `ISecretProvider` | Resolve secret references | Environment variables; Docker secrets | Azure Key Vault; DPAPI-protected file on Windows | Dependent adapters report unhealthy; Production refuses to start without required secrets |
| Audit sink | `IAuditSink` | Forward audit events to SIEM | Disabled | Syslog; HTTPS event collector | Internal audit unaffected; backlog is kept |

## Common behaviours every adapter must implement

- `HealthAsync()` returns healthy, degraded or unhealthy with a non-secret reason.
- `DescribeAsync()` returns capabilities, limits and the data categories that leave the platform (shown to administrators before enabling).
- Mock implementations are deterministic, are covered by the same contract tests, and are visibly labelled "Mock" in the UI and in readiness reports.
- Cancellation tokens and timeouts everywhere; no unbounded buffering of document content.
