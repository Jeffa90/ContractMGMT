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


# App settings (APPENV_ prefix is stripped and stamped into web.config). Mirrors deploy/iis/web.config.
# The connection string itself lives in a file on the server, never in GitHub (SEC-030).
appvars() {
  local env="$1" root="$2"
  setvar "$env" APPENV_Ecp__Mode                    "Production"
  setvar "$env" APPENV_Ecp__DemoTenantResolution    "false"
  setvar "$env" APPENV_Ecp__DemoSeed                "false"
  setvar "$env" APPENV_Ecp__DataDirectory           "$root\Data"
  setvar "$env" APPENV_Database__Provider           "SqlServer"
  setvar "$env" APPENV_Database__ConnectionStringRef "file:$root\Secrets\sql-connection.txt"
  setvar "$env" APPENV_Database__AutoMigrate        "false"
}

# ---- QA: same server as JBS AI Initiatives (AUQLDDC01QA2WI), its own site/pool/host name ----
create_env QA
setvar QA IIS_SITE_NAME       "ContractPlatform"
setvar QA IIS_APP_POOL        "ContractPlatform"
setvar QA IIS_SITE_PATH       'C:\inetpub\ContractPlatform'
setvar QA IIS_BINDING         "https:*:443:contracts-qa.jbssa.com.au"
setvar QA IIS_CERT_THUMBPRINT "UPDATE ME"
setvar QA IIS_APP_POOL_USER   'AMH\svc_contracts_qa'
setvar QA SMOKE_TEST_URL      "https://contracts-qa.jbssa.com.au/health/live"
appvars QA 'C:\EcpData\ContractPlatform'

# ---- Production: server not yet chosen ----
create_env Production
setvar Production IIS_SITE_NAME       "ContractPlatform"
setvar Production IIS_APP_POOL        "ContractPlatform"
setvar Production IIS_SITE_PATH       'C:\inetpub\ContractPlatform'
setvar Production IIS_BINDING         "https:*:443:contracts.jbssa.com.au"
setvar Production IIS_CERT_THUMBPRINT "UPDATE ME"
setvar Production IIS_APP_POOL_USER   "UPDATE ME"
setvar Production SMOKE_TEST_URL      "https://contracts.jbssa.com.au/health/live"
appvars Production 'D:\EcpData\ContractPlatform'

cat <<MSG

Done. Still to do:
  gh secret set IIS_APP_POOL_PASSWORD --env QA -R $REPO
  On the server, create <root>\Secrets\sql-connection.txt (read access for the app pool only)
  and <root>\Data (modify access for the app pool). The connection string is NOT a GitHub secret.
Then register a runner for this repo on AUQLDDC01QA2WI with label iis-qa (see docs/DEPLOYMENT.md).
MSG
