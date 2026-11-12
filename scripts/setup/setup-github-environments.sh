#!/usr/bin/env bash
# Creates or updates the QA and Production GitHub Environments and their deploy variables.
#
# SAFE BY DEFAULT. Read this before running it:
#   * Run with --dry-run first. A dry run changes nothing and prints what would be done and what is missing.
#   * It never writes a placeholder. A value that is missing, empty, or looks like a placeholder ("UPDATE ME", "TODO", "<...>") stops the run before anything is changed.
#   * It never changes a variable that already exists with a different value, unless you name it with --overwrite NAME (or pass --overwrite-all after reading the dry run).
#   * Production needs an explicit reviewer (PROD_REVIEWER). The reviewer must not be the person running this script, and the environment is created with "prevent self-review".
#   * Deployments are limited to the branch you name for each environment (QA_BRANCH, PROD_BRANCH); the branch must exist.
#   * Secrets are never read, stored or printed here. Set them yourself with the printed `gh secret set` commands.
#
# Needs: bash, the GitHub CLI (gh) logged in as a repository ADMIN, and a values file.
#
# Usage:
#   scripts/setup/setup-github-environments.sh --repo OWNER/REPO --values FILE [--dry-run] [--overwrite NAME]... [--overwrite-all]
#   scripts/setup/setup-github-environments.sh --list            (print every variable and secret, change nothing)
#
# The values file holds KEY=VALUE lines (see scripts/setup/environments.example.env). Keep the real one outside the repository.
set -euo pipefail

REPO=""; VALUES_FILE=""; DRY_RUN=0; OVERWRITE_ALL=0; LIST_ONLY=0
declare -a OVERWRITE=()
declare -A VALUE=()
declare -a MISSING=() ERRORS=() PLAN=() SECRETS_MISSING=()

usage() { sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --repo) REPO="${2:-}"; shift 2 ;;
    --values) VALUES_FILE="${2:-}"; shift 2 ;;
    --dry-run) DRY_RUN=1; shift ;;
    --overwrite) OVERWRITE+=("${2:-}"); shift 2 ;;
    --overwrite-all) OVERWRITE_ALL=1; shift ;;
    --list) LIST_ONLY=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

# ---- What has to be supplied (this list is the contract; nothing is guessed) ----------------------------------------------------------
# Per environment (prefix the key with QA_ or PROD_ in the values file). R = required, O = optional (then the setting is not written).
REQUIRED_PER_ENV=(
  "IIS_SITE_NAME|IIS site name"
  "IIS_APP_POOL|IIS application pool name"
  "IIS_SITE_PATH|Physical path of the IIS site, for example D:\\inetpub\\site (NOT the data folder)"
  "IIS_BINDING|IIS binding, for example https:*:443:host.example.org"
  "IIS_CERT_THUMBPRINT|Thumbprint of the certificate Infrastructure installed for the binding (NEEDS INFRASTRUCTURE ANSWER)"
  "IIS_APP_POOL_USER|Windows account the application pool runs as, DOMAIN\\name (NEEDS INFRASTRUCTURE ANSWER)"
  "SMOKE_TEST_URL|URL checked after a deploy, for example https://host.example.org/health/live"
  "DATA_DIRECTORY|Absolute path of the data root OUTSIDE the site (documents, keys, logs), for example D:\\EcpData"
  "SQL_SERVER|SQL Server name (NEEDS DBA ANSWER)"
  "SQL_DATABASE|Database name (NEEDS DBA ANSWER)"
)
OPTIONAL_PER_ENV=(
  "TRUST_SERVER_CERTIFICATE|Set to true ONLY if the DBA confirms the server certificate is not trusted by the web server. Default: not written (certificate is validated). (NEEDS DBA ANSWER)"
  "IIS_CERT_AUTO|true to request the certificate from the certificate authority instead of IIS_CERT_THUMBPRINT (then IIS_CERT_CA and IIS_CERT_TEMPLATE are needed) (NEEDS INFRASTRUCTURE ANSWER)"
  "IIS_CERT_CA|Certificate authority, only with IIS_CERT_AUTO"
  "IIS_CERT_TEMPLATE|Certificate template, only with IIS_CERT_AUTO"
  "IIS_CERT_DNS_NAMES|Extra certificate names, space-separated, only with IIS_CERT_AUTO"
  "APP_NETWORK_PROXIES|Known proxy/load-balancer addresses, if any (NEEDS INFRASTRUCTURE ANSWER)"
)
# Per repository (not per environment).
REQUIRED_GLOBAL=(
  "PROD_REVIEWER|GitHub username who must approve Production deployments. Must not be the person running this script."
  "QA_BRANCH|Branch QA may be deployed from"
  "PROD_BRANCH|Branch Production may be deployed from"
)
# Secrets: set by hand, never by this script.
SECRETS=(
  "QA|IIS_APP_POOL_PASSWORD|Password of IIS_APP_POOL_USER, only if the pool runs as a domain account that needs a password to be set by the pipeline"
  "Production|IIS_APP_POOL_PASSWORD|Same, for Production"
)
# Settings the script writes with fixed, safe values (not supplied by you): Production mode, no demonstration data, SQL Server, no automatic migration, recovery owned by the DBA.
FIXED=(
  "APPENV_Ecp__Mode=Production" "APPENV_Ecp__DemoTenantResolution=false" "APPENV_Ecp__DemoSeed=false" "APPENV_Database__Provider=SqlServer"
  "APPENV_Database__AutoMigrate=false" "APPENV_Ecp__Recovery__Mode=External"
)

