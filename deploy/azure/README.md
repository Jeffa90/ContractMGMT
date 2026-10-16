# Azure deployment

**Status: Scaffolded / Not Verified.** `main.bicep` has not been compiled or deployed: the build workspace has no Azure subscription, `az` CLI or Bicep compiler. Expect to adjust API versions, networking (private endpoints, VNet integration) and database access (an Entra-based contained user for the app's managed identity) when first validated.

Design intent (all revisable by configuration, none of it hard-coded in the product):
- Container image built from `deploy/docker/Dockerfile`; App Service for Linux; managed identity.
- Azure SQL with Entra-only administration; no SQL passwords in the template.
- Connection string held in Key Vault and surfaced to the app as a Key Vault reference; the application receives only a *secret reference* (`env:`), never a literal.
- Migrations run as a deliberate release step (`dotnet Ecp.Host.dll migrate`) under a migration identity; the runtime identity has no schema rights.
- `Ecp__Mode=Production`: the application refuses demo identity, demo seed, SQLite and inline secrets.

First-run setup: open an SSH session to the app (`az webapp ssh`), run `dotnet Ecp.Host.dll setup-token`, and set the app setting `Ecp__Setup__AllowRemote=true` **only while setup runs** (requests reach the app through the platform front end, not from loopback), then remove it. The data directory must be on persistent storage (App Service `/home`) so the setup state survives restarts. Not Verified on Azure.

Not covered yet (future): private networking, Front Door/WAF, backup/restore drills, blob storage provider for documents (the local file provider is not suitable for scaled-out instances), pipelines.
