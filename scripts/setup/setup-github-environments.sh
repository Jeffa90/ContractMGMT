#!/usr/bin/env bash
# Creates the QA and Production GitHub Environments and their deploy variables for this repo.
# Needs a gh login with ADMIN rights on the repo. Safe to re-run (values are overwritten).
# Values marked UPDATE ME must be replaced once IT supplies them (cert, service account, DNS).
# Secrets are never stored here - set them afterwards with: gh secret set <NAME> --env <ENV> -R <REPO>
set -euo pipefail

REPO="${1:-Jeffa90/ContractMGMT}"
PROD_REVIEWER="${PROD_REVIEWER:-}"   # GitHub username required to approve Production deploys

create_env() {
  local env="$1" body='{}'
  if [ "$env" = "Production" ] && [ -n "$PROD_REVIEWER" ]; then
    local id; id=$(gh api "users/$PROD_REVIEWER" -q .id)
    body="{\"reviewers\":[{\"type\":\"User\",\"id\":$id}]}"
  fi
  echo "$body" | gh api -X PUT "repos/$REPO/environments/$env" --input - >/dev/null
  echo "Environment $env ready"
}

setvar() { gh variable set "$2" --env "$1" -R "$REPO" --body "$3"; }


# App settings (APPENV_ prefix is stripped and stamped into web.config). Mirrors src/Ecp.Host/web.config.
# The app refuses an inline connection string, so it reads it from the env var ECP_SQL_CONNECTION
# (Database__ConnectionStringRef=env:...). With Integrated Security it holds no password, so it is a
# variable, not a secret; it connects as the app pool account.
appvars() {
  local env="$1" root="$2" sqlserver="$3"
  setvar "$env" APPENV_Ecp__Mode                    "Production"
  setvar "$env" APPENV_Ecp__DemoTenantResolution    "false"
  setvar "$env" APPENV_Ecp__DemoSeed                "false"
  setvar "$env" APPENV_Ecp__DataDirectory           "$root\Data"
  setvar "$env" APPENV_Database__Provider           "SqlServer"
  setvar "$env" APPENV_Database__ConnectionStringRef "env:ECP_SQL_CONNECTION"
  setvar "$env" APPENV_ECP_SQL_CONNECTION           "Server=$sqlserver,1433;Database=ContractPlatform;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;"
  setvar "$env" APPENV_Ecp__Recovery__Mode          "External"
  setvar "$env" APPENV_Database__AutoMigrate        "false"
}

# ---- QA: app AUQLDDC01QA2WI, database AUQLDDC01QA2XI (shared with JBS AI Initiatives); own site/pool/host ----
create_env QA
setvar QA IIS_SITE_NAME       "ContractPlatform"
setvar QA IIS_APP_POOL        "ContractPlatform"
setvar QA IIS_SITE_PATH       'C:\inetpub\ContractPlatform'
setvar QA IIS_BINDING         "https:*:443:contracts-qa.jbssa.com.au"
setvar QA IIS_CERT_THUMBPRINT "UPDATE ME"
setvar QA IIS_APP_POOL_USER   'AMH\svc_jbsai_qa'   # shared with JBS AI; also runs migrations
setvar QA SMOKE_TEST_URL      "https://contracts-qa.jbssa.com.au/health/live"
appvars QA 'C:\EcpData\ContractPlatform' 'AUQLDDC01QA2XI'

# ---- Production: app AUQLDDC01APP3WI, database AUQLDDC01DBP3WI ----
create_env Production
setvar Production IIS_SITE_NAME       "ContractPlatform"
setvar Production IIS_APP_POOL        "ContractPlatform"
setvar Production IIS_SITE_PATH       'C:\inetpub\ContractPlatform'
setvar Production IIS_BINDING         "https:*:443:contracts.jbssa.com.au"
setvar Production IIS_CERT_THUMBPRINT "UPDATE ME"
setvar Production IIS_APP_POOL_USER   "UPDATE ME"
setvar Production SMOKE_TEST_URL      "https://contracts.jbssa.com.au/health/live"
appvars Production 'D:\EcpData\ContractPlatform' 'AUQLDDC01DBP3WI'

cat <<MSG

Done. Still to do:
  gh secret set IIS_APP_POOL_PASSWORD --env QA -R $REPO   (svc_jbsai_qa's password; also used to run migrations)
  On the server, create <root>\Data with modify access for the app pool account.
Then register a runner for this repo on AUQLDDC01QA2WI with label iis-qa (see docs/DEPLOYMENT.md).
MSG