print_contract() {
  echo "Values to supply (per environment, prefix with QA_ or PROD_):"
  for e in "${REQUIRED_PER_ENV[@]}"; do printf '  required  %-24s %s\n' "${e%%|*}" "${e#*|}"; done
  for e in "${OPTIONAL_PER_ENV[@]}"; do printf '  optional  %-24s %s\n' "${e%%|*}" "${e#*|}"; done
  echo "Values to supply (once):"
  for e in "${REQUIRED_GLOBAL[@]}"; do printf '  required  %-24s %s\n' "${e%%|*}" "${e#*|}"; done
  echo "Written with fixed safe values:"
  for e in "${FIXED[@]}"; do printf '  %s\n' "$e"; done
  echo "Secrets (set by hand; this script never sees them):"
  for e in "${SECRETS[@]}"; do IFS='|' read -r env name note <<<"$e"; printf '  %-11s %-22s %s\n' "$env" "$name" "$note"; done
}

if [ "$LIST_ONLY" = 1 ]; then print_contract; exit 0; fi

fail() { echo "ERROR: $*" >&2; exit 2; }
[ -n "$REPO" ] || fail "--repo OWNER/REPO is required."
[ -n "$VALUES_FILE" ] || fail "--values FILE is required (copy scripts/setup/environments.example.env and fill it in, outside the repository)."
[ -f "$VALUES_FILE" ] || fail "Values file not found: $VALUES_FILE"

# ---- Read the values file: KEY=VALUE only, no shell evaluation ------------------------------------------------------------------------
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"
  case "$line" in ''|'#'*) continue ;; esac
  case "$line" in *=*) ;; *) fail "Not a KEY=VALUE line in $VALUES_FILE: ${line:0:20}..." ;; esac
  key="${line%%=*}"; val="${line#*=}"
  [[ "$key" =~ ^[A-Z0-9_]+$ ]] || fail "Unusable key name in $VALUES_FILE: $key"
  VALUE["$key"]="$val"
done < "$VALUES_FILE"

is_placeholder() {
  local v; v="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
  case "$v" in
    ''|*'update me'*|*updateme*|*todo*|*tbd*|*changeme*|*'change me'*|*'<'*'>'*|*xxx*|*'to be supplied'*|*placeholder*|*'needs '*answer*) return 0 ;;
  esac
  return 1
}

need() { # need KEY  -> records MISSING when absent or a placeholder
  local k="$1"
  if [ -z "${VALUE[$k]+x}" ] || is_placeholder "${VALUE[$k]}"; then MISSING+=("$k"); return 1; fi
  return 0
}

for env in QA PROD; do
  for e in "${REQUIRED_PER_ENV[@]}"; do need "${env}_${e%%|*}" || true; done
  for e in "${OPTIONAL_PER_ENV[@]}"; do
    k="${env}_${e%%|*}"
    if [ -n "${VALUE[$k]+x}" ] && [ -n "${VALUE[$k]}" ] && is_placeholder "${VALUE[$k]}"; then ERRORS+=("$k holds a placeholder. Remove the line or supply a real value."); fi
  done
done
for e in "${REQUIRED_GLOBAL[@]}"; do need "${e%%|*}" || true; done

