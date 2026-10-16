#!/usr/bin/env bash
# Behavioural checks for a built image. Shared by deploy/docker/smoke.sh (full build, CI) and tools/docker-runtime-smoke.sh (runtime stage only).
# Usage: deploy/docker/verify-image.sh <image> [--network host]
# Checks: seeded Demo is ready and has setup closed; a fresh installation is gated to /Setup (API 503) and can issue a one-time token
# that is never written to the container log; ICU and time-zone data are present; the process is not root; Production refuses Demo/SQLite settings.
set -euo pipefail
IMAGE=${1:?image}; NET=(); [[ "${2:-}" == "--network" ]] && NET=(--network "$3")
PORT=${ECP_SMOKE_PORT:-18080}; FRESH=$((PORT + 1))
cleanup() { docker rm -f ecp-smoke ecp-smoke-fresh >/dev/null 2>&1 || true; }
trap cleanup EXIT; cleanup
fail() { echo "FAILED: $*" >&2; exit 1; }
wait_for() { for i in $(seq 1 60); do curl -fsS "$1" >/dev/null 2>&1 && return 0; sleep 1; done; return 1; }

docker run -d --name ecp-smoke "${NET[@]}" -e ASPNETCORE_URLS="http://127.0.0.1:$PORT" -e Ecp__Mode=Demo \
  -e Database__Provider=Sqlite -e Database__ConnectionString="Data Source=/var/ecp/ecp-demo.db" "$IMAGE" >/dev/null
wait_for "http://127.0.0.1:$PORT/health/ready" || { docker logs --tail 40 ecp-smoke >&2; fail "seeded Demo did not become ready"; }
READY=$(curl -fsS "http://127.0.0.1:$PORT/health/ready")
echo "$READY" | grep -q '"id":"R-OPS-02","status":"Healthy"' || fail "ICU/time-zone data missing in the image: $READY"
LOC=$(curl -sS -o /dev/null -w '%{redirect_url}' "http://127.0.0.1:$PORT/"); [[ "$LOC" == */SignIn* ]] || fail "seeded Demo did not ask for sign-in ($LOC)"
[[ "$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/Setup")" == "404" ]] || fail "setup pages are open on a completed installation"
USER_ID=$(docker exec ecp-smoke id -u); [[ "$USER_ID" != "0" ]] || fail "runs as root"

docker run -d --name ecp-smoke-fresh "${NET[@]}" -e ASPNETCORE_URLS="http://127.0.0.1:$FRESH" -e Ecp__Mode=Demo -e Ecp__DemoSeed=false \
  -e Database__Provider=Sqlite -e Database__ConnectionString="Data Source=/var/ecp/ecp-fresh.db" "$IMAGE" >/dev/null
wait_for "http://127.0.0.1:$FRESH/health/live" || { docker logs --tail 40 ecp-smoke-fresh >&2; fail "fresh installation did not start"; }
LOC=$(curl -sS -o /dev/null -w '%{redirect_url}' "http://127.0.0.1:$FRESH/"); [[ "$LOC" == */Setup ]] || fail "fresh installation did not redirect to /Setup ($LOC)"
[[ "$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:$FRESH/api/v1/contracts")" == "503" ]] || fail "API not closed during setup"
TOKEN=$(docker exec ecp-smoke-fresh dotnet Ecp.Host.dll setup-token | grep -E '^[A-Za-z0-9_-]{43}$' | head -n 1 || true); [[ ${#TOKEN} -ge 43 ]] || fail "setup-token printed no token"
if docker logs ecp-smoke-fresh 2>&1 | grep -qF "$TOKEN"; then fail "the setup token appears in the container log"; fi

if docker run --rm "${NET[@]}" -e Ecp__Mode=Production -e Database__Provider=Sqlite -e Database__ConnectionString="Data Source=x.db" "$IMAGE" >/dev/null 2>&1; then
  fail "Production accepted demo settings"; fi
echo "VERIFIED: seeded Demo ready and setup closed, fresh installation gated to /Setup (API 503), setup token issued and not logged, ICU and time-zone data present (R-OPS-02), non-root uid $USER_ID, Production refuses demo/SQLite settings."