# Value shape checks that catch the commonest mistakes before anything is written.
for env in QA PROD; do
  p="${VALUE[${env}_IIS_SITE_PATH]:-}"; d="${VALUE[${env}_DATA_DIRECTORY]:-}"
  if [ -n "$p" ] && [ -n "$d" ]; then
    lp="$(printf '%s' "$p" | tr '[:upper:]' '[:lower:]')"; ld="$(printf '%s' "$d" | tr '[:upper:]' '[:lower:]')"
    case "$ld" in "$lp"|"$lp"\\*|"$lp"/*) ERRORS+=("${env}_DATA_DIRECTORY is inside ${env}_IIS_SITE_PATH. A redeploy replaces the site folder; the data root must be outside it.") ;; esac
  fi
  [ -z "$d" ] || [[ "$d" =~ ^([A-Za-z]:\\|\\\\) ]] || ERRORS+=("${env}_DATA_DIRECTORY must be an absolute Windows path (drive letter or UNC).")
  [ -z "${VALUE[${env}_IIS_APP_POOL_USER]:-}" ] || [[ "${VALUE[${env}_IIS_APP_POOL_USER]}" == *\\* ]] || ERRORS+=("${env}_IIS_APP_POOL_USER must be DOMAIN\\name.")
  [ -z "${VALUE[${env}_SMOKE_TEST_URL]:-}" ] || [[ "${VALUE[${env}_SMOKE_TEST_URL]}" == https://* ]] || ERRORS+=("${env}_SMOKE_TEST_URL must be an https:// address.")
  [[ -z "${VALUE[${env}_TRUST_SERVER_CERTIFICATE]:-}" || "${VALUE[${env}_TRUST_SERVER_CERTIFICATE]}" == true || "${VALUE[${env}_TRUST_SERVER_CERTIFICATE]}" == false ]] || ERRORS+=("${env}_TRUST_SERVER_CERTIFICATE must be true or false.")
  for k in "${env}_SQL_SERVER" "${env}_SQL_DATABASE"; do
    [[ -z "${VALUE[$k]:-}" || "${VALUE[$k]}" =~ ^[A-Za-z0-9._,\\-]+$ ]] || ERRORS+=("$k contains characters that are not allowed in a server or database name (no semicolons, quotes or spaces).")
  done
done

# ---- Stop here if anything is missing or wrong: nothing has been changed yet ------------------------------------------------------------
report_and_stop() {
  if [ "${#MISSING[@]}" -gt 0 ]; then
    echo "MISSING required values (supply real values; a placeholder is never written):" >&2
    printf '  %s\n' "${MISSING[@]}" >&2
  fi
  if [ "${#ERRORS[@]}" -gt 0 ]; then
    echo "PROBLEMS:" >&2; printf '  %s\n' "${ERRORS[@]}" >&2
  fi
}
if [ "${#MISSING[@]}" -gt 0 ] || [ "${#ERRORS[@]}" -gt 0 ]; then
  report_and_stop
  echo >&2; print_contract >&2
  if [ "$DRY_RUN" = 1 ]; then echo "Dry run: nothing was changed. Fix the above, then run again." >&2; fi
  exit 2
fi

command -v gh >/dev/null 2>&1 || fail "The GitHub CLI (gh) is required."

# ---- Checks that read from GitHub (read-only, also done in a dry run) -------------------------------------------------------------------
ME="$(gh api user -q .login 2>/dev/null)" || fail "gh is not logged in. Run: gh auth login"
[ "$(gh api "repos/$REPO" -q .permissions.admin 2>/dev/null || echo false)" = "true" ] || fail "You need ADMIN rights on $REPO to create environments."

REVIEWER="${VALUE[PROD_REVIEWER]}"
[ "$(printf '%s' "$REVIEWER" | tr '[:upper:]' '[:lower:]')" != "$(printf '%s' "$ME" | tr '[:upper:]' '[:lower:]')" ] || fail "PROD_REVIEWER is you ($ME). Production approval must be given by someone else; self-approval is not allowed."
REVIEWER_ID="$(gh api "users/$REVIEWER" -q .id 2>/dev/null)" || fail "GitHub user '$REVIEWER' was not found."

for b in QA_BRANCH PROD_BRANCH; do
  branch="${VALUE[$b]}"
  gh api "repos/$REPO/branches/$(printf '%s' "$branch" | sed 's#/#%2F#g')" -q .name >/dev/null 2>&1 || fail "$b names '$branch' but that branch does not exist in $REPO."
done

existing_var() { gh variable get "$2" --env "$1" -R "$REPO" 2>/dev/null || true; }
may_overwrite() { local n="$1"; [ "$OVERWRITE_ALL" = 1 ] && return 0; local o; for o in "${OVERWRITE[@]:-}"; do [ "$o" = "$n" ] && return 0; done; return 1; }

# ---- Plan, then (unless --dry-run) apply ------------------------------------------------------------------------------------------------
run() { if [ "$DRY_RUN" = 1 ]; then PLAN+=("would: $*"); else "$@"; fi; }

set_var() { # set_var ENV NAME VALUE
  local env="$1" name="$2" val="$3" cur
  if is_placeholder "$val"; then fail "Refusing to write a placeholder to $name on $env."; fi
  cur="$(existing_var "$env" "$name")"
  if [ -n "$cur" ] && [ "$cur" = "$val" ]; then PLAN+=("unchanged: $env $name"); return 0; fi
  if [ -n "$cur" ] && ! may_overwrite "$name"; then
    PLAN+=("KEPT (already set to a different value; use --overwrite $name to change): $env $name"); return 0
  fi
  PLAN+=("$([ -n "$cur" ] && echo change || echo set): $env $name")
  [ "$DRY_RUN" = 1 ] || gh variable set "$name" --env "$env" -R "$REPO" --body "$val" >/dev/null
}

ensure_environment() { # ensure_environment ENV BRANCH [reviewer]
  local env="$1" branch="$2" reviewer="${3:-}" body
  if [ -n "$reviewer" ]; then
    body="{\"reviewers\":[{\"type\":\"User\",\"id\":$REVIEWER_ID}],\"prevent_self_review\":true,\"deployment_branch_policy\":{\"protected_branches\":false,\"custom_branch_policies\":true}}"
  else
    body="{\"deployment_branch_policy\":{\"protected_branches\":false,\"custom_branch_policies\":true}}"
  fi
  PLAN+=("environment $env: deployments only from branch '$branch'$([ -n "$reviewer" ] && echo "; required reviewer $reviewer; self-review prevented")")
  if [ "$DRY_RUN" != 1 ]; then
    printf '%s' "$body" | gh api -X PUT "repos/$REPO/environments/$env" --input - >/dev/null
    # Replace the allowed-branch list with exactly the one named branch.
    for id in $(gh api "repos/$REPO/environments/$env/deployment-branch-policies" -q '.branch_policies[].id' 2>/dev/null || true); do
      gh api -X DELETE "repos/$REPO/environments/$env/deployment-branch-policies/$id" >/dev/null
    done
    gh api -X POST "repos/$REPO/environments/$env/deployment-branch-policies" -f name="$branch" >/dev/null
  fi
}

apply_env() { # apply_env GitHubEnvName PREFIX
  local env="$1" p="$2" connection data
  data="${VALUE[${p}_DATA_DIRECTORY]}"
  connection="Server=${VALUE[${p}_SQL_SERVER]};Database=${VALUE[${p}_SQL_DATABASE]};Integrated Security=True;Encrypt=True"
  [ "${VALUE[${p}_TRUST_SERVER_CERTIFICATE]:-}" = "true" ] && connection="$connection;TrustServerCertificate=True"
  # The connection string holds no user name or password (Windows integrated authentication, as the application pool account), so it is a variable, not a secret.
  for name in IIS_SITE_NAME IIS_APP_POOL IIS_SITE_PATH IIS_BINDING IIS_CERT_THUMBPRINT IIS_APP_POOL_USER SMOKE_TEST_URL; do set_var "$env" "$name" "${VALUE[${p}_$name]}"; done
  for name in IIS_CERT_AUTO IIS_CERT_CA IIS_CERT_TEMPLATE IIS_CERT_DNS_NAMES; do
    [ -z "${VALUE[${p}_$name]:-}" ] || set_var "$env" "$name" "${VALUE[${p}_$name]}"
  done
  for f in "${FIXED[@]}"; do set_var "$env" "${f%%=*}" "${f#*=}"; done
  set_var "$env" APPENV_Ecp__DataDirectory "$data"
  set_var "$env" APPENV_Database__ConnectionString "$connection"
  [ -z "${VALUE[${p}_APP_NETWORK_PROXIES]:-}" ] || set_var "$env" APPENV_Ecp__Network__KnownProxies "${VALUE[${p}_APP_NETWORK_PROXIES]}"
}

echo "Repository: $REPO   Running as: $ME   Mode: $([ "$DRY_RUN" = 1 ] && echo 'DRY RUN (nothing is changed)' || echo APPLY)"
ensure_environment QA "${VALUE[QA_BRANCH]}"
apply_env QA QA
ensure_environment Production "${VALUE[PROD_BRANCH]}" "$REVIEWER"
apply_env Production PROD

# Secrets: report which are missing (names only), never read or write a value.
for e in "${SECRETS[@]}"; do
  IFS='|' read -r env name _ <<<"$e"
  if ! gh secret list --env "$env" -R "$REPO" 2>/dev/null | awk '{print $1}' | grep -qx "$name"; then SECRETS_MISSING+=("gh secret set $name --env $env -R $REPO"); fi
done

printf '%s\n' "${PLAN[@]}"
if [ "${#SECRETS_MISSING[@]}" -gt 0 ]; then
  echo; echo "Secrets still to set by hand (only if the application pool needs a password set by the pipeline; the command asks for the value):"
  printf '  %s\n' "${SECRETS_MISSING[@]}"
fi
cat <<MSG

Not done by this script (each needs the owner of the thing):
  * On each server: create the data root with Modify access for the application pool account only (NEEDS INFRASTRUCTURE ANSWER).
  * Register a self-hosted runner on each web server with the labels iis-qa / iis-prod (docs/DEPLOYMENT.md).
  * Database permissions for the runtime and migration accounts (NEEDS DBA ANSWER).
MSG
[ "$DRY_RUN" = 1 ] && echo "Dry run complete: nothing was changed."
exit 0
